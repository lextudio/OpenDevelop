# Common Designer Out-of-Process Protocol (DDP)

This technote is the home for the unified, runtime-neutral architecture that the three OpenDevelop
visual designers — WinForms, WPF, and WinUI/Uno — are built on. It covers three things:

1. **The architecture**: what an out-of-process (OOP) visual designer is, why OpenDevelop
   runs the real runtime objects in a separate child process, and the wire contract (DDP)
   that every backend speaks to its child.
2. **The three implementations**: how WinForms, WPF, and WinUI/Uno each realize that
   architecture today — their processes, files, capabilities, and known limits.
3. **A feature matrix**: which designer feature exists in which framework, to what degree,
   and what technical constraint explains the difference.

Per-runtime details (engine internals, packaging, runtime selection, deeper known gaps) stay
in the dedicated technotes:

- [`winforms-designer.md`](winforms-designer.md) — the most complete OOP implementation;
- [`wpf-designer.md`](wpf-designer.md) — the WPF designer, cut over to OOP on 2026-08-17/18;
- [`winui-designer.md`](winui-designer.md) — the WinUI/Uno out-of-process host, the retired
  ProGPU in-process profile, and the native WinUI (Windows App SDK) planned adapter;
- [`xaml-services.md`](xaml-services.md) — the cross-designer roadmap, framework detection and
  the shared IDE-level contracts.

The protocol described here is a **target contract**. No implementation must be rewritten to
match it in one step; each backend converges by extracting and renaming its existing RPC
surface. The WinForms host (`FormsDesigner/Host/`), the Uno host
(`WinUIXamlDesigner.UnoHost/`), and the WPF surface host (`WpfDesign.SurfaceHost/`) are the
three data points this contract generalizes.

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
`DocumentOutlineControl`, `IDesignHostClient`), treating each backend as an adapter.

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
| `design/theme` | host → child | switch Light/Dark theme and re-render |
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
- **Deliberately not present**: a separate handshake/close/render verb. The handshake is owned by
  `DesignerHostProcessClient` (shared base, run during `StartAsync`); closing a document is
  disposing the client in the one-document-per-child model in use today; rendering is not a
  separate call on either backend — frames come back inside `DesignerSessionState.Render`.
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

# Part II — The three implementations

All three backends converge on the same stack:

| Layer | Shared project | WinForms | WPF | WinUI/Uno |
|---|---|---|---|---|
| Protocol DTOs + process lifecycle | `src/Main/Designer/Designer.Remote/` | `FormsDesignerHostClient : DesignerHostProcessClient, IDesignHostClient` | `WpfSurfaceHostClient : DesignerHostProcessClient, IDesignHostClient` | `UnoDesignClient : DesignerHostProcessClient, IDesignHostClient` |
| Geometry/rendering helpers | `src/Main/Designer/Designer.Presentation/` (`DesignViewport`, `DesignFramePresenter`, `SelectionAdornerLayer`) | used | used | used |
| Canvas shell (toolbar/edge/theme) | `ICSharpCode.SharpDevelop.Widgets/Project/DesignerCanvas.cs` | `RemoteFormsDesignerControl : DesignerCanvas` | `WpfSurfaceDesignerControl : DesignerCanvas` | `UnoDesignSurfaceControl : DesignerCanvas` |
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
  DesignerHostService  — RPC target: session/version/flush + 13 discrete design/* RPCs
  SnapshotDesignerLoader + DesignSurface (LibreWinForms) — the real controls
  Program — token/auth, dotnet exec with the project's runtimeconfig/deps
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
  WpfSurfaceHostService — RPC target: open/update/flush + mutations + App.xaml merge
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
- `ScrollViewer`-based zoom/pan: Ctrl+wheel zoom-at-cursor, space/middle-button pan, scrollbars;
  design↔surface math = `DesignViewport` ± scroller offsets (`ToDesignPoint`/`DesignToSurfacePoint`).
- Frame: BGRA8 deflate+base64, decoded with `BitmapSource.Create` (WIC avoidance on macOS).

### WinUI/Uno-only features

- **Zoom/pan** with `MinZoom 0.1 / MaxZoom 16`, zoom combo sync (Fit / 25%–400%).
- **Gridlines**: 20-design-unit grid, tile-brushed at scale.
- **Snap alignment guides**: orange guide lines from `ApplySnap` (align left/center/right,
  top/middle/bottom), `SetSnapGuides` draws them.
- **Grid row/column guide resize**: select a Grid, drag its row/column separators, commit
  `RowDefinitions`/`ColumnDefinitions` source edits (`GridGuideDragCommitted`).
- **Inline text editor**: double-click TextBlock/TextBox/Button → in-surface text edit
  (font scaled by viewport scale), Enter/focus-loss commits, Esc cancels; writes Text/Content
  through the incremental render path.
- **Design-theme toggle**: Light/Dark re-render via `design/theme`; the button label reads the
  theme the *next* click switches to.
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
| `design/theme` (design Light/Dark re-render) | ✗ (toolbar button inert) | ✗ (toolbar button hidden) | ✓ |
| `app/resources` (App.xaml/merged dictionaries) | ✗ (not applicable) | ~ (App.xaml merged into root Resources, child-side) | ✓ |
| Capability negotiation (optional interfaces feature-detected) | ✓ (Reset/DefaultEvent/Layout) | ~ (core only; SetEvent gap should become a capability) | ✓ (Theme/Export/AppResources) |
| Crash/restart recovery | ✓ (disconnected overlay + Restart) | ✓ (crash/restart covered by tests) | ✓ (lifecycle probes + status) |
| Safe mode (project code disabled) | ✓ (protocol-level) | planned (same protocol field) | ✓ (protocol-level) |

## Canvas shell & presentation (shared)

| Feature | WinForms | WPF | WinUI/Uno |
|---|---|---|---|
| Shared `DesignerCanvas` shell + toolbar | ✓ | ✓ | ✓ |
| Zoom combo (100% default, VS behavior) | ✓ (**fixed 2026-08-18**: `RebuildViewport` no longer short-circuited by the frame-sequence guard; hit-test/marquee/toolbox-drop now divide by `viewport.Scale`; guides/UIA bounds use `DesignToSurface`) | ✓ (Stretch.Fill fix landed) | ✓ |
| Fit | ✓ | ✓ | ✓ |
| Gridlines toggle | ~ (button visible but inert — no capability) | ✓ | ✓ |
| Light/Dark design-theme toggle | ✗ (inert) | ✗ (hidden) | ✓ |
| Design-size (device) preset combo | ✗ (hidden — not a WinForms concept) | ✗ (hidden) | ✓ |
| Edge pattern around the design bitmap | ✓ | ✓ | ✓ |
| Toolbar follows IDE theme (dark toolbar, light text) | ✓ | ✓ | ✓ |
| Shared `DesignViewport` coordinate math | ✓ | ✓ | ✓ |
| Shared `DesignFramePresenter` | ✓ PNG/WIC | ✓ BGRA/WIC-free | ✓ BGRA/WIC-free |
| Shared `SelectionAdornerLayer` | ✓ (single "se" handle visual, no label, recolor for locked) | ✓ (8 handles + label) | ✓ (8 handles + label) |
| Frame sequence backpressure (stale frames dropped) | ✓ | ✓ | ✓ |

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
| Toolbox: child-side reflection catalog | ✓ | ~ (child builds DTOs; IDE still feeds project dlls — Phase 4) | ✓ |
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
| Child-process RPC test suite | ✓ (`FormsDesignerHostClientTests`) | ✓ 22/22 (`WpfSurfaceHostRpcTests`) | ✓ 3/3 (`UnoDesignHostRpcTests`) |
| `od.<x>-designer.surface-geometry` probe | ✓ (Frame=whole bitmap, Selection=element outline, Handle=derived corner, Element=element — shared `DesignerSurfaceGeometry` record, 2026-08-18) | ✓ (same shared shape; `frame` = whole bitmap since 2026-08-18, was `frame==element`) | ✓ (same shared shape) |
| Resize-drag integration tests | ✓ | ✓ (4-attempt retry loop — known flake) | ✓ |
| `query-element-screen-bounds` / `query-toolbox-item-bounds` | ✗ | ✓ | ✓ |
| Outline probes (`outline-status`/`outline-select`) | ✓ (direct on shared control) | ✓ (from DDP tree) | ✓ (child count + names) |
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
4. **Gridlines brush trio**: `CreateGridBrush`/`UpdateGridBrush`/`SetGridlines` +
   `GridCellSize = 20` are duplicated verbatim between `WpfSurfaceDesignerControl` and
   `UnoDesignSurfaceControl` (same gray `Color.FromRgb(0x80,0x80,0x80)`, same `TileMode.Tile`).
   The shell already owns `ShowGrid`/`GridRequested` — a `DesignGridlinesOverlay` in
   Designer.Presentation can be driven straight from it.
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

## Priority

1. Contract drift fixes (Part IV "Contract drift") — correctness of probes and tests.
2. `DesignViewport.BaseOrigin` + `SurfaceGeometry` record — removes the largest verbatim copies.
3. Zoom state machine + gridlines overlay into the shell (fixes the WinForms zoom bugs as a
   side effect).
4. `FrameCodec`, client boilerplate, child DTO sharing.
5. `ResizeDragTestBase` + DevFlow plumbing.

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