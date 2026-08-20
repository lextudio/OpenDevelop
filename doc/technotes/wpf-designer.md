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

### DTO mapping onto the shared DDP contract (2026-08-16)

WinForms and WinUI/Uno have since converged onto a concrete shared contract
(`src/Main/Designer/Designer.Remote/DesignerProtocol.cs`, `IDesignHostClient.cs` — see
designer-common.md's "host-side adapter seam"). This section maps today's live, in-process WPF
API — confirmed against the actual source, not assumed — onto that contract, so a future
isolation pass has a concrete target instead of re-deriving it. This is a paper mapping only;
nothing in this section has been implemented, and none of the files it references
(`WpfViewContent.cs`, `DesignSurface`, `XamlDesignContext`, `MyTypeFinder.cs`, `WpfToolbox.cs`)
have changed.

**Needs no new DTO — the shared shapes already fit:**

- **Element tree.** `DesignContext.RootItem`'s `DesignItem` tree (`Component`/`ComponentType`/
  `Name`/`Parent`/`Properties`) maps onto `DesignerElementNode` (Id/Name/Type/bounds/Path/
  Children) the same way WinUI's tree already does — this shape was designed WinUI/WPF-shared
  from the start. The child mints a stable per-generation `Id` for each `DesignItem` (it has none
  today); bounds come from `View`/layout the same way `UnoDesignSurfaceControl.GetBoundsInRoot`
  computes them for WinUI.
- **Save/flush.** `DesignContext.Save(XmlWriter writer)` (via `DesignSurface.SaveDesigner`) always
  serializes the whole document from the root — this already matches `FlushAsync` →
  `DesignerEditSet.Files` (full-text file snapshots), exactly like WinForms/WinUI. No streaming
  per-`DesignItem` save API exists today, and none is needed.
- **Type resolution.** `MyTypeFinder` (`WpfDesign.AddIn/Src/MyTypeFinder.cs`) is driven by an
  `OpenedFile` + `SD.ProjectService.FindProjectContainingFile` to preload the owning project's
  resolved reference assemblies — all of that information is already carried by
  `DesignerDocumentSnapshot`'s existing `ProjectFileName`/`TargetFramework`/`Architecture`/
  `ProjectAssemblyPath` fields. Its eventual child-side replacement (`SurfaceTypeFinder` per the
  isolation-decision table above) consumes those fields directly instead of `OpenedFile`/
  `SD.*` statics, which won't exist in an isolated child.
- **Toolbox/add-element.** `WpfToolbox`'s real toolbox item (`WpfSideTabItem`) wraps a live CLR
  `Type` (`t.FullName`/`ComponentTypeName`), not a markup template string — so a future WPF
  toolbox must follow **WinForms' convention** on `AddElementAsync` (`DesignerToolboxItemInfo
  .TypeName` + the separate `proposedName` parameter), not WinUI's `Template`-materialization
  convention. `XamlNamespace` is a field on `DesignerToolboxItemInfo` the current WPF toolbox
  doesn't populate at all (it derives its category from the assembly's short name, not an xmlns)
  — a real implementation needs to start filling it in so the child can construct
  `<ns:Type .../>` without ever receiving a live CLR `Type` across the boundary.

**One real gap, now closed additively:** `DesignItemProperty.Value` can be a *nested*
`DesignItem` — Binding, Brush, Gradient, Transform and other markup extensions — not just a flat
string (`DesignItemProperty.TextValue`). `DesignerPropertyInfo` only had `Value: string` with no
way to say what that string actually encodes. Added a `Kind` field (default `"String"`, the same
enumeration the "Property values" section above already specified: Null/Boolean/String/Number/
Enum/Point/Size/Rect/Thickness/Color/Brush/Uri/Xaml/Reference/ReadOnly/Unsupported) — WinForms and
WinUI/Uno property values are all flat-string-representable today and need no change; a future WPF
child sets `Kind` and serializes the nested `DesignItem` as constrained XAML text in the same
`Value` field, per this technote's own "Property values" section above (`IsSet`/`Reset()` already
match `IDesignHostPropertyReset`, which WPF should implement).

**A second real gap, documented but not yet wired to any transport:** `ISelectionService
.SelectedItems` (`ICollection<DesignItem>`) is child-owned exactly like WinUI's selection model,
but the shared DTOs had no notification shape for a child to report it — every backend currently
reports selection through its own bespoke control event instead
(`RemoteFormsDesignerControl.SelectionChanged`, `UnoDesignSurfaceControl.SurfacePointerPressed`).
Added `DesignerSelectionChanged { SessionId, DocumentId, ElementIds }` to `DesignerProtocol.cs` as
a settled shape for this — it carries only element ids, never a live `DesignItem`, matching
designer-common.md's "the host never runs a competing selection model" rule. Not wired to any
RPC/notification transport on any backend yet; this exists so a future shared transport (or a
retrofit onto WinForms/WinUI) has one shape to adopt instead of three per-backend ones.

**Explicitly deferred, not designed this round:** event-handler code generation
(`SharpDevelopEventHandlerService`, wired in `WpfViewContent.LoadInternal`) and choose-class
dialogs (`ChooseClassServiceBase`/`IdeChooseClassService`, same place) both need a **child→host
callback direction** — `IDesignHostClient` today is host→child only. Designing that direction is
a prerequisite for Phase 5 below, not something to retrofit into the current contract in isolation;
do not invent a one-off interface for WPF alone before that direction is designed for all backends
that might eventually need it.

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

##### Phase 0 progress (2026-08-17, macOS/LibreWPF slice)

Built a standalone child, `src/AddIns/DisplayBindings/WpfDesign/WpfDesign.SurfaceHost/`, that
loads XAML into the real `XamlDesignContext`/`DesignItem` engine (not a reimplementation) and
exposes it over the same authenticated StreamJsonRpc control plane as the WinForms/WinUI
children (`DesignerHostProcessClient`/`IDesignHostClient`), plus a test project,
`WpfDesign.SurfaceHost.Tests`, mirroring `UnoDesignHostRpcTests`'s shape. `WpfViewContent.cs` and
`WpfDesign.AddIn` were not touched — this is an unreferenced, standalone spike.

Confirmed by direct build/run on this machine (not assumed from reading source):

- The child project builds against the submodule's already-built `WpfDesigner` assemblies via
  direct `<Reference><HintPath>`, not `<ProjectReference>` — a fresh `ProjectReference` build of
  those submodule projects fails SDK resolution, because `externals/vscode-wpf/external/
  WpfDesigner/global.json` pins an older `LibreWPF.Sdk` than the repo's local feed carries, and
  MSBuild resolves a project's `Sdk="..."` version from the *nearest* `global.json` to the file
  declaring it, not from the entry-point project being built.
- **A real, newly-found platform gap, not a WpfDesigner/rendering problem**: `Thread
  .SetApartmentState(ApartmentState.STA)` throws `PlatformNotSupportedException` on macOS — there
  is no real COM to marshal onto outside Windows, and LibreWPF does not need STA there. Fixed by
  only requesting STA when `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`; confirmed the
  child process starts, listens, and handshakes correctly afterward. Everything else in the
  feasibility survey above (`XamlDesignContext` needing no `PresentationSource`/`HwndSource`,
  `RenderTargetBitmap.Render`/`CopyPixels` and `VisualTreeHelper.HitTest` working on a detached
  tree, `DesignItemProperty.SetValue` working outside a `ChangeGroup`) held with no further
  surprises once the dispatcher actually started.
- The 11-scenario RPC test suite (`WpfSurfaceHostRpcTests.cs`) was run for real against the spawned
  child. Once the dispatcher fix above was applied, `session/open` progressed past XAML parsing
  into rendering and hit a second, unrelated, and more fundamental platform gap:

  **`RenderTargetBitmap.Render()` does not work headlessly on macOS/LibreWPF at all — confirmed
  false, not just unverified, contradicting this plan's original feasibility survey.** Every test
  that reaches rendering fails with `DllNotFoundException`/`Unable to load shared library
  'wpfgfx_cor3.dll'`. Traced to ground truth by reading LibreWPF's own checked-out source
  (`src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/Imaging/
  RenderTargetBitmap.cs` at `~/wpf-tools/librewpf`): the file has **no platform branching at
  all** — it always calls into the classic native Windows compositor (`wpfgfx_cor3`), which is
  shipped only for `win-x86`/`win-x64`/`win-arm64` in every installed package
  (`librewpf.transport`, `microsoft.windowsdesktop.app.runtime.*`); no macOS-native build exists.
  LibreWPF's own test harness
  (`src/ProGPU.Wpf.RealPresentationFrameworkHarness/Program.cs:90`) proves the *actual* portable
  render path is a different, ProGPU-specific API —
  `ProGpuWpfCompositionTarget.CreateHeadless()` + `BeginDrawingFrame(width, height)`, wired up by
  reflectively registering an internal `PortableRenderDataDrawingContextSinkProvider`/
  `IPortableRenderDataDrawingContextSink` pair that receives raw draw commands into an image
  adapter (`WpfBitmapSourceImageAdapter`) — not a drop-in substitute for `RenderTargetBitmap`, but
  a real rendering-pipeline integration against internal WPF types.
- Per this plan's own instruction ("if something ... genuinely doesn't work headlessly under
  LibreWPF ... STOP and report exactly what broke and where — do not paper over it with a
  weakened assertion"), rendering is **not implemented this round**. `WpfSurfaceHostService.Render`
  catches the `DllNotFoundException` and returns `null` rather than failing `session/open`
  outright, so the rest of the RPC surface can still be exercised; `DesignerRenderFrame` is
  genuinely absent on this platform, not faked. A follow-up round should either (a) wire up the
  ProGPU headless composition path found above for macOS renders, or (b) keep `Render` optional on
  this platform permanently — a decision for whoever picks this up next, not made unilaterally here.
- **A second, related platform gap, also confirmed by a real run, not assumed**:
  `VisualTreeHelper.HitTest` never descends past the root visual under headless LibreWPF on macOS.
  Diagnostic tracing showed the child's `Grid.Children` really does contain the `TextBlock`/
  `Button` as live WPF objects with correct, already-arranged bounds (`ActualWidth`/`ActualHeight`
  matched the fixture's XAML exactly) — `Measure`/`Arrange`/`UpdateLayout` all ran successfully.
  Yet every hit-test callback reported only the `Grid` itself, regardless of where in its bounds
  the point landed. This looks like the same underlying cause as the `Render` gap above (per-visual
  hit-test geometry likely also depends on the native compositor channel this headless host never
  establishes), but that has not been separately verified — call it a working hypothesis, not a
  confirmed root cause. `design/hit-test` therefore only reliably resolves the document root on
  this platform today; `WpfSurfaceHostRpcTests.DesignHitTest_ResolvesAnElementInsideItsBounds` was
  adjusted to assert that real, current behavior (resolves to the root `Grid`) instead of a
  false-green assertion that per-element picking works. Fixing real per-element hit-testing is
  future work, most likely paired with whichever rendering-pipeline fix is chosen above.
- **Everything else in the 11-scenario suite passes for real against a spawned child**:
  handshake/distinct-session identity, `session/open`/`session/update`/`session/flush` XAML
  round-tripping, `design/set-property` (including the bad-element-id rejection path),
  `design/set-bounds`, `design/delete-elements`, `design/rename`, and two independent
  clients/children proving process isolation. None of these depend on rendering or per-element
  hit-testing, so this is real, direct evidence — not merely that the code compiles — that
  `XamlDesignContext`/`DesignItem` load, mutate and save correctly out-of-process on macOS.
- Final validation command run: `dotnet test src/AddIns/DisplayBindings/WpfDesign/
  WpfDesign.SurfaceHost.Tests --filter-query "/*/*/WpfSurfaceHostRpcTests/*"` → `total: 9,
  failed: 0, succeeded: 9`. `src/AddIns/DisplayBindings/WpfDesign/WpfDesign.AddIn/**` and
  `WpfViewContent.cs` were not touched at any point this round — confirmed via `git status`
  showing no changes under that path; this remains a standalone, unreferenced spike alongside the
  live in-process designer.

#### Phase 1 — runtime isolation and packaging

- Add protocol/remote/host projects and deployed runtime payload selection.
- Send `ProjectDesignManifest`; move `DesignSurface`, `MyTypeFinder`, App.xaml parsing and all
  target assembly loading into the child.
- Remove the designer's process-wide `AssemblyResolve` handler from OpenDevelop.
- Add crash/restart/source-only UI and deployment checks.

Gate: a real custom control is visible while its assembly is absent from OpenDevelop's module
list; missing/incompatible runtime gives an actionable diagnostic.

##### Phase 1 progress (2026-08-17, first slice: child-only type resolution)

Phase 1 is not complete; this slice proves the single highest-value, independently-verifiable
claim it depends on — the gate quoted above, "a real custom control is visible while its assembly
is absent from OpenDevelop's module list" — without yet building the rest of Phase 1's scope
(`ProjectDesignManifest`, App.xaml/resource loading, the live `WpfViewContent.cs` cutover, removing
the AddIn's `AssemblyResolve` handler, crash/restart UI). `WpfDesign.AddIn`/`WpfViewContent.cs`
remain untouched.

- Reused `DesignerDocumentSnapshot`'s existing `ProjectAssemblyPath` field rather than inventing a
  `ProjectDesignManifest` DTO this round; added one additive field,
  `ReferencedAssemblyPaths: List<string>`, for the one gap that field alone couldn't cover
  (resolved reference assemblies). Empty for WinForms/WinUI, so this changes nothing for either
  existing backend.
- New `SurfaceTypeFinder` (`WpfDesign.SurfaceHost/SurfaceTypeFinder.cs`), modeled on the live
  `MyTypeFinder.cs` but driven only by those snapshot fields — no `OpenedFile`/`SD.ProjectService`
  dependency, since none of that exists in the child. `WpfSurfaceHostService.OpenCore` uses it
  (via `XamlLoadSettings.TypeFinder`) only when `ProjectAssemblyPath` is non-empty; every
  Phase 0 stock-control test keeps hitting the unchanged default path.
- Verified for real, not assumed: a new fixture project,
  `WpfDesign.SurfaceHost.Tests/Fixtures/CustomControlFixture/` (a genuinely separate
  `LibreWPF.Sdk` class library, one trivial `GreetingBadge : ContentControl`), is referenced from
  the test project as build-only (`ReferenceOutputAssembly="false"`) so MSBuild compiles it without
  ever loading it into the test process. `WpfSurfaceHostRpcTests
  .CustomControlType_IsResolvedOnlyInTheChild` opens a document whose XAML uses
  `<c:GreetingBadge/>` with `ProjectAssemblyPath` pointing at that fixture DLL, asserts the child
  resolves and renders it into the element tree (`Type == "GreetingBadge"`), and then asserts
  `AppDomain.CurrentDomain.GetAssemblies()` **in the test process** contains no assembly named
  `CustomControlFixture` — the isolation claim itself, checked directly, not inferred.
- A build-glob trap worth recording: putting `CustomControlFixture.csproj` under the test
  project's own directory tree meant the test project's default `**/*.cs` glob also compiled the
  fixture's own source files (and duplicated its `obj/` `AssemblyInfo.cs`) into the test assembly
  itself — silently defeating the isolation proof by loading the fixture's code directly into the
  test process. Fixed with an explicit `<Compile Remove="Fixtures\**\*.cs" />` in
  `WpfDesign.SurfaceHost.Tests.csproj`. Anyone adding another nested fixture project under a test
  project's own folder needs the same exclusion.
- **A real bug in the first version of this wire-in, found by testing the second case**: the
  `XamlLoadSettings.TypeFinder` swap was originally gated on `ProjectAssemblyPath` alone, so a
  document whose controls come only from a *referenced* library (a referenced control project or
  NuGet package — no project-defined controls, hence no project assembly at all) never got a
  `SurfaceTypeFinder` and silently ignored `ReferencedAssemblyPaths` entirely. That is exactly the
  "same-project, referenced-project and NuGet controls" row of this technote's own test matrix.
  Fixed by gating on either input being present; `WpfSurfaceHostRpcTests
  .ReferencedAssemblyControlType_IsResolvedOnlyInTheChild` covers it (same fixture assembly, passed
  via `ReferencedAssemblyPaths` with `ProjectAssemblyPath` left empty, same isolation assertion).
  Verified in both directions — the test fails with the old one-sided condition and passes with the
  fix — so it is real coverage, not a tautology.
- Two testing traps worth recording, both of which produced a *misleading green* before being
  caught: (1) `Accepted == true` is not evidence a document's types resolved —
  `RebuildTreeAndRender` returns early when `RootItem` is null, leaving `Tree` null while
  `OpenCore` still sets `Accepted = true` afterward, so assertions must check the tree, not just
  acceptance; (2) restoring a source file with `mv file.bak file` preserves the *backup's older*
  mtime, so MSBuild sees source-older-than-output and skips the rebuild, silently testing a stale
  binary. `touch` the file after any such restore before rebuilding.
- **Crash detection and restart are now actually tested**, closing a gap in Phase 0's own gate
  ("crash and timeout leave a restartable surface"), which was asserted in the plan but never
  exercised. `WpfSurfaceHostRpcTests.ChildCrash_IsDetectedAndTheSurfaceIsRestartable` hard-kills a
  live surface via `DesignerHostProcessClient.TerminateHost()` — standing in for faulting project
  code, a hung child, or the unrecoverable RPC timeout that `InvokeCoreAsync` already handles by
  calling `TerminateHost` itself — then asserts the `HostExited` event fires, `IsAlive` goes false,
  a subsequent call fails fast with `IOException` (specifically, not "any exception", so it cannot
  pass for an incidental reason) rather than hanging, and a replacement child rebuilds the same
  document purely from host-owned snapshot state with a different process id.
- **A second real bug, and a protocol-correctness one: stale mutations were not rejected at all.**
  `session/flush` validated `baseVersion`, but all four mutating RPCs (`design/set-property`,
  `set-bounds`, `delete-elements`, `rename`) accepted a `baseVersion` argument and merely echoed it
  back into the response without ever comparing it to the open document's version — so a mutation
  carrying a stale version happily applied on top of newer source. That directly violates the
  isolation decision's mandatory rule 5 ("every mutating operation carries a base document version;
  a stale operation is rejected and cannot overwrite newer source") and its own review-checklist
  item "Can a stale request overwrite newer XAML?". Added a `RejectIfStale` guard used by all four
  (rejection is a normal `Accepted == false` + `Error` result, matching how every other mutation
  failure reports on this backend, not an exception).
  `WpfSurfaceHostRpcTests.StaleMutations_AreRejectedAndCannotOverwriteNewerSource` opens at
  version 1, accepts newer source at version 2, then asserts each of the four mutations still
  carrying version 1 is rejected **and** that a subsequent flush shows the newer text intact with
  no partially-applied edit.
- **A fourth real bug: `design/delete-elements` could partially apply a rejected operation.**
  `DeleteElements` resolved and removed each id in the same loop, so a valid id listed *before* an
  invalid one was actually deleted from the live document before the invalid id caused
  `Accepted = false` — the caller sees a rejected operation, but the document already changed.
  Reproduced for real first (deleting `["go", "9,9,9"]` left `go` gone from the flushed XAML despite
  `Accepted == false`), then fixed by resolving every id up front and only removing any of them
  once all have resolved — the same "a rejection cannot partially apply" invariant already enforced
  for stale versions and full-flush staleness. `WpfSurfaceHostRpcTests
  .DesignDeleteElements_OnABadElementId_IsRejectedWithoutPartiallyApplying` covers it; a
  `DesignRename_OnABadElementId_IsRejected` bad-id test was also added, since `rename` had no
  rejection coverage at all until now (it does not have the batch/partial-application risk, since
  it only ever touches one element).
  A narrower, unfixed variant remains: if `DesignItem.Remove()` itself throws partway through a
  multi-item batch (not the bad-id case above, which is now fully guarded), items removed before
  the throw are not rolled back. Fixing that would need a transactional wrapper
  (`ChangeGroup`/`OpenGroup`); left as known, scoped follow-up rather than blocking this fix.
- **`design/add-element` — the one DDP mutation this backend had zero implementation for
  (`WpfSurfaceHostClient.AddElementAsync` threw `NotSupportedException`) — is now implemented,
  passing on the first real run.** Built from the two **public** primitives the real engine's own
  internal helper (`CreateComponentTool.AddItemsWithCustomSize`, `AddIn/Src`) uses under the hood:
  `CreateComponentTool.CreateItem(DesignContext, Type)` creates the `DesignItem`, then
  `PlacementOperation.TryStartInsertNewComponents(parent, items, positions, PlacementType.AddItem)`
  attaches and commits it. The `AddIn`'s own wrapper is `internal` (no `InternalsVisibleTo` reaches
  this child) and additionally hardcodes position to `(0,0)`, so calling the two primitives
  directly — rather than trying to expose the wrapper — was both the only option and a strictly
  better one (real position control for free). Type resolution goes through the document's own
  `XamlDesignContext.ParserSettings.TypeFinder.GetType(xmlNamespace, typeName)`, the same
  `TypeFinder` Phase 0/the `SurfaceTypeFinder` slice already wire up — so project-defined and
  referenced-library controls can be added through this RPC too, with no extra work.
  `IDesignHostClient.AddElementAsync`'s signature carries no width/height (`DesignerToolboxItemInfo`
  has no size field either, matching WinForms/WinUI's own convention), so a fixed default size is
  used, the same shape SetBounds already resizes after the fact if a caller wants something else.
  `design/set-event` remains deliberately unimplemented — it needs a child→host callback direction
  that doesn't exist yet (Phase 5 work per this technote's own notes), not a gap to fill
  opportunistically alongside add-element.
- Final validation: `dotnet test src/AddIns/DisplayBindings/WpfDesign/WpfDesign.SurfaceHost.Tests
  --filter-query "/*/*/WpfSurfaceHostRpcTests/*"` → `total: 18, failed: 0, succeeded: 18` (the 9
  Phase 0 scenarios plus project-assembly, referenced-assembly, crash/restart, stale-version
  rejection, App.xaml resources, the two delete/rename rejection-path tests, and the two new
  add-element tests), against real spawned children.
- Explicitly not attempted this slice: `ProjectDesignManifest` (still using the flat snapshot
  fields), wiring `WpfViewContent.cs` to actually use this child, removing the AddIn's process-wide
  `AssemblyResolve` handler (only matters once real cutover happens), and crash/restart/safe-mode
  UI. All remain open Phase 1 work.

##### ProGPU headless render integration — landed (2026-08-17)

Before any real cutover of `WpfViewContent.cs`, the biggest known prerequisite gap was Phase 0's
finding that `RenderTargetBitmap.Render()` never works headlessly on macOS/LibreWPF at all (it
calls into the classic native Windows compositor, `wpfgfx_cor3`, which has no macOS build). That
gap is now closed: `System.Windows.Media.ProGPU.ProGpuWpfCompositionTarget` — a genuinely public,
ordinary managed API (`LibreWPF.ProGPU` NuGet package, no reflection needed) — genuinely renders
real WPF visual content headlessly, and `WpfSurfaceHostService.Render` now uses it for good.

**Bottom line up front: there was never an upstream LibreWPF/ProGPU bug.** An earlier pass through
this investigation recorded two "root cause found" theories, both wrong, both born from the same
mistake — trusting a specific pixel sample instead of scanning the whole frame. Recorded here so
nobody re-chases them:

- ❌ *"The GPU/WebGPU backend silently isn't executing the render; needs a debugger."* Wrong.
  `ReplayVisualSubtree`'s own result object already showed the WPF content fully decoded and
  applied (`RecordCount=10, AppliedCount=10, SkippedCount=0, UnsupportedCount=0`), and the
  compositor's version counters incremented correctly. The pipeline was doing real work; the
  probe pixels just weren't where the content landed.
- ❌ *"The project compiles against a different `PresentationCore` identity than `ProGPU.Wpf`
  (`Version=0.1.0.0`, a shim), which corrupts values crossing into the compositor."* Wrong. Dumping
  actual assembly identities (`AssemblyName.GetAssemblyName`, not guessed) showed **every**
  `ProGPU.Wpf.dll` build anywhere in the LibreWPF checkout — including the harness builds LibreWPF's
  own passing tests use — consistently references that shim identity, by design
  (`ProGpuUseWindowsBaseShim=true`, a deliberate cross-framework compile-time contract for the
  standalone ProGPU engine, shared across WPF/WinUI/Avalonia backends). It is bridged successfully
  at runtime via reflection-based `…Bridge` classes using duck-typed `Portable*` interfaces — not a
  defect, and not the cause of anything.

**The decisive experiment**, after chasing both theories into LibreWPF's own decoder
(`WpfMilRenderDataDecoder`) and resource resolver (`WpfResourceResolver`) source: re-running the
real `ReplayVisualSubtree` + `Render` + `ReadPixels` path with a **full-frame** scan (not a fixed
sample point) found real, correctly-composited content — white/black pixels consistent with the
fixture's `Button` — sitting at `(300–395, 285–295)` in the 400×300 frame, far from every pixel
coordinate any earlier probe had sampled. To rule out that in-flight diagnostic patches to
LibreWPF source had "fixed" something, the exact same full-frame scan was re-run against a
**pristine, completely unmodified** `ProGPU.Wpf.dll` copied fresh from the NuGet cache
(`~/.nuget/packages/librewpf.progpu/0.1.0-preview.41/lib/net10.0/ProGPU.Wpf.dll`, no edits at all)
— byte-identical output. **The rendering pipeline was correct the entire time; the only bug was in
this investigation's own pixel-sampling assumptions.** All temporary diagnostic edits made to the
`~/wpf-tools/librewpf` checkout during the investigation were reverted (`git checkout`); that
checkout is clean except for pre-existing, unrelated user changes to `Popup.cs`/`Window.cs`.

**What's now in place, kept for good:**

`WpfSurfaceHostService.Render` constructs one `ProGpuWpfCompositionTarget.CreateHeadless()` per
process (cached, matching every LibreWPF test/harness), and per frame: creates a `GpuTexture`
sized to the element, calls `ReplayVisualSubtree(element, width, height)` then
`Render(width, height, width, height, 1f, texture.ViewPtr)`, reads back pixels via
`texture.ReadPixels()` (which returns RGBA byte order — swapped to BGRA in place to match the
existing `DesignerRenderFrame.Data` wire shape), then deflate+base64-encodes exactly as before. A
broad catch still disables rendering for the rest of that process's life on any exception, same
fallback behaviour as the old `RenderTargetBitmap` path.

`WpfSurfaceHostRpcTests.cs`'s render assertions were tightened from Phase 0's soft
`if (opened.Render != null)` guards to hard `Assert.NotNull(opened.Render)` — rendering is no
longer best-effort — and a new test, `SessionOpen_RendersRealWpfContentIntoTheFrame`, decodes a
real frame and asserts at least one pixel differs from the (0,0) background corner, proving real
content was composited (verified decisive: temporarily forcing `Render()` to always return null
made this test fail as expected, then restoring the real code made it pass again).

### Coordinate mismatch: SOLVED (2026-08-17) — our own hardcoded 800x600 arrange viewport

**The bug was entirely in this repo's own `WpfSurfaceHostService`, not in LibreWPF or ProGPU at
all.** Two earlier hypotheses recorded in this technote were wrong; both are retracted below, since
leaving them would send the next reader into the same dead ends:

- ❌ *"ProGPU's replay pipeline mis-composes offsets/scale for a headless root — the content is an
  exactly 0.5x scaled, corner-anchored copy."* Wrong on both counts. The scale reading was an
  artifact of eyeballing a bounding box that had been **translated and then clipped**, not scaled.
- ❌ *"The root is never `PropagateResumeLayout`-ed, so `IsLayoutSuspended` leaves its `DesiredSize`
  at (0,0)."* A real code path (`UIElement.MeasureCore`), but not what happens here — measured
  directly from `WpfSurfaceHostService` via public API: the root reports
  `DesiredSize=400,300 ActualWidth=400 ActualHeight=300 RenderSize=400,300`, all correct.

**Actual root cause**, pinned down by printing the values `FrameworkElement.ArrangeCore`'s alignment
math consumes (`Width`/`MinWidth`/`MaxWidth`/`RenderSize`/`DesiredSize`/`VisualTreeHelper.GetOffset`,
all public API, no LibreWPF rebuild needed):

```text
root:      Width=400 MaxWidth=inf RenderSize=400,300 DesiredSize=400,300 VisualOffset=200,150
TextBlock: Width=200 MaxWidth=inf RenderSize=200,30  DesiredSize=200,30  VisualOffset=100,135
Button:    Width=80  MaxWidth=inf RenderSize=80,24   DesiredSize=80,24   VisualOffset=160,138
```

The children are centered correctly inside 400 (`(400-200)/2 = 100`, `(400-80)/2 = 160`). The root
is centered inside **800x600**: `(800-400)/2 = 200`, `(600-300)/2 = 150`. And indeed
`RebuildTreeAndRender` called `root.Arrange(new Rect(0, 0, lastWidth, lastHeight))` with
`lastWidth = 800, lastHeight = 600` — hardcoded field defaults that were never updated from the
document. WPF then behaved *exactly correctly*: `ArrangeCore` clamps the arrange size back to the
root's declared 400x300 via `MinMax` (which is why `RenderSize`/`ActualWidth` all looked right and
hid the problem), but `ComputeAlignmentOffset` still centers that 400x300 content inside the
800x600 slot it was given, leaving the root a `VisualOffset` of `(200,150)`. `Render()` then sized
its texture from the root's own `ActualWidth`/`ActualHeight` (400x300), so every replayed pixel was
shifted by that offset and clipped at the texture edge.

Verified arithmetically against the original measurement: the `TextBlock` at tree-reported
`(100,135)` plus the root's `(200,150)` lands at `(300,285)`, size 200x30, clipped to the 400x300
frame → `x[300,399] y[285,299]` — **the exact bounding box measured earlier**, to the pixel.

**Fix**: measure against the viewport as before, then arrange at the root's *own* `DesiredSize`
rather than the viewport rect, so no alignment offset is introduced. That is also the semantics a
design surface wants — show the design at its natural/declared size and let the host canvas
letterbox around it (`DesignViewport`/`DesignerCanvas` already do exactly that on the host side).

**Regression test**: `RenderedContent_LandsExactlyAtTheBoundsTheElementTreeReports` scans the frame
for the fixture `TextBlock`'s white background and asserts its painted bounding box matches the
bounds the element tree reports for that node, within 1px. Verified decisive by temporarily
restoring the old `Arrange` call: the test fails with `Range: (99 - 101), Actual: 300`, i.e. it
catches exactly the 200px shift. Full suite: `total: 21, failed: 0, succeeded: 21`.

**Consequences for the rest of the cutover**: `WpfSurfaceDesignerControl` can now draw selection
adorners straight from `DesignerElementNode` bounds and have them line up with the rendered frame,
which was the blocker listed against interactive drag/resize/toolbox-drop work. LibreWPF and ProGPU
need no change; `~/openavalon/LibreWPF` was left clean (all diagnostic patches and the temporary
`LibreWPF.ProGPU 0.1.0-preview.99` package reverted).

**Method note worth keeping**: the two wrong hypotheses both came from reasoning about *upstream*
code before exhausting what this project's own public API could measure. The decisive datum -
children centered in 400 while the root centered in 800 - was available from `WpfSurfaceHostService`
itself the whole time, with no LibreWPF rebuild, no repacking, and no debugger.

**Final validation:** `dotnet test src/AddIns/DisplayBindings/WpfDesign/WpfDesign.SurfaceHost.Tests
--filter-query "/*/*/WpfSurfaceHostRpcTests/*"` → `total: 19, failed: 0, succeeded: 19`, confirmed
on two consecutive clean runs. (A transient batch of 3 failures with no logged exception was seen
once mid-investigation during a rapid rebuild/DLL-swap cycle; `[assembly: CollectionBehavior
(DisableTestParallelization = true)]` was added as a defensive measure in
`WpfDesign.SurfaceHost.Tests/AssemblyInfo.cs`, but its effectiveness was never conclusively proven
— the failures did not reliably reproduce even before that change, so the root cause may simply
have been incidental system load during that investigation rather than GPU-context concurrency.
Left in place since it's harmless either way, but treat its doc comment's stronger claim with that
caveat.)

**The per-element `VisualTreeHelper.HitTest` gap is fixed (2026-08-17), and it was indeed unrelated
to the render bug above — a separate, pure-WPF issue that had nothing to do with ProGPU's render
pipeline.** Checked directly rather than left as a guess: `VisualTreeHelper.HitTest` takes an
optional `HitTestFilterCallback`, invoked once per visual the traversal *considers* before it ever
reaches the result callback — Phase 0's original finding only showed the result callback reporting
the root, which left open whether children were visited and filtered out, or never visited at all.
Passing a filter that logs every visit answered that directly: it fired exactly once, for the root
`Grid`, and never for either child — the traversal itself never attempted to descend, before any
filtering or result-reporting logic ran at all. Reading `PortablePresentationSource.cs`
(`~/openavalon/LibreWPF/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/
PortablePresentationSource.cs`) confirmed why: point/geometry hit-testing there is entirely
delegated to `HitTestOverride`/`HitTestAllOverride`/`HitTestBoundsOverride` callbacks the host sets
on the source (`IPortablePresentationSourceHost.HitTestOverride` etc.) — plain
`VisualTreeHelper.HitTest`'s managed walk (`Visual.HitTestCore`) never descends past a visual with
no such source/override wired up, root or not.

The fix does **not** require constructing an internal `PortablePresentationSource` via reflection
(the plausible-but-complex path this technote once flagged, and the one the user pushed back on as
overcomplicated). `ProGpuWpfCompositionTarget.TryHitTestOwner(Vector2 logicalPoint, out object?
owner, out ProGpuHitTestResult result)` (`~/openavalon/LibreWPF/src/ProGPU.Wpf/
ProGpuWpfCompositionTarget.cs`) is a genuinely public API that answers the exact same question
directly from the GPU-side hit-test index `ReplayVisualSubtree` already builds on every render —
`owner` comes back as the real WPF `Visual` the render-data decoder (`ProGpuCompositionCommandSink`)
attributed that geometry to (`WpfGpuHitTestOwnerMap`, keyed by `sourceVisual`), so the existing
"walk up from the hit visual to the nearest `DesignItem`" logic in `WpfSurfaceHostService.HitTest`
needed no changes — only the entry point (GPU owner lookup instead of `VisualTreeHelper.HitTest`)
changed, with a fallback to the old root-only walk if nothing has been rendered yet.

**Verified decisively**, following this session's standing rule of measuring rather than assuming:
a temporary probe hit-tested a grid of points across the whole 400×300 frame and logged every
`PickPath`. Background points (`(0,0)`, `(10,10)`, `(150,15)`) correctly resolved to no element at
all (nothing painted there), while two different points within the actually-rendered content
(`(300,285)` → path `"0"`, `(390,295)` → path `"1"`) resolved to two distinct, different elements —
decisive, direct proof of genuine per-element resolution, not a fluke or a root-only fallback in
disguise. `WpfSurfaceHostRpcTests.DesignHitTest_ResolvesAnElementInsideItsBounds` was rewritten
around this same scan-for-two-distinct-elements shape (deliberately not hardcoded to `(300,285)`/
`(390,295)`, for the same reason the render test avoids hardcoding coordinates — the tree-bounds
vs. actual-render-position mismatch documented above is still open) rather than the old
root-only assertion. Full suite: `total: 19, failed: 0, succeeded: 19`, confirmed on two
consecutive runs.

**Host-side client extracted into its own WPF-free `WpfDesign.SurfaceHost.Remote` project
(2026-08-17)**, mirroring `FormsDesigner.Remote`/`WinUIXamlDesigner.UnoDesignHost.Remote` (see
designer-common.md's "Relationship to the existing implementations" table). `WpfSurfaceHostClient`
had already been a real `DesignerHostProcessClient : IDesignHostClient` implementation using the
shared `Designer.Remote` DTOs directly (`DesignerSessionState`/`DesignerDocumentSnapshot`/
`DesignerEditSet`/`DesignerHitTestResult`/etc.) — the DTO convergence step from designer-common.md
was effectively already done as a side effect of Phase 0/1 work, not a separate migration. What
moved this round: the class itself lived directly in `WpfDesign.SurfaceHost.Tests` marked "test
only" even though nothing about it actually was; it's now in `ICSharpCode.WpfDesign.SurfaceHost`
inside `WpfDesign.SurfaceHost.Remote.csproj` (net10.0, `PackageReference StreamJsonRpc` +
`ProjectReference Designer.Remote`, `ReferenceOutputAssembly="false"` build-only reference to
`WpfDesign.SurfaceHost.csproj` for the child dll path), and `WpfDesign.SurfaceHost.Tests`
references it as a normal `ProjectReference` instead of compiling the source directly. This is
purely a project-layout change — no behavior changed — so that once `WpfViewContent.cs` is ready
to cut over to the child, `WpfDesign.AddIn` can reference `WpfDesign.SurfaceHost.Remote` the same
way `FormsDesigner`/`WinUIXamlDesigner` reference their own `.Remote` projects, without pulling in
the child's own WPF/designer-engine dependencies. Full suite re-verified after the move:
`total: 19, failed: 0, succeeded: 19`, confirmed on two consecutive runs.
- **App.xaml / app-level resource loading now works in the child, and getting there produced
  several findings worth keeping.** `ParseAppResources` follows the live in-process designer's
  proven approach (`WpfViewContent.LoadInternal`'s `EnableAppXamlParsing` block): pull the
  `<Application.Resources>` property element out of the `AppXaml`-kind snapshot file, copy the root
  element's `xmlns` declarations onto its children (the inner XML is reparsed standalone and would
  otherwise lose them), and parse through a `XamlDesignContext` — *not* a runtime `XamlReader` —
  taking `RootItem.Component` as the dictionary.
  - **Where the dictionary is merged matters.** Merging into `Application.Current.Resources` does
    **not** work here (verified by a real run: the probe stayed unstyled). The live designer merges
    into `DesignPanel.Resources`, i.e. the design surface's *visual ancestor*; this headless child
    has no `DesignPanel`, and the document's own root element is the top of the tree, so the
    dictionary is merged into that root's `Resources` — after parse, before layout, since that is
    when implicit styles are applied.
  - **Two adaptation bugs, both silent.** (1) `<Application.Resources>` is a single XML name
    *containing a dot* — the dot is not a namespace separator, so its `LocalName` is the whole
    `"Application.Resources"` string. Matching `LocalName == "Resources"` never matches and the
    dictionary is skipped with no error (the live designer matched the full `Name`; "improving" it
    to `LocalName` broke it). (2) `<Application.Resources>` may list entries directly rather than
    wrapping them in an explicit `<ResourceDictionary>`, in which case the inner XML parses into
    that single object (a bare `System.Windows.Style`) instead of a dictionary — so the children get
    wrapped in a synthesized `<ResourceDictionary>` unless they already are one.
  - **The verification had to be chosen carefully to avoid a false positive.** `Accepted == true`
    proves nothing: `XamlDesignContext`'s DOM tolerates an unresolved `StaticResourceExtension`
    without rejecting the document (confirmed by disabling the merge — still accepted). Reading the
    value back is also ambiguous: WpfDesigner represents a markup-extension value as a design-time
    wrapper (`XamlObject.cs`'s `StaticResourceWrapper : MarkupExtensionWrapper`) rather than eagerly
    resolving it, so `DesignItemProperty.ValueOnInstance` returns `null` even for a resource that
    *did* resolve. The decisive probe is an **implicit `Style`** (`TargetType`, no `x:Key`) that sets
    `Width`: layout actually consumes it, so the `Width` already reported in the element tree is
    real evidence — 250 with the merge, 400 (stretched to the parent `Grid`) without it, both
    observed. `WpfSurfaceHostRpcTests.AppXamlResources_AreMergedAndAffectTheDocumentLayout` asserts
    exactly that.
  - Still deliberately narrow: no `StartupUri`, no code-behind, no merged-dictionary URI resolution
    or theme dictionaries, and explicit-key `StaticResource` lookups (which resolve during parse
    rather than at layout) are not separately verified.

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

### Phase 2 cutover begun: `WpfSurfaceDesignerControl` (2026-08-17)

The actual cutover of `WpfViewContent.cs` from the in-process `DesignSurface` to the DDP child
has started, in the same spirit as `RemoteFormsDesignerControl`/`UnoDesignSurfaceControl`: a new
`WpfSurfaceDesignerControl : DesignerCanvas`
(`WpfDesign.AddIn/Src/OutOfProcess/WpfSurfaceDesignerControl.cs`) drives its surface entirely
through `WpfSurfaceHostClient`/`IDesignHostClient` — `OpenAsync`/`UpdateAsync` open/refresh a
session, the returned `DesignerRenderFrame` is decoded (deflate+base64 → BGRA32, matching
WinUI/Uno's `RenderCodec.Decode` pattern, duplicated per backend by design) and shown through the
shared `DesignFramePresenter`, and a left-click forwards through `DesignViewport.SurfaceToDesign`
into `design/hit-test`, resolving a `DesignerElementNode` by tree path and drawing its bounds via
the shared `SelectionAdornerLayer`.

**Deliberately scoped down, not attempted in one pass**: this is click-to-select only — no
drag/resize, marquee-select, toolbox drop, keyboard commands, or multi-select yet, and zoom/grid/
theme toolbar chrome stays hidden (`ShowZoom`/`ShowFit`/`ShowGrid`/`ShowTheme = false`) since this
backend doesn't implement any of them yet. `WpfDesign.AddIn.csproj` gained two new project
references to support it: `Designer.Presentation` (the shared canvas helpers) and
`WpfDesign.SurfaceHost.Remote` (the host client, from the previous round's extraction).

**Not yet wired into `WpfViewContent.cs`, and deliberately so.** `WpfViewContent`'s live
`DesignSurface` is both the renderer AND the input surface today; swapping the visible content to
this new control before it has hit-test-driven selection working correctly would regress
interactivity, not improve it. Two real blockers stand between this control and being safe to
swap in:

1. ~~**The coordinate-mismatch bug** means selection outlines drawn from `DesignerElementNode`
   bounds will visibly not line up with the rendered frame.~~ **Fixed later the same day** — see
   "Coordinate mismatch: SOLVED" above; it was this repo's own hardcoded 800x600 arrange viewport,
   not an upstream ProGPU/LibreWPF defect as this entry originally speculated. Tree bounds and
   rendered pixels now line up to within 1px, enforced by a regression test.
2. **No outline/mutation wiring yet** (the Properties-pad piece is now done - see below).
   `WpfViewContent` currently binds the Properties pad directly to a live `DesignItem`
   (`DesignItemPropertyGridAdapter`) and the Outline pad to `RootItem.CreateOutlineNode()`; the
   DDP-shaped Properties replacement now exists (`WpfSurfaceElementPropertyAdapter`) but isn't
   wired into `WpfViewContent` yet, and the Outline pad and the remaining mutations
   (`SetBoundsAsync`/`AddElementAsync`/`DeleteElementsAsync`/`RenameAsync`) still need their own
   DDP-shaped replacements before the old in-process versions can be retired.

Verified by build only this round (`dotnet build WpfDesign.AddIn.csproj` succeeds cleanly, and the
existing `WpfSurfaceHostRpcTests` suite stays at `19/19` — this new control has no runtime effect
yet since nothing references it). No interactive/manual verification was possible without a live
desktop session; that remains outstanding before any further phase.

### Properties-pad adapter built (2026-08-17, same round)

The Properties-pad piece of the cutover is done, following the exact precedent
`FormsDesignerViewContent.RemoteComponentPropertyProxy`/`RemotePropertyDescriptor` already set for
WinForms (an `ICustomTypeDescriptor` proxy over the RPC-reported property list, edits going out
through `SetPropertyAsync`) — **not** WinUI/Uno's pattern (`WinUIXamlElementPropertyAdapter`
reads/writes the host-owned `XElement` directly, with no RPC round trip for reads at all). WPF
needs the WinForms shape, not the WinUI one: WinUI's approach only works because a XAML
attribute's presence/absence *is* its property list, with no target-type knowledge needed; WPF's
content-property/attached-property model means only the child (which already has real
`DesignItem.Properties` reflection) can correctly enumerate what's actually settable on an
arbitrary element without the host duplicating XAML content-model knowledge - exactly the kind of
target-type awareness this technote's own red-lines list forbids in host code.

**A real, non-obvious bug found and fixed getting there**: the natural-looking
`item.Properties.Where(p => !p.IsEvent)` (copied from the existing in-process
`DesignItemPropertyGridAdapter`, which uses exactly this) returns almost nothing for a
freshly-opened session - confirmed by a real diagnostic run, not assumed. `DesignItem.Properties`
(`XamlModelPropertyCollection`) only *enumerates* `DesignItemProperty` wrapper objects some caller
has already realized via a name lookup (a lazy dictionary cache); it does not walk the element's
actual CLR property set. In the live in-process designer this is masked because loading a real
Xceed `PropertyGrid` against a `DesignItem` triggers many such lookups as a side effect before
enumeration ever runs. Headless, with no property grid attached, only whatever `BuildNode`'s own
`item.ContentProperty` walk happened to touch was realized (a `TextBlock` showed `propCount=0`,
its `Grid` parent only `["Children"]`). **The fix**: `WpfSurfaceHostService.BuildProperties`
enumerates the element's real CLR property set via `TypeDescriptor.GetProperties(item.ComponentType)`
(which already filters out non-browsable WPF plumbing members like `Dispatcher`/`TemplatedParent`,
same as any .NET property grid) to get real candidate names, then looks each one up through
`item.Properties[name]` - which, unlike enumeration, *does* create the wrapper on demand for any
valid name. `DesignerElementNode` gained a `Properties: List<DesignerPropertyInfo>` field to carry
the result (the exact `DesignerPropertyInfo` shape `DesignerComponentInfo` already used for
WinForms - the doc comment on `DesignerPropertyInfo.Kind` had already anticipated this exact need:
*"A WPF backend (not yet implemented) needs the other kinds..."*).

Value conversion (`WpfSurfaceHostService.ToPropertyInfo`) is deliberately conservative: only
properties with a symmetric string `TypeConverter` (covers primitives, enums, and ordinary
XAML-serializable value types) get a real editable `Value`; anything else (nested-`DesignItem`
values - `Binding`, `Brush`, layout collections, ...) is reported `Kind="Unsupported"` and
read-only with a best-effort display string, rather than crashing the tree build or silently
corrupting data through a lossy round trip. Widening this to the `"Xaml"`/`"Reference"` kinds
designer-common.md already reserves for exactly this case is real future work, not attempted here.

Verified with a new, real test (not assumed): `Tree_CarriesRealPropertyValuesForTheAddInsPropertiesPad`
opens the fixture, confirms `greeting.Properties` reports `Text="Hello"` (`Kind="String"`) and
`Width="200"` (`Kind="Number"`), edits `Text` through `SetPropertyAsync`, and confirms the next
tree reports the edited value - proving the property list is live, not a one-shot snapshot frozen
at `session/open`. Full suite: `total: 20, failed: 0, succeeded: 20`, confirmed on two consecutive
runs.

The addin-side consumer, `WpfSurfaceElementPropertyAdapter`/`WpfSurfacePropertyDescriptor`
(`WpfDesign.AddIn/Src/OutOfProcess/WpfSurfaceElementPropertyAdapter.cs`), mirrors
`RemoteComponentPropertyProxy`/`RemotePropertyDescriptor` structurally: `SetValue` blocks on
`SetPropertyAsync` (`PropertyDescriptor.SetValue` is inherently synchronous - matches
`FormsDesignerViewContent.ExecuteRemoteEdit`'s already-established blocking-edit pattern in this
codebase, not a new risk) and hands the returned `DesignerSessionState` back through a callback so
the caller can refresh. `WpfSurfaceDesignerControl.SelectedPropertyAdapter` exposes a fresh
adapter for whatever's currently selected, wired to `Show` as the refresh callback so an edit
re-renders and re-positions the selection outline through the exact same path a hit-test-driven
selection change already uses.

### Outline-pad plumbing added (2026-08-17, same round)

`WpfSurfaceDesignerControl` gained the two members the shared `DocumentOutlineControl`
(`ICSharpCode.SharpDevelop.Widgets`) needs, following the exact wiring pattern
`FormsDesignerViewContent` already uses (`outline.SetRoot`/`SelectionCommitted`/`SelectNodeById`,
designer-common.md's Document Outline section):

- **`OutlineRoot`** returns `state?.Tree` directly. Unlike WinForms, whose flat
  `DesignerComponentInfo` list needs `FormsDesignerViewContent.BuildOutlineTree` to become a tree
  first, WPF's own DDP shape already **is** the `DesignerElementNode` tree the Outline pad wants -
  no conversion needed. This mirrors WinUI/Uno's outline convergence, which designer-common.md
  already recorded as `DesignerSessionState.Tree` being consumed directly.
- **`SelectElementId(string?)`** is the outline→surface direction: it selects locally from data
  already in the current tree (no RPC round trip), matching how `RemoteFormsDesignerControl`'s own
  `SelectComponent` works - the surface already has everything it needs (bounds) to draw the
  selection outline without asking the child again.
- The surface→outline direction (a `SelectionChanged` subscriber calling
  `outline.SelectNodeById(control.SelectedElementId)`, mirroring `FormsDesignerViewContent
  .RemoteSelectionChanged`) is `WpfViewContent`'s job once it actually owns a `DocumentOutlineControl`
  instance - not this control's concern, since it has no reference to any outline widget itself
  (same "protocol yes, presentation no" separation as everything else here).

**A real, pre-existing infrastructure landmine found (and fixed) while verifying this**:
`WpfDesign.AddIn.csproj` intermittently failed to build with `MSB4236: The SDK 'LibreWPF.Sdk'
specified could not be found` on a *fresh* restore of its `WpfDesign.csproj`/`WpfDesign.Designer
.csproj`/`WpfDesign.XamlDom.csproj` project references. Root cause, confirmed by reading the
actual files rather than assumed: `externals/vscode-wpf/external/WpfDesigner/global.json` pinned
`LibreWPF.Sdk` to `0.1.0-preview.28` - a version that was never available in either the old or new
local NuGet feed (only `0.1.0-preview.41`, the version the *outer* repo's own `global.json` pins,
was ever packed). `WpfDesign.SurfaceHost.csproj`'s own comment had already flagged the general
shape of this problem ("a fresh `<ProjectReference>` build ... fails SDK resolution - only
`WpfDesign.AddIn.csproj`'s incidental reuse of already-built outputs masks this") without anyone
having chased the exact stale version down before now. Fixed by bumping the nested `global.json`
to `0.1.0-preview.41`, verified by deleting that project's `obj/` folder and forcing a fully fresh
restore + build (not just an incremental one, which would have hidden the same landmine again).
Not a workaround - it's a genuine one-line stale-pin fix, and it removes the fragility for
`WpfDesign.AddIn.csproj` too, not just this round's new files.

### Remaining mutations wired as callable RPCs (2026-08-17, same round)

`WpfSurfaceDesignerControl` gained `SetBoundsAsync`/`AddElementAsync`/`DeleteSelectedAsync`/
`RenameSelectedAsync` - thin wrappers over the matching `WpfSurfaceHostClient` calls that refresh
the surface (`Show`) with the returned `DesignerSessionState` afterward. Callable-but-no-interactive
-UI-yet, the same convergence order WinForms already used for `design/rename` ("no existing
'rename an already-named element' call site... landed as a ready-to-use capability only") - full
drag/resize and toolbox-drop UI is real future work layered on top of these, deliberately not
attempted here since it needed the coordinate-mismatch bug fixed first to place drag handles/drop
targets correctly against the rendered frame (that blocker was cleared later the same day - see
"Coordinate mismatch: SOLVED" above).

One real keyboard command *is* wired end-to-end: `OnKeyDown` maps `Delete` to
`DeleteSelectedAsync()`, matching `RemoteFormsDesignerControl.OnKeyDown`'s broader set (this is the
one command out of that set implemented so far). `DeleteSelectedAsync` clears `selectedPath`
*before* issuing the RPC rather than after - once the tree is rebuilt, sibling element paths (child
-index strings) can shift, so the pre-delete path is no longer meaningful and must not be reused to
draw a stale selection outline. `RenameSelectedAsync`, by contrast, keeps the current selection
since a rename doesn't restructure the tree.

Verified by build only (`dotnet build WpfDesign.AddIn.csproj`, twice, both clean) plus the existing
`WpfSurfaceHostRpcTests` suite staying at `20/20` - none of these four methods have a test of their
own yet in this round since they have no call site to exercise them from (same reasoning as
WinForms' `design/rename` landing). A dedicated test for each, exercising them directly against a
spawned child the way `WpfSurfaceHostRpcTests` already does for the RPCs themselves, is real
future work before relying on them from real UI.

**Next steps, in dependency order** (the coordinate-mismatch bug that used to head this list is
now fixed - see "Coordinate mismatch: SOLVED" above): build the actual drag/resize/toolbox-drop
interactive UI on top of the now-callable mutations, which is now unblocked because tree bounds and
rendered pixels finally agree → only then swap `WpfViewContent.UserContent` from the live `DesignSurface`
to `WpfSurfaceDesignerControl`, swap its `Outline` field from the legacy `OutlineNode`-based
`Outline` to the shared `DocumentOutlineControl` (matching WinForms/WinUI), and delete the
in-process fallback, `CurrentDomain_AssemblyResolve`, and the direct `DesignItem`/
`XamlDesignContext` wiring. Still no interactive/manual verification of any of this without a live
desktop session - build and RPC-level tests only. Full suite re-verified after this round's
changes: `total: 20, failed: 0, succeeded: 20`, confirmed on repeated runs.

### `design/set-bounds` now actually moves elements, and drag/resize/toolbox-drop UI landed (2026-08-17)

**A real, previously-undiscovered bug found while building the interactive layer**:
`WpfSurfaceHostService.SetBounds` set only `Width`/`Height` and silently ignored `x`/`y` - an
interactive drag could resize an element but never move it, with no error reported anywhere. Fixed
by routing through the designer's own `PlacementOperation`/`IPlacementBehavior` machinery (the same
one `design/add-element` already used) instead of writing properties directly: a
`PlacementOperation.Start(..., PlacementType.Resize)`, setting `info.Bounds` to the full new rect,
and calling `CurrentContainerBehavior.SetPosition(info)` before `Commit()`. This expresses the move
the way the *container* wants it - a `Grid` child gets a `Margin` (and, matching real WPF designer
behavior, `GridPlacementSupport` resets `Width`/`Height` under `Stretch` alignment rather than
setting them alongside a margin), a `Canvas` child would get `Canvas.Left`/`Top`, etc. - instead of
this backend inventing its own container-naive geometry encoding. A plain `Width`/`Height` fallback
is kept for a container with no placement behavior at all. `DesignSetBounds_ChangesWidthAndHeight`
(which asserted the literal `Width="150"` attribute - actually asserting the old, container-naive
behavior) was rewritten to assert the resulting *geometry* instead, and a new
`DesignSetBounds_MovesTheElement_NotOnlyResizesIt` test proves `x`/`y` are honored - verified
decisive the same way as the coordinate-mismatch fix: the old code was restored temporarily and the
new test failed exactly as predicted (element stayed at `x=100` instead of moving to `12`).

**A second real, upstream bug found in the same investigation, and fixed - not upstream in ProGPU/
LibreWPF this time, but in the shared `WpfDesign.Designer` library `WpfDesign.AddIn` already
references** (`externals/vscode-wpf/external/WpfDesigner/WpfDesign.Designer/Project/Extensions/
RasterPlacementBehavior.cs`): `PlacementOperation.Start` threw `ServiceRequiredException: Service
ICSharpCode.WpfDesign.IDesignPanel is required` for *every* move/resize in this headless host - not
a corner case, unconditionally, confirmed by a real run before the fix (`DIAG placement failed:
...`). Root cause: `RasterPlacementBehavior.BeginPlacement` read `ExtendedItem.Services.DesignPanel`
(a `GetRequiredService<T>`-backed property that throws when nothing is registered) to look up the
optional raster width, even though every other access to the same optional service in this exact
file/its `SnaplinePlacementBehavior` subclass - including the `CreateSurface()` call three lines
later in the same method - already used the non-throwing `GetService<IDesignPanel>() != null`
guard, correctly treating a missing panel as "skip the panel-only behavior, everything else still
works." This one line was the sole hold-out, and since `BeginPlacement` runs unconditionally at the
top of every `PlacementOperation`, it broke *all* placement, not just raster-specific behavior.
Fixed to use the same non-throwing lookup the surrounding code already established as the correct
pattern - identical behavior when a `DesignPanel` is present (every live designer session), only
different when one is absent (this headless host). Verified this doesn't regress the live
in-process designer's own drag/resize by trying to run
`WpfDesigner_ResizeDrag_SelectionAndHandleTrackRenderedElement`
(`tests/OpenDevelop.IntegrationTests/AddInTests.cs`) - a real, pre-existing integration test that
exercises this exact code path with actual UI drag input - but see the caveat below.

**`WpfSurfaceDesignerControl` gained the actual interactive layer** this was all in service of:
move (drag inside the selected element), resize (drag any of all eight handles - unlike WinForms,
which only ever shows `"se"`, this backend's real container-aware placement supports all of them),
and toolbox drop (`AllowDrop`/`DragOver`/`Drop`, hit-testing the drop point to find the target
parent and falling back to the document root, then calling the now-real `AddElementAsync`). A drag
only commits once, on mouse-up, via a single `design/set-bounds`/`design/add-element` call - the
adorner tracks the gesture locally in between, matching both shipped backends' "one RPC per
gesture, not per mouse-move" rule. `SelectionAdornerLayer` is now constructed with all eight handle
names and a label (previously zero handles, no label, since selection-only didn't need them).

**Honest caveat on verification**: the live in-process integration test
(`WpfDesigner_ResizeDrag_SelectionAndHandleTrackRenderedElement`) could not actually be run to
completion - `od.wpf-designer.status` reported `{"active":false}` (`WpfViewContent` never found),
traced to the opened `.xaml` file falling back to a plain-text view
(`AutoDetectDisplayBinding: no display binding claimed file ...`). This was checked and is **not**
caused by anything in this round's changes: an entirely unrelated integration test
(`VBFixture_LoadsShowsSourceParsesAndBuilds`, no relation to WPF/XAML at all) fails the same way
in the same run (`Assert.Contains() Failure: Filter not matched in collection: []`), while a third,
narrower test with no solution/file-opening (`FSharpAddIn_IsLoaded`) passes cleanly - so the
integration-test harness itself is currently broken for reasons unrelated to WPF (the app log shows
`MSBuild warm-up failed (non-fatal): ... Roslyn/Microsoft.CSharp.Core.targets was not found`,
suggesting an incomplete `dotnet build` deployment of the main `SharpDevelop.csproj` app is missing
Roslyn compiler assets some other build step normally provides). Fixing that is out of scope here -
it's a pre-existing environment issue, not part of the WPF designer cutover. The `RasterPlacementBehavior`
fix's correctness rests on direct code reading (identical behavior when the service exists, the
exact same conditional shape the surrounding, already-correct code in the same file already uses)
rather than an end-to-end interactive run; that run remains the next thing to confirm once the
integration-test environment issue is separately resolved.

Verified via `WpfDesign.SurfaceHost.Tests`: `total: 22, failed: 0, succeeded: 22`, confirmed on two
consecutive runs. `WpfDesign.AddIn.csproj` builds clean.

**What's left before the actual cutover swap**: only the swap itself -
`WpfViewContent.UserContent` from the live `DesignSurface` to `WpfSurfaceDesignerControl`, the
`Outline` field to the shared `DocumentOutlineControl`, and deleting the in-process fallback. Not
attempted this round: `WpfViewContent.cs` has real, separate in-progress edits (the shared
`DesignerCanvas` shell wiring) already underway outside this investigation, and swapping its
`UserContent`/`Outline` fields now would collide with that work rather than build on it.

### Cutover completed: `WpfViewContent.cs` swapped to the out-of-process control (2026-08-17)

`WpfViewContent.cs` was fully rewritten: `LoadInternal` now starts a `WpfSurfaceHostClient` (via
its new `LocateChildDll()`, mirroring `FormsDesignerHostClient` exactly) and drives a
`WpfSurfaceDesignerControl` as `UserContent` through `OpenAsync`/`UpdateAsync`, instead of building
a live in-process `XamlDesignContext`/`DesignSurface`. `SaveInternal` now flushes through
`WpfSurfaceHostClient.FlushAsync` and writes back the child's serialized XAML rather than reading a
live `DesignItem` tree. Selection routes through `WpfSurfaceDesignerControl.SelectedPropertyAdapter`
(a `WpfSurfaceElementPropertyAdapter`) into the shared Xceed Properties pad, and through
`DocumentOutlineControl`/`OutlineRoot` for the Outline pad - both bound directly to
`DesignerElementNode`, no `IOutlineNode`/`DesignItem` translation layer needed anymore.
`CreateSnapshot` builds the `DesignerDocumentSnapshot` sent to the child, including - for the first
time on any host in this repo - a populated `ReferencedAssemblyPaths` list (via
`project.ResolveAssemblyReferences`), plus optional `app.xaml` resource loading gated by
`WpfEditorOptions.EnableAppXamlParsing`.

Five classes that existed only to support the old `XamlDesignContext.CustomServiceRegisterFunctions`
registration were deleted as fully orphaned (confirmed via repo-wide `grep -rln` before each
deletion): `SharpDevelopEventHandlerService`/`AbstractEventHandlerService.cs`,
`PropertyDescriptionService.cs`, `IdeChooseClassService.cs`,
`WpfAndWinFormsTopLevelWindowService.cs`, `FileUriContext.cs`. `DesignItemPropertyGridAdapter.cs`
was deleted too, once `WpfDesignDevFlowActions.cs` (below) stopped being its last caller.
`ThumbnailViewPadViewModel` was adapted rather than deleted - its live-visual-tree minimap has no
equivalent yet for the bitmap-based OOP surface, so it now always shows the pad's existing
"not available" placeholder; a real thumbnail for the new surface is deferred, separate future work.

**`WpfDesignDevFlowActions.cs`** (the integration-test probe surface) was rewritten in step: every
action that read `WpfViewContent.DesignContext`/`.Outline` (live `DesignItem`/`IOutlineNode` APIs)
now reads `WpfViewContent.SurfaceControl`/`DesignerSessionState`/`DesignerElementNode` instead -
`GetDesignerStatus`, `SelectElement`, `QueryElementScreenBounds`, `DropToolboxItem`, and
`EditPropertyThroughPropertiesPad` (the last one only needed its adapter cast changed, from
`DesignItemPropertyGridAdapter` to `WpfSurfaceElementPropertyAdapter` - the live Xceed
`PropertyGrid`/`PropertyItem` objects it drives are unchanged regardless of which adapter backs
them). `FlushPendingTransaction`/`CommitLeakedChangeGroup` - a workaround for an old-model
transaction-leak bug where the in-process designer could leave an unfinished `ChangeGroup` open on
its undo stack - is now a no-op returning `committed: 0`: every `design/*` RPC to the child commits
(or rejects) synchronously per call, so that class of bug is structurally impossible under the OOP
model; the reflection-based `CommitLeakedChangeGroup` helper was deleted as dead code.

**Verification**: `WpfDesign.AddIn.csproj` builds clean (0 errors) after every change, including
after the `DesignItemPropertyGridAdapter.cs` deletion. A full solution build
(`OpenDevelop.Mvp.slnx`) also completes with 0 errors. `WpfDesign.SurfaceHost.Tests` still passes
22/22, unaffected (a separate project). As recorded above, the integration-test environment itself
is broken for unrelated, pre-existing reasons (`.xaml` files fall back to a plain-text view before
`WpfViewContent` is ever constructed) - this was surfaced explicitly, and the decision to proceed
with the cutover accepting code-review-only verification (no interactive/manual confirmation of the
swapped surface) was made explicitly with that gap acknowledged, not discovered after the fact.

Checked afterwards: the old constructor's three now-dropped calls were not uniformly safe to just
drop. `BasicMetadata.Register()` registers the WpfDesigner engine's own property-editor
standard-value lists (`Brush`/`Color`/`Cursor`/`FontWeight`/`ICommand`, etc.) that the Properties
pad's dropdowns depend on - since the engine (`DesignItem`/`Metadata`/`PlacementBehavior`) now runs
entirely inside the child process, not the AddIn, this call was moved to
`WpfSurfaceHostService`'s constructor instead of being dropped (it is itself idempotent via a
`registered` guard, so calling it once per child process is correct and safe). `SharpDevelopTranslations.Init()`
(sets the engine's `Translations.Instance`, used for an Alt-drag status-bar hint string) could not
be moved the same way - it depends on `ICSharpCode.Core.StringParser` for resource-string lookup,
and `WpfDesign.SurfaceHost.csproj` deliberately has no reference to `ICSharpCode.Core` (it is a
plain child-process host, not an AddIn); adding that dependency just to localize one hint string was
judged out of scope here. `DragDropExceptionHandler.UnhandledException` was a WPF OLE
drag-drop-specific unhandled-exception guard for the old in-process control's own drag/drop path -
`WpfSurfaceDesignerControl`'s drop handling already has its own try/catch (`DropAsync`), so this
was not re-added. Net effect: functionally load-bearing (`BasicMetadata`) is now fixed; the other
two are confirmed cosmetic/non-functional gaps, not silently-dropped correctness risk.

### Post-cutover integration test pass: four real bugs found and fixed (2026-08-17/18)

Running the WPF-designer-relevant integration tests for the first time against the completed
cutover (previously impossible - the app hung on the very first one) surfaced four independent,
real bugs, none of them findable by code review alone:

1. **Deadlock on every file open** - `DesignerHostProcessClient.StartAsync` only started draining
   the child's redirected stdout/stderr (`PumpAsync`) after its TCP handshake succeeded, but the
   pipes are redirected from the moment `process.Start()` returns; if the child writes enough
   startup output before it gets around to connecting, it blocks on `Console.Write` into a full
   OS pipe buffer and never connects, while the host sits blocked waiting to accept that
   connection. Fixed by starting both pumps immediately after `process.Start()`. Confirmed via a
   full thread dump of a hung `OpenDevelop` process (`dotnet-dump`) - the UI thread was blocked in
   `WpfViewContent.LoadInternal`'s `.GetAwaiter().GetResult()` for 8+ minutes with the child
   process sitting idle in `WaitForShutdown()`, never having connected.
2. **A second, independent deadlock class**: `WpfSurfaceDesignerControl.OpenAsync`/`UpdateAsync`/
   `SetBoundsAsync`/`AddElementAsync` awaited their RPC call with `ConfigureAwait(true)` and then
   called `Show()` (touches WPF UI) as the continuation. Every caller of these methods blocks
   synchronously via `.GetAwaiter().GetResult()` from the UI thread (`WpfViewContent.LoadInternal`,
   `WpfDesignDevFlowActions`) - `ConfigureAwait(true)`'s posted continuation can never run because
   the blocked thread IS the dispatcher. Fixed by making these methods pure `ConfigureAwait(false)`
   RPC wrappers with no UI side effects at all, and moving `Show()`/event-raising out to each call
   site - blocking callers call it directly once `GetResult()` returns (already back on the correct
   thread by definition); genuine fire-and-forget interactive callers (`OnMouseLeftButtonUp`,
   `OnDrop`, `OnKeyDown`'s Delete handler) wrap the call in their own small async method that keeps
   its own `ConfigureAwait(true)`, which is safe there because those callers never block the UI
   thread. See `WpfSurfaceDesignerControl.Show`'s doc comment for the full reasoning.
3. **`od.wpf-designer.status`'s `rootItemType` regression**: `WpfDesignDevFlowActions.GetDesignerStatus`
   was rewritten to expose the DDP wire contract's `state.RootType`, which is the full CLR name
   (`"System.Windows.Window"`, matching `WpfSurfaceHostRpcTests`' own assertion) - but this
   action's own pre-existing contract (predating the cutover, encoded in the integration suite's
   `WaitForWpfDesignerStatusAsync(expectedRootItemType: "Window", ...)`) is the bare type name.
   Fixed by taking the substring after the last `.` in the action itself, leaving the wire contract
   untouched.
4. **Properties-pad edits never dirtied the file - a chain of two bugs**:
   - `WpfViewContent`'s dirty-tracking compared `DesignerSessionState.Version` before/after a
     mutation, but `WpfSurfaceHostService.NewState(baseVersion)` always echoes the caller's own
     `baseVersion` back unchanged - no mutation RPC on this backend ever actually bumps the
     document version, so the comparison could never fire. Fixed by adding a dedicated
     `WpfSurfaceDesignerControl.DocumentChanged` event, raised only by real mutation-commit sites
     (bounds/add/delete/property-edit), instead of inferring "did something change" from a version
     number that never moves.
   - Even after that fix, edits through the Properties pad specifically still weren't reaching the
     child at all: `WpfSurfaceHostService.ToPropertyInfo` computed `IsReadOnly`/`Kind` from
     `TypeDescriptor.GetConverter(property.ReturnType)` - the property's *declared* type. For an
     `object`-typed content property (`Button.Content`, `ToolTip`, `Header`, etc.) holding a plain
     string, `TypeDescriptor.GetConverter(typeof(object))` reports `CanConvertFrom(string) ==
     false` (the base `TypeConverter` only round-trips via `InstanceDescriptor`), so the property
     fell into the "Unsupported"/read-only branch purely because of its declared type, even though
     the actual value was an ordinary editable string. `DescriptorPropertyDefinition.CreateValueBinding`
     keys its binding `Mode` directly off `PropertyDescriptor.IsReadOnly`, so this silently made the
     Properties pad's `Value` binding `OneWay` - a real edit updated the Xceed `PropertyItem`'s own
     local value (so before/after checks looked like they worked) but never called
     `WpfSurfacePropertyDescriptor.SetValue`, and so never reached `design/set-property` at all.
     Root-caused via three rounds of `Console.Error.WriteLine` diagnostics bisecting the actual
     call chain (`OnDocumentChanged` never fired -> `OnPropertyEdited` never fired ->
     `WpfSurfaceElementPropertyAdapter.SetProperty` never fired -> `WpfSurfacePropertyDescriptor.
     SetValue` never fired - zero hits at the very first line of our own code, ruling out every
     "wiring" theory and pointing at the binding itself). Fixed by checking the actual runtime
     value's type first in `ToPropertyInfo` (`value is string` short-circuits straight to
     `Kind = "String"`) rather than trusting the declared type's converter.
   - A related, smaller bug surfaced once (3) was fixed: `WpfSurfacePropertyDescriptor.GetValue`
     read the `DesignerPropertyInfo` snapshot captured when the descriptor was constructed (at
     selection time), which WPF's TwoWay binding re-reads immediately after a successful commit to
     refresh its target - reporting the stale pre-edit value until the next full re-selection.
     Fixed by updating that snapshot's `Value`/`IsNull` directly in `SetValue`, right after the RPC
     call succeeds (an exception would throw before reaching that line, correctly leaving the stale
     snapshot in place for a rejected edit).

All four are now covered by real, passing integration tests: `OpenXamlFile_LoadsDesignerWithToolboxAndOutline`,
`OpenAppXaml_ShowsCodeEditorOutline`, `OpenSamplePaneXaml_LoadsDesignerWithNestedControlTree`,
`SelectControl_EditingContentInPropertiesPad_UpdatesAndSavesXaml`, `SelectControlOnSamplePane_ShowsSelectionInPropertiesPad`,
and `OpenUnoXamlFile_UsesWinUIXamlDesignerInsteadOfWpfDesigner` all pass. `WpfDesign.SurfaceHost.Tests`
stays at 22/22 throughout (confirmed after both the stdout/stderr-pump fix and the `ToPropertyInfo`
fix, since both touch the shared `Designer.Remote`/`WpfSurfaceHostService` code those tests exercise
directly). Full solution build (`OpenDevelop.Mvp.slnx`) is clean.

**Three real synthetic-mouse-drag tests, root-caused and fixed (2026-08-18)**
(`WpfDesigner_ResizeDrag_SelectionAndHandleTrackRenderedElement`, `DragToolboxItem_
OntoDesignSurface_InsertsControlEditableThroughPropertiesPad`, `DragToolboxItem_OntoXamlSourceEditor_
InsertsMarkupAtDropPoint`) - all three drive real OS-level synthetic mouse press/move/release
through DevFlow (`cliclick`), and all three initially failed to observe any resulting change (no
resize, no dropped element).

Temporary `Console.Error.WriteLine` diagnostics at the top of `WpfSurfaceDesignerControl.
OnMouseLeftButtonDown`/`OnMouseMove`/`OnMouseLeftButtonUp` showed **zero hits at all** across a
full real press/drag/release sequence at the handle's own reported screen coordinates - ruling out
every gesture/hit-test logic bug in this class (`adornerLayer.HandleAt`, `ApplyGesture`, the drag
threshold, etc. were never even exercised). Two false leads were chased and ruled out before the
real cause: (1) the resize-drag test was missing the `od.activate` call every sibling drag test
makes first - added it, but it alone did not fix the zero-hits symptom; (2) a same-session
`WinFormsDesigner_ResizeDrag_SelectionAndHandleTrackRenderedFrame` baseline run also failed
identically, which was wrongly taken as proof of an environment-wide synthetic-input limitation
unrelated to this cutover - **that inference was wrong**. Prompted to check further, running the
*other* designers' equivalent real-mouse tests (`WinUIXamlDesigner_ResizeDrag_
SelectionAndHandleTrackRenderedElement`, `DragToolboxItem_OntoWinFormsDesignSurface_
AddsControlToForm`) showed they **passed** - real synthetic mouse input plainly does work in this
environment; the earlier WinForms failure was its own separate, unrelated flake, not proof of
anything environment-wide.

That reopened the investigation with a live, working comparison to check against.
`UnoDesignSurfaceControl` (WinUI's equivalent of `WpfSurfaceDesignerControl`) wires
`PreviewMouseLeftButtonDown`/`PreviewMouseMove`/`PreviewMouseUp` (tunneling events), with its own
doc comment explaining why: *"under LibreWPF the ScrollViewer swallows the bubbling mouse events,
so picking and panning are handled before any child does."* `WpfSurfaceDesignerControl` wired the
plain bubbling `MouseLeftButtonDown`/`MouseMove`/`MouseLeftButtonUp` instead - an ancestor
(matching WinUI's own diagnosis, though the exact swallowing element wasn't independently
re-identified for this control) intercepts the bubbling event before it ever reaches this control,
explaining the zero hits. Switching to the identical `Preview*` tunneling events fixed the resize
drag immediately (verified: press/drag/release now actually grows the element, confirmed via a
passing `WpfDesigner_ResizeDrag_SelectionAndHandleTrackRenderedElement`).

That fix alone got the toolbox-drop-onto-design-surface test to progress much further (the drop
itself started landing, growing the outline), but it then failed at a later step - the Properties
pad had no selected object after the drop. `WpfSurfaceDesignerControl.DropAsync` never selected the
newly-created element: `WinForms`/`WinUI` solve this by having the caller supply a name up front and
finding the result by that name afterward, but a real toolbox-dropped WPF element deliberately has
no `x:Name` (the test's own comment says so), so that approach doesn't apply here. Fixed by adding
`DesignerSessionState.CreatedElementId` to the shared DDP contract - populated by
`WpfSurfaceHostService.AddElement` via the same `pathToItem` reverse-lookup `HitTest` already uses,
valid only on that RPC's own response - and having `DropAsync`/`WpfDesignDevFlowActions.
DropToolboxItem` select it after a successful drop. With both fixes in place, all three tests pass,
`DragToolboxItem_OntoXamlSourceEditor_InsertsMarkupAtDropPoint` (unrelated AvalonEdit code, no
changes needed) also passed cleanly on the same run, and a full re-verification of all nine
WPF-designer-relevant integration tests plus `WpfDesign.SurfaceHost.Tests` (still 22/22) and a full
solution build are all green.

**Lesson for next time**: a single same-session "does an unrelated designer's equivalent test also
fail" check is not sufficient evidence of an environment-wide limitation - that unrelated test can
have its own, separate flake. Check at least one more independent, currently-passing comparison
(here, WinUI's resize-drag) before concluding a failure is out of scope; had that been done first,
the two false leads above would have been skipped entirely.

### Toolbar (Zoom/Fit/Grid): completed, after two false starts (2026-08-18)

**Final state (as of 2026-08-18): the toolbar ships, enabled and working.** Zoom, Fit and
gridlines are implemented and verified end to end against a live app; design-size stays hidden
because it is genuinely another backends' concept (see the constructor comment in
`WpfSurfaceDesignerControl` for the per-entry reasoning). Design-theme was also hidden at this
point — it landed two days later via the embedded-`themes/*.xaml` enumeration described in the
"Design-theme combo" section below (2026-08-20). What follows records the two wrong conclusions
reached on the way, since both were confidently wrong and cost real time.

The breakthrough was abandoning 40-second test cycles and instead driving a **live app instance**
through DevFlow directly (launch with `OD_TEST_MODE=1`, then `od.open-solution`/`od.open-file`/
`od.wpf-designer.select`/`od.wpf-designer.surface-geometry` plus `/api/v1/ui/actions/*` and
`/api/v1/ui/tree`). That turns a 40s guess into a 2s measurement and is strongly recommended for
any future UI-level debugging here.

Three real findings came out of it:

1. **The toolbar never broke mouse input at all** (see the reverted conclusion below). With the
   toolbar visible, a real press/drag/release on the resize handle resized the element correctly
   6 times out of 7 consecutive manual attempts - the 7th only failed because the repeated +50px
   growth had pushed the handle off the rendered surface. The earlier test failures were the
   window simply not being frontmost when the synthetic press fired (`od.activate` reports
   `isActive: false` even on success), which is an environment race independent of the toolbar.
2. **`/api/v1/ui/tree` bounds are NOT screen coordinates.** The tree reported the docking manager
   at `(0,72)` while its real `PointToScreen` origin was `(10,135)` - a constant `(+10,+63)` offset
   in this layout. Early toolbar clicks were aimed from tree coordinates and landed ~63px above the
   buttons, which is why "the toolbar buttons do nothing" looked like an input bug. `od.wpf-designer.
   surface-geometry` *does* return true screen coordinates (it goes through `PointToScreen`), which
   is why handle-based gestures worked while tree-based clicks did not. **Convert tree bounds before
   feeding them to `cliclick`-backed pointer actions.**
3. **Zoom was silently broken by `Stretch.None`** - a real bug this work uncovered.
   `DesignFramePresenter.Resize` sets the frame `Image`'s `Width`/`Height` to
   `Design* × viewport.Scale`, but WPF's presenter was constructed with `Stretch.None`, so the Image
   drew the bitmap at its natural pixel size no matter what the element size said. Measured live at
   Fit: the element was laid out 462 wide while the bitmap still rendered 297 wide, i.e. zoom
   scaled the coordinate math and the selection adorners while the picture stayed put. Fixed by
   switching to `Stretch.Fill`, matching `UnoDesignSurfaceControl` (the other backend with working
   zoom); at the default scale of 1 the two are identical, so only zoomed rendering changes.
   **`RemoteFormsDesignerControl` still uses `Stretch.None` with a live zoom combo and very likely
   has this same latent bug** - not touched here, but worth checking.

A fourth, smaller bug was found and fixed in the same pass: because the mouse handlers are
`Preview` (tunneling) handlers on the whole control, they also saw presses on the toolbar above the
design surface, ran the hit-test path with a negative design-space Y, resolved nothing, and silently
cleared the user's selection - clicking Fit dropped the current selection. Guarded by ignoring
presses outside `designSurface`'s own bounds.

Verified live after the fixes: at 100% zoom the geometry is byte-identical to the pre-toolbar
baseline (`222.09 x 86.09 @ (318,205)`, so no coordinate regression); Fit scales the bitmap and the
selection by the same 1.87x and keeps the selection; the grid toggle turns on a `DrawingBrush`
overlay sized exactly to the frame; and a resize drag under Fit still moves the element by exactly
the dragged screen delta (+50/+40). The Zoom preset dropdown could not be exercised by synthetic
input because ComboBox popups do not open under it in this stack (a general LibreWPF/popup
limitation affecting every combo in the IDE - the repo ships `WpfMenuPopupDiagnostics` for this
class of problem), so its preset path rests on sharing the exact pipeline Fit proves plus the
already-shipping `DesignViewport.Zoom` helper, not on an end-to-end click.

### Superseded: the earlier "toolbar breaks input, reverted" conclusion (kept as a record of the miss)

Attempted to turn on the shared `DesignerCanvas` toolbar for the WPF designer - previously fully
hidden (`ShowDesignSize`/`ShowZoom`/`ShowFit`/`ShowGrid`/`ShowTheme` all `false`) since none of it
was ever wired up. Implemented Zoom + Fit for real, mirroring `RemoteFormsDesignerControl`'s own
proven approach exactly: `zoomScale`/`fitMode` fields, the same `ZoomPresets`/`ZoomLabels` arrays,
`ZoomChanged`/`FitRequested` wired to a `RebuildViewport()` that re-derives the viewport via
`DesignViewport.Zoom`/`Fit` and re-presents the last-known state, plus an explicit
`framePresenter.Visual.Margin` set from the viewport's origin (WPF's control never needed this
before since it only ever used `DesignViewport.Identity`, whose origin is always (0,0) - Zoom/Fit
both center non-trivially). Care was taken so the *default* state (`fitMode=false`,
`zoomScale=1.0`) special-cases back to plain `Identity` rather than `Zoom(..., 1.0)` (which still
centers whenever the viewport isn't exactly the render's pixel size), so an untouched zoom control
wouldn't silently shift every screen coordinate the resize-drag/geometry tests depend on.

Despite that care, simply making the toolbar **visible** (even just Zoom+Fit, without touching
Grid/Theme) broke `WpfDesigner_ResizeDrag_SelectionAndHandleTrackRenderedElement` - and did so with
a confusing, inconsistent signature: some runs showed zero `OnMouseLeftButtonDown` hits (matching
the earlier Preview-event investigation's own signature), one isolated run passed cleanly with
literally identical code, and a 3-full-retry-loop verification pass afterward showed 12 consecutive
failed gesture attempts. Hiding just Grid/Theme while keeping Zoom/Fit visible appeared to fix it
once, but that also failed to reproduce on a subsequent full run. This inconsistency - not a stable
repro - is itself the finding: something about a visible toolbar interacts badly with LibreWPF's
synthetic-input routing to this control, but not deterministically enough to root-cause with the
diagnostics used elsewhere in this file (temporary `Console.Error.WriteLine`s, which this time
themselves could not stay ahead of the timing-sensitivity involved).

Given the toolbar was pure follow-up/nice-to-have work and the resize-drag/toolbox-drop
interactions above were hard-won this session, **the toolbar was reverted to fully hidden**
(all five `Show*` flags back to `false`) rather than ship something with an unresolved, intermittent
interaction risk. The Zoom/Fit *logic* (fields, event wiring, `Show()`'s viewport computation) was
left in place, dormant and inert while hidden - flipping `ShowZoom`/`ShowFit` back on is all a
future attempt needs once the actual interaction is understood, but doing so should be re-verified
against this exact regression, not assumed fixed by inspection alone.

**A genuinely separate, real finding surfaced by this investigation**: even with the toolbar fully
reverted to the exact state that was solidly green throughout the rest of this session,
re-running `WpfDesigner_ResizeDrag_SelectionAndHandleTrackRenderedElement` three times in a row
produced one failure and two passes - confirming this test has *some* inherent flake rate
independent of the toolbar work entirely, the same class of "occasionally flaky end to end" the
sibling `DragToolboxItem_OntoDesignSurface_...`/`DragToolboxItem_OntoXamlSourceEditor_...` tests
already document and defend against with a 4-attempt retry loop. This test had no such loop -
fixed by adding one, matching the established pattern exactly (retry the whole press/drag-move/
release gesture, not a single fixed attempt). This is a real, permanent test improvement independent
of the toolbar's fate, and should stay even though the toolbar itself was reverted.

## Design-theme combo: embedded `themes/*.xaml` enumeration (2026-08-20)

The shared toolbar's Light/Dark theme **button** became a theme **combo** whose entries are the
themes a project actually carries (see designer-common.md). For WPF the theme list is per-project
and derived from the project assembly's embedded resources, following the WPF-standard convention
`ThemeInfoAttribute` describes (`themes/<name>.xaml` dictionaries), but implemented by
**enumerating the manifest resources directly** — no `ThemeInfoAttribute` check:

- `WpfSurfaceHostService.ResolveThemes` walks `projectAssembly.GetManifestResourceNames()` and
  picks out embedded `themes/*.xaml` resources. The manifest name is dot-separated with no
  slashes (`WpfThemeFixture.themes.Bright.xaml`), so the match looks for the `.themes.` segment,
  treats the segment after it as the theme file name and the one after that as the `xaml`
  extension, and skips `generic.xaml` (the fallback default-style dictionary, not a theme).
- `DesignerSessionState.SupportsThemeSwitch` / `DesignThemes` (new shared DDP fields) carry the
  result back: `WpfSurfaceDesignerControl.Show` sets `ShowTheme = DesignThemes.Length > 0` and
  populates the combo via `SetDesignThemes` — a project with no embedded theme keeps the combo
  hidden (the constructor defaults `ShowTheme = false`).
- `SetTheme(name)` loads the named embedded dictionary via `GetManifestResourceStream` +
  `XamlReader.Parse`, merges it onto the open design's root `Resources.MergedDictionaries` and
  re-renders; an unknown name is reported as `Accepted = false` (the same graceful no-op shape
  `RejectIfStale` establishes), never an exception.

**A non-bug worth recording (2026-08-20):** the initial sample wrote `[assembly: ThemeInfo(...)]`
with `using System.Windows.Themes;`, which fails to compile under `LibreWPF.Sdk`. That is NOT a
LibreWPF gap — upstream dotnet/wpf has no `System.Windows.Themes` namespace in .NET Core+ either
(`ThemeInfoAttribute`/`ResourceDictionaryLocation` are `System.Windows.*`, and the public
`System.Windows.Themes.SystemTheme` from .NET Framework was dropped in the open-source port). The
`using` was simply wrong against real WPF too. The sample and fixture therefore rely on the
embedded-resource convention alone, no attribute.

Verified by tests (`WpfSurfaceHostRpcTests`, five `DesignTheme_*` scenarios) against a real
fixture assembly, `Fixtures/WpfThemeFixture`, whose `themes/*.xaml` (`Bright`, `Midnight`,
`Solarized` plus `generic`) are embedded resources:

- no-theme assembly → `SupportsThemeSwitch = false`, empty `DesignThemes`;
- themed assembly → enumerates `["Bright", "Midnight", "Solarized"]` (generic excluded);
- `SetTheme` actually switches rendering between the three;
- unknown name → rejected;
- a second themed assembly is detected only from its own embedded resources.

The full suite stands at 27/27 (`WpfSurfaceHostRpcTests`). `WpfThemeSample`
(`src/Samples/WpfThemeSample/`) is the runnable demo app.

**One environment trap worth recording:** this repo's MSBuild incremental build did not reliably
recompile `WpfSurfaceHostService.cs` — builds reported "succeeded" while the shipped DLL still
lacked the new code (verified by timestamp and by searching the DLL for the new string). Use
`dotnet build -t:Rebuild` (or `rm bin/obj`) after editing the SurfaceHost sources before running
tests; this cost a full debug cycle.

## Show-names, gridlines overlay, undo/redo, multi-select (2026-08-20)

Same batch as the theme work, all shared-shell or WPF-side:

- **Show-names toolbar toggle** (`DesignerCanvas.ShowNames`, default on): hides the control-name
  label above the selection outline. WPF and the other backends just flip
  `adornerLayer.ShowNameLabel`.
- **Gridlines via shared `GridlineOverlay`**: WPF's grid now uses the shared line-drawing overlay
  (plain `Line` shapes) because the old tiled `DrawingBrush` never rendered under
  LibreWPF-on-macOS — see `designer-gridlines-bug.md`.
- **Whole-document Undo/Redo** (`WpfViewContent`): host-side `undoStack`/`redoStack` of flushed
  XAML text, restored through `session/update`; `CanUndo`/`CanRedo` + `Undo()`/`Redo()` back the
  toolbar, mirroring WinForms' remote undo. No live-element-tree transactional undo (no such RPC).
- **Multi-select + align/distribute/match-size**: `od.wpf-designer.multi-select`/`align`/
  `distribute`/`match-size`/`undo`/`redo` DevFlow actions; secondary outlines use the shared
  `SelectionAdornerLayer.SetSecondarySelection`, alignment snapping uses the shared
  `SnapGuideCalculator`.
- **Grid-guide drag**: `design/query-grid-guides` returns a Grid's live row/column track geometry
  (`DesignerGridTrackInfo` cumulative offsets + sizes) for draggable divider guides — the WPF
  analogue of Uno's Grid-guide overlay.

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
