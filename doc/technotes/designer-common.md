# Common Designer Out-of-Process Protocol (DDP)

This technote is the home for the unified, runtime-neutral architecture that the five OpenDevelop
visual designers — WinForms, WPF, WinUI/Uno, MewUI, and GTK 4 — converge on. It covers three things:

1. **The architecture**: what an out-of-process (OOP) visual designer is, why OpenDevelop
   runs the real runtime objects in a separate child process, and the wire contract (DDP)
   that every backend speaks to its child.
2. **The five implementations**: how the three runtime-rendering designers and two source-model
   designers realize that
   architecture today — their processes, files, capabilities, and known limits.
3. **A feature matrix**: which designer feature exists in which framework, to what degree,
   and what technical constraint explains the difference.

Per-runtime details (engine internals, packaging, runtime selection, deeper known gaps) stay
in the dedicated technotes:

- [`winforms-designer.md`](winforms-designer.md) — the most complete OOP implementation;
- [`wpf-designer.md`](wpf-designer.md) — the WPF designer, cut over to OOP on 2026-08-17/18;
- [`winui-designer.md`](winui-designer.md) — the WinUI/Uno out-of-process host, the retired
  ProGPU in-process profile, and the native WinUI (Windows App SDK) planned adapter;
- [`mewui-designer.md`](mewui-designer.md) — the C#-first/Roslyn-backed MewUI designer,
  generated `.Designer.cs` convention, source transformations and safe preview projection;
- [`gtk-designer.md`](gtk-designer.md) — the GTK 4 GtkBuilder designer, GIR/catalogue model,
  native rendering boundary, macOS background-host behavior and Cambalache non-reuse policy;
- [`xaml-services.md`](xaml-services.md) — the cross-designer roadmap, framework detection and
  the shared IDE-level contracts.

The protocol described here is a **target contract**. No implementation must be rewritten to
match it in one step; each backend converges by extracting and renaming its existing RPC
surface. The WinForms host (`FormsDesigner/Host/`), the Uno host
(`WinUIXamlDesigner.UnoHost/`), and the WPF surface host (`WpfDesign.SurfaceHost/`) are the
three data points this contract generalizes.

## Shared-host lifecycle design (2026-08-23)

This section is normative and implemented across all five backends: GTK4, MewUI, WinForms, WPF and WinUI/Uno. It replaces the accidental
"one client object owns one child process" lifetime with a two-level model:

```text
backend pool key                         one shared process
(designer kind + runtime compatibility) ────────────────┐
                                                        │ authenticated RPC connection
                                                        ▼
  document lease A ── DocumentId A ── DocumentSession A (model/history/frame)
  document lease B ── DocumentId B ── DocumentSession B (model/history/frame)
  document lease C ── DocumentId C ── DocumentSession C (model/history/frame)
```

The pool key is not globally "all designers". Runtime-incompatible backends remain in different
processes. GTK and MewUI normally have one process for the IDE instance; WinForms, WPF and WinUI
use separate pools for incompatible target frameworks, architectures, dependency graphs or
native runtimes. Documents with the same pool key must reuse the connection.

The common `SharedDesignerHostBroker` owns process acquisition, reference counting, idle
retention, invalidation and restart coordination. A backend client is only a document lease. It
owns `DocumentId`, its recovery snapshot and view callbacks; disposing it sends `session/close`
and releases the lease, but cannot shut down a process used by other documents.

`SharedDesignerHostPool<TKey, TConnection>` is the compatibility partition above the broker.
It creates one broker per normalized runtime key and exposes the broker's active lease count and
monotonic connection generation. Backends must not use a project path as the key when two projects
have the same effective runtime graph; conversely, they must not merge differing runtimeconfig,
deps, architecture or native-runtime inputs merely because both projects target the same TFM.

Lifecycle rules:

1. The first lease starts and authenticates the process. Concurrent acquisitions await the same
   start instead of launching competitors.
2. Further compatible documents reuse its PID and receive distinct `DocumentId` values.
3. Closing a document releases its child-side model immediately. After the final lease closes,
   the connection stays idle for ten seconds. Reopening during that grace period cancels shutdown
   and reuses the PID; expiry performs bounded `shutdown` and kills a stuck child.
4. A transport failure, timeout or unexpected exit invalidates the pool generation exactly once.
   Every live lease becomes disconnected and receives the same generation-change notification.
5. Recovery starts one replacement, then reopens every live document from its latest parent-owned
   snapshot. `DocumentId` remains stable across recovery while `SessionId` changes; selection is
   restored by stable element name/id.
6. Recovery is per-document after reconnection: failure to reopen one malformed document reports
   diagnostics in that view without tearing down successfully restored siblings.
7. Restart Host is a pool operation. It captures all live documents, replaces the process once,
   and restores every lease. It must not leave sibling views bound to a disposed connection.

The parent snapshot is the recovery authority. Hosts never write files, and a frame is never used
to reconstruct source. Each view refreshes its recovery snapshot after load, accepted source edit,
designer mutation and flush. Dirty state remains in `OpenedFile`, so a child crash cannot clear it.

### Asynchronous latest-frame rendering

Model mutation and rendering are separate phases. `session/open`, `session/update` and design
mutations return the accepted tree, properties, bounds/diagnostics and a monotonically increasing
`RenderRevision` without waiting for pixels. The host schedules a render for
`(DocumentId, Version, RenderRevision)` and coalesces queued work per document.

The shell requests `design/render` asynchronously. It displays a returned frame only when its
`SessionId`, `DocumentId`, `Version` and `RenderRevision` still match the view. Older frames are
discarded without changing selection or diagnostics. Until a matching frame arrives, the last
good frame stays visible with a non-blocking rendering status. Invalid source also preserves the
last good pixels while exposing parse diagnostics.

GTK adds one invariant: all native construction, measurement, allocation, snapshot and GSK work
runs serially on the GTK main thread. Coalescing removes obsolete queued renders but does not move
GTK calls to worker threads. All GTK documents share one native scheduler and one `GskRenderer`;
they never spawn per-frame helper processes.

Render cache keys include normalized authoritative source, root id, target runtime/theme and
scale. Cache hits still carry the requesting version/revision and obey the stale-frame rule.

### Common shell and backend boundary

| Common parent-side service | Backend responsibility |
|---|---|
| broker, authentication, idle shutdown | compatible pool-key calculation |
| recovery registry and coordinated restart | reopen/materialize a document |
| stale version/revision rejection | native/source model mutation |
| canvas, frame presenter, selection overlay | pixels and native bounds/hit-test |
| Toolbox/Outline/Properties host adapters | catalogue and property/event descriptors |
| toolbar capability negotiation | runtime-specific capabilities |
| lifecycle DevFlow fields and contract tests | backend-specific fidelity tests |

The common layer cannot assume XAML, C#, GtkBuilder XML, HWNDs or a property system. A backend
cannot reimplement process leasing, idle shutdown, crash fan-out or stale-frame acceptance.

### Observability and acceptance

Every designer status endpoint exposes backend, pool key, host PID, session id, document id, pool
generation, active lease count, connection state, recovery count, document version,
requested/rendered revision, pending-frame state, toolbar capabilities and pad-hosting state.

This round is complete only when automated tests prove:

- two compatible documents share a PID but have distinct document ids;
- edits, undo/redo, selection, Properties, Outline, Toolbox and saves remain isolated;
- closing one document does not affect its sibling;
- close/reopen during idle grace reuses the PID, and final idle expiry terminates it;
- forced termination creates one replacement and restores every open document;
- explicit restart restores all siblings and their unsaved designer edits;
- rapid edits never display an older frame after a newer frame;
- GTK has one host/renderer and no `GtkRenderHelper` or `gtk4-builder-tool` render child;
- saved GTK XML validates and GTK/MewUI fixtures compile after edits;
- lifecycle mechanics have backend-independent unit tests and each backend retains a real
  workbench integration test covering pads and persistence.

Tests use semantic state and process inspection, never OS screenshots. xUnit v3 tests run through
`dotnet run --project ... --` as required by the repository test-runner instructions.

---

# Part I — The OOP visual designer architecture

## Why out-of-process

A visual designer must load the project's real runtime assemblies — the WinForms controls, the
WPF/Silverlight-style `DesignSurface`, the WinUI/Uno page — to render a faithful surface and to
apply designer gestures (move, resize, set property) to real objects. Loading those assemblies
into the IDE process is dangerous and fragile:

- a misbehaving control (constructor exception, static state, `Application` assumptions) can
  take the whole IDE down;
- runtime assemblies can conflict with the IDE's own WPF/WinForms stack (the macOS Libre*
  shims are especially sensitive);
- a full rebuild invalidates every loaded type, so the model must be discarded and rebuilt —
  in-process that means a fragile "unload everything" dance (ALC reload, WPF `Application`
  teardown, …).

The OOP architecture removes all of that from the IDE:

```
┌──────────────────────────── OpenDevelop (host) ────────────────────────────┐
│  Workbench tab (DesignerViewContent / WpfViewContent / WinUIXamlView)      │
│    ├─ source buffer (authoritative document, undo/dirty/save)              │
│    ├─ DesignerCanvas shell (toolbar: zoom/fit/grid/theme/size)             │
│    │    └─ ContentHost → per-backend surface control                       │
│    │         ├─ DesignFramePresenter (the rendered frame Image)            │
│    │         ├─ SelectionAdornerLayer (selection outline + resize handles) │
│    │         └─ per-backend gesture code (mouse/Preview/Thumb)             │
│    └─ pads: Properties (Xceed), Document Outline, Toolbox                  │
│         │                                                                  │
│         │  StreamJsonRpc over loopback TCP + random token                  │
│         ▼                                                                  │
└─────────┐──────────────────────────────────────────────────────────────────┘
          │
┌─────────▼──────────────────────────── child / surface host ────────────────┐
│  dotnet exec <host.dll> --port N --token T [--runtimeconfig … --depsfile …]│
│  ── real runtime objects live here only ──                                 │
│  WinForms  : DesignSurface + SnapshotDesignerLoader (LibreWinForms)        │
│  WPF       : XamlDesignContext + DesignItem + ProGPU headless renderer     │
│  WinUI/Uno : Uno Platform page + XamlReader + layout/render pipeline       │
│  - parses/loads the document snapshot                                     │
│  - renders frames (PNG / BGRA)                                            │
│  - owns hit-testing, selection, gestures, undo during visual editing      │
│  - flushes edits back as versioned source edit sets                       │
└────────────────────────────────────────────────────────────────────────────┘
```

The three backends independently arrived at the same shape:

| Aspect | WinForms | WPF | WinUI/Uno |
|---|---|---|---|
| Runtime objects in IDE process | removed | removed (2026-08-17/18) | removed |
| Transport | StreamJsonRpc, loopback TCP, auth token | same | same |
| Document authority | parent-owned, versioned snapshots | parent-owned XAML buffer | parent-owned XAML buffer |
| Surface | PNG frame + hit-test | BGRA frame + GPU hit-test | PNG/BGRA frame + hit-test |
| Stable identity | component names | `x:Name` + tree path | `x:Name` + tree path |
| Child-owned gesture/selection | yes | yes | yes |
| Toolbox catalog | child-side discovery | child-side reflection (IDE side still feeds dlls) | child-side reflection catalog |

The consequence: the shell-side work — document tabs, surface presenter, selection/outline/
properties/toolbox pads, undo, save, crash recovery — is almost identical regardless of which
runtime renders the surface, and OpenDevelop owns that shell once
(`DesignerCanvas`, `DesignViewport`, `DesignFramePresenter`, `SelectionAdornerLayer`,
`GridlineOverlay`, `SnapGuideCalculator`, `DocumentOutlineControl`, `IDesignHostClient`), treating
each backend as an adapter.

## Scope and non-goals

In scope:

- The wire contract between the OpenDevelop designer canvas and a design host process.
- Identity, versioning, and stale-operation rules shared by all backends.
- The DTO shapes for document, model, surface, properties, toolbox, outline, diagnostics,
  commands, and lifecycle.
- Launch/authentication/handshake/shutdown and failure recovery rules.
- The adapter seam (what a backend must provide to use the shared canvas).

Out of scope (each is owned by the runtime technote or a later phase):

- The internal designer engines (`DesignSurface`, `XamlDesignContext`, `XamlReader`,
  `SnapshotDesignerLoader`, ProGPU pipeline, …) — they stay in the child.
- An in-process profile — it is a legitimate backend (the retired ProGPU profile was one),
  but the common canvas only requires the adapter to implement the contract.
- HWND/child-window presentation — the contract is presentation-neutral; frames are the
  baseline, and a native presenter, if adopted, is one presenter implementation.
- A host-side design-tool extension SDK (`.designtools.dll`-style extensibility).
- Security sandboxing of project code — isolation is reliability, not a trust boundary.

## Terminology

- **Host** (OpenDevelop side): owns buffers, dirty state, save, the pads, and the presenter.
- **Child / design host / surface host**: the process that owns the real runtime objects, the
  design surface, and the gesture engine. One child may own several documents over its life.
- **Session**: one child process, authenticated and version-negotiated. Its lifetime is
  bounded by compatible (project, TFM, architecture, project-code mode).
- **Document**: one designable source document (`.xaml`, `.cs`+`.Designer.cs`+`.resx`,
  `.vb`+`.Designer.vb`+`.resx`) plus its supporting files.
- **Generation**: a per-document counter that increments on every child-side reload of the
  document. All runtime-bound IDs are only meaningful within one generation.
- **Element ID**: the stable handle to a runtime object, valid inside one document generation.
  The child maps it to the live object; the host only ever passes it back.

## Transport, authentication, and handshake

Transport is StreamJsonRpc (JSON-RPC 2.0) over loopback TCP, exactly as all three shipped OOP
hosts do. A future shared-memory or named-pipe transport is allowed only behind the same
contract; nothing in the DTOs assumes a particular pipe.

Launch sequence (host side):

1. Host creates a `TcpListener` on `127.0.0.1:0` and a random 32-byte token.
2. Host starts the child with `dotnet exec [--runtimeconfig <project>.runtimeconfig.json]
   [--depsfile <project>.deps.json] <host.dll> --port <port> --token <token> [--appbin <bin>]`.
   The child connects back; the token is compared in `initialize` (never logged).
3. Child sends `initialize` → `HostHandshake { ProtocolVersion, Runtime, RuntimeVersion,
   ProcessId, Capabilities }`.
4. Host verifies `ProtocolVersion` equals the contract version and `ProcessId` matches the
   process it spawned; otherwise it kills the child and reports an incompatible-runtime
   diagnostic. (WinUI/Uno deliberately skips the version check today — see Part II.)
5. The contract is additive: unknown optional fields are ignored; unknown enum values are
   rejected with both sides' supported ranges in the error.

Every request after `initialize` carries an envelope (see §Identity and versioning). The child
target may be a plain class with `[JsonRpcMethod]` names; the host side is the `IDesignHostClient`
seam described below.

## Session and lifecycle methods

| Method | Direction | Purpose |
|---|---|---|
| `initialize` | child → host | handshake (token, protocol/runtime version, PID, capabilities) |
| `session/open` | host → child | open a document from a snapshot; returns `SessionState` |
| `session/update` | host → child | deliver a new snapshot (source edit, external change) |
| `session/flush` | host → child | commit current state back to host as an edit set |
| `session/close` | host → child | close the current document (release runtime objects) |
| `session/restart` | host → child | reset the child to a clean state (used after rebuild/`generation` bumps) |
| `design/command` | host → child | execute a named design command (see §Commands; read as "the discrete named RPCs a backend needs", not a literal generic verb — see Part I §Commands) |
| `design/set-property` | host → child | set a property on an element |
| `design/reset-property` | host → child | reset a property to its default |
| `design/set-event` | host → child | bind/clear an event handler |
| `design/hit-test` | host → child | map surface coordinates to an element |
| `design/add-element` | host → child | insert a new element under a parent (toolbox drop) |
| `design/set-bounds` | host → child | move/resize an element |
| `design/delete-elements` | host → child | remove elements |
| `design/apply-layout` | host → child | alignment/spacing/format operations |
| `design/rename` | host → child | rename an element (updates source + identity) |
| `design/theme` | host → child | switch design theme by name (Light/Dark default; WPF uses embedded `themes/*.xaml` names, WinUI the app's ThemeDictionaries keys) and re-render |
| `design/render` | host → child | produce a frame (on demand) |
| `design/export-png` | host → child | render to a PNG file (diagnostics/tests) |
| `app/resources` | host → child | supply App.xaml/merged-resource content |
| `ping` | host → child | liveness probe |
| `shutdown` | host → child | bounded graceful shutdown |
| `host/exited` | child → host | notification that the child exited (fault or clean) |

Notifications from child to host (`session/changed`, `design/dirty`, `diagnostics/updated`)
carry document/session identity and are coalesced; they never carry the full model.

## Identity and versioning

Every host→child operation carries:

```text
SessionId          — identifies the child process (host-chosen GUID).
DocumentId         — identifies the document within the session.
Generation         — per-document reload counter.
BaseVersion        — the document version the host believes is current.
```

Rules:

- `SessionId`/`DocumentId` are opaque strings; they are stable for the child's life.
- `Generation` increments whenever the child rebuilds its model from a snapshot (load, theme
  reload, restart). All element IDs, selection, undo state and cached model tokens are invalid
  across a generation change; the host must drop its proxies and re-query.
- `BaseVersion` is the host's authoritative document version. The child rejects any operation
  whose `BaseVersion` is not the version it last accepted, with `StaleVersion` error including
  the child's current version. The host then re-sends a fresh snapshot (`session/update`) and
  re-runs the operation.
- A `session/flush` carries the same `BaseVersion`; the returned `EditSet` is applied only if
  the host's document is still at that version, atomically and through the normal undoable
  document path. The child never writes files.
- Version increments happen on the host whenever it accepts new source (host edit, accepted
  child edit, external change). Version is a `long`, monotonically increasing per document.

## Document synchronization

The host is authoritative for the designed document and its supporting files (App.xaml,
`.Designer.cs`, `.resx`, merged dictionaries). A snapshot is a set of files:

```text
DesignerDocumentSnapshot {
  Version: long
  ProjectFileName: string
  TargetFramework: string
  Architecture: string
  ProjectAssemblyPath: string
  PrimaryFileName: string
  Files: [ { FileName, Kind ("Source"|"Designer"|"Resource"|"AppXaml"), Text, Base64 } ]
  Language: "CSharp" | "VisualBasic" | "" (XAML backends leave it empty)
  ProjectCodeMode: "Enabled" | "Disabled"
}
```

- `session/open` loads a snapshot. `session/update` delivers newer text for any file; the
  child re-parses/re-materializes and returns a fresh `SessionState` (diagnostics included).
- Invalid intermediate text must not destroy the last good surface: the child keeps the last
  accepted model, reports diagnostics for the pending text, and only swaps the surface when a
  valid reload succeeds.
- `session/flush` returns:

```text
DesignerEditSet {
  BaseVersion: long
  Files: [ { FileName, Kind, Text, Base64 } ]
  GeneratedFiles: [ { FileName, Kind, Text } ]   // e.g. a newly created event-handler partial
}
```

  The host applies `Files` and `GeneratedFiles` atomically at `BaseVersion`, marks the
  document dirty, and increments its version. A stale flush fails without a partial save.
- The host never trusts the child's model for saving; save is always `flush → apply → normal
  save`.

## Model: element tree, stable IDs, and selection

The model the host receives is a neutral tree, not runtime objects:

```text
ElementNode {
  Id: string                       // generation-scoped stable handle
  Name: string?                    // x:Name / component name (may be null)
  Type: string                     // CLR type name or metadata name; never a System.Type
  X, Y, Width, Height: double      // root-surface coordinates (DIPs)
  Path: string                     // child-index path from root ("0,2,1")
  IsDesignable: bool               // false for template parts / non-source nodes
  Children: [ ElementNode ]
}
```

- `Id` is the only thing that crosses back into the child. The host never constructs a target
  type, never receives a `Type`, `object`, `DependencyProperty` or converter.
- A pick (surface click) maps through the child's own hit-testing to the innermost element;
  the child returns `HitTestResult { Chain: [ElementNode], PickPath: string }`. The host maps
  the chain/path back to source (auto-naming an unnamed element where the runtime supports it,
  as the WinUI adapter already does), and selects via `Id`.
- Selection lives in the child; the host's pads project it. There is exactly one selection
  state machine (child-owned). Selection changes arrive as notifications with the selected
  `Id`s; the host never runs a competing selection model.

## Property and event values

Property transport is a tagged value, never arbitrary polymorphic CLR serialization:

```text
PropertyValue {
  Kind: "Null" | "Boolean" | "String" | "Number" | "Enum" | "Point" | "Size" |
        "Rect" | "Thickness" | "Color" | "Brush" | "Uri" | "Xaml" | "Reference" |
        "ReadOnly" | "Unsupported"
  Text: string            // invariant text; child converts to the target type
  Display: string         // for the pad; may be empty
}
```

- The child owns conversion and materialization through its target converters/serializers.
- Reference-like values (bindings, resources) travel as constrained XAML text or as a
  stable reference descriptor; the host never resolves them.
- `PropertyDescriptor { Name, DisplayName, Description, Category, TypeName, Kind,
  IsReadOnly, ShouldSerialize, IsEnum }` flows with the model for the Properties pad.
- Events flow as `EventDescriptor { Name, Category, HandlerTypeName, Handler }`; bindings are
  set/cleared through `design/set-event` and generate handler stubs in `GeneratedFiles`.

## Surface and rendering

Frames are the baseline presentation:

```text
RenderFrame {
  Sequence: long
  Width, Height: int
  Dpi: double
  Format: "PNG" | "BGRA8"           // WinForms: PNG via WIC; WPF/WinUI: BGRA8 (WIC-free)
  Data: string                      // base64 (deflate-compressed for BGRA)
  RenderMs: double
}
```

- Frames are produced on demand, not streamed continuously. The host drops stale sequences
  and applies backpressure; one slow client must not queue unbounded frames.
- The viewport is host-owned; the child measures/arranges at that viewport and returns the
  frame plus a fresh element tree.
- A future shared-memory BGRA transport is an optimization behind the same `RenderFrame`
  shape (the `Data` field becomes a handle); the contract does not require it.
- The host's canvas presenter only knows `RenderFrame` + `ElementNode`. Any native presenter
  (HWND island on Windows, etc.) is an adapter-side presenter, not a second protocol.

## Input

Input is forwarded as normalized events, expressed in design DIPs:

```text
InputEvent {
  Kind: "PointerDown" | "PointerMove" | "PointerUp" | "KeyDown" | "KeyUp" | "Wheel"
  X, Y: double
  Button: "Left" | "Middle" | "Right" | "None"
  Modifiers: "None" | "Shift" | "Control" | "Alt" | ...
  Key: string
  Timestamp: long
  PointerId: long
  Dpi: double
}
```

- Hit testing, capture, gesture state, placement, adorners and selection remain child-owned.
- The host forwards raw input; it does not interpret gestures.

## Toolbox, outline, diagnostics

- **Toolbox catalog**: `initialize`/`GetCapabilities` returns a neutral catalog:
  `ToolboxItemInfo { Name, DisplayName, Category, Template, XamlNamespace, TypeName }`. The
  child builds it by reflecting the *loaded* runtime assemblies. The host never inspects
  project assemblies (WPF is the remaining exception — see Part II).
- A toolbox drop is `design/add-element { ParentId, ToolboxItem, X, Y, BaseVersion }`; the
  child materializes and returns the new `SessionState`.
- **Document Outline**: the shared `DocumentOutlineControl` (`ICSharpCode.SharpDevelop.Widgets`)
  shows the design document's element tree from `DesignerSessionState.Tree`
  (`DesignerElementNode`, name + gray type, per-node context menu via `ContextMenuFactory`).
  Selection is bidirectional but single-authority: picking a node routes into the surface's
  normal selection path (one selection concept), and surface/Properties selection mirrors back
  into the control.
- **Diagnostics**: `Diagnostic { Severity, Message, Line, Column }` accompany every
  `SessionState` and arrive as notifications. The host routes them to the Error List /
  Message View (line-navigable) when available, and to the surface status control otherwise.

## Commands and undo

Stable command IDs (`Undo`, `Redo`, `Cut`, `Copy`, `Paste`, `Delete`, `SelectAll`,
`BringToFront`, `SendToBack`, `Align*`, `Size*`, `Spacing*`, …) execute child-side; the host
maps its standard Edit/Format commands to the common IDs; an adapter maps IDs it does not
support to `UnsupportedCommand` and the host disables the corresponding pad items.

In practice both shipped backends use **discrete named RPCs** (`design/rename`,
`design/add-element`, `design/set-z-order`, …) rather than a generic dispatcher, so
`design/command` in the method table reads as "whatever discrete named RPCs a backend needs",
not a literal generic verb.

- Undo/Redo are child-authoritative during visual editing; the result synchronizes source to
  the host (dirty state follows an *accepted* document change).
- A source reload (`session/update`) establishes a designer-history boundary.

## Failure, restart, and safe mode

Host state machine: `Starting → Running → { Unresponsive, Crashed } → Restarting → Running |
Disabled`.

- Interactive calls use short timeouts; initial load/build/resource operations use longer,
  separate limits.
- A timeout/disconnect/crash leaves the last frame visible under a diagnostic overlay, releases
  pending calls, and offers: restart; disable project code (safe mode); open source; view child
  log. It never blocks or exits the IDE.
- One automatic restart is reasonable; a repeated immediate crash stops the loop and shows the
  options above.
- Safe mode is part of the runtime key and the protocol from the beginning (`ProjectCodeMode:
  "Disabled"`); the child renders metadata/XAML placeholders instead of project-defined types.
- Closing the last compatible document requests bounded `shutdown` and then kills a leaked
  child.
- Restart reconstructs state solely from host-owned source and project context — never from
  hidden child state.

## The host-side adapter seam

OpenDevelop-side code depends only on `IDesignHostClient`
(`src/Main/Designer/Designer.Remote/IDesignHostClient.cs`) — that file is the single source of
truth for this contract.

- **Core interface**: lifecycle (`ProcessId`, `IsAlive`, `ChildLog`, `SessionId`, `DocumentId`,
  `HostExited`, `PingAsync`, `ShutdownAsync`, `TerminateHost`), document
  (`OpenAsync`/`UpdateAsync(DesignerDocumentSnapshot)`, `FlushAsync(baseVersion)`), and mutations
  (`SetPropertyAsync`/`SetEventAsync`/`AddElementAsync`/`SetBoundsAsync`/`DeleteElementsAsync`/
  `RenameAsync`/`HitTestAsync`, every one keyed by `baseVersion` first). Every backend must
  implement all of it.
- **Optional capability interfaces**, feature-detected per backend: `IDesignHostPropertyReset`,
  `IDesignHostDefaultEvent`, `IDesignHostLayout` (WinForms implements these — a markup runtime has
  no defaults model or absolute-position layout commands to back them); `IDesignHostTheme`,
  `IDesignHostExport`, `IDesignHostAppResources` (WinUI/Uno implements these). The host disables
  the matching pad UI when a backend doesn't implement one, per DDP's "unsupported command" rule.
- **Close is explicit for shared hosts**: the handshake is owned by `DesignerHostProcessClient`
  (shared base, run during `StartAsync`). Backends that can host multiple documents in one child
  expose `session/close { documentId }` internally and keep the public `IDesignHostClient.Dispose`
  contract as the document-close operation. Rendering is not a separate public call on either
  backend — frames come back inside `DesignerSessionState.Render`.
- **`AddElementAsync` takes both `DesignerToolboxItemInfo item` and `proposedName`** — a CLR-type
  backend (WinForms, and eventually WPF) must be told the new component's name via `proposedName`
  and reads `item.TypeName`; a markup backend (WinUI/Uno) derives the name from the parsed
  `item.Template` and ignores `proposedName`.

Each backend supplies:

- the child process/launcher (`dotnet exec` with the project's runtimeconfig/deps, or the
  bundled child for fixture profiles);
- the DTO mapping between this contract and the backend's engine;
- the `SessionState`/`ElementNode`/property projections;
- framework detection and runtime selection (which document opens which adapter).

The shared canvas (`DesignerCanvas`, presenters, pads) never references a runtime type.

## End-to-end sequences

### Open a document

```text
Host                              Child
  |  spawn + token                  |
  |<---------- initialize ----------|  (verifies token/version)
  |  session/open {snapshot v0} --->|
  |<---------- SessionState --------|  (tree + diagnostics + first render on demand)
  |  (present surface, populate pads)
```

### Source edit → refresh

```text
Host                              Child
  |  (user types; host bumps to v1) |
  |  session/update {v1} --------->|
  |<---------- SessionState --------|  (last good surface retained on parse failure)
```

### Visual edit → source

```text
Host                              Child
  |  design/set-property {v1} ---->|
  |<---------- SessionState --------|  (child mutated, XAML re-serialized)
  |  session/flush {v1} ---------->|
  |<---------- DesignerEditSet -----|  (BaseVersion v1)
  |  apply atomically, mark dirty, bump to v2
```

### Rebuild / generation change

```text
Host                              Child
  |  session/restart ------------>|
  |  session/open {fresh snapshot, Generation+1} -->
  |<---------- SessionState --------|  (old IDs invalid; host re-queries)
```

---

# Part II — The implementations

All backends converge on the same stack (the first three are runtime-rendering designers; the
last two are **source-model** designers — see "The source-model designers" below):

Designer integration-test builds explicitly invoke the GTK and MewUI addin projects. This is
intentional: their deployed assemblies live under the repository `AddIns/` tree rather than the
test runner output, so relying only on the host-app build can otherwise execute stale binaries.

| Layer | Shared project | WinForms | WPF | WinUI/Uno | MewUI | GTK 4 |
|---|---|---|---|---|---|---|
| Protocol DTOs + process lifecycle | `src/Main/Designer/Designer.Remote/` | `FormsDesignerHostClient` | `WpfSurfaceHostClient` | `UnoDesignClient` | `MewUIDesignerHostClient` | `GtkDesignerHostClient` |
| Geometry/rendering helpers | `src/Main/Designer/Designer.Presentation/` | used | used | used | n/a (semantic WPF projection) | native GTK PNG + Gir.Core bounds/hit-test |
| Child-process bootstrap | `src/Main/Designer/Designer.Server/` (`DesignerChildHost`) | `DesignerChildHost.Run` | `DesignerChildHost.Run` | own dispatcher pump | host-specific pump | host-specific pump |
| Canvas shell | `DesignerCanvas.cs` | `RemoteFormsDesignerControl` | `WpfSurfaceDesignerControl` | `UnoDesignSurfaceControl` | shared-pad composition (outline/tools/properties) | shared-pad composition |
| Toolbox pad engine | `SharedToolbox.cs` | `WpfToolbox` facade | `WpfToolbox` facade | `WinUIXamlToolbox` facade | in-addin catalogue | in-addin catalogue |
| Child process | — | `FormsDesigner/Host/` | `WpfDesign.SurfaceHost/` | `WinUIXamlDesigner.UnoHost/` | `MewUIDesigner.Host/` (Roslyn transforms) | `GtkDesigner.Host/` (GtkBuilder XML transforms) |
| DevFlow actions | — | `FormsDesignerDevFlowActions.cs` | `WpfDesignDevFlowActions.cs` | `WinUIXamlDesignerDevFlowActions.cs` | `MewUIDesignerDevFlowActions.cs` | `GtkDesignerDevFlowActions.cs` |

## The source-model designers: MewUI and GTK 4

Added 2026-08; the fifth and fourth backends respectively. They reuse the DDP transport,
handshake, and session lifecycle, but now split process and document ownership explicitly:
`MewUIDesignerHostClient` and `GtkDesignerHostClient` are lightweight per-document leases, while
their nested shared connection owns the single `DesignerHostProcessClient` child process. Every
RPC after open carries `documentId`; the child keeps a `documentId -> DocumentSession` map and
`session/close` removes only that document. This lets multiple windows of the same backend share
one host process without sharing editor state, version counters, native bounds or undo history.
They still have one architectural difference from the runtime-rendering designers that drives
everything else:

**the authoritative document is not a runtime object graph.**

- **MewUI**: a C#-first framework, so the document IS the C# syntax tree. The child process
  performs Roslyn source transformations for every toolbox insertion / property edit / delete /
  rename (strict WinForms-style InitializeComponent grammar: field creations, then property
  assignments, then `parent.Children(...)` relationship calls, anchored by a `Content = root`
  assignment). See [`mewui-designer.md`](mewui-designer.md) for the decision record.
- **GTK 4**: the document is GtkBuilder XML (`.ui`). The child validates and transforms the
  XML tree directly (`<child>/<object>/<property>`), preserving whitespace-free canonical form.

Consequences, all deliberate:

1. **Rendering is an IDE-side WPF approximation**, not runtime pixels: the surface preview is
   built from the parsed model (`StackPanel`-style proxies per node type). There is no ProGPU /
   Uno runtime in the loop, so `hit-test` / `set-bounds` / events are **deterministic
   NotSupported** on the client (same convention as the WPF designer's nudge) rather than
   silently missing.
2. **Undo/redo lives in the child as whole-document snapshots**, matching the WPF backend's
   session-snapshot approach.
3. **Container validation is enforced in the child** (a `<child>` under a leaf widget, or a
   `.Children(...)` call on a non-container control, is rejected at edit time instead of
   producing documents the runtime refuses to load).
4. Deployment follows the standard Host layout (`AddIns/<Category>/<Name>/Host/`), so they are
   `OutOfProcessHost` kind for the addin-trim rules in `Directory.Build.targets`.

|---|---|---|---|---|
| Protocol DTOs + process lifecycle | `src/Main/Designer/Designer.Remote/` | `FormsDesignerHostClient : DesignerHostProcessClient, IDesignHostClient` | `WpfSurfaceHostClient : DesignerHostProcessClient, IDesignHostClient` | `UnoDesignClient : DesignerHostProcessClient, IDesignHostClient` |
| Geometry/rendering helpers | `src/Main/Designer/Designer.Presentation/` (`DesignViewport`, `DesignFramePresenter`, `SelectionAdornerLayer`, `GridlineOverlay`, `SnapGuideCalculator`) | used | used | used |
| Child-process bootstrap | `src/Main/Designer/Designer.Server/` (`DesignerChildHost` — the connect-back/JsonRpc/wait-for-shutdown boilerplate shared by WinForms and WPF children; Uno runs its own pump, see Part II) | `DesignerChildHost.Run` | `DesignerChildHost.Run` | own dispatcher pump |
| Canvas shell (toolbar/edge/theme/names) | `ICSharpCode.SharpDevelop.Widgets/Project/DesignerCanvas.cs` | `RemoteFormsDesignerControl : DesignerCanvas` | `WpfSurfaceDesignerControl : DesignerCanvas` | `UnoDesignSurfaceControl : DesignerCanvas` |
| Toolbox pad engine | `src/Main/Base/Project/Src/Gui/Pads/SharedToolbox.cs` (one ListBox + grouping/drag state machine, per-scope filter) | `WpfToolbox` facade | `WpfToolbox` facade | `WinUIXamlToolbox` facade |
| Child process | — | `FormsDesigner/Host/` (`DesignerHostService`, `SnapshotDesignerLoader`) | `WpfDesign.SurfaceHost/` (`WpfSurfaceHostService`, ProGPU headless render) | `WinUIXamlDesigner.UnoHost/` (`DesignHost`, Uno page runtime) |
| DevFlow actions | — | `FormsDesignerDevFlowActions.cs` | `WpfDesignDevFlowActions.cs` | `WinUIXamlDesignerDevFlowActions.cs` |

## WinForms — the reference OOP backend

The most complete and oldest OOP path; every other backend is measured against it.

### Architecture

```
OpenDevelop
  DesignerViewContent            — view host: snapshot construction, pads, undo, clipboard
    └─ RemoteFormsDesignerControl (: DesignerCanvas)   — surface, gestures, marquee, UIA tree
         ├─ DesignFramePresenter (PNG via BitmapImage/WIC)
         ├─ SelectionAdornerLayer (selection outline, single "se" handle visual)
         ├─ moveThumb / resizeThumb (real interactive WPF Thumbs, bubbling events)
         └─ FormsDesignerHostClient (: DesignerHostProcessClient, IDesignHostClient)
              └─ StreamJsonRpc + token → FormsDesigner.Host.exe
FormsDesigner.Host (child)
  DesignerChildHost.Run — shared connect-back/JsonRpc/wait-for-shutdown bootstrap (Designer.Server)
  DesignerHostService  — RPC target: session/version/flush + discrete design/* RPCs
  SnapshotDesignerLoader + DesignSurface (LibreWinForms) — the real controls
```

### Capabilities (protocol)

- Implements the full `IDesignHostClient` core plus the optional
  `IDesignHostPropertyReset` / `IDesignHostDefaultEvent` / `IDesignHostLayout`
  (alignment/spacing/z-order — only an absolute-positioned CLR backend can back these).
- `IEventBindingHost` on the property proxy: double-click an event row in the Properties pad
  to create a handler stub (goes through `design/activate-default-event`).
- Bounds travel as `double` on the wire; the client rounds to `int` before sending (WinForms
  layout is integer).
- 1:1 identity model: components have generated names (`button1`, …) that are the stable IDs.

### Surface and input model

- Frame: PNG via `BitmapImage` (WIC). `Stretch.None` — the bitmap is exactly design-size × scale.
- Input: **bubbling** mouse events + two real interactive `Thumb` elements (`moveThumb` full-size
  transparent, `resizeThumb` 8×8). No `ScrollViewer` in the surface, so bubbling works (unlike the
  WinUI/WPF tunnel-event workarounds).
- Move/resize: drag deltas are surface pixels, divided by `viewport.Scale` before being added to
  design-space state; committed once on mouse-up via `design/set-bounds` / `design/apply-layout
  "move"` (one RPC per gesture, not per move).
- Only the **south-east** corner resizes (`"se"` single handle), with `Math.Max(8, …)` minimum
  size; no anchored-edge math, no snap, no alignment guides.
- No drag threshold — the Thumb semantics start dragging on press (WinUI uses 4 px, WPF 3 px).
- **Canvas margin (2026-08-19)**: `CanvasMargin = 24` keeps empty space on every side of the
  design (matching WPF's `CanvasPadding`), so the root's handles are reachable and the
  `EdgePattern` shows around the form — before this the WinForms canvas visibly had no border
  around the form while WPF/WinUI did. The margin is folded into the viewport's pan
  (`DesignViewport.Fit/Zoom(…, CanvasMargin, CanvasMargin)`), so the frame, guides and every
  `DesignToSurface`-based adorner stay aligned. **Selection-render fix (2026-08-19)**: selection
  adorner rendering was corrected to track the frame/selection under these new coordinates.

### WinForms-only features

- **Tab-order overlay**: `UpdateDesignGuides` draws the form gray frame + per-component dashed
  outline + name labels + tab-index badges.
- **Component locking**: per-component `Locked` state, locked components recolored
  (DodgerBlue → DarkOrange via `SelectionAdornerLayer.SelectionStroke`).
- **Marquee rubber-band select**: Shift/Ctrl extended selection, `IntersectsWith` on mouse-up.
- **UIA automation peer tree**: `RemoteDesignerAutomationPeer` + `RemoteComponentAutomationPeer`
  (~110 lines) — the only backend exposing an automation tree.
- Keyboard: Escape (select parent), Tab (tab-order rotation), Delete, arrow-key nudge (step 1,
  Shift = 10).
- Toolbox: legacy `System.Drawing.Design.ToolboxItem` drops with a hard-coded container list
  (Form/Panel/GroupBox/TabPage/UserControl).
- **Disconnected overlay**: `disconnectedOverlay` + Restart button on child death.
- Add Components dialog, image resource editor, localization model options, component library.
- **`.Designer.cs`/`.Designer.vb` do not open a design view (2026-08-19)**: both
  `CSharpDesignerSecondaryDisplayBinding` and `VbDesignerSecondaryDisplayBinding` reject
  `*.Designer.cs`/`*.Designer.vb` in `CanAttachTo` — the design view attaches only to the primary
  partial (`Foo.cs`/`Foo.vb`); opening the generated companion from the project browser stays a
  plain source view instead of spawning a second design view over the same form.

### Known bugs and limits (2026-08-18)

- ~~**Zoom was recently added but has scale holes**~~ — **fixed (2026-08-18)**: hit-testing,
  marquee comparison, toolbox-drop coordinates and the guides' sizes all go through
  `viewport.Scale`/`DesignToSurface` now.
- ~~**`RebuildViewport` is short-circuited by the frame-sequence guard in `Show`**~~ —
  **fixed (2026-08-18)**: zoom/fit re-derives the viewport via `ApplyViewport` without touching
  the already-decoded frame, so the guard no longer blocks it. The initial render is a literal
  100% zoom (the combo's default), not Fit. The stale "WinForms never scales or pans" comment
  was removed.
- `Stretch.None` + zoom is the same family of bug the WPF backend already hit (fixed there with
  `Stretch.Fill`); flagged in the WPF technote as a likely same-class latent bug.

## WPF — cut over to OOP (2026-08-17/18)

The WPF designer switched from the in-process `DesignSurface` to a DDP child on 2026-08-17/18.
The old in-process `Src/Designer/*` directory is gone; the engine lives in
`externals/vscode-wpf/external/WpfDesigner/` and is referenced by the child only.

### Architecture

```
OpenDevelop
  WpfViewContent                 — view host: snapshot, flush-on-save, rename sync
    └─ WpfSurfaceDesignerControl (: DesignerCanvas)
         ├─ DesignFramePresenter (BGRA32 via BitmapSource.Create — WIC-free)
         ├─ SelectionAdornerLayer (8 handles + name label)
         └─ WpfSurfaceHostClient (: DesignerHostProcessClient, IDesignHostClient)
              └─ StreamJsonRpc + token → WpfDesign.SurfaceHost.exe
WpfDesign.SurfaceHost (child)
  DesignerChildHost.Run — shared connect-back/JsonRpc/wait-for-shutdown bootstrap (Designer.Server)
  WpfSurfaceHostService — RPC target: open/update/flush + mutations + App.xaml merge + theme
  XamlDesignContext / DesignItem / DesignSurface (WpfDesigner engine)
  ProGpuWpfCompositionTarget — headless GPU render (RenderTarget → texture → ReadPixels)
  TryHitTestOwner — GPU hit-test index, falls back to VisualTreeHelper + ResolveOwner
  SurfaceTypeFinder — child-side type resolution from the snapshot's assembly paths
```

### Capabilities (protocol)

- Implements the full `IDesignHostClient` core; `SetEventAsync` throws
  `NotSupportedException` today (needs a child→host callback direction; Phase 5 item) — this
  should be expressed as a capability interface rather than a throw.
- Frame decode: deflate + base64 → BGRA32 via `BitmapSource.Create` (the macOS WIC avoidance
  workaround shared with WinUI/Uno).
- The element tree lands *exactly* where the rendered pixels are: a regression test asserts the
  rendered content sits within 1 px of the bounds the element tree reports.

### Surface and input model

- Input: **Preview (tunneling)** events — the same LibreWPF/`ScrollViewer` swallow problem the
  WinUI backend documents; bubbling events are unreliable here.
- 8 resize handles `{nw,n,ne,e,se,s,sw,w}`; `ApplyGesture` anchors the opposite edge/corner and
  clamps non-negative sizes. Handles are non-interactive visuals; the control's own Preview
  handlers do hit-testing via the shared `SelectionAdornerLayer.HandleAt`.
- Drag threshold 3 surface px (scaled); one `design/set-bounds` RPC per gesture on mouse-up; a
  rejected/adjusted result restores the adorner from the authoritative tree (`Show`).
- Root element (`RootElementId = ""`) is selectable and its page can be resized by handles, but
  cannot be moved (no container); empty string means root, not "no selection".
- Child-side `SetBounds` runs a real `PlacementOperation.Start(PlacementType.Resize)` with
  `CurrentContainerBehavior.SetPosition` (Canvas.Left/Top, Grid.Margin semantics), falling back
  to Width/Height.
- The frame image uses `Stretch.Fill` — the fix for a real zoom bug (`Stretch.None` + scaling
  left the image mis-sized under zoom).

### WPF-only features

- **Headless GPU render + GPU hit-test** in the child (ProGPU pipeline).
- **App.xaml resource merge**: `ParseAppResources` merges App.xaml into the root element's
  Resources (two real gotchas handled: `Application.Resources` vs bare `Style` wrapping).
- **`WpfControlRenameSync`**: renaming `x:Name` synchronizes the code-behind field via LSP —
  the only backend that keeps a markup name and a CLR field in sync.
- **`DesignerSessionState.CreatedElementId`** (new shared field): WPF-specific need — a dropped
  toolbox element has no `x:Name` and cannot be looked up by name like WinForms/WinUI can.
- **`WpfToolbox.BuildToolboxItemInfo`** generates DDP DTOs, but `AddProjectDlls` still reflects
  project assemblies in the IDE process (Phase 4 item — the "host never inspects project
  assemblies" rule is not yet met here).
- **Design-theme enumeration (WPF-standard convention)**: the child's `ResolveThemes` walks the
  project assembly's manifest resources for embedded `themes/*.xaml` (excluding `generic.xaml`,
  the fallback default-style dictionary) and reports the file names — sans extension — via
  `DesignerSessionState.DesignThemes`/`SupportsThemeSwitch`. `SetTheme(name)` loads that embedded
  dictionary and merges it onto the open design's root. The theme combo is shown only when a
  project actually embeds themes (`ShowTheme = DesignThemes.Length > 0`). Verified by the
  `WpfThemeFixture` (Bright/Midnight/Solarized + generic) and the WpfThemeSample.
- **Grid-guide drag (WPF shape)**: `design/query-grid-guides` returns a Grid's live
  row/column track geometry (`DesignerGridTrackInfo` cumulative offsets + sizes); the host
  draws draggable divider guides over the frame — the WPF analogue of Uno's own Grid-guide
  overlay (which reads the live XAML text instead).
- **Whole-document Undo/Redo**: host-side `undoStack`/`redoStack` of flushed XAML text,
  restored via `session/update` — mirroring WinForms' `DesignerViewContent` remote undo; neither
  backend has live-element-tree transactional undo.
- **Multi-select + align/distribute/match-size** (2026-08-20): `od.wpf-designer.multi-select`/
  `align`/`distribute`/`match-size`/`undo`/`redo` DevFlow actions; multi-select drawing reuses
  the shared `SelectionAdornerLayer.SetSecondarySelection`, and alignment snapping reuses the
  shared `SnapGuideCalculator`.

### Legacy baggage (2026-08-18)

- `WpfDesign.AddIn.csproj` still ProjectReferences the three engine projects
  (`WpfDesign.Designer` / `WpfDesign.XamlDom` / `WpfDesign`) because the dead `Commands/`
  files (CutCopyPaste, UndoRedo, Remove, …) still compile against `DesignSurface`. Those
  commands are commented out of the context menu; deleting the dead files removes the engine
  dependency and closes the "no engine reference from the IDE" red line.
- `ThumbnailViewPad` is a shim showing "unavailable"; real thumbnails are future work.
- `WpfPropertyPad.cs` is a 3-line stub — the shared Xceed PropertyGrid pad (`IHasPropertyContainer`)
  replaced it.
- `MyTypeFinder.cs` is orphaned (its child-side successor `SurfaceTypeFinder` is live).

## WinUI/Uno — the richest surface backend

The WinUI designer targets a **Uno Platform page** in a child process. It is the only backend
that renders a real cross-platform UI runtime and the origin of most shared-layer code (the
viewport math, the frame presenter, the selection adorner were extracted from here).

### Architecture

```
OpenDevelop
  WinUIXamlDesignerViewContent   — view host: XDocument editor (undo/dirty/save authority)
    └─ UnoDesignSurfaceControl (: DesignerCanvas)   — surface, gestures, zoom/pan, guides
         ├─ DesignFramePresenter (BGRA via BitmapSource.Create — WIC-free)
         ├─ SelectionAdornerLayer (8 handles + name label)
         ├─ ScrollViewer (zoom/pan viewport) — swallows bubbling events (see below)
         └─ UnoDesignClient (: DesignerHostProcessClient, IDesignHostClient)
              └─ StreamJsonRpc + token → UnoHost.exe
WinUIXamlDesigner.UnoHost (child)
  DesignHost / UnoDesignRuntimeHost — RPC target: capabilities, load, layout, theme,
                                      hit-test, app-resources, render
  Uno Platform page + XamlReader + layout pipeline
  ProGPU renderer (retired in-process profile reused for the child's rendering)
```

### Capabilities (protocol)

- Implements the `IDesignHostClient` core plus `IDesignHostTheme` / `IDesignHostExport` /
  `IDesignHostAppResources` (the only backend that supplies App.xaml/merged-resource content
  and re-renders per design theme).
- All six mutation RPCs are wired to real IDE call sites; the local `XDocument` (`editor`)
  remains the undo/dirty/save source of truth in every path — the discrete RPC only chooses how
  the render is refreshed (incremental render via an `IWinUIXamlIncrementalRender` capability
  with full-reload fallback).
- `design/rename` is landed as a ready-to-use capability only (no rename UI exists anywhere in
  the IDE yet).

### Surface and input model

- Input: **Preview (tunneling) events**; no mouse capture (LibreWPF stops delivering pointer
  events after `CaptureMouse`); **manual double-click detection** (`ClickCount` is not populated
  under LibreWPF; 500 ms / 8 px thresholds); `CancelStuckDrag` recovery when LibreWPF loses a
  mouse-up.
- 8 resize handles + move; drag threshold 4 surface px; deltas accumulated in surface pixels and
  divided by `viewport.Scale` in the runtime host (`OnSurfaceElementDragDelta`), with
  snap-to-guides applied before commit (8 design-unit tolerance) and the corrected delta (not the
  raw one) committed on mouse-up.
- **Multi-select group drag**: dashed secondary outlines, whole group translated by one delta.
- **Selection/guide offset fixed (2026-08-19)**: selection adorners, snap guides and grid-guide
  drags now map design→surface through a canvas-local viewport (`CanvasLocalViewport`, which folds
  `CanvasMargin` into the pan) instead of re-adding `origin + pan` a second time — previously the
  margin/pan was applied twice and overlays drifted off the rendered frame.
- `ScrollViewer`-based zoom/pan: Ctrl+wheel zoom-at-cursor, space/middle-button pan, scrollbars;
  design↔surface math = `DesignViewport` ± scroller offsets (`ToDesignPoint`/`DesignToSurfacePoint`).
- Frame: BGRA8 deflate+base64, decoded with `BitmapSource.Create` (WIC avoidance on macOS).

### WinUI/Uno-only features

- **Zoom/pan** with `MinZoom 0.1 / MaxZoom 16`, zoom combo sync (Fit / 25%–400%).
- **Gridlines**: 20-design-unit grid via the shared `GridlineOverlay` (plain `Line` shapes —
  tiled `DrawingBrush` does not render under LibreWPF-on-macOS, see
  `designer-gridlines-bug.md`).
- **Snap alignment guides**: orange guide lines from `ApplySnap` (align left/center/right,
  top/middle/bottom), `SetSnapGuides` draws them.
- **Grid row/column guide resize**: select a Grid, drag its row/column separators, commit
  `RowDefinitions`/`ColumnDefinitions` source edits (`GridGuideDragCommitted`).
- **Inline text editor**: double-click TextBlock/TextBox/Button → in-surface text edit
  (font scaled by viewport scale), Enter/focus-loss commits, Esc cancels; writes Text/Content
  through the incremental render path.
- **Design-theme combo**: the shared toolbar's theme combo is populated from the app's actual
  `ResourceDictionary.ThemeDictionaries` keys, hoisted by `AppResourceBuilder.GetThemeNames`
  from the App.xaml under design (`UnoDesignRuntimeHost.EnsureAppResourcesAsync` calls
  `SetDesignThemes`), so the combo lists the themes the app really carries — not a hardcoded
  Light/Dark pair. Selecting one fires `ThemeRequested` → `design/theme` (by name) and re-renders.
- **Design-size (device) presets**: Auto / Phone 390×844 / Tablet 768×1024 / Desktop 1280×720
  drive the child's page layout size.
- **Edge-drag page resize**: `CanvasMargin = 32` leaves room for it, but **only the comment
  exists — no implementation**; size changes come from the combo or DevFlow today.

### Retired profile

The **ProGPU in-process profile** (`WinUIXamlDesigner.ProGPUHost`) is retired: its
`SurfaceGeometry()` returns `default` and `ElementTree => null`. It remains as a historical
backend; the OOP Uno host is the only supported WinUI path.

---

# Part III — Feature matrix across the three frameworks

Legend: **✓** implemented and exercised · **~** partial (see note) · **✗** not implemented ·
**(–)** concept does not exist for this runtime.

## Protocol / out-of-process core

| Feature | WinForms | WPF | WinUI/Uno |
|---|---|---|---|
| Out-of-process child (real runtime objects never in IDE) | ✓ (oldest) | ✓ (2026-08-17/18) | ✓ |
| StreamJsonRpc + loopback TCP + random token | ✓ | ✓ | ✓ |
| Shared `DesignerHostProcessClient` lifecycle (spawn/pump/timeout/dispose) | ✓ | ✓ | ✓ |
| `session/open` / `session/update` snapshots | ✓ | ✓ | ✓ |
| `session/flush` → versioned edit set | ✓ | ✓ | ✓ |
| Envelope (SessionId/DocumentId/Generation/baseVersion) on every mutation | ✓ | ~ (partially — some RPCs omit session/document ids, see Part IV §contract drift) | ✓ |
| `design/set-property` | ✓ | ✓ | ✓ |
| `design/reset-property` | ✓ (optional capability) | ✗ | ✗ |
| `design/set-event` | ✓ (event rows + double-click create handler) | ✗ (throws `NotSupportedException` — child→host callback direction missing) | ✓ (handler-name edits) |
| `design/hit-test` | ✓ (local hit-test first, RPC fallback) | ✓ (GPU hit-test + VisualTreeHelper fallback) | ✓ |
| `design/add-element` (toolbox drop) | ✓ | ✓ (`CreatedElementId` for unnamed drops) | ✓ (named-container drops; unnamed/root falls back to full reload) |
| `design/set-bounds` | ✓ (move + "se" resize, one RPC per gesture) | ✓ (8-handle `PlacementOperation`, one RPC per gesture) | ✓ (8 handles + snap, one RPC per gesture) |
| `design/delete-elements` | ✓ | ✓ | ✓ |
| `design/rename` | ✓ (rename UI exists) | ✗ (capability ready, no UI anywhere) | ~ (capability ready, no UI) |
| `design/apply-layout` (align/spacing/z-order) | ✓ (`IDesignHostLayout`) | ✗ | ✗ |
| `design/theme` (design-theme re-render) | ✗ (combo hidden — no theme concept in WinForms) | ✓ (enumerates embedded `themes/*.xaml`; combo per-project) | ✓ (combo from ThemeDictionaries keys) |
| `app/resources` (App.xaml/merged dictionaries) | ✗ (not applicable) | ~ (App.xaml merged into root Resources, child-side) | ✓ |
| Capability negotiation (optional interfaces feature-detected) | ✓ (Reset/DefaultEvent/Layout) | ~ (core only; SetEvent gap should become a capability) | ✓ (Theme/Export/AppResources) |
| Crash/restart recovery | ✓ (disconnected overlay + Restart) | ✓ (crash/restart covered by tests) | ✓ (lifecycle probes + status) |
| Safe mode (project code disabled) | ✓ (protocol-level) | planned (same protocol field) | ✓ (protocol-level) |

## Canvas shell & presentation (shared)

`DesignerCanvasCapabilities` is the single toolbar visibility contract. Controls always retain
the canonical order `Zoom → Fit → Gridlines → Theme → Show Names → Design Size`; unsupported
controls are collapsed rather than left visible and inert. Refresh, Restart Host, Source, Delete,
Undo and Redo belong to document lifecycle or the IDE command system, not the canvas toolbar.

| Feature | WinForms | WPF | WinUI/Uno | MewUI | GTK 4 |
|---|---|---|---|---|---|
| Shared `DesignerCanvas` shell + toolbar | ✓ | ✓ | ✓ | ✓ | ✓ |
| Declared visible capabilities | Zoom, Fit | Zoom, Fit, Gridlines, Show Names, optional Theme | All six | Zoom, Fit, Gridlines | Zoom, Fit, Gridlines |
| Zoom combo (100% default, VS behavior) | ✓ (**fixed 2026-08-18**: viewport and hit-test scaling) | ✓ (Stretch.Fill fix landed) | ✓ | ✓ (safe projection) | ✓ (safe projection) |
| Fit | ✓ | ✓ | ✓ | ✓ (measured safe projection) | ✓ (measured native frame) |
| Gridlines toggle | ✗ (hidden) | ✓ (shared `GridlineOverlay`) | ✓ (shared `GridlineOverlay`) | ✓ (safe-projection brush) | ✓ (native-frame overlay brush) |
| Show names on selection (toolbar toggle, default on) | ✗ (hidden) | ✓ | ✓ | ✗ (hidden) | ✗ (hidden) |
| Design-theme combo (lists actual themes; Light/Dark default) | ✗ (hidden — not a WinForms concept) | ✓ (embedded `themes/*.xaml`, per-project) | ✓ (ThemeDictionaries keys) | ✗ (hidden) | ✗ (hidden) |
| Design-size (device) preset combo | ✗ (hidden — not a WinForms concept) | ✗ (hidden) | ✓ | ✗ (hidden) | ✗ (hidden) |
| Edge pattern around the design bitmap/projection | ✓ | ✓ | ✓ | ✓ | ✓ |
| Toolbar follows IDE theme (dark toolbar, light text) | ✓ | ✓ | ✓ | ✓ | ✓ |
| Shared `DesignViewport` coordinate math | ✓ | ✓ | ✓ | ✗ (not frame-based) | ✗ (not frame-based) |
| Shared `DesignFramePresenter` | ✓ PNG/WIC | ✓ BGRA/WIC-free | ✓ BGRA/WIC-free | ✗ | native GTK PNG via source-model view |
| Shared `SelectionAdornerLayer` | ✓ (single "se" handle visual, no label, recolor for locked) | ✓ (8 handles + label) | ✓ (8 handles + label) | ✗ | ✗ |
| Frame sequence backpressure (stale frames dropped) | ✓ | ✓ | ✓ | n/a | n/a |

## Selection & gestures

| Feature | WinForms | WPF | WinUI/Uno |
|---|---|---|---|
| Click-to-select | ✓ | ✓ | ✓ |
| Drag-move | ✓ (bubbling events + Thumb) | ✓ (Preview events) | ✓ (Preview events) |
| Resize handles | ~ **1** ("se" only, Thumb) | ✓ **8** (anchored opposite edge) | ✓ **8** |
| Root/page resize by handle | ✗ (form is the root) | ✓ (root element selectable + resizable) | ~ (page size via design-size combo only) |
| Drag threshold | ✗ (none — Thumb semantics) | ✓ (3 px) | ✓ (4 px) |
| Multi-select | ~ (marquee + Shift/Ctrl) | ✗ | ✓ (group drag + dashed outlines) |
| Marquee rubber-band | ✓ | ✗ | ✗ |
| Keyboard nudge | ✓ (step 1, Shift=10) | ✗ | ✓ (step 1, Ctrl=10) |
| Keyboard: Escape parent / Tab rotation | ✓ | ✗ | ✗ |
| Double-click default event | ✓ (`ClickCount == 2`) | ✗ | ~ (manual detection, 500 ms/8 px) |
| Snap guides / alignment | ✗ | ✗ | ✓ (8 design-unit tolerance) |
| Grid row/column guide drag | ✗ (not applicable — no Grid layout) | ✗ | ✓ |
| Inline text editing on surface | ✗ | ✗ | ✓ |
| Group drag commit | ✗ | ✗ | ✓ |
| Selection sync: surface ↔ outline ↔ properties | ✓ | ✓ | ✓ |

## Backend-specific features

| Feature | WinForms | WPF | WinUI/Uno |
|---|---|---|---|
| Tab-order overlay | ✓ | (–) | (–) |
| Component lock state | ✓ | (–) | (–) |
| UIA automation peer tree | ✓ | ✗ | ✗ |
| Add Components dialog / component library | ✓ | ✗ | ✗ |
| Toolbox: child-side reflection catalog (engine shared via `SharedToolbox`) | ✓ | ~ (child builds DTOs; IDE still feeds project dlls — Phase 4) | ✓ |
| Toolbox drop to surface | ✓ | ✓ | ✓ |
| Toolbox drop to XAML source | ✗ | ✓ | ✗ |
| Properties pad adapter | ✓ (`RemoteComponentPropertyProxy`, ICustomTypeDescriptor) | ✓ (`WpfSurfaceElementPropertyAdapter`) | ✓ (through the host) |
| `x:Name` ↔ code-behind field sync | ✗ (Designer.cs regenerated) | ✓ (`WpfControlRenameSync` via LSP) | ✗ |
| App.xaml resources applied to design | ✗ (–) | ✓ (child-side merge) | ✓ |
| Render: PNG (WIC) vs BGRA (WIC-free) | PNG | BGRA | BGRA |
| Headless GPU render + GPU hit-test | ✗ | ✓ (ProGPU) | ✓ (render; hit-test via layout tree) |
| Export PNG (diagnostics/tests) | ~ | ✗ (not yet exposed) | ✓ (`IDesignHostExport`) |

## Testing & DevFlow probes

| Feature | WinForms | WPF | WinUI/Uno |
|---|---|---|---|
| Child-process RPC test suite | ✓ (`FormsDesignerHostClientTests`) | ✓ 27/27 (`WpfSurfaceHostRpcTests`, incl. five `DesignTheme_*`) | ✓ 3/3 (`UnoDesignHostRpcTests`) |
| `od.<x>-designer.surface-geometry` probe | ✓ (Frame=whole bitmap, Selection=element outline, Handle=derived corner, Element=element — shared `DesignerSurfaceGeometry` record, 2026-08-18) | ✓ (same shared shape; `frame` = whole bitmap since 2026-08-18, was `frame==element`) | ✓ (same shared shape) |
| Resize-drag integration tests | ✓ | ✓ (4-attempt retry loop — known flake) | ✓ |
| `query-element-screen-bounds` / `query-toolbox-item-bounds` | ✗ | ✓ | ✓ |
| Outline probes (`outline-status`/`outline-select`) | ✓ (direct on shared control) | ✓ (from DDP tree) | ✓ (child count + names) |
| `od.outline-pad.content` (shared, cross-designer) | ✓ | ✓ | ✓ — new 2026-08-18: reads the LIVE `DocumentOutlineControl` the Outline pad is showing, not any designer's internal tree; this is what caught WPF's `OutlineContent` bug (below), which a designer-side `outline-status`-style probe could not have caught since its own internal tree was already correct |
| Test fixtures: WinForms sample / Uno sample / WPF xaml | ✓ | ✓ (file restored in `finally`) | ✓ (copied sample dir, no pollution) |

## Known technical limits (shared)

| Limit | Effect | Workaround / status |
|---|---|---|
| LibreWPF: `ScrollViewer` swallows bubbling mouse events | bubbling handlers never fire under zoom/pan | WPF + WinUI use **Preview (tunneling)** events; WinForms has no ScrollViewer so bubbling stays correct |
| LibreWPF: `ClickCount` not populated | double-click detection fails | WinUI manual detection (500 ms / 8 px); WinForms relies on `ClickCount` (works — no ScrollViewer) |
| LibreWPF: `CaptureMouse` stops pointer delivery | drag with capture breaks | WinUI/WPF use Preview events without capture; WinForms uses `Thumb` (no capture needed) |
| macOS: WIC not usable for raw pixel frames | PNG decode is fine, BGRA `BitmapImage` is not | WPF + WinUI ship BGRA via `BitmapSource.Create`; WinForms keeps PNG/WIC |
| LibreWPF: implicit styles don't walk `BaseType` | subclasses miss themed styles | per-control `Loaded`-time style application (Xceed controls, combos) |
| LibreWPF: `SystemColors` static properties ignore resource overrides | theme brushes wrong on some controls | semantic theme keys overridden in `Theme.Light.xaml`/`Theme.Dark.xaml` |
| WinForms zoom is scale-hole-riddled | hit-test/marquee/drop wrong above 100%; zoom change may no-op | flagged; fix alongside a shared zoom state machine (Part IV) |
| WPF engine references linger in the IDE csproj | red line "no engine reference from IDE" not met | delete dead `Commands/*` files (CutCopyPaste, UndoRedo, Remove) |
| WPF `design/set-event` missing | no event binding from Properties pad | needs child→host callback direction (Phase 5) |
| WinUI edge-drag page resize | page size via combo only | `CanvasMargin = 32` reserved; unimplemented |
| `PointToScreen` vs DevFlow tree coordinates | DevFlow tree bounds have a ~(10, 63) offset vs screen coords | surface-geometry probes use `PointToScreen` (reliable); tests click with screen coords |

---

# Part IV — Known duplication and the further-convergence list (2026-08-18)

Everything below respects the "protocol yes, presentation no" red line: no backend's mouse-gesture
state machine is touched. The list is pure geometry/rendering helpers, contract alignment, test
scaffolding, and DevFlow plumbing.

## Contract drift (fix before extracting anything)

1. **`surface-geometry` `frame` semantics differ.** ~~WinForms/WinUI return the whole design
   bitmap; WPF returns the *selected element* (`frame == selection`, the same Rect twice).~~ **Done
   (2026-08-18):** the three probes now share one `DesignerSurfaceGeometry` record
   (`Designer.Presentation/DesignerSurfaceGeometry.cs`) with a unified shape — `frame` = the
   whole design bitmap, `element` = the selected element's bounds, `selection` = its outline,
   `handle` = the bottom-right corner (derived, no more real-Thumb probe on WinForms) — plus a
   shared `DesignerSurfaceGeometryProbe` (`ScreenBoundsOf`, `DesignRectToScreen`, `ToJson`) that
   the three `surface-geometry` DevFlow actions now call; the three resize-drag integration
   tests assert against `element` on all three backends. The WinForms zoom bug that made
   selection drift from the frame at scale ≠ 1 was fixed in the same pass
   (`RebuildViewport` no longer short-circuits through `Show`'s frame-sequence guard;
   hit-test/marquee/toolbox-drop coordinates now divide by `viewport.Scale`; guides and UIA
   bounds go through `DesignToSurface`).
2. **RPC parameter names differ across the three clients** even though the DTOs are shared:
   `version` (WinForms/Uno) vs `baseVersion` (WPF), `componentName` vs `elementName` vs
   `elementId`, `controlType` vs `itemXaml` vs `item`. "JSON field names are the contract" —
   then there are three contracts. Unify toward the superset; WinForms/Uno then converge into a
   shared template. (open)
3. **WPF `SetEventAsync` throws** rather than declaring absence — express it as a capability
   interface (`IDesignHostEventBinding`), matching the `IDesignHostPropertyReset` precedent, and
   let the host disable the Events UI.
4. **WinUI skips the handshake protocol-version check** (only echoes `SessionId`); WPF checks.
   Align on the DDP rule (reject mismatched version, report both ranges).

## Shared helpers that should exist (Designer.Presentation / Designer.Remote)

1. **`SurfaceGeometry` as a named record + shared probe helper** — **done (2026-08-18)**: the
   three `(Rect Frame, Rect Selection, Point Handle)` tuple copies are replaced by the shared
   `DesignerSurfaceGeometry` record and `DesignerSurfaceGeometryProbe` in
   `Designer.Presentation` (`ScreenBoundsOf`, `DesignRectToScreen` with an optional scroll
   offset, `ToJson`); the three DevFlow `surface-geometry` actions each shrank to ~3 lines.
2. **`DesignViewport.BaseOrigin`**: `(Math.Max(0, OriginX) + PanX, Math.Max(0, OriginY) + PanY)`
   is written out verbatim in `RemoteFormsDesignerControl.Show` and `WpfSurfaceDesignerControl.Show`
   (identical `Thickness` formula, identical comment) and again inside `DesignToSurface` and the
   WinUI canvas placement. One property kills three copies; the WinUI `CanvasMargin` difference
   stays explicit at the call site.
3. **Zoom state machine in `DesignerCanvas`**: `ZoomPresets`/`ZoomLabels`/`fitMode`/`zoomScale`/
   `RebuildViewport` + the combo/fit handlers are byte-for-byte identical in
   `RemoteFormsDesignerControl` and `WpfSurfaceDesignerControl` (comments acknowledge the
   duplication), with a third variant in `UnoDesignSurfaceControl`. Move it into the shell (the
   shell already owns the combo); this also gives a place to fix the WinForms sequence-guard bug.
 4. ~~**Gridlines brush trio**: `CreateGridBrush`/`UpdateGridBrush`/`SetGridlines` +
    `GridCellSize = 20` are duplicated verbatim between `WpfSurfaceDesignerControl` and
    `UnoDesignSurfaceControl` (same gray `Color.FromRgb(0x80,0x80,0x80)`, same `TileMode.Tile`).
    The shell already owns `ShowGrid`/`GridRequested` — a `DesignGridlinesOverlay` in
    Designer.Presentation can be driven straight from it.~~ **Done (2026-08-20)**:
    `GridlineOverlay` in `Designer.Presentation` draws the grid as plain `Line` shapes
    (tiled `DrawingBrush` does not render under LibreWPF-on-macOS — see
    `designer-gridlines-bug.md`) and both WPF and WinUI/Uno surfaces use it.
5. **Deflate+base64 frame decode**: `WpfSurfaceDesignerControl.DecodeFrame` and
   `RenderCodec.Decode` are identical pure-managed code (the comment "duplicated per backend by
   design" is no longer justified — both decode the same BGRA wire format). One `FrameCodec` in
   Designer.Remote. The child-side compressors (`DesignHost`, `WpfSurfaceHostService`) are the
   same shape; unify them later in the same pass.
6. **Design-rect → screen-rect**: `PointToScreen(0,0)` + `PointToScreen(ActualWidth, ActualHeight)`
   appears in all three `SurfaceGeometry` implementations, both DevFlow `GetScreenBounds`
   actions, `QueryToolboxItemBounds`, and `OpenDevelopDevFlowActions` — a shared
   `BoundsToScreen(FrameworkElement)` helper.
7. **Client boilerplate**: `DocumentId` minting, `LocateChildDll`, and the `StartAsync` factory
   are triplicated in the three `*HostClient` classes; the per-mutation anonymous-object envelopes
   are hand-written 12+10+10 times. After the parameter-name unification (drift item 2), these can
   drop into `DesignerHostProcessClient` as template methods.
8. **`DesignerHostService` bottom re-declares 11 private DTOs** that mirror
   `Designer.Remote/DesignerProtocol.cs` field-for-field (the child project doesn't reference the
   shared project). StreamJsonRpc only matches field names — make the child reference
   Designer.Remote and delete the copies. Same for the WPF/WinUI children where applicable.

## Test scaffolding

- **`ResizeDragTestBase`**: the three resize-drag integration tests share ~80% verbatim code —
  the `Bounds` local function, the 6-step drag loop, the growth-polling loop, and the
  `AssertSelectionTracksFrame`/`AssertHandleAtBottomRight` assertions are identical modulo action
  prefix, selection action, growth field, and deltas. A base class parameterizes those; the WPF
  `od.activate` + 4-attempt retry loop becomes an optional `RetryDragGestureAsync`, and WinUI
  gains the same robustness for free.

## DevFlow plumbing

- **`RegisterDevFlowActionsCommand`** empty-`Run` classes are duplicated across 11+ addins (all
  three designers plus ILSpy/AspNetCore/XamlBinding/Android*/SearchAndReplace/PackageManagement)
  — a shared base class or an assembly attribute removes the pattern.
- **Action bodies**: `query-toolbox-item-bounds`, `query-element-screen-bounds`, and
  `properties-pad.edit` are near-duplicates between WinUI and WPF (the WPF versions carry
  `FindRealizedContainer`/`WaitUntilHitTestableAt` hardening that should be back-ported to the
  shared implementation); the three outline probes should all go through one
  `DocumentOutlineControl.Snapshot()`.

## Open task list (2026-08-18)

Everything still open, in dependency order; each item names the files to touch and the
acceptance check, so the list can be resumed by a fresh session at any point.

### P0 — Fixing red tests

1. **UI-tree bounds are offset from real rendering (~69 px).** `DoubleClickEventRow` fails
   because `FindUiTextBounds(uiTree, "Shown")` yields a point that hits the `ScrollViewer`
   gutter, never an event row. Measured by manual DevFlow reproduction against a live app
   (port 9299): clicking the tree's reported bounds always logs `double-click not on an
   EventItem`; clicking `treeY + 69` lands on the actual row (hit `LostFocus` at tree y=345
   while clicking screen y=414) and binds the handler successfully. The x axis appears correct
   to within a row. Suspects, in order: (a) DevFlow `LeXtudio.DevFlow.Agent.LibreWpf` 0.2.2
   computing `bounds` from a transform that drops the window's top chrome (title bar +
   tab/toolbar ≈ 62-69 px on this macOS layout); (b) the window's reported `PointToScreen`
   origin drifting after `od.activate`; (c) Xceed `PropertyGrid` row layout differing from
   what the visual tree reports. Fix options: compensate in `FindUiTextBounds` (verify the
   offset is stable across window positions/DPI), switch the double-click to window-relative
   `global:false` pointer events if the DevFlow client supports them reliably, or query a row
   by index via a new DevFlow action instead of screen coordinates.
   **Acceptance:** `WinFormsDesigner_DoubleClickEventRow_CreatesAndBindsHandler` passes
   consistently, and the handler it binds is `Form1_Shown`, not a neighbor row.

2. ~~**Decide the event-binding navigation behavior.**~~ **Decided (2026-08-18): keep the jump.**
   `DesignerViewContent.SetRemoteEvent`
   (src/AddIns/DisplayBindings/FormsDesigner/Project/Src/DesignerViewContent.cs:142-144)
   jumps to the source tab after binding a handler ("VS-style") — this stays as-is, on
   purpose, for all three designers, not just WinForms. The jump makes the design surface
   disappear from view and empties the Properties pad (it follows `ActiveViewContent`), which
   reads as "the designer selection changed", but the selection state itself never changes —
   only the active view does, and jumping to the newly-generated handler stub is the
   behavior a user actually wants after binding an event.
   **Acceptance:** `WinFormsDesigner_DoubleClickEventRow_CreatesAndBindsHandler` asserts the
   source tab becomes active after binding (not that it stays on the design tab); WinUI/WPF
   should do the same once/if they grow an equivalent event-binding UI (today only WinForms
   has one - see Part III's `design/set-event` row).

### P1 — Wire and behavior parity leftovers

3. **`design/hit-test` version parameter still diverges.** WPF sends
   `baseVersion` and validates staleness; WinUI sends neither; WinForms sends `version`
   (already renamed). Either unify on `baseVersion` everywhere (WinUI needs to start sending
   it, WinForms already does) or document hit-test as intentionally version-free.
   **Acceptance:** all three `IDesignHostClient.HitTestAsync` signatures agree.

4. **Full three-designer integration regression run.** The RPC renames below are built and
   the WinUI child-process suite (`UnoDesignHostRpcTests`, 3/3 green) plus the WinForms
   resize/outline tests pass; a full `AddInTests` run against the renamed wire is still owed
   once item 1 is fixed.
   **Acceptance:** `dotnet test --project tests/OpenDevelop.IntegrationTests` green for the
   FormsDesigner/WinUIXamlDesigner/WpfDesigner groups.

### P2 — Shared shell extraction (Part IV "Shared helpers")

5. **`DesignViewport.BaseOrigin`.** Extract the duplicated origin/scroll-offset math behind
   `DesignerSurfaceGeometryProbe.DesignRectToScreen` into the viewport itself (both WinForms
   `RemoteFormsDesignerControl` and WinUI `UnoDesignSurfaceControl` derive their scroll
   origin from it today).
   **Acceptance:** `DesignViewport` exposes `BaseOrigin`; both call sites use it.

6. **Zoom state machine + gridlines brush into `DesignerCanvas`.** Move the zoom combo
   state machine (WinForms `ZoomLevel`, WPF, WinUI) and the design-grid brush into the shared
   `DesignerCanvas` so the three surfaces stop re-deriving them.
   **Acceptance:** `DesignerCanvas` owns zoom state; WinForms keeps its fixed zoom bugs
   fixed (Part II "Known bugs and limits" stays green).

7. **`FrameCodec` sharing.** WinForms and WPF each encode the rendered frame; WinUI uses a
   Skia path in the child. One codec in `Designer.Presentation` for the two WPF-side ones.
   **Acceptance:** no duplicated Png/Bitmap encoding code outside the shared project.

8. **Client boilerplate convergence.** The three `StartAsync`/`LocateChildDll`/ping/shutdown
   wrappers around `DesignerHostProcessClient` collapse into one shared client (WinForms
   `FormsDesignerHostClient` and WinUI `UnoDesignClient` become thin subclasses, WPF
   `WpfSurfaceHostClient` the base shape).
   **Acceptance:** one `DesignerHostProcessClient` subclass per backend, no copy-pasted
   `InvokeAsync` mapping tables.

9. **Child-process DTO sharing policy.** `FormsDesigner.Host` and `WinUIXamlDesigner.UnoHost`
   each carry a hand-written DTO file (JSON is the contract, per `DesignProtocol`); the
   WinForms host just gained a local `DesignerToolboxItemInfo` copy. Decide whether the child
   projects should ProjectReference `Designer.Remote` (type identity still irrelevant across
   the wire) or keep local copies — then document it here.
   **Acceptance:** stated policy; both child projects follow it.

### P3 — Test and DevFlow plumbing

10. **`ResizeDragTestBase`.** The three resize-drag tests share ~80% verbatim (Part IV
    "Test scaffolding"): parameterize action prefix, selection action, growth field, deltas;
    make the WPF `od.activate` + retry loop an optional `RetryDragGestureAsync` and give
    WinUI the same robustness.
    **Acceptance:** three tests inherit one base, no duplicated drag/assert loops.

11. **`RegisterDevFlowActionsCommand` collapse.** The empty `Run` classes are duplicated
    across 11+ addins; a shared base class or assembly attribute removes the pattern.
    **Acceptance:** no new addin adds a fourth copy.

12. **Action-body convergence.** `query-toolbox-item-bounds`, `query-element-screen-bounds`,
    and `properties-pad.edit` are near-duplicates between WinUI and WPF (back-port the WPF
    `FindRealizedContainer`/`WaitUntilHitTestableAt` hardening); the three outline probes all
    go through one `DocumentOutlineControl.Snapshot()`.
    **Acceptance:** each probe exists once, in `Designer.Presentation` or its DevFlow layer.

### Done (2026-08-18)

- WinForms zoom correctness (`RemoteFormsDesignerControl`: `ApplyViewport`, scale-aware
  hit-test/marquee/toolbox-drop/guides/UIA bounds; initial 100% zoom).
- Unified `surface-geometry` contract: shared `DesignerSurfaceGeometry` +
  `DesignerSurfaceGeometryProbe` (`Designer.Presentation/DesignerSurfaceGeometry.cs`), three
  DevFlow actions down to ~3 lines each, three resize tests assert `element` semantics.
- RPC parameter names unified to the WPF superset: `version`/`requestVersion` → `baseVersion`,
  `componentName`/`elementName` → `elementId`, `elementNames` → `elementIds`,
  `parentName` → `parentId`, `controlType`/`itemXaml` → `item` (DTO) — WinForms and WinUI
  clients and child processes (client + `Program.cs` RPC wrapper + host), including a local
  `DesignerToolboxItemInfo` in `DesignerHostService.cs`; all four projects build, WinUI child
  RPC suite green.
- `OpenLensRenderer` crash: `resolving` is now a `ConcurrentDictionary` (was a plain
  `HashSet` mutated from the render pass and the async continuation — `AddIfNotPresent`
  threw `IndexOutOfRangeException` and killed the app mid-test).
- `OpenDevelopDevFlowActions.cs` was missing `using ICSharpCode.SharpDevelop.Widgets;` and
  `using ICSharpCode.SharpDevelop.Designer.Remote;` (wpftmp build would not compile).
- `designer-common.md` Part III matrix and Part II WinForms bug list updated to match.
- TS/JS addin direction decided (investigation closed): the legacy `TypeScriptBinding`
  (SharpDevelop 5.x) and its MonoDevelop port (`mrward/typescript-addin`) are the same
  codebase on two dead JS bridges (Noesis.Javascript x86 / V8.NET) and are not references.
  OpenDevelop's LSP infrastructure is already in place (`LanguageServices/Lsp/`,
  `LspServerRegistry.CreateDefault` registers `.ts/.tsx/.js/.jsx` →
  `typescript-language-server --stdio`; the F# addin shows the addin-side registration
  pattern). Decision made: use the TypeScript 7 Go language server (preview build,
  `@typescript/native-preview` or GA `typescript`, `tsc --lsp --stdio`) and swap the launch
  spec accordingly; 7.1 (Stable 2026-11-10) is API stabilization only, so the LSP client is
  not exposed to the remaining feature gaps. Recorded as P4 item 13 with acceptance
  criteria; the two legacy projects are marked for removal from `SharpDevelop.sln`.
- Open task list (P0-P4) and matching Priority section established in this file.
- **Solution build was red** after the `DesignerSurfaceGeometry` unification: two files were
  missing `using ICSharpCode.SharpDevelop.Designer.Presentation;`
  (`WinUIXamlDesigner.UnoDesignHost/UnoDesignRuntimeHost.cs`,
  `WinUIXamlDesigner.ProGPUHost/ProGpuRuntimeHost.cs` — the same class of miss as the
  `OpenDevelopDevFlowActions.cs` one already listed above), the retired ProGPU in-process
  profile's `SurfaceGeometry()` stub still returned the old `(Rect,Rect,Point)` tuple instead
  of the new `DesignerSurfaceGeometry` record, and `UnoDesignSurfaceControl.cs` had an
  ambiguous `Vector` reference (`System.Numerics.Vector` vs `System.Windows.Vector`, both
  `using`d) once its own `SurfaceGeometry()` needed to construct one. Fixed; full solution
  builds clean again.
- **Two more instances of the "root id is `""`, not "no selection"" bug** (the same class
  already fixed in `WpfSurfaceDesignerControl`/`WpfSurfaceHostService` for hit-testing and
  selection):
  - `WpfViewContent.OutlineContent` walked the OTHER open views and returned the SOURCE
    editor's `IOutlineContentHost` instead of its own - a leftover from the old in-process
    designer, which had no outline of its own to return. With the Design tab active, the
    Outline pad showed the XAML text editor's LSP symbol list (one entry, e.g.
    `TextBlock [PaneTitle]`) instead of the designed element tree that
    `WpfViewContent.UpdateOutline` was building and nobody ever displayed. Fixed to
    `=> outline` (matching `FormsDesignerViewContent`/`WinUIXamlDesignerViewContent`, which
    both already just return their own).
  - `DocumentOutlineControl.SelectNodeById(string id)` used `string.IsNullOrEmpty(id)` as its
    "nothing to select" guard, so selecting the WPF root (id `""`) from the surface was
    silently swallowed and the Outline pad never highlighted it even after the root became
    selectable. Changed the parameter to `string?` and the guard to `id == null` - a real
    empty-string id (the WPF root) now works, and every other caller (WinForms/WinUI, which
    pass component/x:Name strings, never `""`) is unaffected.
  - Found by adding `od.outline-pad.content` (`OpenDevelopDevFlowActions.cs`), a new DevFlow
    probe that walks the LIVE `DocumentOutlineControl` the pad is actually showing (not any
    designer's internal tree) — the gap in Part III's own "Outline probes" row: a
    designer-side outline-status action can report a perfectly correct tree while the pad
    displays something else entirely, and no existing probe could have caught that
    divergence. Verified live: before the `OutlineContent` fix the probe reported
    `names: ["TextBlock [PaneTitle]"]`; after, it reports the full designed tree and tracks
    `selected` through a root selection made on the surface.
  - Full 12-test cross-designer regression (all resize-drag/drag-drop/outline/properties
    tests across WinForms, WPF, WinUI) green after these fixes.
- **WPF mouse-driven mutations were silently not marking the document dirty.**
  `WpfSurfaceDesignerControl`'s four mutation-commit paths (resize/move drag, toolbox drop,
  hit-test/select, Delete key) were fire-and-forget `async void` handlers that called their
  RPC wrapper with `.ConfigureAwait(true)`, trusting the WPF dispatcher's
  `SynchronizationContext` to resume the continuation on the UI thread. Proven unreliable
  live under LibreWPF on macOS: a real drag-resize genuinely committed and rendered (confirmed
  via `od.wpf-designer.surface-geometry` showing the correct new size), but the continuation
  resumed on a thread-pool thread instead (`Dispatcher.Thread.ManagedThreadId` differed from
  `Environment.CurrentManagedThreadId` at that point), so touching WPF objects afterward threw
  a cross-thread `InvalidOperationException` that the `async void` handler silently swallowed —
  `DocumentChanged` never reached `WpfViewContent`, so `MakeDirty()` was never called even
  though the edit had genuinely applied. A user could resize/move/drop/delete via the mouse,
  close the file without touching anything else, and silently lose the change. Fixed by
  converting all four commit methods (`CommitBounds`/`CommitDrop`/`HitTestAndSelect`/
  `CommitDelete`) to block synchronously via `.GetAwaiter().GetResult()` instead — every
  caller is already on the dispatcher thread (they're WPF routed-event handlers), so no
  `SynchronizationContext` capture is needed at all. Matches the already-proven-reliable
  pattern `WpfViewContent.LoadInternal`/`WpfDesignDevFlowActions` already use. Verified live:
  `od.file.is-dirty` now correctly returns `true` after a mouse-driven resize, and the saved
  XAML shows the exact expected `Width`/`Height`.
- **RESOLVED (2026-08-18) - WinUI/Uno resize-drag mutated the wrong element, plus two related
  coordinate/selection bugs, all traced to a single family of root causes and fixed.** Dragging
  a correctly-selected element's own resize handle (e.g. `PrimaryButton`) used to instead mutate
  its PARENT (`RootStack`, a `StackPanel`), adding an unexpected `Margin` attribute while leaving
  the intended child untouched. Reproduced with both `od.winui-designer.select` and a genuine
  mouse drag, ruling out a test-harness-only artifact. Root-caused via live diagnostics
  (temporary `Console.Error.WriteLine` calls in `UnoDesignSurfaceControl.BeginDrag`,
  `UnoDesignRuntimeHost.OnSurfaceElementDragStarted`/`OnSurfaceElementDragCommitted`, and
  `WinUIXamlDesignerViewContent.OnElementDragCommittedOnSurface` - same technique that found the
  WPF dirty-tracking bug above): the synthetic (and real) click on the reported resize-handle
  screen position never actually registered as landing on the handle at all
  (`dragHandle` came back empty from `HandleAt`), so `BeginDrag` silently fell back to treating
  the gesture as a plain element-drag, resolving whatever was under the mis-shifted point -
  `RootStack`, not `PrimaryButton`.

  **Root cause**: `UnoDesignSurfaceControl.ToDesignPoint(Point point)` - the single entry point
  every mouse handler (`OnMouseLeftButtonDown`, `OnMouseMove`, grid-guide dragging, etc.) uses to
  convert a WPF mouse event position into a design-space point - took `point` from
  `e.GetPosition(this)` (relative to the WHOLE surface control, toolbar row included) but only
  adjusted for the ScrollViewer's *scroll* offset, silently assuming `point` was already relative
  to the ScrollViewer (`scroller`) itself. `this`'s own origin sits above `scroller`'s by the
  shared toolbar's height (a fixed, non-scroll offset the formula never accounted for at all), so
  every mouse gesture on the WinUI/Uno canvas - not just resize - resolved to a design-space point
  shifted by that height. Confirmed by comparing `this`/`scroller`/`framePresenter` screen origins
  via a new diagnostic probe (`od.winui-designer.diagnose-screen-anchors`): the gap matched
  exactly. **Fixed** by translating the point into `scroller`'s coordinate space with
  `TranslatePoint` before applying scroll offset and running the viewport math - correct
  regardless of the toolbar's actual height, not a hardcoded constant. Verified live and via
  `WinUIXamlDesigner_ResizeDrag_SelectionAndHandleTrackRenderedElement`: `PrimaryButton` itself
  now gets the resized `Width`/`Height`, `RootStack` is untouched, and the exact-delta assertion
  passes (grew by the exact dragged distance).
  - **Fixed** - `WinUIXamlDesignerViewContent.RebuildOutline` cleared `SelectedElementName` on
    every edit. `DocumentOutlineControl.SetRoot` clears `Items` then re-selects the previous id
    via `SelectNodeById` - but under LibreWPF, `TreeView.SelectedItemChanged` for a freshly-added
    `TreeViewItem` doesn't always fire before `SetRoot` returns (its container isn't generated
    yet), while `Items.Clear()`'s own "nothing selected" `SelectedItemChanged` fires
    synchronously. That left a window where `OnOutlineSelectionChanged` had already nulled
    `SelectedElementName` from the clear, but the matching re-selection event was still queued -
    so `od.winui-designer.properties-pad.edit` reported `selectedName: null` immediately after a
    successful edit, even though the edit itself correctly targeted the still-selected element.
    Fixed by restoring `SelectedElementName` directly in `RebuildOutline` rather than trusting
    that event ordering. Verified live and via `WinUIDesigner_PropertiesPadEdit_UpdatesSourceAndRender`
    (green).
  - **Fixed** - `WinUIXamlHost.QueryElementScreenBounds` (used by every DevFlow action/test that
    drives a synthetic click/drag by element name, including the click-selection test below)
    computed the wrong screen point, via two compounding mistakes: (1) it ran `QueryElementBounds`'
    result through `PointToScreen` on `this` (`WinUIXamlHost`, the outer `ContentControl`) rather
    than the actual surface control or its scroll viewport - `this` sits ~26px above the surface
    control's own origin, which itself sits ~32px above the innermost rendered-frame element's
    origin (three different, non-interchangeable screen anchors, measured via
    `od.winui-designer.diagnose-screen-anchors`); (2) `QueryElementBounds`/`nodesByName` report
    element positions in DESIGN-space (verified: the exact same source the resize-drag's own
    `dragStartRect` uses, which live diagnostics showed printing design-local coordinates like
    `(0, 20, 89, 33)` for `PrimaryButton`), not surface-local pixels as an earlier, incorrect
    comment claimed - so the conversion needs the full design-to-surface viewport transform
    (`UnoDesignSurfaceControl.DesignToSurfacePoint`), not a bare `PointToScreen`. Fixed by routing
    through `IWinUIXamlDesignView.DesignToScreenPoint` → `DesignToSurfacePoint` →
    `scroller.PointToScreen` - the same pair `SurfaceGeometry()` itself already used correctly.
    Verified live: synthetic clicks driven from this probe's numbers went from never reaching the
    design surface's mouse handlers at all (`lastPick` stuck at `"no click yet"`) to reliably
    landing on and selecting the correct element on the first attempt.
  - **Now passing** - `WinUIDesigner_ClickOnDesignSurface_SelectsSourceElementInPropertiesPad`,
    which was still failing under `dotnet test` even after the first (incomplete) coordinate fix
    above, now passes reliably through the real test harness once the `QueryElementBounds`
    design-space fix (not just the anchor fix) was in place - confirming the click failure and
    the resize wrong-element bug shared the same underlying coordinate-conversion family, not two
    unrelated issues.

  Full regression check after all of the above: `WinUIXamlDesigner_ResizeDrag_...`,
  `WinUIDesigner_ClickOnDesignSurface_...`, `WinUIDesigner_PropertiesPadEdit_...`, and
  `WinUIDesigner_DragToolboxItemOntoDesignSurface_...` all green individually; full suite run
  pending as of this writing.

### P4 — TypeScript/JavaScript addin (rebuilt, not migrated)

13. **TS/JS support via the existing LSP infrastructure.** The legacy `TypeScriptBinding`
    (SharpDevelop 5.x, `src/AddIns/BackendBindings/TypeScript/`) and `ICSharpCode.Scripting`
    are dead: they depend on `Noesis.Javascript` (x86 V8 bridge) whose DLLs are gone from
    `Libraries/`, package a 2014-era `typescriptServices.js`, and are v4.5/x86/old-style
    csproj — they must be dropped from `SharpDevelop.sln` rather than migrated. The MonoDevelop
    port (`mrward/typescript-addin`) is the same code on V8.NET; neither is a reference.
    **No new LSP infrastructure is needed** — OpenDevelop already has a full LSP client stack
    (`ICSharpCode.SharpDevelop.LanguageServices.Lsp` in
    `src/Main/Base/Project/Src/LanguageServices/`: `LanguageServiceRegistry`,
    `LspServiceManager` per-workspace-root service caching, `LspLanguageService`,
    `LspCodeCompletionBinding`). `LspServerRegistry.CreateDefault()` already registers
    `.ts/.tsx/.js/.jsx` → `typescript-language-server --stdio`
    (src/Main/Base/Project/Src/LanguageServices/Lsp/LspServerRegistry.cs:107-111), and the
    F# addin demonstrates the addin-side pattern — `RegisterFSharpLanguageServiceCommand`
    is a 5-line `registry.RegisterExtension(".fs", LspServiceManager.GetService)`.
    **Decision (2026-08-18): use the TypeScript 7 Go language server, preview build.** TS 7.0
    is GA (2026-07-08; `typescript` npm package, `tsc --lsp --stdio` is the LSP entry point)
    and the `@typescript/native-preview` npm package ships current nightlies with the same
    `--lsp --stdio` surface; the native binary needs no Node runtime. The language service is
    "nearly all features implemented" and the 7.1 iteration plan (Beta 2026-09-09, RC
    2026-10-20, Stable 2026-11-10) is API stabilization — not LSP feature work — so an LSP
    client is not exposed to the gap. Swap the `.ts/.tsx/.js/.jsx` launch spec in
    `LspServerRegistry.CreateDefault()` (LspServerRegistry.cs:107-111) from
    `typescript-language-server --stdio` to the TS 7 binary: command = the npm-installed
    `tsgo`/`tsc` binary path (`@typescript/native-preview` or `typescript`), arguments =
    `--lsp --stdio`, languageId stays `typescript`. Same for `.js`/`.jsx` (languageId
    `javascript`). Validate the documented preview gaps (string-literal completion, signature
    help in edge cases) against the sample workspace before treating them as regressions; pin
    the npm version in a lockfile so a nightly cannot move underneath the IDE. Keep the F#
    pattern as the addin shell.
    **Acceptance:** `.ts`/`.tsx`/`.js`/`.jsx` open in AvalonEdit with highlighting,
    completion, go-to-definition, find-references, rename, and diagnostics through the LSP
    client; legacy `TypeScriptBinding`/`Scripting` removed from the solution.

### Done (2026-08-20)

- **Shared `GridlineOverlay`** (`Designer.Presentation`) — gridlines drawn as plain `Line`
  shapes because tiled `DrawingBrush` doesn't render under LibreWPF-on-macOS; adopted by both
  WPF and WinUI/Uno surfaces (see `designer-gridlines-bug.md`).
- **Shared `SnapGuideCalculator`** (`Designer.Presentation`) — pure geometry for drag-move
  alignment snapping, relocated from Uno's `ApplySnap` so WPF/WinForms can share it.
- **Shared `SelectionAdornerLayer.SetSecondarySelection`** — dashed secondary multi-select
  outlines, relocated from Uno's `secondaryBoxes` pattern for cross-backend multi-select.
- **Shared `DesignerChildHost`** (`Designer.Server`) — the child-side connect-back/JsonRpc/
  wait-for-shutdown bootstrap, extracted from the WinForms and WPF child `Program.cs` files;
  Uno keeps its own dispatcher pump.
- **Shared `SharedToolbox` pad engine** (`Base/Project/Src/Gui/Pads/SharedToolbox.cs`) — one
  ListBox + grouping/drag state machine with per-scope filtering, replacing the duplicated
  WPF/WinForms `WpfToolbox` and WinUI `WinUIXamlToolbox` state machines; each keeps a thin
  facade. WinForms routes through `SharedToolboxAccess` so a pure WinForms session's pad still
  shows content.
- **Design-theme combo in the shared toolbar** — the Light/Dark toggle button became a combo
  that lists the themes a project actually carries: WinUI/Uno hoists the app's
  `ResourceDictionary.ThemeDictionaries` keys (`AppResourceBuilder.GetThemeNames`), WPF
  enumerates embedded `themes/*.xaml` (`ResolveThemes`), WinForms hides it (no theme concept).
  `DesignerCanvas` gained `DesignTheme`/`SetDesignThemes`/`IsDarkTheme` and
  `ThemeRequested` now carries the theme name (`EventHandler<string>`).
- **Show-names toolbar toggle** — `DesignerCanvas.ShowNames`/`IsShowingNames` (default on);
  toggling hides the control-name label above the selection outline on all three backends.
- **WinForms canvas margin + selection-render fix** — `CanvasMargin = 24` (matching WPF's
  padding) so the root handles are reachable and the `EdgePattern` shows around the form;
  selection adorners track the frame under the new coordinates.
- **Uno selection/guide offset fix** — overlay geometry now maps through a canvas-local
  viewport (`CanvasLocalViewport`, margin folded into the pan) instead of double-applying
  origin+pan.
- **`.Designer.cs`/`.Designer.vb` no longer open a design view** — both secondary display
  bindings reject `*.Designer.*` in `CanAttachTo`; the design view attaches only to the
  primary partial.

## Priority

1. P0-1 UI-tree bounds offset (~69 px) — unblocks `DoubleClickEventRow`; then the full
   three-designer integration regression (P1-4).
2. ~~P0-2 event-binding navigation decision~~ — done, keep the jump (see above).
3. P1-3 `design/hit-test` version parity.
4. P2 `DesignViewport.BaseOrigin`, zoom state machine + gridlines in the shell (fixes the
   WinForms zoom bugs as a side effect), `FrameCodec`/client boilerplate/child DTO policy.
5. P3 `ResizeDragTestBase` + DevFlow plumbing.
6. P4 TypeScript/JavaScript addin rebuilt on the TypeScript 7 Go LSP (drop the dead
   `TypeScriptBinding`/`Scripting` from the solution first).

---

# Part V — Acceptance gates and references

## Acceptance gates

- The three real project types open the correct adapter and render, select, edit, save, and
  recover through the **same** `IDesignHostClient`.
- No DTO contains a WPF/WinUI/WinForms/runtime CLR object or a `System.Type`.
- Every mutation carries session/document/generation/base-version; a stale request is rejected
  and cannot overwrite newer source.
- A crash/timeout/rebuild recovers solely from host-owned state; project assemblies never
  load into OpenDevelop (WPF's `AddProjectDlls` IDE-side reflection is the one open violation).
- Selection authority is in the child; the host never runs a second selection model.
- Frame traffic is bounded and backpressured; no unbounded frame queue exists.
- Tests cover open/edit/flush/save races, stale versions, invalid XAML recovery, crash/restart,
  and simultaneous projects with incompatible runtimes.

## References

- [`winforms-designer.md`](winforms-designer.md), [`wpf-designer.md`](wpf-designer.md),
  [`winui-designer.md`](winui-designer.md), [`xaml-services.md`](xaml-services.md).
- Existing code: `FormsDesigner/*`, `WpfDesign/*`, `WinUIXamlDesigner/*`,
  `src/Main/Designer/Designer.Remote/`, `src/Main/Designer/Designer.Presentation/`,
  `src/Main/ICSharpCode.SharpDevelop.Widgets/Project/DesignerCanvas.cs`.
- StreamJsonRpc: https://github.com/microsoft/vs-streamjsonrpc
