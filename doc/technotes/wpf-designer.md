# WPF Designer

This technote is the dedicated home for the WPF designer (`WpfDesign`): current state,
architecture, the drag-and-drop findings, and testing notes. The cross-designer roadmap
(WinForms + WPF + WinUI together), framework detection, provider contracts, phases, and the
test matrix live in [`xaml-services.md`](xaml-services.md). The WinUI/Uno designer's dedicated
technote is [`winui-designer.md`](winui-designer.md).

Current status: the WPF designer is the official WPF backend, added to the main solution and
built on `LibreWPF.Sdk`.

## Current Baseline

| Component | Location | Current Status |
|---|---|---|
| WPF Designer | `src/AddIns/DisplayBindings/WpfDesign/` and `externals/vscode-wpf/external/WpfDesigner/` | Added to the main solution, uses `LibreWPF.Sdk`; it is the official WPF backend |
| XAML language server | `externals/vscode-wpf/` | The WPF language server for `.xaml` is wired up; framework detection cannot rely on file extension alone |

The WPF designer engine lives partly in OpenDevelop (`src/AddIns/DisplayBindings/WpfDesign/`)
and partly in the `externals/vscode-wpf` submodule (`external/WpfDesigner/`), which also carries
the XAML language services (`external/wxsg/`). See
[`winui-designer.md`](winui-designer.md) for the externals layout and the local-feed packaging
workflow that applies to `vscode-wpf` changes.

The designer follows the same "split chrome" philosophy as the WinUI designer: the design
surface renders inside the document tab, while Toolbox, Properties, and Outline are served
through OpenDevelop's shell contracts (`IToolsHost.ToolsContent`,
`IHasPropertyContainer.PropertyContainer`, `IOutlineContentHost.OutlineContent`) — the shared
`WpfToolbox` pad in particular is reused by the WinForms designer too
(see [`winforms-designer.md`](winforms-designer.md)).

## Portable Drag-and-Drop Findings (2026-08-12)

Real WPF's drag source blocks inside a Win32 OLE modal loop. `PortablePresentationSource` has no
such loop, so LibreWPF reimplements the source half in
`src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/PortableDragDropOperation.cs`:
capture the mouse, push a nested `DispatcherFrame`, and hit-test on every mouse move to drive the
same `DragEnter`/`DragOver`/`DragLeave`/`Drop` routed events. Two OpenDevelop-visible consequences
came out of investigating toolbox → WPF-designer drag-drop.

### Resolved: re-entrancy guard turned a drag-source cleanup handler into a mid-drag tool reset

`OnPreviewMouseMove` on the drag source is **re-entered for every mouse move of the drag it just
started** — the nested `DispatcherFrame` keeps pumping input through WPF's normal event system,
which real OLE's native modal loop never does. `PortableDragDropOperation.Run` guards against the
resulting recursive `DoDragDrop` with a `[ThreadStatic] s_isRunning` flag (added in librewpf
`1e8db81ec`, "Implement macOS drag and drop"), failing the nested call closed.

Failing *closed* means the nested call **returns normally** rather than blocking. Any `finally`
around the caller's `DoDragDrop` therefore runs *while the outer drag is still in flight*. In
OpenDevelop that finally was `WpfToolbox.ResetToolSelection()`, which sets
`toolService.CurrentTool = PointerTool` → `CreateComponentTool.Deactivate()` →
`designPanel.DragOver -= designPanel_DragOver`. The in-flight drag then had no `DragOver` handler
left and the drop silently created nothing.

Neither change is wrong alone; together they broke drag-drop. Fixed on the OpenDevelop side with an
`isDragging` re-entrancy guard in `WpfToolbox.OnPreviewMouseMove`
(`src/AddIns/DisplayBindings/WpfDesign/WpfDesign.AddIn/Src/WpfToolbox.cs`).

**Rule for any portable drag source:** guard your own `PreviewMouseMove` against re-entry, and never
put drag-teardown work in a `finally` around `DoDragDrop` without one.

### Open: `DragOver` delivery is sparse and path-dependent

Measured with temporary tracing in `CreateComponentTool.designPanel_DragOver`, driving synthetic
input through DevFlow (`/api/v1/ui/actions/{press,drag-move,release}`) against the
`externals/vscode-wpf/sample/net6.0/SamplePane.xaml` fixture:

| gesture | `drag-move` calls | `DragOver` events delivered |
| --- | --- | --- |
| toolbox → bottom of `PaneStack` | 6 | **2** |
| toolbox → top of `PaneStack` | 24 | **0** |

The 24-step case delivered nothing at all, so the drop was silently lost — no control created. A
representative trace of the working case (`p` is design-panel-relative):

```
DragOver p=171,396 ModelHit=Border      -> AddItems: Border FAILED, UserControl FAILED (retry)
DragOver p=335,387 ModelHit=StackPanel  -> AddItems: StackPanel OK  (item created HERE)
(no further DragOver for the rest of the gesture)
```

Consequences for the designer, all downstream of this one defect:

- **The control is created wherever the drag first lands on a valid container, not where the pointer
  is released.** `CreateComponentTool.designPanel_DragOver` creates the item in its
  `moveLogic == null` branch; `MoveLogic` is supposed to make it follow the pointer afterwards, but
  `Start(createPoint)` and `Move(p)` are two *separate* `DragOver` events and only run once the new
  element reports `IsLoaded`. With ~2 events per gesture, `MoveLogic` never runs at all.
- For a `StackPanel` the position *is* the child index, so this shows up as "dropped at the top,
  inserted at the bottom". The index logic itself is fine — an item created at
  `pos=323,5` was correctly placed at index 1 (right after `PaneTitle`); it is the *creation point*
  that is wrong, not `StackPanelPlacementSupport`.
- One gesture was observed creating **two** controls (item created, `moveLogic` reset, item created
  again), so something also resets `moveLogic` mid-drag — likely a spurious `DragLeave`.
- Dropping onto an existing child can lose the drop entirely: `HitTest` returns that child (e.g. a
  `TextBlock`), and `AddItemsWithCustomSize` walks up to the parent — but if no `DragOver` arrives
  while the pointer is over a container that accepts the item, nothing is ever created.

A partial mitigation is in `CreateComponentTool.designPanel_Drop` (replay the real release point
through `MoveLogic` before committing, so the release point is authoritative regardless of how many
`DragOver` events arrived). It does not regress the passing tests, but its benefit could **not** be
demonstrated end to end, because the only scenario that would show it (dropping at the top of the
stack) is blocked by the zero-delivery case above. Treat it as unverified until the delivery problem
is fixed.

Root cause is on the LibreWPF side — `OnPointerUpdate` only runs when `PreviewMouseMove` actually
reaches the drag source — so fixing it in WpfDesigner would only paper over it.

## Testing and Instrumenting

The designer's DevFlow actions live in
`src/AddIns/DisplayBindings/WpfDesign/WpfDesign.AddIn/Src/WpfDesignDevFlowActions.cs`
(`od.wpf-designer.*`); the integration tests follow the DevFlow action pattern documented in
[`integration-testing.md`](integration-testing.md).

For instrumenting the drag path (`DragOver` delivery is invisible from the outside): add
temporary `Console.WriteLine` tracing (gated on an env var) in
`CreateComponentTool.designPanel_DragOver` and `CreateComponentTool.AddItemsWithCustomSize`,
launch with `OD_TEST_MODE=1`, and drive the gesture over DevFlow. Notes that cost time:

- Use `OD_TEST_MODE=1` so the window does not steal focus, and delete
  `~/Library/Application Support/ICSharpCode/SharpDevelop5/{LastViewStates.xml,preferences,layouts}`
  first — restored view state otherwise makes which view is active nondeterministic.
- `WpfDesign.Designer.csproj` pins an older `LibreWPF.Sdk` than the local feed carries, so it cannot
  be built standalone; build `src/AddIns/DisplayBindings/WpfDesign/WpfDesign.AddIn/WpfDesign.AddIn.csproj`
  instead, which builds it as a project reference and deploys the DLL.
- Screen coordinates from `od.wpf-designer.query-element-screen-bounds` and window-relative
  coordinates in `/api/v1/ui/tree` differ by the window origin (~`(10, 61)` here) — do not compare
  them directly.
- Repeatedly calling `od.open-file` in a wait loop can leave the workbench in a state where the drag
  never reaches the design panel at all. Open once, then poll `od.wpf-designer.status`.

## Out-of-process / Surface Isolation decision (2026-08-16)

This section records the review of
`OpenDevelop-WPF-Designer-Surface-Isolation-Architecture.md` against the repository at
`0bd949f9a5b2018ee6e2ee631953d80fee28cbf9`, the `vscode-wpf` submodule at
`f646a6ff06067ed0d0b354093c9464e5107c51ac`, and the public Microsoft documentation listed below.
The research document is directionally correct. OpenDevelop should adopt its process, model and
document boundaries, with an important correction to the proposed presentation sequence: a child
HWND is a Windows-only presenter experiment, not the cross-platform Phase 1 architecture.

### Decision summary

The shipping WPF designer must isolate target-runtime WPF/LibreWPF objects and project code in a
child process. OpenDevelop owns the editor buffers and product UI; the child owns the actual
`DesignSurface`, `XamlDesignContext`, `DesignItem` graph and target-runtime objects.

The following rules are mandatory:

1. OpenDevelop must not load the target project output, project controls or their runtime
   dependencies to display, inspect or populate the designer.
2. `DesignItem`, `DesignItemProperty`, `DependencyObject`, `UIElement`, `DependencyProperty`,
   target `Type`, converters, bindings, resources and arbitrary target objects never cross RPC.
3. Cross-process identity is `(project session, document session, generation, item ID)`. A numeric
   item ID alone is not valid outside one document generation.
4. OpenDevelop is authoritative for XAML/App.xaml buffers, versions, encoding, dirty state and
   saving. The child never writes project files directly.
5. Every mutating operation carries a base document version. A stale operation is rejected and
   cannot overwrite newer source.
6. The child owns WpfDesigner selection, placement and gesture semantics. Host pads project that
   model through DTOs; they do not implement a second competing selection state machine.
7. Process restart, rather than collectible `AssemblyLoadContext`, is the reliable unload boundary
   after rebuilds, runtime changes, hangs or project-code faults.
8. Surface presentation is replaceable. The protocol and remote model must not depend on HWND,
   PNG, shared memory or a particular compositor.

These rules match the useful part of Microsoft's Surface Isolation model: target controls execute
in a different runtime process, while designer-facing tooling uses model/type identifiers rather
than direct runtime objects. They do not attempt to clone Visual Studio's private protocol or
extension SDK.

### Findings verified in the current implementation

The current add-in is fully in process and therefore does not meet the isolation boundary:

- `WpfDesign.AddIn.csproj` directly references `WpfDesign`, `WpfDesign.XamlDom` and
  `WpfDesign.Designer`.
- `WpfViewContent` constructs `DesignSurface`, registers metadata, installs a process-wide
  `AppDomain.AssemblyResolve` handler, parses App.xaml resources, owns selection/undo/outline and
  calls `SaveDesigner`.
- `MyTypeFinder` uses the IDE's type-resolution service to load project/reference assemblies.
- `WpfToolbox.AddProjectDlls` loads and reflects resolved assemblies inside OpenDevelop.
- `DesignItemPropertyGridAdapter` exposes live `DesignItem`, `DesignItemProperty`, target `Type`,
  `object`, `DependencyProperty` and target `TypeConverter` behavior to the property pad.

The hard completion check is not merely that a child process exists. When a form containing a
custom project control is open, the project assembly must be absent from OpenDevelop's loaded
modules and present only in the appropriate surface host.

### What we adopt from the research

| Proposal | Decision | Rationale |
|---|---|---|
| Move the complete WpfDesigner runtime island to a child first | Adopt | Preserves the mature selection, placement, adorner, path editing and gesture engine. |
| DTO/proxy model with stable IDs and type/property identifiers | Adopt | Live CLR objects and target types cannot safely cross runtime/process boundaries. |
| Reuse StreamJsonRpc and existing designer lifecycle code | Adopt | WinForms already proves authenticated launch, cancellation, timeout, logs, shutdown and restart. |
| One process per design runtime context, multiple documents per process | Adopt, after single-document spike | The key includes project, TFM, architecture and project-code mode; this avoids repeated project/resource loads. |
| Host-owned, versioned full-text XAML synchronization first | Adopt | Full text is simple, observable and correct; AST deltas require evidence from profiling. |
| Remote Property Grid, Outline and Toolbox | Adopt | Their current implementations load or hold runtime objects in the IDE. |
| Child-owned WpfDesigner undo during visual editing | Adopt with a boundary | Accepted child changes update the host buffer; a source reload starts a new designer-history boundary. |
| Project-code-disabled safe mode | Adopt as a first-class process/session mode | Crash-loop recovery needs placeholders and a no-project-code option. |
| Process restart as normal lifecycle | Adopt | WPF metadata, static caches, timers, native dependencies and user threads make perfect ALC unload unrealistic. |
| Future host-side design-tool extensions | Defer | Runtime isolation must not expand into a clone of `.designtools.dll` in the first delivery. |

### Corrections and non-goals

#### A child HWND is not the universal first presenter

The research correctly identifies child HWND hosting as the least disruptive way to preserve the
existing routed-input engine on Windows. It is worth an early Windows spike, but it cannot be the
product-wide Phase 1 assumption:

- HWND/HwndHost/HwndSource are Windows concepts; OpenDevelop's LibreWPF designer also targets macOS.
- `SetParent` has documented DPI-awareness and cross-process caveats. Both processes must use a
  compatible awareness context, and mixed-monitor behavior still needs explicit testing.
- HWND islands impose WPF airspace constraints. Host overlays cannot be freely composed above the
  surface, while popups, context menus, IME, accessibility and focus remain separate integration
  risks.
- A foreign top-level window reparented directly into WPF is not the desired design. If the Windows
  path is accepted, OpenDevelop creates a local `HwndHost` container and the child creates a
  `WS_CHILD` `HwndSource` under that container.

Therefore the presenter spike has two tracks:

| Platform/path | Spike | Production direction |
|---|---|---|
| Windows WPF | Local `HwndHost` container + child `HwndSource` | Keep if focus, capture, DPI, popup, IME, UIA and airspace acceptance all pass. |
| macOS LibreWPF | Child-owned render and hit-test/gesture model projected to the host | Shared-memory BGRA frames plus normalized input; PNG/base64 is acceptable only for an initial proof. |

The macOS path means WpfDesigner eventually needs `IDesignInputSource`, pointer/key DTOs and a
pointer-capture abstraction. That refactor must adapt the existing gesture engine; it must not move
placement or adorners into OpenDevelop. Even with image projection, the child renders both content
and design adorners and performs hit testing.

#### Runtime selection requires compatible host binaries

Passing a project's `runtimeconfig.json` to `dotnet exec` does not make a `net10.0-windows` host a
.NET 8 or .NET Framework binary. Supported design runtimes require a declared host/WpfDesigner
matrix, initially expected to include separately built payloads such as `net481`, `net8.0-windows`
and `net10.0-windows` only when the WpfDesigner fork actually builds and tests those targets.
OpenDevelop itself can remain on .NET 10.

No native hostfxr bootstrapper is needed initially. Use the proven `dotnet exec` path and add a
bootstrapper only if runtime roll-forward, architecture or diagnostics cannot be controlled
reliably with deployed runtimeconfig/deps files.

#### Isolation is reliability, not a security sandbox

The child still runs project code as the current user and can access files, network and processes.
The random authentication token and bounded RPC contract protect the protocol from accidental or
unrelated local connections; they do not make project code untrusted. AppContainer/restricted-token
execution would be a separate security project.

### Target architecture and dependency rule

```text
OpenDevelop.exe
  WpfViewContent / document authority
  ProjectDesignManifestBuilder
  RemoteDesignSession
  Remote Property / Outline / Toolbox models
  IRemoteSurfacePresenter
  WpfDesignerHostClient
             |
             | authenticated StreamJsonRpc control plane
             | DTOs + versioned operations
             v
WpfDesigner.SurfaceHost (target runtime / architecture)
  ProjectSurfaceSession
    DocumentSurfaceSession(s)
      DesignSurface / XamlDesignContext
      DesignItemIdRegistry
      property/outline/toolbox/command projectors
  SurfaceTypeFinder
  target assemblies, resources, converters and markup extensions
```

The desired build-time dependency graph is:

```text
WpfDesign.AddIn -> WpfDesigner.Remote -> WpfDesigner.Protocol
WpfDesigner.SurfaceHost -> WpfDesigner.Protocol
WpfDesigner.SurfaceHost -> WpfDesign + WpfDesign.XamlDom + WpfDesign.Designer
```

The final graph must not contain `WpfDesign.AddIn -> WpfDesign.Designer`. This is an inexpensive
architecture test that prevents accidental reintroduction of runtime objects into the IDE.

The protocol project must be UI/runtime neutral. It must not reference WPF, LibreWPF, WpfDesigner
or OpenDevelop shell assemblies merely to reuse `Thickness`, `Type`, `DesignItem` or similar types.

### Project, process and document identity

The process key is conceptually:

```text
(project ID, target framework, architecture, project-code mode)
```

Start implementation with one project/one document per host to prove isolation and lifecycle.
Move to multiple documents per compatible runtime process only after close/reopen/resource tests
pass. Target framework, architecture, project-code-mode or incompatible dependency-graph changes
create a new generation or restart boundary.

OpenDevelop builds and sends a `ProjectDesignManifest`; the child does not reimplement the project
system. At minimum it contains:

- project identity/path, configuration, TFM and architecture;
- assembly/root namespace and output paths;
- runtimeconfig/deps paths;
- resolved reference and project-reference outputs;
- project/content roots;
- current App.xaml path and unsaved text;
- current project-code mode.

All dirty XAML used by the designer, beginning with the designed document and App.xaml, comes from
the host buffer. A later virtual-document callback extends this to merged dictionaries and other
open project resources. Disk is not authoritative when a buffer is open.

### Protocol and remote model

The handshake includes exact/minimum protocol versions, process ID, product/runtime/architecture
and capability strings. Additive optional fields are preferred; unknown enum/command values are
handled explicitly. A mismatch reports both sides' supported ranges.

Every operation includes:

```text
ProjectSessionId
DocumentSessionId
Generation
BaseDocumentVersion
```

The first protocol should cover:

- project open/update/reload/close;
- document open/update/flush/close and invalid-XAML diagnostics;
- presenter create/resize/focus/visibility;
- selection and command state;
- undo/redo/cut/copy/paste/delete/select-all and stable layout command IDs;
- property list/set/reset;
- outline projection/reparent;
- toolbox list/current tool/drop;
- resource invalidation and project-code mode;
- ping, bounded shutdown and fault notifications;
- host callbacks for event-handler creation, class selection, navigation, known dialogs and dirty
  virtual-document reads.

IDs are meaningful only inside one document generation. The child registry maps those IDs to live
`DesignItem`s. Restart creates a new generation and invalidates every old proxy.

#### Property values

Property transport is a tagged value, not arbitrary polymorphic CLR serialization. Initial kinds:

- null, Boolean, string, invariant numeric, enum and URI/time values;
- known structs such as Point, Size, Rect, Thickness, CornerRadius, GridLength, Color and Matrix;
- WPF values represented as constrained XAML where appropriate: Brush, Geometry, Transform,
  Binding and resource expressions;
- target-type invariant text converted only in the child;
- remote object reference/display text for expandable values;
- explicit unsupported/read-only values.

The host never instantiates a project-defined type. `RemotePropertyDescriptor.PropertyType` uses
host-known primitives or a neutral editor model. Set/reset materializes the target value through
the target converter/XAML services in the child, with a bounded synchronous bridge only while the
existing `PropertyDescriptor`-based pad requires it. The long-term property-pad API is async.

Property, selection, outline and command changes are notifications with version/context. Avoid
re-sending the complete model on every mouse move or property invalidation.

### Document synchronization, invalid text, save and undo

OpenDevelop increments the authoritative version whenever it accepts new XAML. A committed visual
transaction serializes full XAML in the child and emits a change based on the expected previous
version. OpenDevelop applies it to the normal opened document and marks that buffer dirty.

Source edits send a new snapshot to the child. Invalid temporary XAML must not immediately destroy
the last good surface: retain the last good context, report diagnostics for the pending text and
replace the surface only after a valid reload.

Save is:

```text
flush(expected version) -> receive matching XAML/resources -> apply to host buffers -> normal save
```

The child never writes XAML/App.xaml/resource files. A stale flush fails without partial save.

During the first implementation, WpfDesigner's child `UndoService` remains authoritative for
visual operations. IDE Undo/Redo routes to child commands when the designer is active; each result
still synchronizes XAML to the host. A source reload establishes a designer-history boundary.
Dirty state follows an accepted document change, not merely `UndoStackChanged`.

### Surface and data planes

StreamJsonRpc is the control plane for lifecycle, model, commands, input metadata and
notifications. It supports JSON-RPC over streams/pipes/web sockets and request cancellation, and
OpenDevelop already has a working authenticated loopback implementation.

Continuous frames must not remain PNG/base64 JSON. If the macOS or optional Windows image
presenter proceeds, use a bounded shared-memory BGRA buffer with sequence number, dimensions,
stride, DPI scale and dirty rectangles. RPC announces frame metadata/invalidation. The host drops
old sequences and applies backpressure; one slow client cannot create an unbounded frame queue.

Input is expressed in design DIPs with explicit physical/DIP transform, buttons, modifiers,
timestamp and pointer ID. Capture, hit testing, gesture state, selection, adorners and placement
remain child-owned.

### Lifecycle, failure and safe mode

The host state machine is `Starting`, `Running`, `Unresponsive`, `Crashed`, `Restarting` and
`Disabled`. Interactive calls use short timeouts; initial load/build/resource operations use
separate longer limits. An unrecoverable timeout captures diagnostics, kills the child and rebuilds
sessions from host-owned snapshots.

One automatic restart is reasonable. A repeated immediate crash stops the loop and offers:

- restart;
- disable project code / safe designer;
- open XAML source;
- view child log.

Safe mode is part of the runtime key and protocol from the beginning. Its initial implementation
may replace project-defined controls with metadata/XAML placeholders; it must not pretend that
simply skipping the project DLL yields full fidelity.

Closing the last compatible document/project requests bounded shutdown and then kills a leaked
child. On Windows, a Job Object with `KILL_ON_JOB_CLOSE` is useful hardening after the basic
lifecycle works. Authentication secrets are never logged.

Structured diagnostics include IDE/host/protocol versions, PID, runtime/TFM/architecture,
manifest/reference resolution, document generation/version, parse/resource/user-code failures,
RPC method duration/timeouts and exit code. The host PID is visible so project-code debugging can
attach to the surface process.

### Implementation plan and acceptance gates

#### Phase 0 — dual presenter and runtime spike

- Extract/reuse the designer process launcher, authenticated StreamJsonRpc lifecycle and logging.
- Start an STA WPF/LibreWPF surface host and load a simple XAML text snapshot.
- Windows: validate local HWND container + child `HwndSource`.
- macOS: validate child render/hit-test and a minimal shared-memory or temporary PNG input loop.
- Crash/hang the child and prove the IDE remains alive and responsive.

Gate: select, move, resize and Delete work; focus returns to the IDE; Windows 100/150% DPI and
macOS Retina scaling are correct; crash and timeout leave a restartable surface.

#### Phase 1 — runtime isolation and packaging

- Add protocol/remote/host projects and deployed runtime payload selection.
- Send `ProjectDesignManifest`; move `DesignSurface`, `MyTypeFinder`, App.xaml parsing and all
  target assembly loading into the child.
- Remove the designer's process-wide `AssemblyResolve` handler from OpenDevelop.
- Add crash/restart/source-only UI and deployment checks.

Gate: a real custom control is visible while its assembly is absent from OpenDevelop's module
list; missing/incompatible runtime gives an actionable diagnostic.

#### Phase 2 — document authority

- Versioned open/update/change/flush/close.
- Host-owned save and dirty state.
- stale rejection, invalid-XAML last-good surface, external/source reload and rebuild restart.

Gate: designer→source, source→designer, undo→save and simultaneous pending edits lose no changes.

#### Phase 3 — selection, commands and remote model

- item registry, generation-safe selection and command-state bridge;
- child undo/redo/copy/cut/paste/delete and stable command IDs;
- remote property value union with common WPF values and notifications.

Gate: host never receives target `Type`/object; common property edits and multi-selection remain
functional through process restart.

#### Phase 4 — Outline and Toolbox

- projected outline with selection, rename, delete, reparent and insertion index;
- child-side framework/project/reference control discovery;
- neutral toolbox DTO and type-ID drag/drop;
- remove `WpfToolbox.AddProjectDlls` assembly reflection from the IDE.

Gate: project controls appear and drop correctly without their assembly loading in OpenDevelop.

#### Phase 5 — service broker and resource hardening

- event-handler creation/navigation, choose-class and known dialog callbacks;
- App.xaml dirty buffer, merged dictionaries, pack/relative URI and virtual-document reads;
- resource invalidation, project rebuild and multi-document restore.

#### Phase 6 — safe mode and presenter decision

- crash-loop project-code-disabled placeholders;
- measure the two presenter paths against interaction, accessibility and performance tests;
- keep Windows HWND only if all acceptance criteria pass; otherwise converge on projected frames.

Do not begin a complete host-side design extension SDK, GPU sharing path or semantic AST delta
protocol before these gates demonstrate a need.

### Required test matrix

The dedicated suite must cover:

- declared runtime/architecture combinations and incompatible runtime diagnostics;
- Window/UserControl/Page and common panels/controls;
- same-project, referenced-project and NuGet controls; custom dependency/attached properties,
  converters, markup extensions and dictionaries;
- throwing constructors/static constructors/converters/extensions, hangs, native dependency
  failures, version conflicts, background threads and modal dialogs;
- App.xaml, merged dictionaries, pack/relative URI, templates, static/dynamic resources and dirty
  resource buffers;
- all existing WpfDesigner interactions: single/multi/rubber-band selection, Canvas/Grid/StackPanel
  placement, move/resize, snaplines, margins, rotate/skew, path/polyline editing, in-place editing,
  toolbox drop, clipboard, delete, undo/redo, context menu and keyboard;
- property read/set/reset, attached/multi-selection/enum/known structs/brush/binding/resource,
  invalid input, custom converter and unsupported custom type;
- launch/close/crash/hang/timeout/protocol/auth/deployment/rebuild/TFM change, multiple documents
  and incompatible simultaneous projects;
- source/design/save races, stale versions, invalid XAML and external changes;
- Windows mixed DPI, popup/airspace, IME, high contrast, UIA, RDP and restore; macOS Retina,
  keyboard/IME/accessibility and window restore.

From Phase 0, record cold/warm startup, first render, selection/property latency, XAML
serialization/reload, memory per process/document and frame/input performance. High-frequency
pointer/frame traffic is not JSON/base64 data-plane traffic.

### Review checklist / architectural red lines

- Does new IDE code load or reflect a project assembly?
- Does a wire DTO contain a WPF/WpfDesigner/runtime CLR object or arbitrary type metadata?
- Is a target-defined type incorrectly represented by host `System.Type`?
- Does every mutation carry session, generation and base version?
- Can a stale request overwrite newer XAML?
- Does save still flow through OpenDevelop buffers?
- Can timeout/crash/rebuild recover solely from host-owned state?
- Is selection authority in the child rather than duplicated?
- Does the Toolbox inspect project assemblies only in the target-runtime child?
- Is presenter-specific state leaking into the document/model protocol?
- Is a WpfDesigner core rewrite actually required, or can a runtime-local projection adapter do it?
- Does the change rely on perfect ALC unload instead of a process restart boundary?

## Reference record for the isolation decision

The links below are intentionally annotated and revision-pinned where possible. Public Microsoft
documentation establishes architectural facts; repository links establish the exact code baseline.
Incidental support stacks or private Visual Studio class names are not treated as contracts.

### Microsoft XAML/WPF designer architecture

1. [XAML designer extensibility migration](https://github.com/microsoft/xaml-designer-extensibility/blob/main/documents/xaml-designer-extensibility-migration.md)
   is the primary public Surface Isolation source. It distinguishes Designer Isolation from
   Surface Isolation, states that target controls and host-side extensions are in different
   processes/runtimes, removes direct runtime-object access, introduces `TypeIdentifier` and
   `TypeDefinition`, documents known cross-runtime value types and describes proxy objects. We use
   it to justify the model/type boundary, not as a specification of Visual Studio's private IPC or
   rendering transport.
2. [Debug or disable project code in XAML Designer](https://learn.microsoft.com/en-us/visualstudio/xaml-tools/debugging-or-disabling-project-code-in-xaml-designer?view=vs-2022)
   documents `WpfSurface.exe` for WPF Core and VS 2022 WPF Framework, the older `XDesProc.exe`
   route, attach-to-process debugging, project-code disabling and placeholder behavior. It supports
   making surface PID, safe mode and crash recovery product features.

### Microsoft designer OOP analogues

3. [State of the Windows Forms Designer for .NET Applications](https://devblogs.microsoft.com/dotnet/state-of-the-windows-forms-designer-for-net-applications/)
   describes target-runtime DesignToolsServer processes, object proxies, remote property
   descriptors and the input-shield concept. It is an architectural analogue, not evidence that
   WPF should copy WinForms rendering or gesture implementation.
4. [Custom Controls for WinForms' Out-of-Process Designer](https://devblogs.microsoft.com/dotnet/custom-controls-for-winforms-out-of-process-designer/)
   describes client/server custom-control design-time code, proxy ViewModels and JSON-RPC. It
   informs the split between runtime-local surface extensions and future host-side design tools.
5. [WinForms in a 64-bit world](https://devblogs.microsoft.com/dotnet/winforms-designer-64-bit-path-forward/)
   explains why runtime/bitness isolation is a product capability, reinforcing the explicit WPF
   runtime/architecture matrix.

### IPC and runtime loading

6. [StreamJsonRpc](https://github.com/microsoft/vs-streamjsonrpc) documents JSON-RPC 2.0 over
   streams, pipes and WebSockets with cancellation and notifications. It supports reusing the
   existing OpenDevelop authenticated process/control plane without binding the protocol to TCP.
7. [.NET AssemblyLoadContext overview](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/understanding-assemblyloadcontext)
   explains isolated loading scopes and version resolution. We use ALC/ADR for dependency probing,
   while treating process restart as the reliable WPF unload boundary.
8. [AssemblyDependencyResolver](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblydependencyresolver)
   is the supported helper for resolving managed/unmanaged dependencies using a component's deps
   graph; it belongs in the modern target-runtime host.
9. [Write a custom .NET runtime host](https://learn.microsoft.com/en-us/dotnet/core/tutorials/netcore-hosting)
   documents `nethost`/`hostfxr`. It is retained for a possible advanced bootstrapper, not required
   for the first `dotnet exec` implementation.

### Windows surface hosting and lifetime

10. [WPF and Win32 interoperation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-and-win32-interoperation)
    documents `HwndHost`, `HwndSource` and the Win32/WPF interop boundary. It supports the local
    container plus child `HwndSource` spike and highlights HWND airspace constraints.
11. [`HwndHost`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.hwndhost) and
    12. [`HwndSource`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.hwndsource)
    are the concrete APIs for the Windows experiment; neither is a macOS presenter contract.
13. [`SetParent`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent)
    documents cross-process and DPI-awareness behavior. This is why HWND embedding requires an
    early mixed-DPI spike and cannot be assumed portable or universally reliable.
14. [Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)
    provide optional `KILL_ON_JOB_CLOSE` process-tree hardening after graceful shutdown/kill works.

### Revision-pinned OpenDevelop baseline

15. [OpenDevelop `0bd949f9`](https://github.com/lextudio/OpenDevelop/commit/0bd949f9a5b2018ee6e2ee631953d80fee28cbf9)
    is the reviewed repository revision.
16. [WPF AddIn project](https://github.com/lextudio/OpenDevelop/blob/0bd949f9a5b2018ee6e2ee631953d80fee28cbf9/src/AddIns/DisplayBindings/WpfDesign/WpfDesign.AddIn/WpfDesign.AddIn.csproj)
    proves the current direct WpfDesigner dependency.
17. [`WpfViewContent`](https://github.com/lextudio/OpenDevelop/blob/0bd949f9a5b2018ee6e2ee631953d80fee28cbf9/src/AddIns/DisplayBindings/WpfDesign/WpfDesign.AddIn/Src/WpfViewContent.cs)
    is the current in-process integration/assembly-resolution/resource/selection/undo/save cut line.
18. [`DesignItemPropertyGridAdapter`](https://github.com/lextudio/OpenDevelop/blob/0bd949f9a5b2018ee6e2ee631953d80fee28cbf9/src/AddIns/DisplayBindings/WpfDesign/WpfDesign.AddIn/Src/DesignItemPropertyGridAdapter.cs)
    demonstrates why the current live property model cannot cross RPC.
19. [`MyTypeFinder`](https://github.com/lextudio/OpenDevelop/blob/0bd949f9a5b2018ee6e2ee631953d80fee28cbf9/src/AddIns/DisplayBindings/WpfDesign/WpfDesign.AddIn/Src/MyTypeFinder.cs)
    contains target assembly loading that must move to the child.
20. [`WpfToolbox`](https://github.com/lextudio/OpenDevelop/blob/0bd949f9a5b2018ee6e2ee631953d80fee28cbf9/src/AddIns/DisplayBindings/WpfDesign/WpfDesign.AddIn/Src/WpfToolbox.cs)
    contains IDE-process project/reference reflection that remote toolbox discovery replaces.

### Existing OpenDevelop OOP infrastructure

21. [`FormsDesignerProtocol`](https://github.com/lextudio/OpenDevelop/blob/0bd949f9a5b2018ee6e2ee631953d80fee28cbf9/src/AddIns/DisplayBindings/FormsDesigner/Project/Src/OutOfProcess/FormsDesignerProtocol.cs),
    22. [`FormsDesignerHostClient`](https://github.com/lextudio/OpenDevelop/blob/0bd949f9a5b2018ee6e2ee631953d80fee28cbf9/src/AddIns/DisplayBindings/FormsDesigner/Project/Src/OutOfProcess/FormsDesignerHostClient.cs),
    23. [`RemoteFormsDesignerControl`](https://github.com/lextudio/OpenDevelop/blob/0bd949f9a5b2018ee6e2ee631953d80fee28cbf9/src/AddIns/DisplayBindings/FormsDesigner/Project/Src/OutOfProcess/RemoteFormsDesignerControl.cs), and
    24. [FormsDesigner host entry point](https://github.com/lextudio/OpenDevelop/blob/0bd949f9a5b2018ee6e2ee631953d80fee28cbf9/src/AddIns/DisplayBindings/FormsDesigner/Host/Program.cs)
    are the concrete reusable precedents for neutral DTOs, authenticated launch, StreamJsonRpc,
    timeout, stale-version rejection, frame sequencing, crash/restart UI and packaging. WPF should
    extract common lifecycle code, not copy the WinForms interaction model wholesale.

### vscode-wpf and WpfDesigner runtime baseline

25. [`vscode-wpf` design document at `f646a6ff`](https://github.com/lextudio/vscode-wpf/blob/f646a6ff06067ed0d0b354093c9464e5107c51ac/docs/DESIGN.md)
    records the existing standalone/persistent per-project process and named-pipe experience.
26. [WpfDesigner revision `ffe6073f`](https://github.com/lextudio/WpfDesigner/commit/ffe6073f04ecaa0e84f7e4e4261d91a3d019ae6b)
    is the engine revision reviewed through the pinned `vscode-wpf` submodule.
27. [`DesignSurface`](https://github.com/lextudio/WpfDesigner/blob/ffe6073f04ecaa0e84f7e4e4261d91a3d019ae6b/WpfDesign.Designer/Project/DesignSurface.cs)
    shows the mature runtime-local surface/services that should move as one island.
28. [`DesignItem`](https://github.com/lextudio/WpfDesigner/blob/ffe6073f04ecaa0e84f7e4e4261d91a3d019ae6b/WpfDesign/Project/DesignItem.cs) and
    29. [`DesignItemProperty`](https://github.com/lextudio/WpfDesigner/blob/ffe6073f04ecaa0e84f7e4e4261d91a3d019ae6b/WpfDesign/Project/DesignItemProperty.cs)
    expose runtime-bound `object`, `Type`, UI and dependency-property APIs; projection adapters are
    preferred to an immediate rewrite of those public APIs.
30. [`MouseGestureBase`](https://github.com/lextudio/WpfDesigner/blob/ffe6073f04ecaa0e84f7e4e4261d91a3d019ae6b/WpfDesign.Designer/Project/Services/MouseGestureBase.cs)
    demonstrates direct routed mouse/key and capture dependencies. It justifies preserving the
    child interaction engine and treating input abstraction as a tested workstream.
31. [WpfDesigner `Directory.Build.props`](https://github.com/lextudio/WpfDesigner/blob/ffe6073f04ecaa0e84f7e4e4261d91a3d019ae6b/Directory.Build.props)
    establishes the reviewed TFM baseline and the need for an explicit runtime payload matrix.
32. [`XamlDesigner/App.xaml.cs`](https://github.com/lextudio/WpfDesigner/blob/ffe6073f04ecaa0e84f7e4e4261d91a3d019ae6b/XamlDesigner/App.xaml.cs)
    is prior art for a standalone WpfDesigner process and command channel; OpenDevelop still uses
    its own unified lifecycle/protocol rather than adding a second permanent IPC stack.
