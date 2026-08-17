# Common Designer Out-of-Process Protocol (DDP)

This technote is the home for the unified, runtime-neutral protocol that the three OpenDevelop
visual designers — WinForms, WPF, and WinUI/Uno — speak to their out-of-process design hosts.
It defines the wire contract, the identity/versioning rules, the DTO shapes, and the lifecycle
rules once, so OpenDevelop owns a single designer canvas implementation and each runtime backend
is only a pluggable adapter. The per-runtime details (engine internals, packaging, runtime
selection, known gaps) stay in their dedicated technotes:

- [`winforms-designer.md`](winforms-designer.md) — the most complete OOP implementation today;
- [`wpf-designer.md`](wpf-designer.md) — the WPF designer, currently in-process, with the
  surface-isolation architecture decision (2026-08-16);
- [`winui-designer.md`](winui-designer.md) — the WinUI/Uno out-of-process host, the ProGPU
  in-process profile, and the native WinUI (Windows App SDK) planned adapter;
- [`xaml-services.md`](xaml-services.md) — the cross-designer roadmap, framework detection and
  the shared IDE-level contracts.

The protocol described here is a **target contract**. No implementation must be rewritten to
match it in one step; each backend converges by extracting and renaming its existing RPC
surface. The WinForms host (`FormsDesigner/Remote/FormsDesignerProtocol.cs` +
`FormsDesignerHostClient.cs`), the Uno host (`WinUIXamlDesigner.UnoHost/DesignProtocol.cs` +
`DesignHost.cs`), and the WPF isolation plan (wpf-designer.md §"Out-of-process / Surface
Isolation decision") are the three data points this contract generalizes.

## Why a common protocol

All three designers independently arrived at the same architecture:

| Aspect | WinForms | WinUI/Uno | WPF (planned) |
|---|---|---|---|
| Runtime objects in IDE process | removed | removed | to be removed |
| Transport | StreamJsonRpc, loopback TCP, auth token | StreamJsonRpc, loopback TCP | StreamJsonRpc, auth token (adopted) |
| Document authority | parent-owned, versioned snapshots | parent-owned XAML buffer | parent-owned XAML buffer |
| Surface | PNG frame + hit-test | PNG/BGRA frame + hit-test | image projection or child HWND (spike) |
| Stable identity | component names | `x:Name` + tree path | `(session, generation, item ID)` |
| Child-owned gesture/selection | yes | yes (child hit-test) | yes (adopted) |
| Toolbox catalog | child-side discovery | child-side reflection catalog | child-side discovery (planned) |

The consequence is that the shell-side work — document tabs, the design surface presenter,
selection/outline/properties/toolbox pads, undo, save, crash recovery — is almost identical
regardless of which runtime renders the surface. Today that shell work is copied per backend
with per-backend DTO shapes and per-backend client classes. A single contract lets OpenDevelop
own that canvas once and treat the backends as adapters behind one client.

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
- The in-process ProGPU profile — it is a legitimate backend, but the common canvas only
  requires the adapter to implement the contract; an adapter may be in-process.
- HWND/child-window presentation — the contract is presentation-neutral; frames are the
  baseline, and a native presenter, if adopted, is one presenter implementation.
- A host-side design-tool extension SDK (`.designtools.dll`-style extensibility).
- Security sandboxing of project code — isolation is reliability, not a trust boundary.

## Terminology

- **Host** (OpenDevelop side): owns buffers, dirty state, save, the pads, and the presenter.
- **Child / design host / surface**: the process that owns the real runtime objects, the
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

Transport is StreamJsonRpc (JSON-RPC 2.0) over loopback TCP, exactly as both shipped OOP hosts
already do. A future shared-memory or named-pipe transport is allowed only behind the same
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
   diagnostic.
5. The contract is additive: unknown optional fields are ignored; unknown enum values are
   rejected with both sides' supported ranges in the error.

Every request after `initialize` carries an envelope (see §Identity and versioning). The child
target may be a plain class with `[JsonRpcMethod]` names; the host side is the `DesignHostClient`
class described below.

## Session and lifecycle methods

| Method | Direction | Purpose |
|---|---|---|
| `initialize` | child → host | handshake (token, protocol/runtime version, PID, capabilities) |
| `session/open` | host → child | open a document from a snapshot; returns `SessionState` |
| `session/update` | host → child | deliver a new snapshot (source edit, external change) |
| `session/flush` | host → child | commit current state back to host as an edit set |
| `session/close` | host → child | close the current document (release runtime objects) |
| `session/restart` | host → child | reset the child to a clean state (used after rebuild/`generation` bumps) |
| `design/command` | host → child | execute a named design command (see §Commands) |
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
  Format: "PNG" | "BGRA8"           // baseline: PNG; BGRA8 + shared memory is a later op
  Data: string                      // base64 (compressed when large; see notes)
  RenderMs: double
}
```

- Frames are produced on demand (`design/render`), not streamed continuously. The host drops
  stale sequences and applies backpressure; one slow client must not queue unbounded frames.
- The viewport (`design/render` carries `{ Width, Height, Dpi }`) is host-owned; the child
  measures/arranges at that viewport and returns the frame plus a fresh element tree.
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
- The host forwards raw input; it does not interpret gestures. (The WinForms adapter already
  forwards pointer events; the WinUI adapter maps picks through hit-test; the WPF isolation
  plan calls for the same input DTO layer.)

## Toolbox, outline, diagnostics

- **Toolbox catalog**: `initialize`/`GetCapabilities` returns a neutral catalog:
  `ToolboxItemInfo { Name, DisplayName, Category, Template, XamlNamespace, TypeName }`. The
  child builds it by reflecting the *loaded* runtime assemblies (the project's own, once the
  child runs under the project deps). The host never inspects project assemblies.
- A toolbox drop is `design/add-element { ParentId, ToolboxItem, X, Y, BaseVersion }`; the
  child materializes and returns the new `SessionState`.
- **Outline**: the element tree is the outline model; the same tree backs the Outline pad.
  Reparent/delete/rename go through `design/command` or the dedicated methods, all versioned.
- **Diagnostics**: `Diagnostic { Severity, Message, Line, Column }` accompany every
  `SessionState` and arrive as notifications. The host routes them to the Error List /
  Message View (line-navigable) when available, and to the surface status control otherwise.

## Commands and undo

Stable command IDs (`Undo`, `Redo`, `Cut`, `Copy`, `Paste`, `Delete`, `SelectAll`,
`BringToFront`, `SendToBack`, `Align*`, `Size*`, `Spacing*`, …) execute child-side through
`design/command { CommandId, Selection, BaseVersion }`:

- Undo/Redo are child-authoritative during visual editing; the result synchronizes source to
  the host (dirty state follows an *accepted* document change).
- A source reload (`session/update`) establishes a designer-history boundary.
- The host maps its standard Edit/Format commands to the common command IDs; an adapter maps
  IDs it does not support to `UnsupportedCommand` and the host disables the corresponding
  pad items.

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
  child (on Windows, a Job Object with `KILL_ON_JOB_CLOSE` is hardening after basic lifecycle
  works).
- Restart reconstructs state solely from host-owned source and project context — never from
  hidden child state.

## The host-side adapter seam

OpenDevelop-side code depends only on:

```text
IDesignHostClient {
  Task<HostHandshake> InitializeAsync(token, version, ct)
  Task<SessionState> OpenAsync(snapshot, ct)
  Task<SessionState> UpdateAsync(snapshot, ct)
  Task<DesignerEditSet> FlushAsync(baseVersion, ct)
  Task CloseAsync(ct)
  Task<SessionState> SetPropertyAsync(id, property, value, baseVersion, ct)
  Task<SessionState> ResetPropertyAsync(id, property, baseVersion, ct)
  Task<SessionState> SetEventAsync(id, evt, handler, baseVersion, ct)
  Task<HitTestResult> HitTestAsync(x, y, ct)
  Task<SessionState> AddElementAsync(parentId, item, x, y, baseVersion, ct)
  Task<SessionState> SetBoundsAsync(id, x, y, w, h, baseVersion, ct)
  Task<SessionState> DeleteElementsAsync(ids, baseVersion, ct)
  Task<SessionState> ApplyLayoutAsync(commandId, ids, baseVersion, ct)
  Task<SessionState> RenameAsync(id, newName, baseVersion, ct)
  Task<SessionState> SetThemeAsync(theme, ct)
  Task<RenderFrame> RenderAsync(width, height, dpi, ct)
  Task<string> ExportPngAsync(path, ct)
  Task<AppResourcesResult> SetAppResourcesAsync(xaml, ct)
  Task PingAsync(ct)
  Task ShutdownAsync()
  event EventHandler HostExited
  string ChildLog { get; }
}
```

**As shipped (2026-08-16)** the seam is implemented in
`src/Main/Designer/Designer.Remote/IDesignHostClient.cs`, with these deliberate differences from
the sketch above:

- **Split into a core interface plus optional capability interfaces.** Only what every backend
  must speak stays on `IDesignHostClient`. `ResetPropertyAsync`, `ApplyLayoutAsync`/`SetZOrderAsync`
  and default-event activation moved to `IDesignHostPropertyReset` / `IDesignHostLayout` /
  `IDesignHostDefaultEvent` (WinForms implements these; a markup runtime has no defaults model or
  absolute-position layout commands to back them). `SetThemeAsync`, `ExportPngAsync` and
  `SetAppResourcesAsync` moved to `IDesignHostTheme` / `IDesignHostExport` /
  `IDesignHostAppResources` (WinUI/Uno implements these). The host feature-detects and disables
  the matching UI, which is what DDP's "unsupported command" rule asks for.
- **`baseVersion` leads rather than trails** every mutation's parameter list, so the envelope
  reads consistently across methods.
- **`AddElementAsync` also takes `proposedName`** — a CLR-type backend must be told the new
  component's name, whereas a markup backend derives it from the item template.
- **`HitTestAsync` takes `baseVersion`** (the sketch omitted it), matching every other operation.
- **No `InitializeAsync`/`CloseAsync`/`RenderAsync`.** The handshake is owned by
  `DesignerHostProcessClient` (shared base, run during `StartAsync`); closing a document is
  disposing the client in the one-document-per-child model in use today; and rendering is not a
  separate verb on either backend — frames come back inside `DesignerSessionState.Render`. Should
  multi-document-per-child or on-demand rendering land later, these are the three to add back.

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

## Relationship to the existing implementations

| Current implementation | Status vs. DDP |
|---|---|
| `src/Main/Designer/Designer.Remote/` (new) | The shared contract project: `DesignerProtocol.cs` (unified DTOs), `DesignerHostProcessClient.cs` (shared launch/auth/log/timeout/dispose lifecycle), `IDesignHostClient.cs` (host-side seam). net10.0, UI-neutral, referenced by both designer addins. |
| `FormsDesigner/Remote/FormsDesignerProtocol.cs` + `FormsDesignerHostClient.cs` | Already implements the session/version/flush/auth/timeout core. DDP renames and generalizes these DTOs (e.g. `DesignerSessionState` → `SessionState`, adds `SessionId`/`DocumentId`/`Generation`, adds `design/command` and `design/theme`). |
| `WinUIXamlDesigner.UnoHost/DesignProtocol.cs` + `DesignHost.cs` | Implements capabilities/load/layout/theme/hit-test/app-resources/render. DDP adds versioned operations, flush, commands, property/event methods. |
| `WinUIXamlDesigner.UnoDesignHost/DesignProtocol.cs` | The newer Uno host; same delta. |
| WPF (`WpfDesign.AddIn`) | Currently fully in-process. The wpf-designer.md isolation decision already mandates DDP's rules (no target types over RPC, child-owned selection, host-owned buffers, versioned ops, generation identity, restart-over-ALC). Its Phase 0-6 plan maps onto DDP §Document/§Model/§Surface/§Failure. |

The DDP is deliberately a superset. Each backend converges by mapping its existing methods
onto the DDP names and filling the missing pieces (version envelope, flush, commands) rather
than by rewriting its engine.

## Convergence status (2026-08-16)

The shared `Designer.Remote` project is in place and both shipped OOP backends have been
migrated onto it, keeping their wire shapes untouched (the DTOs are a superset, so neither
child process changed its JSON output):

| Item | WinForms | WinUI/Uno |
|---|---|---|
| Shared DTOs (`Designer.Remote/DesignerProtocol.cs`) | `FormsDesignerProtocol.cs` deleted; the addin now uses the shared `DesignerSessionState`/`DesignerComponentInfo`/`DesignerRenderFrame`/… types | Local `DesignProtocol.cs` deleted; the IDE-side files keep their old type names via `using` aliases to the shared types |
| Process lifecycle (`DesignerHostProcessClient`) | `FormsDesignerHostClient : DesignerHostProcessClient, IDesignHostClient`; launch/token/pump/timeout/dispose now inherited | `UnoDesignClient : DesignerHostProcessClient, IDesignHostClient`; same inherited lifecycle |
| Authentication | Already token-authenticated; unchanged | Child (`UnoHost/Program.cs`) now parses `--token` and validates it plus the protocol version in `initialize` (which also returns capabilities in the same round trip) |
| `ping` | `[JsonRpcMethod("ping")]` added to `DesignerHostService` | `ping` endpoint added to the Uno child |
| DDP document methods (open/update/flush/commands) | Already implemented (`session/open`, `session/update`, `session/flush`, `design/*`) | `session/open`/`session/update`/`session/flush`/`design/set-property`/`design/set-event`/`design/add-element`/`design/set-bounds`/`design/delete-elements`/`design/rename` all exist on the child and IDE-side client (2026-08-16); `design/load` stays registered but is now unused by the IDE client — `UnoDesignRuntimeHost` calls `session/open` on first render and `session/update` thereafter. All six mutation RPCs are now wired to real IDE call sites (see step 1 below); `editor` (the local `XDocument`) remains the undo/dirty/save source of truth in every path, with the discrete RPC only choosing how the render is refreshed |
| Envelope (`SessionId`/`DocumentId`/`Generation`) | Added (2026-08-16): `DesignerHostProcessClient` mints a `SessionId` and echoes/validates it at handshake; `FormsDesignerHostClient` mints a per-client `DocumentId`; both flow through `DesignerDocumentSnapshot`/`DesignerSessionState`/`DesignerEditSet`/`HostHandshake`. All remaining per-mutation RPCs (`design/hit-test`, `design/set-property`, `design/reset-property`, `design/rename`, `design/set-event`, `design/activate-default-event`, `design/add-element`, `design/set-bounds`, `design/delete-elements`, `design/set-z-order`, `design/apply-layout`) were retrofitted with the envelope in a later pass | `design/hit-test` retrofitted with the envelope in the same later pass, matching the other mutation RPCs already added on 2026-08-16 |
| Host-side seam (`IDesignHostClient`) | **Unified (2026-08-16)**: the interface now carries the full DDP surface (`OpenAsync`/`UpdateAsync`/`FlushAsync` + `SetProperty`/`SetEvent`/`AddElement`/`SetBounds`/`DeleteElements`/`Rename`/`HitTest`), not just lifecycle. `FormsDesignerHostClient` implements it plus `IDesignHostPropertyReset`/`IDesignHostDefaultEvent`/`IDesignHostLayout` | `UnoDesignClient` implements the same core interface plus `IDesignHostTheme`/`IDesignHostExport`/`IDesignHostAppResources`. IDE-side code can now drive either backend through one contract |
| Canvas | `RemoteFormsDesignerControl` (per-backend) | `UnoDesignSurfaceControl` (per-backend) |
| Shared presentation helpers | `DesignViewport` + `DesignFramePresenter` + `SelectionAdornerLayer` (`ICSharpCode.Designer.Presentation`) | same three types; each backend keeps its own frame decode and gesture code |

Deliberate deviation from the canonical method list: **no generic `design/command` dispatcher
exists on either backend.** WinForms already used discrete named RPCs
(`design/rename`, `design/add-element`, `design/set-z-order`, …) before this
convergence pass, and WinUI's new methods followed that precedent rather than introducing a
second, incompatible mutation-dispatch style. `design/command` in the method table above should
be read as "whatever discrete named RPCs a backend needs," not as a literal generic verb.

Signature conflicts resolved when unifying the seam (2026-08-16), each toward the superset so
neither backend lost expressiveness:

| Conflict | Was | Unified as |
|---|---|---|
| Bounds coordinates | WinForms `int`, WinUI `double` | `double`; WinForms rounds on its own side before the wire |
| Delete | WinForms single `componentName`, WinUI `string[]` | `string[]`; WinForms loops one RPC per name against the same base version (verified safe: its child's delete never bumps the document version) |
| Add element | WinForms `(controlType, componentName)`, WinUI `itemXaml` | `DesignerToolboxItemInfo item` + `proposedName`; WinForms reads `item.TypeName`, WinUI materializes `item.Template` |
| Open/update | WinForms `DesignerDocumentSnapshot`, WinUI raw `(xaml, width, height, dpi)` | `DesignerDocumentSnapshot`; WinUI extracts the primary file's text and takes the viewport from a separate non-RPC `SetViewport(...)`, keeping presentation state out of the document model (a wpf-designer.md red line) |
| Hit-test | WinForms `(version, int x, int y)`, WinUI `(double x, double y)` | `(long baseVersion, double x, double y)`; the Uno child validates session/document identity but not per-call version, so it ignores the value |
| Rename | `RenameComponentAsync` vs `RenameAsync` | `RenameAsync` |

### Where convergence stops: protocol yes, presentation no

This is an architectural rule, not an unfinished task. The contract covers the **protocol** —
session/document lifecycle, the element mutations, the DTO shapes, the identity/versioning
envelope — and the host-side seam that expresses it (`IDesignHostClient`). It deliberately does
**not** dictate how a backend's canvas control draws or behaves:

- **Gesture/input models are legitimately per-backend.** `RemoteFormsDesignerControl` drives
  selection through WPF `Thumb` + bubbling mouse events; `UnoDesignSurfaceControl` uses
  `Preview` (tunneling) events and manual double-click detection because its `ScrollViewer`
  (needed for zoom/pan) swallows bubbling events under LibreWPF, and `ClickCount` isn't
  populated there. Both are correct for their runtime. Forcing one model on the other would be
  a rewrite of live input handling with no protocol benefit.
- **Runtime-specific chrome stays local**: zoom/pan viewport, Grid row/column guides, snap
  guides, size presets, theme toggle and the inline text editor are WinUI/Uno concerns; tab-order
  overlay, per-component lock state, marquee rubber-band select and the UIA automation-peer tree
  are WinForms concerns. Neither set belongs in a shared canvas.
- **What IS shared** lives in `ICSharpCode.Designer.Presentation` and is limited to pure,
  runtime-neutral geometry/rendering helpers both backends were independently duplicating:
  `DesignViewport` (design↔surface coordinate math), `DesignFramePresenter` (the frame `Image`
  element + its sizing) and `SelectionAdornerLayer` (selection outline, resize handles, label
  placement and handle hit-testing). Each backend still decodes its own frame bytes (PNG via
  WIC for WinForms; raw BGRA via `BitmapSource.Create` for WinUI/Uno, a deliberate
  WIC-avoidance workaround on macOS) and still owns its own gesture code.

The practical payoff of the shared pieces on macOS: LibreWinForms, LibreWPF, ProGPU WinUI and
Uno Platform designs all present through the same coordinate/frame/adorner code, so a geometry
or DPI-scaling bug is fixed once rather than four times — without pretending four different
input stacks are one.

Remaining convergence steps, in order:

1. ~~Wire the remaining four RPCs (`design/set-event`, `design/add-element`,
   `design/delete-elements`, `design/rename`)~~ — done (2026-08-16). `design/set-property`/
   `design/set-bounds` were wired first; `WinUIXamlDesignerViewContent.cs`'s
   `ApplyDocumentChange()` gained an optional `Action<string> incrementalRender` — `editor`
   (the `XDocument`-based undo/dirty/save source of truth) is still mutated first as before, and
   the callback only chooses whether the resulting render push goes out as a discrete DDP edit
   (via a new `IWinUIXamlIncrementalRender` capability on `WinUIXamlHost`/`UnoDesignRuntimeHost`,
   which falls back to a full `LoadXaml` reload itself on any rejection/exception) or the old
   full-document `session/update`. `design/set-event` (Properties-pad handler-name edits) and
   `design/delete-elements` (`DeleteElement`) are wired the same way; `design/add-element` is
   wired only when the drop container has a resolvable `x:Name` (falls back to full reload for
   an unnamed/root container, since the item XAML must already carry the locally-resolved name
   to avoid the remote parse disagreeing with `editor`'s own naming); `design/rename` has no
   existing "rename an already-named element" call site in the IDE today, so it's landed as a
   ready-to-use capability only — no new rename UI was invented, that's a feature addition, not
   a wiring task. Insert/paste/wrap-in-container/reparent and group drag/grid-guide-resize stay
   on the full-reload path for the same naming/restructuring-safety reason.
2. ~~Retrofit the SessionId/DocumentId envelope onto WinForms' and WinUI's older per-mutation
   RPCs (hit-test, add-control/add-element, set-bounds, delete, rename, apply-layout,
   set-z-order)~~ — done.
3. ~~Rename WinForms' method names to DDP's canonical spelling (`design/rename-component` →
   `design/rename`, `design/add-control` → `design/add-element`, `design/delete-component` →
   `design/delete-elements`)~~ — done; only the wire strings changed, the C# method names
   (`RenameComponent`/`AddControl`/`DeleteComponent` and their `*Async` client counterparts)
   are unchanged.
4. Extract the shared canvas (`DesignerCanvas` + frame presenter + selection/outline
   adorners) from `RemoteFormsDesignerControl`/`UnoDesignSurfaceControl`, then delete the
   per-backend canvas classes — **two of three layers done (2026-08-16)**, in a new
   `ICSharpCode.Designer.Presentation` project (`src/Main/Designer/Designer.Presentation/`):
   - **`DesignViewport`** (done): design size + scale + origin + pan → design↔surface
     coordinate conversion, extracted verbatim from `UnoDesignSurfaceControl`'s
     `EffectiveScale()`/`ViewportParams()`/`ToDesignPoint()`/`DesignToSurfacePoint()` formulas —
     same numbers, just relocated, including the degenerate-input fallback shape. WinUI/Uno
     computes it via `DesignViewport.Fit(...)`; WinForms (always 1:1, no zoom/pan) via
     `DesignViewport.Identity(...)`, wired through `PositionAdorners`/`UpdateDesignGuides`.
   - **`DesignFramePresenter`** (done): owns the one `Image` element each control displays its
     decoded frame in, with `SetSource(ImageSource)` (backend keeps its own PNG-via-`BitmapImage`
     vs raw-BGRA-via-`BitmapSource.Create` decode - genuinely different codecs, not unified) and
     `Resize(DesignViewport)` (replaces the `image.Width/Height = ... * scale` math both
     backends previously duplicated inline). Both `RemoteFormsDesignerControl` and
     `UnoDesignSurfaceControl` now hold a `DesignFramePresenter` field instead of a raw `Image`
     field; every other reference to the old field (hit-test point conversion,
     `PointToScreen`, `IsVisible`) now goes through `framePresenter.Visual`. This became safe to
     extract only once `DesignViewport` existed — the earlier blocker was that frame size and
     selection/handle/guide geometry shared the same `pixelWidth`/`pixelHeight` state; centralizing
     that math in `DesignViewport` decoupled frame placement from everything else that reads it.
   - **`SelectionAdornerLayer`** (done, 2026-08-16, scoped narrowly): the selection outline +
     up to 8 named resize handles + optional label are now a shared type
     (`ShowSelection`/`ClearSelection`/`HandleAt`, all formulas relocated verbatim from
     `UnoDesignSurfaceControl`'s `LayoutSelection`/`HandlePositions`/`HandleAt`). Deliberately
     **excludes the mouse-gesture state machine that drives it** — WinUI/Uno keeps its own
     `Preview`-event-based drag handling (a documented LibreWPF/`ScrollViewer` workaround) and
     WinForms keeps its own `Thumb`-based drag handling (bubbling events, no `ScrollViewer` to
     swallow them) completely untouched; both still call the shared layer's methods instead of
     touching private rectangle/handle fields, but neither backend's *input* pipeline changed.
     WinForms' `moveThumb`/`resizeThumb` stay real, separate interactive `Thumb` elements outside
     the shared layer (handles there are non-interactive by design, matching WinUI where the
     surrounding control's own mouse handlers do the hit-testing) — only the non-interactive
     selection rectangle moved into the shared type, plus a small `SelectionStroke` setter added
     to reproduce WinForms' locked-component recolor (DodgerBlue → DarkOrange), a purely visual
     property untouched by the gesture-code exclusion.
   - **Gesture/input state machine, marquee-select, grid/snap guides, toolbar chrome, zoom/pan
     input, context menu, inline text editor, UIA automation peer tree**: intentionally NOT
     shared — see "Where convergence stops: protocol yes, presentation no" above. These are
     per-backend presentation concerns, not protocol gaps, and this item is complete as scoped
     rather than blocked. (Should someone later want them merged anyway, it would be a rewrite
     of live mouse-input handling needing interactive WPF/LibreWPF verification — but the
     contract does not ask for it.)

   Both layers landed this round are verified by code review (formulas/wiring unchanged in
   effect - `DesignViewport.Identity`'s case is a numeric no-op, `DesignViewport.Fit`'s formulas
   are a direct relocation) plus both existing test suites staying green
   (`FormsDesignerHostClientTests`, `UnoDesignHostRpcTests`, 6/6). Neither confirms the frame or
   selection outline actually renders correctly on screen - that still needs a live GUI session.
5. Implement the WPF surface host behind the same contract (wpf-designer.md phases).

Done (2026-08-16): a WinUI/Uno host test project now exists —
`WinUIXamlDesigner.UnoHost.Tests` (plus a WPF-free `WinUIXamlDesigner.UnoDesignHost.Remote`
project that links `UnoDesignClient.cs` by source, mirroring `FormsDesigner.Remote.csproj`'s
pattern) — spawning a real headless Uno child and covering handshake, `session/open`/`update`/
`flush`, all six mutation RPCs (including not-found/bad-name rejection paths), and independent
client lifetimes. 3/3 passing; run with:
`dotnet test src/AddIns/DisplayBindings/WinUIXamlDesigner/WinUIXamlDesigner.UnoHost.Tests --filter-query "/*/*/UnoDesignHostRpcTests/*"`.
Not yet covered: the incremental-render accept/reject/exception-fallback paths inside
`UnoDesignRuntimeHost` itself (that class is WPF-hosted and untested by this suite, which talks
to the child directly) — a real desktop smoke test is still the only way to confirm those.

## Phased adoption

1. **Contract** — publish this document as the contract; extract the shared DTO project
   (`Designer.Protocol`) that the host and every child reference (UI/runtime neutral).
2. **WinForms first** — the most complete OOP path is the cheapest to conform: rename/extend
   its protocol to DDP, add `SessionId`/`DocumentId`/`Generation` and `design/command`.
   Prove the shared `IDesignHostClient` against it.
3. **WinUI/Uno** — add the version envelope and the mutation methods to the Uno host; keep
   the existing render path behind `design/render`.
4. **WPF** — implement the DDP child per the isolation plan's phases; the existing
   in-process engine stays behind an in-process adapter until parity.
5. **Unified canvas** — move the presenter/pads to the shared client; delete the per-backend
   client classes.

## Acceptance gates

- The three real project types open the correct adapter and render, select, edit, save, and
  recover through the **same** `IDesignHostClient`.
- No DTO contains a WPF/WinUI/WinForms/runtime CLR object or a `System.Type`.
- Every mutation carries session/document/generation/base-version; a stale request is rejected
  and cannot overwrite newer source.
- A crash/timeout/rebuild recovers solely from host-owned state; project assemblies never
  load into OpenDevelop.
- Selection authority is in the child; the host never runs a second selection model.
- Frame traffic is bounded and backpressured; no unbounded frame queue exists.
- Tests cover open/edit/flush/save races, stale versions, invalid XAML recovery, crash/restart,
  and simultaneous projects with incompatible runtimes.

## References

- [`winforms-designer.md`](winforms-designer.md), [`wpf-designer.md`](wpf-designer.md),
  [`winui-designer.md`](winui-designer.md), [`xaml-services.md`](xaml-services.md).
- Existing code: `FormsDesigner/Remote/*`, `FormsDesigner/Host/*`,
  `WinUIXamlDesigner.UnoHost/*`, `WinUIXamlDesigner.UnoDesignHost/*`, `WpfDesign.AddIn/*`.
- StreamJsonRpc: https://github.com/microsoft/vs-streamjsonrpc
