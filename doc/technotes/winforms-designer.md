# WinForms Designer

This technote is the dedicated home for the WinForms designer (`FormsDesigner`): current state,
the Roslyn `BasicDesignerLoader` architecture, the round-trip pipeline, and known gaps. The
cross-designer roadmap (WinForms + WPF + WinUI together), framework detection, provider
contracts, phases, and the test matrix live in [`xaml-services.md`](xaml-services.md).

The workbench side now uses `Designer.Shell.DesignerSelectionController` as the common authority
for the remote element forest, stable-ID selection, and Properties adapter lifetime. Its shared
`DocumentOutlineControl` is only the WPF presentation of that state. This is the same shell path
used by WPF, WinUI, GTK 4, and MewUI; WinForms-specific code remains responsible for Roslyn
round-tripping, runtime hosting, toolbox metadata, and property RPC.
Undo, Redo and multi-selection Delete are now registered with the common
`DesignerCommandController`; the WinForms backend retains its multi-file snapshot stacks and
remote component mutation rules.

Current status: the in-process C# backend is complete (CodeDOM-free Roslyn loader,
`.Designer.cs` round-trip, legacy migration, shared Toolbox Pad, real drag-drop tests). The
out-of-process C# path is now the default: a UI-framework-neutral protocol/client assembly and a real
authenticated LibreWinForms child process now implement handshake, versioned document snapshots,
stale-update rejection, flush and bounded shutdown. The child creates and owns a real
`DesignSurface`/`System.Windows.Forms.Form` on macOS. A child-local Roslyn snapshot loader now
materializes standard controls, properties, bounds and parent/child relationships from
`InitializeComponent`; updates rebuild the child component graph and return a framework-neutral
component snapshot. The child
produces a portable-painted PNG frame (or the common deflate-BGRA frame when GPU readback is
unavailable) and performs child-side coordinate hit-testing back to
stable component names. A parent WPF adapter now presents that frame, forwards pointer hit tests,
and exposes remote state through DevFlow. The first child-owned edit path changes an existing
scalar property, refreshes the frame, rewrites its Roslyn assignment, and applies the
version-matched flush to the parent document during save. The legacy in-process path is retained
only as an emergency fallback with `OPENDEVELOP_WINFORMS_OOP=0`. Standard and project custom
WinForms toolbox items can now also be dragged onto the
remote root surface: the parent forwards type metadata and coordinates, while the child creates
the component and generates its field/initialization source. Nested drops are supported.
Move/resize, direction-key nudging and delete now also execute in the child, refresh the remote
frame, and rewrite/remove the corresponding Roslyn statements. The parent overlay provides a
selection rectangle, drag-to-move, a bottom-right resize handle, and Delete-key removal without
hosting any project control in the IDE process. Design-time outlines and component-name labels
keep empty or same-background LibreWinForms containers visible even when their portable paint is
visually indistinguishable from the white canvas. Selecting a remote component now also supplies
the Properties Pad with a parent-owned proxy for name/type, Text, X/Y and Width/Height; writes
are converted back into versioned property/bounds RPC calls. Successful child edits are now
immediately flushed into the parent-owned in-memory documents, so a later child failure cannot
lose unsaved designer work. Unexpected process exit keeps the last frame visible while the common
shared-host recovery coordinator rebinds every open compatible document and reconstructs it from
those parent documents; the view then replaces the frame and Outline from the restored state. RPC
operations have a bounded timeout; a hung operation terminates the child process tree and enters
the same recovery path. Project file, target framework and output assembly
metadata travel with each snapshot, and the child loads project/custom-control assemblies in a
collectible dependency-resolving load context while keeping LibreWinForms/Drawing contracts in
the host context. The VB backend is implemented in the same
out-of-process child (see "VB.NET WinForms support" below); only the legacy in-process
fallback remains C#-only.

## Current Baseline

| Component | Location | Current Status |
|---|---|---|
| WinForms Designer | `src/AddIns/DisplayBindings/FormsDesigner/` | The out-of-process LibreWinForms host is the default C# path on macOS. It owns the real `DesignSurface`, project controls and dependencies; renders to PNG or the shared deflate-BGRA frame; supports selection, nested Toolbox drops, Properties, events, move/resize/delete, Undo/Redo, resources, save, timeout/crash recovery and restart. The VB backend runs in the same child for `.vb` files. |

## Actual State of WinForms Round-Trip and Toolbox

The earlier claim that this was "not yet restored" came from an outdated exclusion comment in `FormsDesigner.csproj`, not from the current implementation. The actual pipeline is:

- `CSharpBinding.FormsDesigner.RoslynFormsDesignerSecondaryDisplayBinding` uses Roslyn to decide whether a C# partial class is designable;
- `RoslynDesignerLoader` reads the main file and `.Designer.cs`, converts a supported subset of `InitializeComponent` into a CodeDOM object graph, and rewrites methods and added fields on save;
- `FormsDesignerViewContent.ToolsContent` exposes the shared `WpfToolbox`; the latter shows WinForms categories and creates controls through a real `System.Drawing.Design.IToolboxService` and a WPF/WinForms drag bridge;
- `DragToolboxItem_OntoWinFormsDesignSurface_AddsControlToForm` verifies end-to-end drag-drop, visible sizing, persistence into `.Designer.cs`, and tool-selection reset.

This audit also added startup preloading for the FormsDesigner DevFlow actions, so that lazy AddIn loading does not lag behind DevFlow's one-shot action discovery and cause test 404s.

## Roslyn `BasicDesignerLoader` Architecture

The old implementation was a "Roslyn parser + CodeDOM serializer bridge"; it has been replaced. OpenDevelop's goal goes further than Microsoft 17.5's "Roslyn code generator": the active WinForms backend no longer treats CodeDOM as an intermediate model. The new `RoslynFormsDesignerLoader` derives directly from `BasicDesignerLoader`, not `CodeDomDesignerLoader`; on the read side it projects the project `Document`'s syntax/semantic models into a component graph, and on the save side it generates a C# syntax tree from the component graph. The `this.` prefixes, fully qualified types, and explicit delegates produced by the old CodeDOM generator must be accepted as compatible input, but the first designer save migrates to the Roslyn style; it will not fall back to CodeDOM serialization for compatibility with old files.

The implementation uses the project `Document`/`Workspace`, compilation, Simplifier, Formatter, and AnalyzerConfigOptions, and replaces only annotated fields and `InitializeComponent`. Resource reading/writing was also extracted from `ProjectResourcesComponentCodeDomSerializer` / `ProjectResourcesMemberCodeDomSerializer` into a syntax-tree-independent `RoslynDesignerResourceModel`, which the Roslyn backend uses to handle `ComponentResourceManager.ApplyResources`. Still pending are wiring the full project Workspace / `.editorconfig`, the VB backend, and the async/parallel, `nameof`, and high-DPI work from Microsoft's newer generator.

The core backend does not use `System.CodeDom` as its document model; the integration test also asserts that the runtime loader is not a `CodeDomDesignerLoader`. The old loader is not a runtime fallback. For compatibility with the third-party WinForms control ecosystem, explicitly declared custom `CodeDomSerializer`s are allowed to run inside a `LegacyCodeDomSerializerAdapter` boundary; their short-lived output is immediately converted into Roslyn statements and discarded, with the final write-back still done by the Roslyn formatter / project document. Return shapes that cannot be converted block saving and report the serializer/control type — properties are never silently dropped.

## Known Gaps

- Native-surface optimization beyond the portable frame adapter.
- Complex binary property editors and third-party legacy serializer edge cases beyond the
  string-convertible/resource paths covered by the child protocol.
- Full project `Workspace` / `.editorconfig` wiring for the Roslyn loader.
- Async/parallel generation, `nameof`, and high-DPI work from Microsoft's newer generator.

## Out-of-process host decision (2026-08-15)

**Decision: the WinForms designer must run project controls and designer services in a child
process.** The current in-process implementation is a migration baseline, not the shipping
architecture. No project output or third-party control assembly may be loaded into OpenDevelop's
process.

[`winui-designer.md`](winui-designer.md#out-of-process-host-decision-2026-08-14) made
out-of-process hosting the *required* architecture for real-project WinUI/Uno support, because
`ProGPU.WinUI` is a from-scratch reimplementation of `Microsoft.UI.Xaml` whose types cannot
coexist in one Roslyn compilation with the real Uno.WinUI SDK's types of the same name.

WinForms does not have WinUI's same-name type-identity forcing function:
`DesignerViewContent`/`WpfToolbox` currently host the real `System.Drawing` and
`System.Windows.Forms` types via `WindowsFormsHost` and LibreWinForms. It nevertheless has an
equally important product boundary: opening a form executes project and third-party designer
code. A crashing control, blocked UI thread, incompatible target runtime, static-state mutation,
or dependency conflict must not crash or contaminate the IDE. Microsoft's out-of-process
WinForms designer
([devblogs post](https://devblogs.microsoft.com/dotnet/custom-controls-for-winforms-out-of-process-designer/))
establishes the same isolation and target-runtime principles for Visual Studio. OpenDevelop will
reuse the runtime-neutral transport, lifecycle, timeout, and surface-transfer patterns already
implemented for the Uno host rather than inventing an in-process exception list.

### Process and ownership boundary

- OpenDevelop owns source buffers, dirty state, Undo/Redo, commands, Toolbox/Properties/Outline
  pads, and the authoritative save transaction.
- A per-project-runtime child owns `DesignSurface`, `IDesignerHost`, component instances, custom
  designers and serializers, `ITypeResolutionService`, and all project/control assemblies.
- The child is launched with the designed project's runtime/dependency context. Host selection is
  explicit by target framework and platform; the IDE must not load a project assembly to decide.
- Contracts contain descriptors, stable component handles, property values, diagnostics, source
  edits, input events, and pixels/native-surface metadata only. No `Control`, `Component`,
  `Type`, `Image`, service-provider, or designer object crosses the boundary.
- The existing Roslyn loader/resource model moves behind the child boundary. The parent sends
  versioned document snapshots and applies a returned, version-matched edit set atomically;
  stale results are rejected and reloaded.

The initial presentation path is a captured BGRA surface with explicit viewport, DPI, and frame
sequence metadata. Pointer, keyboard, focus, drag/drop, accessibility, and selection requests are
forwarded over RPC. A native child-window path may be added where supported, but cannot become a
requirement for non-Windows LibreWinForms hosts. Toolbox items are metadata in the parent and are
materialized only by the child.

### Failure and lifecycle rules

- RPC uses a private authenticated endpoint, a protocol/version handshake, request cancellation,
  bounded payloads, and per-operation timeouts. The child must never expose a general object
  invocation or arbitrary file API.
- A timeout, disconnect, or child crash leaves the last frame visible with a diagnostic, releases
  all pending calls, and offers a clean restart. It must not exit or block OpenDevelop.
- Closing the document/project terminates the child after a bounded graceful shutdown; leaked
  child processes are killed. Restart reconstructs state solely from parent-owned source and
  project context, never from hidden child state.
- Save succeeds only after the parent receives edits for its current document version, applies
  them through the normal undoable document path, and persists all participating files. A host
  failure cannot produce a partial `.Designer.cs`/`.resx` save.

### Delivery plan and acceptance

1. Extract a transport-neutral designer-session contract and adapt the current in-process loader
   behind it without changing round-trip output.
2. Add the child executable and launch it under the project's runtime/dependency graph; move all
   component creation, custom-control loading, and serialization into it.
3. Replace `WindowsFormsHost` ownership in the workbench with the remote frame/input adapter and
   reconnect Toolbox, Properties, Outline, selection, Undo/Redo, and save.
4. Remove the in-process project-control loading path after parity tests pass. A source-only/error
   view is the fallback when no compatible child runtime is available.

Completion requires existing load/edit/drag-drop/round-trip tests to pass through the child plus
tests for host crash, hung control timeout, restart, stale-response rejection, multi-file atomic
save, DPI/resize/input, custom control dependency conflicts, close cleanup, and simultaneous
projects targeting incompatible runtimes. Tests must assert that project and third-party control
assemblies are absent from OpenDevelop's process.

Implementation lives in `FormsDesigner/Remote` (the `net10.0`, UI-type-free client/contract),
`FormsDesigner/Host` (the deployed LibreWinForms child), and `FormsDesigner/Host.Tests`
(process-level protocol tests). The test executes on macOS and asserts that the child owns a real
`DesignSurface` whose root is `System.Windows.Forms.Form`, then loads a real Button from Roslyn
syntax and verifies its text, bounds and parent through RPC. The same process test verifies a
non-empty PNG frame, that a surface coordinate hits the Button by stable name, and that editing
its `Text` property changes both the live child component and the flushed `.Designer.cs`
snapshot. The test also creates a standard Label and verifies its generated field, construction,
location, and parent-add statements, then verifies bounds rewrites and component deletion.
Component snapshots now carry both parent-local and root-surface coordinates, so selection,
screen-bounds queries and move gestures work for nested controls while source writes remain local
to the parent. Toolbox drops hit-test the child surface and target supported containers such as
Panel and GroupBox. The process test builds a separate custom-control fixture, loads its
`FancyButton` only in the child collectible context, and asserts that the parent/test `AppDomain`
never sees that assembly. Component snapshots also carry browsable property descriptors as
runtime-neutral metadata; the Properties Pad dynamically exposes primitive and
string-convertible values, and the child writes new scalar, enum, Point, Size and Color
assignments through Roslyn. Event descriptors and bindings also cross the neutral contract; a
binding updates `.Designer.cs` and generates a missing handler in the primary partial file.
Binary `.resx` files travel as snapshot data and `ComponentResourceManager.ApplyResources` is
resolved inside the child without granting arbitrary filesystem access. The parent owns Undo/Redo
document snapshots and the WPF frame/input adapter. `FormsDesignerViewContent` selects this path
by default; set `OPENDEVELOP_WINFORMS_OOP=0` only to diagnose the legacy in-process fallback.
Transport awaits do not capture the WPF synchronization context, preventing the UI-thread startup
deadlock that otherwise occurs when the synchronous secondary-view lifecycle launches the child.
LibreWinForms controls whose native-style implementation reports
`SupportsPortablePainting=false` are rendered by a child-side standard-control theme renderer;
Button, TextBox, CheckBox, RadioButton, ComboBox, NumericUpDown, GroupBox, Panel, ListBox,
ProgressBar and Label therefore carry their normal background, border, text and glyph structure
in the PNG rather than appearing only as parent-side design outlines.
TabControl, TreeView, ListView, DataGridView, MenuStrip and ToolStrip also have dedicated
design-time renderers; the Host regression test asserts that adding a DataGridView changes the
actual PNG, not only the component metadata. Remote single-selection Copy/Cut/Paste/Delete are
wired to the IDE's standard clipboard commands. The parent retains only a neutral component
description, and Paste asks the child to create an offset, uniquely named component.
Bring to Front and Send to Back now execute in the child and persist through
`Controls.SetChildIndex`; remote Tab Order mode overlays each component's `TabIndex` on the
rendered design surface.
Shift/Ctrl-click remote multi-selection now drives the Format commands through one child-side
transaction: grid snapping, edge/center alignment, matching size/width/height, parent centering,
equal spacing, spacing increase/decrease, and concatenation all refresh the frame and persist
their resulting bounds into `.Designer.cs`.
Select All now targets the remote component snapshot. Lock Controls is implemented in the parent
adapter: locked controls retain selection and an orange design outline, but movement, resize,
direction-key nudging and Format operations are suppressed without loading a control into the IDE.
Copy/Cut/Paste/Delete now consume the full remote selection. Nested copied controls are recreated
parent-first with remapped unique names, while grouped paste or deletion records one parent-side
Undo snapshot; deleting a selected container does not issue duplicate child deletions.
Dragging or using direction keys now moves the remote selection as one child-side transaction.
Selected descendants of another selected container are excluded from the move request so their
effective position changes exactly once, and Shift-direction continues to use the 10-pixel step.
Dragging on empty canvas space now displays a translucent marquee and selects every intersecting
remote component from its root-surface bounds; Shift/Ctrl preserves and extends the prior selection.
Property descriptors now carry `ShouldSerializeValue` across the neutral contract. Group paste
restores changed string, Boolean, numeric, enum, Point, Size and Color values after recreating each
control, while deliberately skipping unsupported complex editors and structural bounds properties.
The remote Properties Pad `(Name)` field is editable. Renaming validates C# identifiers and
container uniqueness in the child, updates the site/control name, rewrites the generated field and
all `InitializeComponent` references through Roslyn, and remaps parent-side selection/lock handles.
Editing the root Form Width/Height now resizes its child-owned design `Size`, refreshes the PNG
viewport, and inserts or updates the corresponding `this.Size` Roslyn assignment. The portable
host uses `Size` because LibreWinForms' macOS `ClientSize` setter is not yet reliable.
Selecting the root Form now shows its design outline and bottom-right resize handle; dragging the
handle uses the same versioned bounds path, so visual resizing and Properties Pad resizing share
one Undo/source/render pipeline while the root remains non-movable.
Remote event descriptors are now editable in the Properties Pad under an Events category (shown
with a lightning prefix). Assigning a handler updates `.Designer.cs` and creates a missing method
in the primary partial class; clearing/resetting the value removes the event subscription while
leaving user method bodies intact.
Browsable remote properties now expose reset semantics from `PropertyDescriptor`: Reset asks the
child to call `ResetValue`, removes the matching Roslyn assignment, refreshes metadata/rendering,
and participates in the same parent-owned Undo history.
Double-clicking a remote control now activates its `DefaultEventAttribute` event. The child reuses
an existing binding or creates the conventional `<component>_<event>` handler through the same
Roslyn event pipeline, so the designer source, primary partial class and Undo history stay atomic;
the parent then navigates to the resulting handler in the primary source file.
The remote surface also implements keyboard hierarchy navigation: Escape selects the current
control's parent, while Tab and Shift+Tab cycle controls in `TabIndex` order without transferring
keyboard focus into the child process.
Render frames now carry a monotonically increasing sequence and an explicit DPI scale. The parent
drops stale frames and sizes the WPF image in device-independent units. Component snapshots forward
accessible name, description and role metadata, and property descriptors retain their display names
and descriptions for the Properties Pad. Snapshot validation now caps file count, path length and
aggregate payload size before design-time code is loaded. Process tests cover those limits, graceful
close cleanup and two independent designer hosts remaining isolated throughout their lifetimes.
The WPF adapter exposes a hierarchical virtual UI Automation tree for the root form and every
remote component, including automation id, semantic control type, help text, screen bounds, focus,
multi-selection and selection-item operations. Accessibility clients therefore interact with
individual designed controls without loading WinForms accessibility objects into the IDE process.
Complex string-convertible property edits are validated for Roslyn serialization before the live
component is mutated, preventing a failed serializer from splitting surface and source state.
`Padding`/`Margin` and `Font` now have explicit C# serializers and child-loader round-trip support.
Modern high-DPI initialization using `AutoScaleDimensions = new SizeF(...)` is parsed, editable
through the remote Properties Pad and serialized back with invariant floating-point literals;
`AutoScaleMode` continues through the enum path.
The child syntax evaluator accepts `nameof(...)` in designer expressions, including modern
`ApplyResources(control, nameof(control))` calls. Component-name assignments remain string
literals for LibreWinForms site-container compatibility, while Roslyn rename updates both forms.
Binary image/bitmap entries embedded in `.resx` are decoded only inside the child and resolved by
the common `(Image)resources.GetObject(...)` designer expression. Parent snapshots expose only a
`[binary]` property marker, never a live `Image`; the original resource bytes remain part of the
atomic multi-file flush.

## VB.NET WinForms support (2026-08-16)

`.vb` forms now design through the same out-of-process child as C#. The parent/child split is
asymmetric by design: the parent-side binding is a thin syntactic gate, while all VB parsing and
source rewriting happens child-side in `SnapshotDesignerLoader`/`DesignerHostService` using
`Microsoft.CodeAnalysis.VisualBasic` (added to the Host project).

### Parent side (`VbDesignerSecondaryDisplayBinding` / `VbDesignerLoaderProvider`)

- Registered in both `VBBinding.addin` and `FormsDesigner.addin` under
  `/SharpDevelop/Workbench/DisplayBindings` for `fileNamePattern="\.vb$"`; `VBBinding` declares a
  dependency on `ICSharpCode.FormsDesigner`.
- `CanAttachTo` is **syntactic only** — no semantic model or compilation. It parses the primary
  file plus its co-located `Foo.Designer.vb` (the classic VB split: the base type lives in
  `Foo.vb`, `InitializeComponent` in the designer file, so a single partial declaration rarely
  has both), groups `ClassBlockSyntax` declarations by name, and attaches when some partial has
  both a parameterless `Sub InitializeComponent` and a base type ending in `Form`, `UserControl`,
  or `Component` — mirroring `RoslynFormsDesignerSecondaryDisplayBinding`'s resolution.
- `VbDesignerLoaderProvider.CreateLoader` deliberately throws: there is **no in-process VB
  loader**, so the legacy `OPENDEVELOP_WINFORMS_OOP=0` fallback remains C#-only. The provider's
  `GetSourceFiles` locates the `.Designer.vb` companion for the round-trip.
- The protocol's `SnapshotData` gains a `Language` field (`"CSharp"`/`"VisualBasic"`), derived
  from the primary file extension by `DesignerViewContent`.

### Child side (`SnapshotDesignerLoader` / `DesignerHostService`)

- `PerformLoadVisualBasic` parses `InitializeComponent` (`Sub`, parameterless) from the designer
  file, executes the same statement subset as C# — `Me.X = New ...()` creations,
  `Me.X.Property = value` assignments, `Me.Controls.Add(Me.X)` — via VB-specific
  `AssignmentStatementSyntax`/`InvocationExpressionSyntax` handling, with `Me.` stripped before
  matching.
- Property writes rewrite VB assignments in place (`AssignmentStatementSyntax` in
  `InitializeComponent`) or append `Me.<component>.<prop> = <expression>` when absent, using a VB
  expression serializer for string/Boolean/numeric/enum/Point/Size/Color literals.
- Renaming validates with VB's own `SyntaxFacts.IsValidIdentifier`, then replaces
  `IdentifierToken`s in `InitializeComponent` and field declarations; `ThisQualifierRewriter` has
  a `MeQualifierRewriter` counterpart so the whole designer file is written in VB style.
- Event binding/creation and the other child-side edit operations are likewise VB-aware and land
  through the same versioned snapshot/RPC path as C#.

### Verification

`VbDesigner_OutOfProcess_RoundTripsEditsToDesignerFile` (integration test) opens the
`tests/fixtures/VbWinFormsFixture` (`Form1.vb` + `Form1.Designer.vb`, classic
`Me.button1 = New System.Windows.Forms.Button()` style), waits for the designer in its own
process, asserts the child owns a real `System.Windows.Forms.Form` root with `button1`, then
drives add-control/set-property/set-event/set-bounds through `od.forms-designer.*` and verifies
after save that the handler landed in `Form1.vb` and the generated statements in
`Form1.Designer.vb`.

## Recent alignment (2026-08-19/20)

- **`.Designer.cs` / `.Designer.vb` no longer open a design view (2026-08-19)**:
  `CSharpDesignerSecondaryDisplayBinding` and `VbDesignerSecondaryDisplayBinding` both reject
  `*.Designer.cs`/`*.Designer.vb` in `CanAttachTo`. The design view attaches only to the primary
  partial (`Foo.cs`/`Foo.vb`); opening the generated companion from the project browser stays a
  plain source view instead of spawning a second design view over the same form.
- **Canvas margin (2026-08-19)**: `RemoteFormsDesignerControl` gained `CanvasMargin = 24`
  (matching WPF's `CanvasPadding`), so the root component's handles are reachable and the shared
  toolbar's `EdgePattern` is visible around the form — the WinForms canvas previously had no
  border around the form while WPF/WinUI did. Fit/zoom inset the viewport and fold the margin
  back into the pan (`DesignViewport.Fit/Zoom(…, CanvasMargin, CanvasMargin)`), keeping the
  frame bitmap, guides and all `DesignToSurface`-based adorners aligned.
- **Selection-render fix (2026-08-19)**: selection adorner rendering was corrected to track the
  frame/selection under the new margin coordinates.
- **Shared Toolbox engine (2026-08-19/20)**: `WpfToolbox` (which serves WPF + WinForms) became a
  facade over the shared `SharedToolbox` pad engine (`Base/Project/Src/Gui/Pads/SharedToolbox.cs`);
  the WinForms view routes through `SharedToolboxAccess` so a pure WinForms session's Tools pad
  still shows content (the shared ListBox's "winforms" scope is seeded before the pad mounts it).
- **Show-names toolbar toggle (2026-08-20)**: `DesignerCanvas.ShowNames` (default on) toggles the
  component-name label on the selection outline, consistent with the other two designers.

## Designer overhaul (2026-09-03/04)

A single long push brought the out-of-process designer much closer to the real WinForms
designer: backend selection, smart tags, ToolStrip item insertion, the component tray, real
per-type icons, and a cluster of input-routing bugs. Everything below was derived by reading the
**actual** WinForms designer sources — the LibreWinForms fork at
`…/openavalon/LibreWPF/external/LibreWinForms/src/System.Windows.Forms.Design/src/System/Windows/Forms/Design/`
is a real fork of `dotnet/winforms`, so it is the authority for design-time behavior, and
`C:\Users\lextudio\source\repos\SharpDevelop-old` is the authority for what the original IDE did.
Prefer reading those over reasoning from memory: several rules below are counter-intuitive and
every earlier guess about them was wrong.

### Backend selection is by target framework

`FormsDesignerHostClient.ResolveBackend(useMicrosoftDesktopRuntime, runtimeOverride, targetFramework)`
now defaults to the **Microsoft** backend whenever `OperatingSystem.IsWindows()` and the TFM
carries a `-windows` platform suffix; an explicit `UseMicrosoftDesktopRuntime` property or the
`runtimeOverride` still wins. Before this, every ordinary `Microsoft.NET.Sdk` WinForms project
(which never sets that bespoke property) silently got the portable LibreWinForms host.

### Smart tags (`DesignerActionList`)

- `design/list-smart-tag-actions` reads `(host.GetDesigner(component) as ComponentDesigner)?.ActionLists`
  **directly**. It deliberately does *not* go through `DesignerActionService.GetComponentActions`:
  that service is never registered on a bare `DesignSurface` (the VS shell's own loader installs
  it), so it returns nothing here.
- `design/invoke-smart-tag-method` re-resolves the `DesignerActionMethodItem` by
  `(listIndex, itemIndex)` and invokes it inside a `host.CreateTransaction`.
- Property items round-trip through the **existing** `design/set-property` via the
  `PropertyOwnerElementId`/`MemberName` pair on `DesignerSmartTagActionInfo` — no new commit RPC.
- Microsoft backend only; LibreWinForms has no action-list support and returns `Accepted=false`
  rather than silently no-op'ing.

### ToolStrip item insertion

`design/add-toolstrip-item` creates a real sited `ToolStripItem` via `host.CreateComponent` and
appends to `.Items`/`.DropDownItems`, then rewrites `InitializeComponent` the way
`RewriteAddedControl` does. The per-strip type lists are copied verbatim from
`ToolStripDesignerUtils`' own `s_newItemTypesFor*` arrays (ToolStrip: Button, Label, SplitButton,
DropDownButton, Separator, ComboBox, TextBox, ProgressBar; StatusStrip: StatusLabel, ProgressBar,
DropDownButton, SplitButton; MenuStrip: MenuItem, ComboBox, TextBox) — do not invent your own
ordering or contents.

### Component tray

The tray is the icon+name strip below the surface. Two rules matter and they come from two
*different* places — conflating them is the mistake this went through twice:

- **Membership** is `DocumentDesigner.OnComponentAdded`, whose own comment reads *"If the
  component is a toolstrip or a top level form, we should add to the tray"*:
  ```csharp
  bool addControl = designer is ToolStripDesigner
      || designer is not ControlDesigner cd
      || (cd.Control is Form form && form.TopLevel);
  if (!addControl || !attributes.Contains(DesignTimeVisibleAttribute.Yes)) return;
  ```
  So **every MenuStrip/ToolStrip/StatusStrip gets a tray entry in addition to being laid out on
  the surface** (first clause), `ContextMenuStrip` and `PrintPreviewDialog` get one because their
  designers are `ComponentDesigner`s (second clause), and `ToolStripContainer` gets none (a
  `ControlDesigner` that is not a `ToolStripDesigner`). `ToolStripDesigner` is **internal**, so
  `DesignerHostService.IsToolStripDesigner` matches it by full name along the designer type's base
  chain — which also catches `BindingNavigatorDesigner`, as it must.
- **`ComponentTray.CanCreateComponentFromTool`** answers a *different* question (may a toolbox
  item be created by dropping it ONTO the tray) and excludes the strips. Do not use it for
  membership.

Protocol/UI: `DesignerComponentInfo.IsTrayComponent` carries the flag; `RemoteFormsDesignerControl`
hosts the tray as a **sibling of the zoomable `scroller`** inside `ContentHost` (a second Grid
row, `TrayHeight = 80` matching `ComponentTray._trayHeight`), with its own `ScrollViewer` — this
mirrors how the real designer hosts the tray through `ISplitWindowService.AddSplitWindow`, and is
why canvas zoom does not scale it. Canvas adorners are suppressed only for **tray-only**
components (`IsTrayComponent && Parent == ""`); a strip keeps its outline, thumbs, smart tag and
insert glyph because it also lives on the surface.

### Real per-type icons (Toolbox + tray)

`Base/Project/Src/Gui/Pads/WinFormsToolboxIconProvider.cs`. The parent process loads the
**LibreWinForms** `System.Windows.Forms` (identity `v0.1.0.0`), which carries **zero** manifest
resources, so `[ToolboxBitmap]`/`ToolboxBitmapAttribute.GetImageFromResource` can never produce an
icon here regardless of how the lookup is written. Icons therefore come from two sources, both
read as **resource-only PE reads** (`PEReader`/`MetadataReader`, never `Assembly.Load` — loading
Microsoft's `System.Windows.Forms` would collide with the identically named fork already in the
process):

1. The installed `Microsoft.WindowsDesktop.App\<highest version>\System.Windows.Forms.dll` — 199
   manifest resources, per-type entries named **exactly the full type name with no extension**
   (`System.Windows.Forms.Button`), payload a **Windows ICO** (magic `00 00 01 00`; Button is
   1150 bytes/16×16, ToolStripButton 52366 bytes/7 images/up to 64×64). The legacy
   `component.FullName + ".bmp"` lookup in `ComponentLibraryLoader.GetIcon` always missed purely
   because of that naming change.
2. Nine icons **shipped with OpenDevelop** at `Base/Project/Resources/WinFormsToolboxIcons/*.bmp`
   (embedded with `LogicalName="%(Filename)%(Extension)"`, so the resource name *is* the
   `<TypeFullName>.bmp` lookup key), for the components modern .NET dropped icons for entirely:
   DataSet, DataView, BackgroundWorker, EventLog, PerformanceCounter, Process, FileSystemWatcher,
   SerialPort, PrintDocument. This was verified exhaustively — scanning every manifest resource of
   every assembly in Microsoft.WindowsDesktop.App / Microsoft.NETCore.App / Microsoft.AspNetCore.App
   yields exactly one hit (`System.Windows.Forms.Timer`), and scanning all 10716 DLLs in the NuGet
   package cache yields **zero**. They exist only in the .NET Framework assemblies shipped with
   Windows (`System.dll`, `System.Data.dll`, `System.Drawing.dll`) under the legacy
   `<TypeFullName>.bmp` name, and were extracted from there.

`Decode` sniffs the magic bytes: ICO → `System.Drawing.Icon`; BMP → `new Bitmap` **plus
`MakeTransparent()`**, because those legacy bitmaps use the classic "bottom-left pixel is the
transparent colour" convention and otherwise render on an opaque block. Everything is normalized
to 16×16 and cached per process.

### Toolbox catalog

The Tools pad's WinForms entries are seeded by `WpfToolbox.AddWinFormsControls`, which used to
hardcode ten "popular" controls — so the strips, the dialogs, `Timer`/`ImageList` and the whole
Data/Components/Printing groups were simply absent, unlike the original SharpDevelop. It now walks
`WinFormsToolboxCatalog`, a 60-entry table in the same categories and order as the shipped
`data/options/SharpDevelopControlLibrary.sdcl`. Entries are **type-name strings resolved at
runtime**, not `typeof(...)`: the portable fork implements only 43 of the 53 WinForms types
(DateTimePicker, MonthCalendar, NotifyIcon, BindingSource, HScrollBar, DomainUpDown, HelpProvider,
BindingNavigator, PageSetupDialog, PrintPreviewControl are missing there) and several component
entries live in optional runtime assemblies, so a `typeof` reference would not even compile
against the fork. Unresolvable entries are skipped silently; live item count went 11 → 51.

Note `od.forms-designer.toolbox.filter` reports the **currently selected category's** count, not
the total — an easy way to misdiagnose the catalog as truncated.

### Input routing: the `handledEventsToo` cluster

Four separate reports ("clicking a control never selects it", "clicking Show Names moves the
canvas", "clicking a tray entry loses the selection", "clicking a scrollbar jumps selection to the
form") were all the same root cause chain:

1. The hosting `ScrollViewer` marks bubbling `MouseLeftButtonDown` handled on its way up, so the
   plain `+=` selection handler on `RemoteFormsDesignerControl` **never ran at all** —
   click-to-select on the canvas had never worked, and only the Document Outline could change the
   selection (which is why no test caught it: the only canvas-selection test used
   `od.forms-designer.outline-select`). The resize gesture had already worked around the same
   swallowing with a `Preview` handler. Fix: register Down/Move/Up via
   `AddHandler(..., handledEventsToo: true)`. Move/Up need it too — otherwise `marqueeSelecting`
   stays true forever after the first click that misses, blocking every later click.
2. `handledEventsToo` then also delivers presses consumed by *unrelated* chrome. For those,
   `e.GetPosition(framePresenter.Visual)` yields a nonsense point, a marquee starts, and a
   zero-size marquee ends by **selecting the root form and calling `Focus()`** — exactly the
   reported "focus jumps to the form". Fix: `IsOutsideDesignSurface` bails unless the press
   originated inside **`scrollContent`**. That boundary is deliberate and was narrowed twice: the
   toolbar sits outside `ContentHost`, the tray is a sibling of the scroller *inside*
   `ContentHost`, and the scrollbars belong to the ScrollViewer's *template* rather than its
   Content. The empty canvas margin around the form is part of `scrollContent`, so rubber-band
   selection there still works.

### Hit-test coordinate space

`design/hit-test` receives **surface (rendered-bitmap)** coordinates, the same space
`DesignerComponentInfo.SurfaceX/SurfaceY` report. On the Microsoft backend the bitmap is the whole
native window (`Form.DrawToBitmap` paints border + caption), so surface space and each Control's
client-space `Bounds` differ by the root form's non-client offset. `RootClientOffset` is now shared
between `SurfaceLocation` (which adds it) and `HitTest` (which subtracts it); without that, clicks
resolved to whatever control sat "offset pixels" above the real target. A press on the
caption/border falls back to selecting the form itself.

### Extensibility contract: match Visual Studio's out-of-process designer (2026-09-04)

**Decision: OpenDevelop targets exactly Visual Studio's out-of-process designer experience and
its author-facing contract. A control author who adapts their control per Microsoft's OOP
designer requirements gets OpenDevelop support with no additional work.** We do not invent a
second extensibility model, and we do not lower the bar below VS's.

Consequences, which resolve the "do we host real HWNDs?" question:

- Out-of-process stays mandatory (see the 2026-08-15 decision) — VS does the same, for the same
  isolation and target-runtime reasons.
- VS's OOP contract is itself **client/server split**: the author's designer logic runs in the
  server process, while the interactive UI is rendered by the client (the IDE). That means
  reimplementing in-canvas chrome — the "Type Here" cell, smart-tag panels, the component tray,
  the insert-item glyph — **in WPF on the client is the architecturally correct thing**, not a
  workaround for missing HWND hosting. It is the same division of labour VS's client performs.
- Therefore we deliberately do NOT reparent the child's real design-surface HWND into the WPF
  shell. That path (`SetParent` across processes + `HwndHost`) would buy in-canvas fidelity for
  legacy in-process designers at the cost of WPF airspace, canvas zoom, and cross-process
  input/focus complexity — and it is not how VS presents its OOP designer, so it would diverge
  from the experience we are matching.
- The long-term work item is server-side support for the SDK types VS's contract asks authors to
  build against (`Microsoft.WinForms.Designer.SDK`'s designer/editor proxies) rather than only
  `System.Windows.Forms.Design`'s in-process types. Our child already runs the project's own
  runtime with the real `System.Windows.Forms.Design`, so designers written against the
  in-process types load today; the SDK proxies are what make an author's *client-side* editor UI
  work, and are the piece to add when we grow past the built-in chrome.

### MenuStrip in-place editing

`ToolStripTemplateNode` is the whole in-place editing experience, and it branches per strip kind in
`SetupNewEditNode`:

| | MenuStrip / `ToolStripDropDownItem` | ToolStrip / StatusStrip / **ContextMenuStrip** |
|---|---|---|
| builder | `SetUpMenuTemplateNode` | `SetUpToolTemplateNode` |
| content | one `ToolStripLabel` (`_centerLabel`) reading `SR.ToolStripDesignerTemplateNodeEnterText` — "Type Here" | `ToolStripSplitButton` (`_addItemButton`), `DisplayStyle=Image` + built-in dropdown arrow |
| a11y role | `ComboBox` | `ButtonDropDown` |

So a **MenuStrip must not get the split-button insert glyph** — it gets an editable "Type Here"
cell. ContextMenuStrip does use the split button. Machinery to mirror:

- `EnterInSituEdit()` swaps `_centerLabel` out of `_miniToolStrip.Items` for a `_centerTextBox`
  (`ToolStripControlHost` over a `TemplateTextBox`), hooks `OnKeyUp`/`OnKeyDown`, `SelectAll()`s
  and focuses it; `ExitInSituEdit()` swaps back and resets the label text.
- `Commit(enterKeyPressed, tabKeyPressed)` → empty text rolls back; otherwise `CommitEditor` →
  `CommitTextToDesigner`, where: typing `-` in a dropdown creates a `ToolStripSeparator`; with no
  type explicitly picked the default is `ToolStripDesignerUtils.GetStandardItemTypes(component)[0]`;
  new items go through `ToolStripDesigner.AddNewItem(type, text, enterKeyPressed, tabKeyPressed)`
  (**Enter and Tab mean different things** — continue into the dropdown vs. move to the next
  sibling); renames go through `ToolStripItemDesigner.CommitEdit(...)`.
- `FocusEditor(item)` puts an **existing** item into edit (prefilled with its `Text`);
  `ToolStripItemDesigner.ShowEditNode(clicked)` is the double-click/F2 entry point, and each item
  owns its own `_editorNode`.
- `ShowDropDownMenu()` handles `_addItemButton == null` (i.e. MenuStrip) by popping the type menu
  at the mini-toolstrip's location — that is the hover "hot region" arrow on the Type Here cell,
  whose visual states live in `TemplateNodeSelectionState`
  (`MouseOverLabel`/`MouseOverHotRegion`/`HotRegionSelected`/…) driven by `MiniToolStripRenderer`.
- `ToolStripDesigner.AddNewTemplateNode` wraps the node in a `DesignerToolStripControlHost` and
  `ToolStrip.Items.Add`s it, i.e. the node is a **real last item** of the strip whose position the
  strip's own layout maintains (`OnItemAdded` re-appends it) — which is why our own insert glyph is
  anchored to the last real item rather than to the strip's right edge.
- IDE integration is the part with no WPF equivalent yet: `ToolStripInSituService`
  (`ISupportInSituService`: `IgnoreMessages`, `HandleKeyChar`, `GetEditWindow`) tells the shell
  "the keyboard is mine right now", and `ToolStripKeyboardHandlingService` takes over arrow
  keys/Enter/Esc/Tab (`MenuCommands.Key*`) while editing, restoring the previous commands on exit.

**Superseded by the finding below.** The plan that follows (client-drawn "Type Here") was built
and then removed: driving the child's real selection service turned out to produce the genuine
chrome, which is strictly more faithful.

### Selection forwarding makes the REAL chrome render (2026-09-05)

The decisive finding of this whole effort: **the child's design surface had no services and its
real `ISelectionService` was never told anything** (`new DesignSurface()` with no service
container, and selection lived only in the parent). Every piece of interactive strip/menu chrome
in WinForms is *selection-driven*, so none of it ever activated:

- `ToolStripDesigner.AddNewTemplateNode` already appends its template node (`_editorNode`, a
  `DesignerToolStripControlHost`) to `ToolStrip.Items` at load, but leaves it `Visible = false`
  until its strip is selected.
- `ToolStripMenuItemDesigner`, when its item is selected, calls `CreatetypeHereNode()`, sets
  **`MenuItem.DropDown.TopLevel = false`** and **`AutoClose = false`**, then `ShowDropDown()` -
  i.e. the dropdown is held open as a *control* and gets its own per-level "Type Here" node in
  `DropDown.Items`.

So `design/set-selection` now pushes the parent's selection into the child's real
`ISelectionService` and returns a freshly rendered state. With that one call, the genuine "Type
Here" cell, the split button, the expanded dropdown and the per-level "Type Here" all appear —
no client-side redrawing. The client-drawn `typeHereCell`/`toolStripInsertChevron` were therefore
switched off; they only double-drew on top of the real thing.

Two coordinate/rendering details this exposed:

- **The expanded dropdown is parented into the designer's adorner window, not the form**, so
  `Form.DrawToBitmap` never captured it (its geometry was reported correctly while its pixels
  were missing — `Application.DoEvents()`/`PerformLayout()` does not help, it is not a layout
  race). `PaintExpandedDropDowns` now walks the visible `ToolStripDropDown`s (outermost first, so
  nested submenus paint over their parent) and `DrawToBitmap`s each onto the frame.
- **`SurfaceLocation` had to change basis.** Summing `Location` up the parent chain cannot
  describe an adorner-hosted dropdown: the walk never reaches the root and adds unrelated
  ancestor offsets, which is why the dropdown items' outlines and name labels landed well below
  the dropdown that was actually drawn (`openToolStripMenuItem` reported `(407,484)` instead of
  `(384,438)`, directly under its `fileToolStripMenuItem` at `(384,417)`). It now measures
  against the root's **screen** origin, which is also what `PaintExpandedDropDowns` composites
  with — one basis, so reported geometry and painted pixels agree by construction.

### Popup overlays: each expanded dropdown is its own WPF surface (2026-09-05)

The guides/name-label overdraw noted above, plus the fact that the composited-into-the-root-frame
approach gave the client no way to route input to just the dropdown, led to a further
architecture change: **every expanded `ToolStripDropDown` is now captured and hosted as its own
independent surface**, not baked into the root bitmap.

- **Protocol**: `DesignerSessionState.Popups` (`List<DesignerPopupFrame>`) — one entry per visible
  dropdown, each an `OwnerElementId` (the owning `ToolStripDropDownItem`'s element id, `""` for a
  strip's own `ContextMenuStrip`), an `X`/`Y` in the same surface-coordinate basis as
  `DesignerComponentInfo.SurfaceX/Y`, and its own `DesignerRenderFrame` (own PNG, own size).
  `CapturePopupFrames`/`ExpandedDropDowns` in `DesignerHostService.cs` walk every strip's items
  breadth-first (outermost dropdown first, so a client that z-orders by list position still
  stacks nested submenus correctly) and `DrawToBitmap` each dropdown into its own bitmap - the
  root frame no longer has `PaintExpandedDropDowns` composited into it at all.
- **Client**: `RemoteFormsDesignerControl` keeps one real WPF `Image` per open popup
  (`popupOverlays`, keyed by `OwnerElementId` so the same overlay survives across frames rather
  than being torn down and rebuilt), added as a child of `adorners` — which is why no new
  click-suppression guard was needed: `IsAdornerSource` already treats anything under `adorners`
  as self-handling. Positioned/sized via the same `viewport.DesignToSurface`/`Scale` every other
  adorner uses (`PositionPopupOverlays`, called both when popups change and on every
  zoom/pan via `ApplyViewport`), so a popup tracks its owning strip correctly at any zoom level.
- **Hit-testing inside a popup is a SEPARATE RPC**, `design/hit-test-popup`
  (`DesignerHostService.HitTestPopupAndSelect`), because a dropdown's control tree is not
  reachable from the root form's `Controls` (it is parented into the designer's adorner window):
  the owner element id says which live `ToolStripDropDown` to test against, and since
  `ToolStripDropDown` IS-A `ToolStrip`, the EXISTING root hit-test walk (`FindDeepest`) already
  knows how to test its `Items` - it just needed a dropdown to start from instead of the form.
  A hit selects that item through the same real `ISelectionService` `SetSelection` uses.
- **Closing the loop back to the client's own selection state** needed one more field:
  `DesignerSessionState.PopupHitElementId`, valid only on `design/hit-test-popup`'s own response
  (mirrors the existing `CreatedElementId` pattern for `design/add-element`). Without it, the
  child's real selection correctly changes but the client's `SelectedComponentName` - tracked
  entirely client-side - has no way to learn what was hit, so the Properties pad/Outline/
  surface-geometry kept reporting the OLD selection even though clicking visibly worked.
- **A second `FindDeepest` gap this exposed**: its `control.Controls` walk had no
  container-membership filter (only the `ToolStrip.Items` walk did, from the earlier Outline-pad
  fix). `ToolStripTemplateNode.EnterInSituEdit`'s in-place-edit `TextBox` is hosted via a
  `DesignerToolStripControlHost`, which makes it a REAL child `Control` of the ToolStrip/dropdown
  it lives in - but it is never sited in the designer's own `IContainer`; it is UI, not a
  component. Clicking it hit-tested as that raw `TextBox` and got passed to
  `ISelectionService.SetSelectedComponents`, which real WinForms treated as "selection left this
  dropdown's ownership" and closed it - reported as "clicking Type Here makes the popup
  disappear". Fixed by applying the same container-membership filter to the `Controls` walk that
  the `Items` walk already had; a click that lands on unsited UI now simply hits nothing (safe
  no-op) instead of destabilizing the real selection service.
- **Also surfaced, fixed alongside**: move/resize thumbs (which exist only to drive
  `design/set-bounds`, itself only valid on a real `Control`) were still shown for a selected
  `ToolStripItem` (a menu item, a toolbar button - never a `Control`), so dragging one threw
  "Control not found" straight out of the child. `DesignerComponentInfo.IsControl` now reports
  this per component, and the client gates the thumbs on it.

**Remaining work on this path**: none of the originally-listed items remain (F2 rename, Delete,
and drag-to-reorder are all done - see their own sections below; double-click-generates-default-
event-handler needed no new code, next paragraph). Only the keyboard-ownership piece's own
services-reuse idea (arrow-key/Tab handling scoped to menu editing specifically) is still just an
idea, not yet needed for anything currently broken.

Double-click-generates-default-event-handler turned out to need NO new code at
all: `RemoteFormsDesignerControl`'s existing double-click path already resolves a ToolStripItem
through the same root `design/hit-test` (`FindDeepest`'s container-membership-filtered walk
already covers ToolStrip items, from the earlier popup-closing fix), and
`design/activate-default-event`'s server-side `ActivateDefaultEvent` was already fully generic -
it works off `TypeDescriptor.GetAttributes(component)[typeof(DefaultEventAttribute)]` and
`GetHost().Container.Components[elementId]` for ANY `IComponent`, never `as Control`, and
`ToolStripItem`'s own real `[DefaultEvent("Click")]` flows through unchanged. Confirmed with a
live DevFlow double-click on `toolStripButton1` in `tests/fixtures/ToolStripFixture`
(`toolStripButton1.Click += toolStripButton1_Click;` appeared in the Designer file and the empty
handler in the source file, `canUndo` flipped `true`) before adding
`ChildHost_ActivateDefaultEvent_WiresUpClickHandlerForToolStripItem` as the regression test - the
existing default-event coverage only exercised a plain `Control` (`button1`).

Reusing SharpDevelop's own designer services is the natural next step for the keyboard-ownership
piece (F2/Delete/arrow-key handling during menu editing) specifically - our
`FormsDesigner/Project/Src/Services/` files are near-identical copies of SharpDevelop's and are
only lightly IDE-coupled (1-8 references each), so they can be source-linked into the Host
projects behind a define, the same way `DesignerHostService.cs` is already shared between the two
hosts. Note `MenuCommandService.cs` exists in SharpDevelop's tree but **not** in ours, and it is
exactly what `ToolStripKeyboardHandlingService` needs (`MenuCommands.Key*` routing for
arrows/Enter/Tab/Delete during menu editing); it is 83 lines with a single IDE call
(`MenuService.ShowContextMenu`) to gate.

### Bug: "Unsupported ToolStripItem type" (fully-qualified vs. short type names)

`design/add-toolstrip-item`'s `ResolveToolStripItemType` matched only short names
(`"ToolStripMenuItem"`), but `DesignerComponentInfo.NewItemTypeNames` (and therefore every
`typeName` the client ever sends back, including from `PopupTypeHereEditor.Commit`) is populated
with fully-qualified names (`"System.Windows.Forms.ToolStripMenuItem"`) - the two were never
compared against the same shape anywhere except the one hardcoded `"System.Windows.Forms.
ToolStripSeparator"` literal check for the lone-`"-"` case. Every real add-item attempt threw
`NotSupportedException("Unsupported ToolStripItem type: System.Windows.Forms.ToolStripMenuItem")`.
Fixed by stripping the namespace (`ShortTypeName`, last `.`-separated segment) before the switch.

### A real WPF Type Here editor over the popup (2026-09-05)

The screenshot-based popup can't show a real blinking caret or accept keystrokes into the real
child's native `TextBox` (it's a picture). Rather than forward keys into the child, we draw our
OWN real WPF `TextBox` overlay directly on top of the template node's reported bounds and let it
capture real WPF keyboard input natively - no forwarding needed, and the client already owns the
insertion RPC (`design/add-toolstrip-item`) from the earlier strip-level Type Here work.

- **Protocol**: `DesignerPopupFrame.TypeHereBounds` (`DesignerRectangle?`), populated by
  `FindTemplateNodeBounds` in `DesignerHostService.cs` - finds the dropdown's item whose
  `GetType().Name` is `"DesignerToolStripControlHost"`/`"ToolStripControlHost"` (the real template
  node's in-place-edit host) and reports its `Bounds`, local to that popup, same basis as
  `DesignerPopupFrame.X`/`Y`.
- **Client**: `PopupTypeHereEditor` (one per popup that reports `TypeHereBounds`) bundles a
  placeholder `TextBlock` ("Type Here") and a real `TextBox`, swapped on click
  (`Begin()`)/commit/cancel. `Commit` mirrors `ToolStripTemplateNode.CommitTextToDesigner`: empty
  text cancels, a lone `"-"` becomes a separator when the owning dropdown's type list has one,
  otherwise the strip's own default new-item type; it resolves the real owning `ToolStrip` by
  walking `Parent` up from the immediate owner until it finds a component with `IsControl == true`
  (a `ToolStripItem` never is one), then raises the same `RemoteToolStripTypeHereEventArgs` the
  strip-level path uses - now carrying a `ParentItemId` so `DesignerViewContent`'s
  `AddToolStripItemAsync` call inserts into the right dropdown's `DropDownItems` instead of the
  strip's own `Items`. `Enter` commits and re-arms editing (matches real VS's "Type Here" letting
  you add several items in a row); `Tab` commits and stops; `Escape` cancels.

**Bug: Enter (and every other key) silently swallowed by an active IME.** With a system IME
active (even one not actually being used to compose CJK text - just switched on), WPF delivers
`Key.ImeProcessed` instead of the real key for essentially every keystroke, with the actual key
only recoverable via `KeyEventArgs.ImeProcessedKey`. Two separate things had to be fixed:

1. `OnKeyDown`'s `switch (e.Key)` never accounted for this, so `Key.Enter`/`Tab`/`Escape` all fell
   into the `default: e.Handled = true;` branch and were absorbed without action - reported as
   "I typed the new item name but Enter does nothing." Fixed by switching on
   `e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key` instead of `e.Key` directly.
2. Even after (1), input sent via `SendKeys` (used for scripted DevFlow repro) still never reached
   the handler at all, while a genuine hardware-level keystroke (simulated via `keybd_event` for a
   real comparison) did. Root cause: `TextBox` has its own internal **class handler** for
   `KeyDown` that runs before instance handlers and marks IME-routed keydowns `Handled` as part of
   routing them to composition - and a plain `editor.KeyDown += OnKeyDown` does not run once
   `Handled` is already `true`. Fixed by registering with `AddHandler(UIElement.KeyDownEvent, ...,
   handledEventsToo: true)` instead of `+=`, on both the popup-level `PopupTypeHereEditor` and the
   (now largely superseded, but kept correct) strip-level `typeHereEditor`.
   `SendKeys.SendWait` apparently drives this IME/class-handler path in a way genuine keyboard
   input and `keybd_event` do not - **prefer `keybd_event`/`SendInput` over `SendKeys` when
   scripting keyboard repro for this designer** (diagnosed by temporarily logging
   `OnKeyDown`'s raw/resolved key and a class-level `PreviewKeyDown` trap to `%TEMP%`, the same
   technique used for the earlier popup-closing bug; removed once the root cause was confirmed).

Verified end-to-end via DevFlow: selecting `fileToolStripMenuItem`, clicking its dropdown's real
Type Here cell, typing "NewItem", and a real `keybd_event` Enter produces `toolStripMenuItem1` in
the Outline pad, `canUndo` flips to `true`, and the popup's rendered dropdown shows the new
"Newitem" item with the Type Here cell re-armed empty below it - matching real VS's own
click-to-add-several-items-in-a-row workflow.

**Original client-side plan, kept for context** (out-of-process, WPF front end):

1. **Protocol**: extend the element/component info with the in-place editing anchor — for each
   strip, the surface rect of its template-node cell (i.e. just past the last real item, the same
   geometry the insert glyph already uses) plus the strip kind and its default new-item type. Add
   `design/add-toolstrip-item`-adjacent RPCs for *rename* (`design/set-property` on `Text` already
   suffices) so the client needs no new commit path.
2. **Client**: replace the MenuStrip case of `toolStripInsertChevron` with a "Type Here" cell
   drawn in the adorner layer at that anchor, and a real WPF `TextBox` overlaid on it while
   editing (the WPF analogue of swapping `_centerLabel` for `_centerTextBox`). Keep the split
   button for ToolStrip/StatusStrip/ContextMenuStrip.
3. **Commit semantics**, ported from `CommitTextToDesigner`: empty → cancel; `-` in a dropdown →
   separator; otherwise the picked type or the strip's default; **Enter** commits and re-arms the
   cell for the next sibling, **Tab** commits and moves to the next item, **Esc** rolls back.
4. **Keyboard ownership**: while the overlay `TextBox` has focus the designer must not see arrow
   keys/Enter/Esc as canvas commands. In WPF this is the `IsOutsideDesignSurface`-style guard
   applied to key events plus not routing `OnKeyDown` while editing — the same problem
   `ISupportInSituService.IgnoreMessages` solves in-process.
5. **Existing items**: double-click/F2 on a `ToolStripItem` enters the same editor prefilled with
   its `Text`, committing through `design/set-property`.

### Integration coverage for MenuStrip popups; ContextMenuStrip overlay; drag-to-reorder (2026-09-05)

**Regression coverage added to `Host.Tests/FormsDesignerHostClientTests.cs`** (all Microsoft-only
where noted; run on both backends per the verification pattern - LibreWinForms asserts the feature
either reports empty/no-op or throws clearly, matching how existing Microsoft-only features are
covered):
- `ChildHost_AddToolStripItem_AcceptsTheFullyQualifiedTypeNameItReportsItself` - round-trips
  `NewItemTypeNames`' own fully-qualified value through `design/add-toolstrip-item`, the regression
  test for the "Unsupported ToolStripItem type" bug below (every other existing test happened to
  pass a short type name explicitly, which is why that gap went unnoticed).
- `ChildHost_SelectingMenuItem_ExpandsPopupWithTypeHereBounds` - selecting a MenuStrip item expands
  its own dropdown as a `Popups` entry with a non-null `TypeHereBounds`, and deselecting collapses
  it again.
- `ChildHost_HitTestPopup_SelectsNestedItemWithoutClosingPopup` - clicking a real, sited item inside
  an expanded popup selects it and keeps the popup open; clicking the (unsited) Type Here cell
  itself is a safe no-op that also keeps the popup open - the exact regression for "clicking Type
  Here makes the popup disappear".

### Bug: "Unsupported ToolStripItem type" (fully-qualified vs. short type names)

`design/add-toolstrip-item`'s `ResolveToolStripItemType` matched only short names
(`"ToolStripMenuItem"`), but `DesignerComponentInfo.NewItemTypeNames` (and therefore every
`itemTypeName` a real client ever sends back, including from `PopupTypeHereEditor.Commit`) is
populated with fully-qualified names (`"System.Windows.Forms.ToolStripMenuItem"`) - the two were
never compared against the same shape anywhere except one hardcoded
`"System.Windows.Forms.ToolStripSeparator"` literal check for the lone-`"-"` case. Every real
add-item attempt threw `NotSupportedException("Unsupported ToolStripItem type: System.Windows.
Forms.ToolStripMenuItem")`. Fixed by stripping the namespace (`ShortTypeName`, last
`.`-separated segment) before the switch.

### ContextMenuStrip: hidden by default, an overlay only while selected (2026-09-05)

Real `System.Windows.Forms.Design.ContextMenuStripDesigner` (a `ToolStripDropDownDesigner`, the
same base class a MenuStrip submenu's `ToolStripMenuItemDesigner` piggybacks on) calls
`InitializeDropDown()` **unconditionally at `Initialize` time**, not from `OnSelectionChanged` -
meaning real VS shows a ContextMenuStrip's dropdown on the design surface as soon as the component
exists, permanently, never gated on selection. OpenDevelop deliberately narrows this to "hidden by
default, shown as an editable overlay only while its tray icon (or one of its own items) is
selected" - the same select-to-edit workflow a MenuStrip submenu already has - rather than
reproducing VS's always-on behavior, per explicit request ("默认不显示在 canvas，只是在 component
tray 显示图标。然后选中这个图标时，以 overlay 的形式在 canvas 显示出来...类似 main menu").

- **`SelectedContextMenuStripPopups`** (`DesignerHostService.cs`): walks
  `host.Container.Components.OfType<ContextMenuStrip>()` and includes a strip only when the
  current `ISelectionService` selection is the strip itself, or one of its own items (however
  deeply nested in its own submenus) - `BelongsTo(item, strip)` checks `.Owner == strip` at every
  submenu level and stops as soon as it matches, rather than climbing all the way up to whatever
  ultimately owns the chain: the real designer wires the strip's own `.OwnerItem` to an internal
  synthetic item (see below), so climbing past a match would walk right past the real strip into
  that internal plumbing and never find it.
- **`CapturePopupFrames` excludes every `ContextMenuStrip` reference `ExpandedDropDowns` itself
  would otherwise find**, re-adding it (correctly named, selection-gated) via
  `SelectedContextMenuStripPopups` instead. This was the actual debugging surprise: real
  `ContextMenuStripDesigner.InitializeDropDown()` wires the strip's own `.OwnerItem` to an
  internal, unnamed-by-the-user synthetic `ToolStripDropDownItem` (its `Site.Name` literally
  defaults to the type name, `"ContextMenuStrip"`) purely so `ExpandedDropDowns`' EXISTING
  MenuStrip-oriented walk (`root.Controls.OfType<ToolStrip>()` → items → `.DropDown`) happens to
  discover it too - always-on, under the WRONG (synthetic) element id, never the real component's
  own name. Diagnosed the same way as the popup-closing bug earlier this session: a temporary
  unconditional `Console.Error.WriteLine` (the usual `traceSessionOpen`-gated `Trace()` helper
  stays silent unless `OPENDEVELOP_DESIGNER_TRACE=1`, and the CHILD PROCESS's own stderr is piped
  into an in-memory `childLog` the test harness never surfaces on success - a temporary
  `DiagChildLog`/`DiagPingAsync`-style passthrough on `FormsDesignerHostClient` was needed to read
  it back at all), all removed once the root cause was confirmed.
- `HitTestPopupAndSelect`'s `ownerElementId` resolution now falls back to
  `host.Container.Components[ownerElementId] as ToolStripDropDown` when it does not name an owning
  `ToolStripDropDownItem` - a ContextMenuStrip's own popup has no owning item, so its element id
  names the strip directly.
- Covered by `ChildHost_ContextMenuStrip_OnlyOverlaysWhileSelected`: hidden by default (`Popups`
  empty right after open), a single popup appears the moment the tray icon is selected, selecting
  one of its own items keeps that SAME popup open (mirrors the MenuStrip "selecting a leaf item
  still shows its own dropdown" behavior), hit-testing works against the strip's own element id
  directly, and deselecting collapses it again.

### Drag-to-reorder for ToolStrip/StatusStrip/MenuStrip items (2026-09-05)

New `design/reorder-toolstrip-item(elementId, targetIndex)` RPC
(`DesignerHostService.ReorderToolStripItem`, forwarded through
`MultiDocumentDesignerHostService` like every other per-document RPC - see the gotcha below) moves
an item to `targetIndex` within whatever collection it is CURRENTLY in (resolved from its own
`.Owner`/`.OwnerItem`, never a different collection than the one it started in). Unlike
`AddToolStripItem` this needs no `DesignerActionService`/`CreateComponent` machinery - just
`ToolStripItemCollection.Remove`/`Insert` plus a designer-source rewrite - but LibreWinForms'
`ToolStripItem` does not expose `Owner`/`OwnerItem` at all, so it is still Microsoft-only, gated
the same way `AddToolStripItem` already is.

- **`RewriteReorderedToolStripItems`** keeps the designer source's own record of item order in
  sync with the live collection, handling both shapes existing fixtures/tests use: a single
  `collection.AddRange(new T[] { a, b, c })` call (its array elements are reordered in place) and a
  sequence of separate `collection.Add(x)` statements (the STATEMENTS are relocated to the
  positions the original ones occupied). C#/VB variants both exist, mirroring
  `RewriteAddedToolStripItem`'s own split.
- **Bug: matching by a hardcoded `"this.{stripId}.Items"` string broke after a `Flush`.**
  `session/flush`'s `ThisQualifierRewriter` persistently drops the `this."`/`"Me."` qualifier from
  `current.Files`' own text (not just its returned copy) - so a SECOND reorder call, re-parsing that
  now-qualifier-stripped text, silently found zero matching statements and did nothing. Fixed by
  matching structurally instead (`IsTargetCollectionAccess`/`IsVbTargetCollectionAccess`: is this a
  member access ending in `.Items`/`.DropDownItems` whose owner's bare name - regardless of any
  qualifier, or none - equals the target strip/parent), rather than comparing exact expression
  text. Caught by a test that deliberately reorders TWICE with a `Flush` in between (the first
  reorder alone would have passed even with the bug).
- **Gotcha: a new instance method on `DesignerHostService` is invisible to RPC dispatch until it
  also has a forwarder on `MultiDocumentDesignerHostService`.** The actual JsonRpc target
  (`Program.cs`: `token => new MultiDocumentDesignerHostService(token)`) is a per-session router
  that resolves `(sessionId, documentId)` to the right `DesignerHostService` and forwards each RPC
  one method at a time - it is NOT reflection over `DesignerHostService` itself. A brand new
  method (even entirely unconditional, no `#if`) that exists only on `DesignerHostService` fails
  with `StreamJsonRpc.RemoteMethodNotFoundException`, which reads exactly like a build/deployment
  problem and is not - confirmed by a throwaway unconditional `[JsonRpcMethod]`/matching test that
  failed identically. Every new per-document RPC this session (`design/set-selection`,
  `design/hit-test-popup`, and now `design/reorder-toolstrip-item`) needed BOTH halves.
- Covered by `ChildHost_ReorderToolStripItem_MovesItemAndRewritesAddStatementOrder` (a ToolStrip
  whose items are separate `.Add()` statements - moves the last item to the front, then back,
  each time verified via `Flush`) and `ChildHost_ReorderToolStripItem_ReordersAddRangeArrayForStatusStrip`
  (a StatusStrip declared via a single `AddRange` array - reorders its elements in place).

### The WPF drag gesture itself (2026-09-05, later same day)

`RemoteFormsDesignerControl` now has a `reorderThumb` - a second, parallel invisible `Thumb`
covering the same bounds `moveThumb` would, shown instead of it whenever the selection is a
ToolStripItem with a `Parent` (`moveThumb`/`resizeThumb` stay gated on `IsControl`, since they
drive `design/set-bounds`; a ToolStripItem is never a `Control`). Horizontal-only, matching how VS
itself only ever lays out a ToolStrip/StatusStrip/MenuStrip's own top-level items in a row:
- **Drag**: `reorderThumb.DragDelta` only accumulates `e.HorizontalChange` into
  `reorderDragDeltaX` - no live visual feedback (no ghost/insertion-line) is drawn during the drag,
  kept deliberately simple for this first pass.
- **Drop** (`OnReorderDragCompleted`): compares the dragged item's own center X (`SurfaceX +
  Width/2 + accumulated delta`) against every SIBLING's center X (same `Parent`, via the already-
  reported `SurfaceX`/`Width` - both fields are populated for ToolStripItems too, not just
  Controls, so no new protocol field was needed) - the target index is how many siblings now sit
  to its left. Raises `ReorderRequested`, wired in `DesignerViewContent.cs` to
  `remoteClient.ReorderToolStripItemAsync` through the existing `ExecuteRemoteEdit` (so it is one
  undo step, same as every other edit).
- **Verified end-to-end via DevFlow**: selecting `toolStripButton1` on `tsTop` (screen bounds
  x=385) and a real mouse-down-move-up drag (`SetCursorPos`/`mouse_event`, stepped in 5px
  increments so `DragDelta` actually fires - a single jump wouldn't) past `toolStripButton2`'s
  center (x=408) swapped their reported bounds AND the Outline pad's own order, `canUndo` flipped
  to `true` - the real UI gesture, not just the underlying RPC called directly.

### Vertical drag-to-reorder inside an open popup (2026-09-05, later still)

Extends the above to a MenuStrip submenu/ContextMenuStrip's own items, stacked vertically inside
an expanded popup rather than laid out horizontally on a root strip.

**No new protocol field was needed.** The original plan was a `DesignerPopupFrame.Items` list (per-
item bounds local to the popup), but a quick Host.Tests-level check
(`SetSelectionAsync` to expand a dropdown, `AddToolStripItemAsync` twice, then read back
`SurfaceX`/`SurfaceY` on the resulting `DesignerComponentInfo`) showed `CurrentState`'s existing,
already-generic `component is ToolStripItem surfaceItem && surfaceItem.Owner != null ?
SurfaceLocation(surfaceItem.Owner).X + surfaceItem.Bounds.X : 0` computation already reports a
POPUP item's bounds correctly, in the exact same absolute basis `DesignerPopupFrame.X/Y` uses
(`SurfaceLocation` works on any live, handle-created `Control` - a shown `ToolStripDropDown`
included - not just root-level ones): for two items added to an expanded dropdown at popup
`(X=14, Y=52)`, the items reported `SurfaceY` `54` and `76` respectively, exactly `popup.Y + 2` and
`popup.Y + 24` - matching their own local bounds within the dropdown. So `state.Components` alone
already carries everything needed; the (now-reverted) protocol addition would have been pure
duplication.

- **Client**: a second thumb, `popupReorderThumb` (vertical, `Cursor.SizeNS`), covering the exact
  same rect `reorderThumb` would (both are computed from the same `dragX/Y/Width/Height`, which
  are already correct for a popup item too) but with a HIGHER z-index (202, above the popup's own
  Image overlay at 200 and its Type Here editor at 201) so it stays draggable while a popup is
  open. `SelectionIsInsideOpenPopup()` (is the selection's `Parent` one of `state.Popups`' own
  `OwnerElementId`s) decides which of the two thumbs `UpdateAdorners` shows - never both.
- **Drop** (`OnPopupReorderDragCompleted`): the exact vertical analogue of
  `OnReorderDragCompleted` - compares the dragged item's center Y (`SurfaceY + Height/2 +
  accumulated VerticalChange`) against every sibling's (same `Parent`) own center Y, ordered by
  `SurfaceY`. Raises the SAME `ReorderRequested` event/RPC as the root-strip case - the server
  resolves the real owning collection (a root strip's `Items` vs. a dropdown item's own
  `DropDownItems`) from the dragged item's own live `Owner`/`OwnerItem` regardless of which
  gesture asked for the move, so no server-side change was needed for this piece either.
- **Verified end-to-end via DevFlow**: selecting `fileToolStripMenuItem` (expands its popup:
  "Open" above "Exit"), a real click on `openToolStripMenuItem` inside the popup overlay (via
  `design/hit-test-popup`), then a real mouse-down/move/up drag stepped in 3px increments
  downward past `exitToolStripMenuItem`'s own position swapped their reported bounds AND the
  popup's own rendered order ("Exit" now drawn above "Open"), `canUndo` flipped `true` - confirmed
  both by `query-control-screen-bounds` and by a screenshot of the actual rendered dropdown.
- Regression coverage: `ChildHost_ReorderToolStripItem_WorksForItemsInsideAnOpenPopup` (expands
  `fileToolStripMenuItem`'s dropdown, confirms `openToolStripMenuItem`/`exitToolStripMenuItem`'s
  `SurfaceY` order before and after dragging the second one to index 0, and that the flushed
  source's `DropDownItems.AddRange` array reflects the new order) - every other reorder test only
  exercises a root strip's `Items`, so this is the first coverage of the `DropDownItems` case.

### Live drag visual feedback: the insertion line (2026-09-05, later still)

The last open item from the reorder work: both gestures now show a thin `insertionLine`
(`Rectangle`, `DodgerBlue`, 4 design units thick) at the CURRENT drop boundary while dragging, not
just applied silently on drop - matching real VS's own insertion-line cue.

- **Shared computation**: `OnReorderDragCompleted`/`OnPopupReorderDragCompleted` and the new
  `ShowReorderInsertionLine` all delegate to one factored-out `ComputeReorderTarget(vertical,
  delta)`, which returns both the target index (unchanged logic) and the design-space coordinate
  along the relevant axis where the line belongs - the midpoint between the two neighboring
  siblings' edges, or the single neighbor's own outer edge at either end of the list.
- **Positioning**: a vertical "|" (spanning the dragged item's own height) for `reorderThumb`'s
  horizontal drags, a horizontal "-" (spanning its width) for `popupReorderThumb`'s vertical ones -
  toggled by rotating which axis gets the 4-unit thickness vs. the item's own cross-axis extent,
  not two separate shapes. Shown on `DragStarted` (at the item's own current position, delta 0)
  and updated on every `DragDelta`; collapsed on `DragCompleted` (both outcomes) and whenever
  `UpdateAdorners` runs (a stale line must not survive a selection change).
- **A debugging dead end worth recording**: the line appeared completely absent from every
  screenshot taken while a drag was held (including an 8-second hold, ruling out timing), even
  after doubling its thickness and confirming - via a temporary diagnostic dump of every computed
  coordinate through `ShowReorderInsertionLine` - that its position/size were entirely sane and
  well within the visible canvas. A separate, much blunter test (a permanently-visible 200x200 RED
  rectangle at the canvas origin) proved the rendering pipeline itself has no problem showing a
  large shape - so the real explanation was neither a logic bug nor a rendering failure, just that
  a 2-4 PHYSICAL-pixel-wide line is easy to lose entirely in a full-window screenshot at normal
  resolution. Cropping the exact toolbar region and upscaling 6x with nearest-neighbor
  interpolation (PowerShell `System.Drawing`, matching the technique this repo's own sibling
  `uno-tools/CLAUDE.md` documents for the WinUI/Uno designer) revealed it clearly, positioned
  exactly where the drag's own target index predicted. **Lesson for verifying any thin adorner
  cue in this designer**: a full-window screenshot at native resolution is the wrong tool - crop
  and zoom into the specific region first, the same way sub-pixel selection-outline misalignments
  already required earlier this project.

### F2 rename and Delete for ToolStripItem; two general (not ToolStripItem-specific) source-rewrite bugs (2026-09-05, later still)

**Delete's canvas gesture already existed and needed no client-side change** - the existing
`Key.Delete` handler in `RemoteFormsDesignerControl.OnKeyDown` never gated on `IsControl`, so a
selected ToolStripItem already raised `DeleteRequested` correctly. **F2 rename needed a small new
client-side gesture** (there was previously no canvas keyboard path to rename ANY component, only
the Properties pad's own "(Name)" row): a `renameEditor` `TextBox`, shown over the current
selection's own bounds (`SurfaceX`/`Y`/`Width`/`Height` - populated for a ToolStripItem exactly
like a Control, so no protocol change was needed here either), prefilled with the current name and
fully selected on F2 (matching real VS). Enter/Tab commits via a new `RenameRequested` event,
wired in `DesignerViewContent.cs` to the EXISTING `RenameRemoteComponent` (the same method the
Properties pad's own rename row already calls - no new RPC needed); Escape or losing focus
cancels. Uses the same `AddHandler(..., handledEventsToo: true)` + `Key.ImeProcessed` resolution as
`PopupTypeHereEditor`, for the same reason.

Both were verified live via DevFlow, but this took two real dead ends worth recording:

1. **A focus-stealing race, not a key-routing bug.** F2 (and briefly, seemingly, Delete)
   intermittently failed to reach `OnKeyDown` at all in scripted testing. Root cause: `Focus()` on
   the canvas was called BEFORE `SelectionChanged?.Invoke(...)` - and that event's own handlers
   (the Properties pad, Outline pad updating their selected row/object) can themselves grab WPF
   keyboard focus as a side effect, stealing it away from the canvas immediately after. Fixed by
   calling `Focus()` a second time, after `SelectionChanged` has already run. A dedicated
   `AddHandler(UIElement.PreviewKeyDownEvent, ..., true)` trap (temporary, removed once diagnosed)
   confirmed the key never reached the canvas at all rather than being swallowed after arriving -
   ruling out a same-window shortcut collision (confirmed separately: the one `shortcut="F2"`
   found elsewhere in the addin tree, ResourceEditor's context-menu item, is scoped to its own
   `ContextMenu.InputBindings` via `MenuService.ShowContextMenu`, not `Window.InputBindings`, so it
   cannot be the culprit for a different view entirely).
2. **A synthetic-input reliability quirk specific to typing, not Enter.** Once focus was fixed and
   confirmed (`renameEditor.IsKeyboardFocused == true`, logged), a LOOP of individual `keybd_event`
   key down/up pairs for each typed character was silently never delivered (zero `KeyDown` log
   entries for any of them) - while the SAME script's single `keybd_event` Enter afterward, and F2
   itself, both worked fine every time. Switching the TYPING portion to
   `[System.Windows.Forms.SendKeys]::SendWait(...)` (keeping `keybd_event` only for the final
   Enter - the reverse of this session's earlier popup-editor finding, which used `keybd_event` for
   everything BECAUSE `SendKeys` specifically broke ENTER via IME routing) fixed it immediately.
   **Net guidance for scripting this designer's keyboard gestures: there is no single reliable
   choice between `SendKeys` and raw `keybd_event` - if one is not being delivered/processed, try
   the other for that specific key/step before concluding the underlying feature is broken.**

Confirmed end-to-end: clicking `toolStripButton1`, pressing F2 (real hardware key - showed the
editor prefilled with "toolStripButton1", fully selected), typing a new name via `SendKeys`, and a
real `keybd_event` Enter renamed the live component (`canUndo` flipped `true`, the new name
appeared in `od.forms-designer.status`'s `controlNames`).

**Two further bugs surfaced while building the ToolStripItem Delete/Rename regression tests -
neither actually specific to ToolStripItem, both general pre-existing defects in
`DesignerHostService`**, caught by giving each new test a fixture that shares state with a
sibling component (an `AddRange`-declared pair) rather than testing a lone, unshared component the
way every prior test happened to:

- **`design/rename`'s `RenameComponent` only refreshed the Name-property STRING LITERAL
  (`RewriteProperty(newName, "Name", newName)`) for `component is Control`** - `RewriteComponentName`
  itself renames every IDENTIFIER reference generically (fine for any component), but a
  ToolStripItem's own real `Name` property is a SEPARATE statement
  (`toolStripButton1.Name = "toolStripButton1";`) whose string argument doesn't move with the
  identifier rename. Fixed by widening the check to `component is Control or ToolStripItem`.
- **`design/delete-elements`'s `RewriteDeletedComponent` could silently delete far more than the
  requested component.** Its "remove every `StatementSyntax` that mentions this identifier" walk
  had two compounding gaps:
  1. A `{ ... }` method body (`BlockSyntax` in C#, `MethodBlockSyntax` in VB - InitializeComponent's
     own included) IS a `StatementSyntax` in Roslyn's model, so it always matched too (the method
     body mentions the deleted identifier somewhere, by construction) - and `RemoveNodes` drops an
     ANCESTOR before its own now-redundant descendants, silently wiping the WHOLE METHOD BODY
     (every OTHER component's statements included), not just the deleted one's few statements.
     Fixed by excluding `BlockSyntax`/`MethodBlockSyntax` from the removal candidates.
  2. A deleted item that is one of SEVERAL elements in a single shared
     `collection.AddRange(new T[] { a, b, c })` call lost the WHOLE STATEMENT - every sibling in
     that same array along with it - rather than just its own array element. Fixed by shrinking the
     array in place (removing just the matching element) BEFORE the generic statement-removal pass
     runs, so that pass no longer sees the identifier in the (now-shorter) array at all.

  Both fixes are backend-agnostic (plain Roslyn text manipulation, no WinForms API involved) and
  apply to deleting ANY component, Control or ToolStripItem alike - confirmed via the full
  regression suite (both fixes land in the shared, non-`#if`-gated section of
  `DesignerHostService.cs`) still passing 66/66 on both backends after the change.

Regression coverage: `ChildHost_Rename_UpdatesNamePropertyLiteralForToolStripItem` and
`ChildHost_Delete_PreservesSiblingStatementsAndAddRangeArrayElements` (the latter's fixture
deliberately shares an `AddRange` between two items specifically to exercise both delete bugs at
once - if bug 1 regressed, the WHOLE method body would vanish; if bug 2 regressed, the surviving
sibling would disappear from the array too).

## TabControl: click a tab header to switch the active page (2026-09-05)

> **STATUS CORRECTION (2026-09-05, later): this feature does NOT actually work.** Everything
> below up through "Async chrome-settling bug" was written as though the settle-sequence fix
> resolved the problem - it did not. The user confirmed live (clicking tab headers in the real
> app) that the page never visually switches, and a follow-up investigation (see "TabControl page
> content never visually switches" further below) proved the bug is NOT a chrome-settling timing
> issue at all: `TabPage.Visible` is already correct on both the initial render and after
> `design/select-tab` runs, yet the rendered bitmap is unaffected either way. The RPC layer
> (`SelectedIndex` changes, no undo step, hit-testing resolves the right component name at given
> coordinates) is correct and IS what the regression test below actually proves - but do not read
> "the regression test passes" as "clicking a tab header works," because it does not, and no
> automated test in this codebase currently catches that gap (none of them decode rendered pixels).
> Root cause is still open; see the later section for what has been ruled out so far.

Real VS's `TabControlDesigner` intercepts `WM_LBUTTONDOWN` on the live `TabControl` and drives
`SelectedIndex` directly, so clicking a header switches which `TabPage` is being edited without
generating any undo entry or persisted source change - navigation, not an edit. This
screenshot-based out-of-process client cannot intercept window messages on the real control, so
the same effect needed a new protocol round-trip.

**Protocol**: `DesignerComponentInfo.TabHeaderBounds` (`Designer.Remote/DesignerProtocol.cs`) - a
`List<DesignerRectangle>`, one entry per `TabPages[i]`, holding that HEADER's own rect (real
`TabControl.GetTabRect(i)`), in the same absolute surface basis `SurfaceX/Y` use. A tab header
isn't a `Component`/`Control` of its own - it's painted by the `TabControl` itself - so nothing
else can report its geometry. Empty for any component that isn't a `TabControl`.
`DesignerHostService.FindTabHeaderBounds` populates it, gated `#if MICROSOFT_WINFORMS` - Libre's
`TabControl` fork does not implement `TabCount`/`GetTabRect`, so tab-header hit-testing is
Microsoft-backend only for now; the client falls back to its ordinary generic hit-test when
`TabHeaderBounds` is empty (Libre users can still switch tabs via the Properties pad's
`SelectedIndex`, same as any other property).

**RPC**: `design/select-tab` (`DesignerHostService.cs`, forwarded through
`MultiDocumentDesignerHostService.cs` per the usual per-document-router pattern) sets
`tabs.SelectedIndex = tabIndex` directly - like `design/set-selection`, deliberately outside any
designer transaction and without any `RewriteProperty`/`RewriteComponentName` call, so it creates
no undo step and emits no designer-source line. Real VS never persists "which tab was open at
design time" either.

**Client**: `RemoteFormsDesignerControl.OnMouseLeftButtonDown` checks `TrySwitchTabAsync` before
falling through to the generic marquee/hit-test flow (after the existing
outside-surface/adorner/marquee guards, so it never preempts a legitimate drag or empty-space
click). It matches the click's design point against `TabHeaderBounds` across all components,
calls `FormsDesignerHostClient.SelectTabAsync`, and on success updates the local selection state
and re-renders.

**Async chrome-settling bug (same family as `SetSelection`'s menu-dropdown chrome).** The first
implementation of `SelectTab` returned `CurrentState` immediately after setting `SelectedIndex`.
The RPC succeeded and bumped the render sequence number, but the returned bitmap still showed the
PREVIOUSLY active page - `TabControl`'s own `OnSelectedIndexChanged` (which flips the old/new
page's `Visible`) runs asynchronously relative to the RPC call, so rendering in the same call
without pumping the message loop captured the frame before the swap had actually happened. Fixed
with the same settle-sequence already used by `SetSelection`, gated `#if MICROSOFT_WINFORMS`:

```csharp
Application.DoEvents();
(GetHost().RootComponent as Control)?.PerformLayout();
Application.DoEvents();
```

Diagnosed via temporary logging in `TrySwitchTabAsync` (removed once confirmed) that showed the
click-to-header-index resolution and the RPC round-trip were both already correct - the render
sequence number legitimately advanced - narrowing the bug to rendering timing specifically, not
hit-testing or the RPC itself.

**Test-fixture note**: `ChildHost_SelectTab_SwitchesActivePageWithoutPersistingOrCreatingAnUndoStep`
(`FormsDesignerHostClientTests.cs`) proves the RPC/hit-test-resolution correctness (accepted,
`SelectedIndex` flips, no undo step, no source change) but does not - and structurally cannot -
catch a render-timing bug like the one above, since it doesn't decode the returned bitmap's
content. That gap is why the settle-sequence bug shipped past the regression suite and only
surfaced via live DevFlow verification; a future test wanting to catch a regression here would
need to compare rendered pixels, not just RPC acceptance and state deltas.

## TabControl: Add Tab / Remove Tab (2026-09-05)

Investigated via a research pass before implementing: TabControl's own "Add Tab"/"Remove Tab"
affordances turned out NOT to be smart-tag actions at all (the first assumption, since ToolStrip's
own affordances are). Confirmed empirically - `design/list-smart-tag-actions` for a `TabControl`
returns zero items. Real VS's `TabControlDesigner` exposes them as **designer verbs**
(`ComponentDesigner.Verbs`, the right-click context-menu mechanism), a distinct API this host had
no support for at all.

**Protocol/RPC**: `DesignerVerbInfo`/`DesignerVerbs` (`Designer.Remote/DesignerProtocol.cs`), and
`design/list-verbs`/`design/invoke-verb` (`DesignerHostService.cs`, forwarded through
`MultiDocumentDesignerHostService.cs`), modeled directly on the existing
`design/list-smart-tag-actions`/`design/invoke-smart-tag-method` pair - same
never-cache-the-live-collection-between-calls reasoning, same `(index)` addressing scheme (a verb
collection has no sublists, so just one index instead of the smart tag pair's
`(listIndex, itemIndex)`). Microsoft-backend only (`#if MICROSOFT_WINFORMS`); the Libre stub throws
`NotSupportedException` rather than silently no-opping, matching every other Microsoft-only RPC in
this file.

**Source persistence - the actual new work.** Unlike `InvokeSmartTagMethod` (which never attempted
to persist a smart-tag method's side effects to source - see its own long-standing doc comment),
`TabControlDesigner`'s `AddTabPage`/`RemoveTabPage` verbs mutate `TabPages` via
`host.CreateComponent`/`host.DestroyComponent` DIRECTLY (no `BehaviorService` dependency, unlike
`ToolStripActionList.InsertStandardItems` - see below), so they create/destroy real sited
components in this headless host that DO need syncing to source. `InvokeAndSyncComponentChanges`
(shared by both `InvokeSmartTagMethod` and `InvokeVerb`) wraps the invoke in a designer
transaction, observes every `IComponent` it adds/removes via `IComponentChangeService` (the same
mechanism the real designer's own undo engine uses to notice this kind of change), and syncs each
one to source using the SAME `RewriteAddedControl`/`RewriteDeletedComponent` helpers the explicit
`design/add-element`/`design/delete-elements` RPCs already use - no new source-rewriting logic
needed, since a `TabPage` added to a `TabControl` emits the identical
`this.tabControl1.Controls.Add(this.tabPage3);` shape as any other added child control.

**Unplanned but genuine fix found along the way: no `INameCreationService` was registered in the
child design surface at all.** Every RPC that adds a component (`AddControl`, `AddToolStripItem`)
passes an explicit caller-chosen name, so this gap went unnoticed for the whole rest of this
session's work - but `TabControlDesigner.AddTabPage` calls `host.CreateComponent(typeof(TabPage))`
itself with NO name, and without a name-creation service the resulting component came back
completely unnamed (empty `Site.Name`), which cannot be synced to source at all (an empty
identifier is not valid C#/VB). Fixed by registering a small custom `DefaultNameCreationService`
into `CreateDesignSurface` unconditionally (this is genuinely backend-agnostic BCL API, not
WinForms-specific, so no `#if` gating needed). This incidentally fixed a SEPARATE known gap too:
`ToolStripActionList.InsertStandardItems` (see
`ChildHost_SupportsSmartTagActionsAndToolStripItemInsertion`'s own long-standing comment) was
believed to be a no-op headlessly for lack of a `BehaviorService` - it never asked for one; it
could not name what it created, which read identically from the outside. It now genuinely
populates the real File/Edit/Tools/Help standard menu structure. The existing regression test's
own hand-picked item name (`fileToolStripMenuItem`) collided with a same-named item
`InsertStandardItems` now legitimately creates, and was renamed to `customMenuItem`/
`customSubMenuItem` to stop asserting on a name that coincidentally matches Microsoft's own
standard-item naming.

**Client UX**: no right-click context-menu surface exists in this client at all (confirmed via
research pass - `RemoteFormsDesignerControl.cs` has no `ContextMenu`/`MouseRightButton` handling
whatsoever), and building one from scratch for a single component type was judged out of scope.
Verbs are instead folded into the EXISTING smart-tag chevron popup
(`DesignerViewContent.RemoteSmartTagRequested`/`ShowSmartTagPopup`) - the popup now fetches both
`ListSmartTagActionsAsync` and `ListVerbsAsync` and renders verb buttons (only `Visible` ones,
disabled when `!Enabled`) below a separator from any smart-tag items. This is a deliberate
simplification versus real VS's separate right-click menu, reusing an existing UI surface rather
than adding a second one for what is currently a single TabControl-only case.

**Verification status**: RPC/persistence correctness is proven by
`ChildHost_TabControlAddRemoveTabVerbs_SyncNewAndRemovedTabPagesToSource`
(`FormsDesignerHostClientTests.cs`) - adds a tab via the verb, asserts the new `TabPage` is sited
with the right parent and its declaration/`Controls.Add`/sibling statements are all correctly
emitted on Flush (this last check specifically guards against the block-wipe/AddRange-wipe class
of bug fixed earlier this session for plain Delete), then removes it via the (re-fetched) "Remove
Tab" verb and asserts it is gone from both state and source. Passing 69/69 (Microsoft) and 68/68
(Libre, where the whole test is compiled out since verbs are Microsoft-only).

Live DevFlow click-through of the actual chevron glyph was attempted but not completed at first -
the glyph is a small element and blind screen-coordinate calculation (the same class of difficulty
encountered earlier this session for the tab-header click feature) did not reliably land a click on
it. Rather than keep guessing coordinates, added four new DevFlow actions
(`od.forms-designer.list-smart-tag-actions`/`invoke-smart-tag-method`/`list-verbs`/`invoke-verb`,
`FormsDesignerDevFlowActions.cs`, backed by new `internal` wrappers on `FormsDesignerViewContent`
in `DesignerViewContent.cs`) that call the same RPCs directly, bypassing OS mouse/keyboard
simulation entirely - a permanent testing improvement for this and any future smart-tag/verb work,
not just TabControl. Using these, live end-to-end verification is now actually complete:
`od.forms-designer.list-verbs "tabControl1"` reported `Add Tab`/`Remove Tab`; invoking `Add Tab`
added a real `tabPage3` (visible in the rendered tab strip, the Outline pad, and
`controlNames`) whose designer source came back exactly as the regression test asserts; invoking
`Remove Tab` removed it again cleanly. Confirmed on the Microsoft backend via
`tests/fixtures/TabControlFixture`.

## Title-bar caption buttons: minimize was drawing "+" instead of "−" (2026-09-05, same day)

While live-verifying the above, noticed `DesignerHostService.PaintFormChrome`'s simulated Windows
caption bar (drawn because the design surface's root `Form` has no real OS non-client area to
screenshot) rendered `[+][□][x]` instead of the correct `[−][□][x]`. The minimize button's drawing
code drew BOTH a vertical and a horizontal line through the button center - together forming a "+"
- instead of just the horizontal dash real Windows chrome uses. Fixed by dropping the vertical
line, in the shared (non-`#if`-gated) part of `DesignerHostService.cs`, so both backends picked it
up in the same rebuild. Confirmed via before/after DevFlow screenshot crop+zoom.

## Selecting a component nested in a hidden TabPage now auto-switches the active tab (2026-09-05, same day)

Closes the item flagged in this session's own earlier summary as "not yet explicitly tested" -
selecting the TabControl itself vs. a TabPage vs. a control nested inside a TabPage. The
underlying server-side identity (Name/Type/Parent per component) was already correct with zero
ambiguity - every existing test that reads these fields proves it implicitly - so there was no
protocol gap to close there. The real gap was client-side UX: real VS's Document Outline switches
the active tab automatically when you select a node nested inside a page that isn't currently
showing (so its selection adorner lines up with something actually visible); this client did not -
`RemoteFormsDesignerControl.SelectSingleComponent`/`SelectComponents` only ever updated local
selection bookkeeping, regardless of whether the target sat on the active page or a hidden one.

Added `EnsureAncestorTabActiveAsync` (called fire-and-forget from both selection entry points,
after the synchronous local-selection-state commit those callers already depend on): walks the
selected component's `Parent` chain looking for a `TabPage` ancestor, then finds that page's
position within its `TabControl` using the **hierarchical `Tree`** (`DesignerElementNode`), not
the flat `Components` list - the latter's order is container-registration/declaration order
(`InitializeComponent` statement order), not layout order, while `Tree.Children` is guaranteed to
match real tab order (`BuildElementTree` walks `control.Controls` directly, which backs
`TabPages` for a `TabControl`). Once found, calls the same `design/select-tab` RPC the tab-header
click already uses.

**Verified logically correct, NOT verified visually.** Temporary diagnostic logging (removed once
confirmed, same pattern as this session's earlier TabControl diagnostics) proved the logic
computes the right ancestor TabPage and the right tab index, and that `design/select-tab` returns
`Accepted: true` with the real `SelectedIndex` genuinely changed. But the DevFlow screenshot kept
showing the same (wrong) page's content regardless - and, critically, this reproduces with NO
code of this feature involved at all: explicitly setting `tabControl1.SelectedIndex` via the
generic `design/set-property` RPC (`od.forms-designer.set-property`) produces the identical
symptom (property change accepted, dirty flag set, rendered bitmap unchanged) on this exact
fixture. This is the same still-unresolved rendering mystery already flagged earlier in this
session for the tab-header-click feature itself ("Live DevFlow re-verification of the
render-timing fix was inconclusive") - not a new bug introduced by this change, but also not
something this change fixes. A future session investigating TabControl rendering should treat
"does `SelectedIndex` changing ever visibly repaint in a DevFlow screenshot on this fixture at
all" as the actual open question, independent of any particular RPC or client code path.

## Smart-tag glyph: real icon + Ctrl+. keyboard shortcut (2026-09-05, later same day)

Two follow-ups from the Add Tab/Remove Tab work above, both prompted by user feedback:

**Real icon.** The smart-tag chevron was a hand-drawn 9x9 "»" glyph in Goldenrod. Checked whether
real WinForms' own `DesignerActionGlyph` paints from an embedded bitmap resource first (grepped
`System.Windows.Forms.Design.dll`'s manifest resources directly for anything glyph/smart-tag/action
related) - it does not; VS's real chevron is painted procedurally via GDI+, no resource to reuse.
Used the VS2017 Image Library's own "SmartTag" icon instead (the same source CLAUDE.md documents
for this repo's other VS chrome icons), embedded as a plain manifest resource
(`Project\Resources\SmartTagGlyph.xaml`, `EmbeddedResource` with a fixed `LogicalName`, `Page
Remove` to keep the WPF markup compiler from treating the loose `Viewbox` root as a navigable
Page) and loaded via `XamlReader.Load` at runtime (`CreateSmartTagGlyph`,
`RemoteFormsDesignerControl.cs`) rather than a hand-drawn `TextBlock`. Sized 16x16 (up from 9x9) -
the chevron's screen position is computed from `smartTagChevron.Width/Height` already, so no
positioning code needed to change. Confirmed rendering correctly via DevFlow screenshot
(crop+zoom, matching the reference PNG from the Image Library).

**Ctrl+. keyboard shortcut.** Real VS's own "Edit.ShowSmartTag" shortcut opens the smart-tag/verb
popup without needing to hit the tiny chevron glyph - added the same binding
(`RemoteFormsDesignerControl.OnKeyDown`, `Key.OemPeriod` + `ModifierKeys.Control`) reusing the
identical `SmartTagRequested` event the chevron's own mouse handler raises. Live DevFlow
verification of the KEY ITSELF was inconclusive: `od.activate`'s own result reported
`nativeFocused: false` even with `foregrounded: true` (`OD_TEST_MODE=1`'s `ShowActivated=false`
appears to prevent the window from ever receiving genuine OS keyboard focus via this route,
regardless of which key is sent) - confirmed by also sending a plain `Escape` (whose handler is
long-established and unrelated to this change) and observing zero effect on selection, ruling out
a bug in the new handler specifically. The code change itself is a direct copy of the same
`OnKeyDown` pattern already used for F2/Delete/Escape in the same method, so it is trusted by
inspection/precedent rather than a completed live keystroke trace.

## TabControl page content never visually switches - root cause narrowed, still open (2026-09-05, later same day)

**The user directly reported that clicking a tab header in the real running app does not switch
which page's content is shown**, contradicting how the click-to-switch-tab feature above was
described. This is the actual, current status: broken. This section records what has been ruled
out so a future investigation does not repeat the same dead ends.

Leading theory going in was that `TabPage.Visible` never flips, because a real `TabControl`'s
Visible-swap on `SelectedIndex` changing is normally driven by its native control's own
`WM_NOTIFY`/`TCN_SELCHANGE` handling - and this out-of-process design surface's control tree,
`CreateControl()`'d but never actually shown inside a real top-level window, might never generate
or receive that notification at all (a structural gap, not a timing race the existing
`Application.DoEvents()` pump could ever fix). Speculative fix attempted: manually setting each
`TabPage.Visible` explicitly (in `SelectTab`, in `SetProperty` for `SelectedIndex`, and once at
initial load in `CreateDesignSurface`) to match the `TabControl`'s own `SelectedIndex`.

**This theory was wrong - disproven by diagnostic logging, not just untested.** Temporary logging
in `CreateDesignSurface` (removed once confirmed; the code changes were reverted, not merged) dumped
each `TabControl`'s `SelectedIndex` and each of its `TabPage.Visible` values right after the surface
finished loading, on `tests/fixtures/TabControlFixture` (which declares
`tabControl1.SelectedIndex = 0`):

```
tabs=tabControl1 SelectedIndex=0 pageCount=2 tabPage1:Visible=True,tabPage2:Visible=False
```

`Visible` was **already correct** before any fix ran - `tabPage1` (index 0, matching
`SelectedIndex`) was `True`, `tabPage2` was `False`. Manually re-asserting the same values was
therefore a genuine no-op, confirmed by rebuilding with it in place and observing the rendered
screenshot was unchanged (still showed `tabPage2`'s content - "Advanced" - despite `tabPage1`
being the `Visible` one). The speculative fix added no value and was reverted rather than left in
as dead code.

**What this actually rules in**: the bug is not in `TabPage.Visible`, not in `TabControl.
SelectedIndex` (both already correct at every point checked), and not in RPC-level acceptance
(`design/select-tab` and `design/set-property` both return `Accepted: true` with the real property
changed). The remaining candidate is the render/paint path itself - `Render()`'s
`root.DrawToBitmap(bitmap, ...)` call (`DesignerHostService.cs`, gated `#if MICROSOFT_WINFORMS`).
`Form.DrawToBitmap` uses `WM_PRINTCLIENT` to rasterize the control tree; a `TabControl`'s own
`WM_PRINTCLIENT`/paint handling for which page's children actually get drawn may not correctly
respect `Visible` (or may rely on additional native state - e.g. the control's own last-known
client rectangle for the selected tab body, established interactively - that this design surface,
whose root `Form` is `CreateControl()`'d but never actually becomes a real visible top-level
window, never properly initializes). This is a hypothesis, not yet confirmed the way the `Visible`
theory was disproven - it has not been tested by, for example, temporarily forcing
`tabPage2.Controls.Clear()` before rendering to see whether button2/label1 disappear from the
bitmap (which would prove DrawToBitmap really is painting an invisible page's children) versus
some entirely different explanation (e.g. stale/misattributed screen coordinates in the DevFlow
screenshot pipeline itself - though this is made less likely by the fact that unrelated
screenshots taken in the same session, e.g. Add Tab's new tab header appearing, the title-bar
caption-button fix, and the bigger smart-tag icon, all correctly reflected real-time state
changes; only TabControl PAGE CONTENT specifically appears stuck).

**Practical impact**: Add Tab / Remove Tab do not depend on this (their own regression test
checks sited components and designer source, never rendered pixels, and both operations were
independently confirmed live via the direct-RPC DevFlow actions in the previous section - a real
`tabPage3` appeared in the rendered TAB STRIP itself, which is unaffected by this bug). Only the
PAGE BODY's content is affected. Anything that reads `SelectedIndex`/selection state via RPC
(Properties pad, Outline pad, hit-testing at given coordinates) is unaffected and reports
correctly - only the rendered bitmap a human or a screenshot-based test actually looks at is
wrong. Do not mark tab-switching as working based on RPC-level tests or DevFlow state checks
alone; only a live visual check (or a future pixel-decoding test) can confirm this specific
gap is fixed.

**Follow-up attempt (same day): manual erase-and-repaint over the TabControl's display area also failed, and inconclusively.** Tried erasing each `TabControl`'s `DisplayRectangle` (computed via the existing `SurfaceLocation` helper) and manually redrawing just `SelectedTab`'s own `DrawToBitmap` output on top, after the main `root.DrawToBitmap` call. Diagnostic logging confirmed every step executed without exception, with the right values (`SelectedTab=tabPage1`, a plausible `eraseRect`, "drew ok") - yet the rendered screenshot was pixel-identical to before the fix, as if the erase+redraw simply never happened. This rules out the most likely quick fix and means the actual mechanism is not yet understood: either the bitmap being drawn onto is not the one actually returned to the client (unlikely - the rest of the bitmap, chrome, controls outside TabControls all update correctly every time), or something about a `TabPage`'s own `DrawToBitmap` call in isolation is itself producing a transparent/blank/no-op result silently. Reverted (kept out of the codebase) rather than leave non-functional complexity in. A future attempt should first verify - by saving `pageBitmap` in isolation before compositing it - whether `activePage.DrawToBitmap` on its own produces any real content at all for a `TabPage` that was never the active one at any point up to that call.

## Unified selection box style with WinUI (color + name label) (2026-09-05, later same day)

User feedback: the WinForms and WinUI out-of-process designers drew their selection box
differently - WinUI shows a name label above/outside the box; WinForms showed no name label at
all. The border colors also differed (WinForms used the stock `Brushes.DodgerBlue`; WinUI uses
`Color.FromRgb(0x00, 0x78, 0xD4)`, the real VS/Fluent accent blue, via `UnoDesignSurfaceControl`).

Both designers already share `SelectionAdornerLayer` (`Designer.Presentation/
SelectionAdornerLayer.cs`), which already supported a `showLabel` constructor flag (WinForms
passed `false`) and already draws the label ABOVE the box (`top - 17`) when enabled - so no
changes were needed to the shared class at all, only to how `RemoteFormsDesignerControl.cs`
constructs and drives it:
- Added a frozen `SelectionBrush` (`Color.FromRgb(0x00, 0x78, 0xD4)`) matching WinUI's own
  `SelectionColor` exactly, and replaced every other `Brushes.DodgerBlue` use in the file (move/
  resize thumbs' visual chrome, the marquee selection rectangle, the rename-editor's border, the
  ComponentTray's selected-entry highlight, and the per-component design-guide outline) with it,
  so the whole designer's selection visual language is consistent, not just the primary box.
- Changed the `SelectionAdornerLayer` construction's `showLabel` from `false` to `true`.
- `PositionAdorners` now passes `selectedComponent?.Name` as `ShowSelection`'s label argument
  (previously omitted, since the label didn't exist for this designer).

Confirmed via DevFlow screenshot (crop+zoom): selecting `label1` now shows a clean "label1" blue
tag above its dashed selection box, matching WinUI's own look exactly.

Incidental correction while verifying this: the "overlapping garbled text" observed in earlier
TabControl screenshots throughout this session (e.g. "button2 ton on / General") was largely
misread - zooming in with the new, clearer consistent coloring shows it is mostly just
word-wrapped button `Text` ("Button on Advanced" wrapping across two lines at this render size)
overlapping with a PRE-EXISTING per-component gray name tag that `UpdateDesignGuides` already
draws for every component (not just the selected one) - not new bleed-through content from
inactive TabPages as first suspected. This does not change the actual open bug (the wrong
TabPage's content and header still render as active regardless of `SelectedIndex`) - that remains
exactly as described in the section above - but weakens confidence in the specific "both pages'
controls are painted overlapping" phrasing used there; what is certain is only that the WRONG
page's content renders, not necessarily that BOTH pages render simultaneously.

## RESOLVED: there was never a TabControl rendering bug - it was phantom client overlays (2026-09-05, later same day)

**Every "TabControl renders the wrong page" claim in the three sections above is wrong.** The
child process's `Form.DrawToBitmap` output was correct the entire time. The real defect was
entirely client-side, and the decisive evidence was one crop-and-zoom of the rendered button text:
it reads **"Button on General"** - that is `button1`, the child of `tabPage1`, the page that
`SelectedIndex`/`TabPage.Visible` said was active all along. Not `button2` ("Button on Advanced").

**What actually happened.** `RemoteFormsDesignerControl.UpdateDesignGuides` drew a dashed outline
AND a name tag for every entry in `state.Components`, with no visibility filter. Every `TabPage` of
a `TabControl` occupies the SAME rect, so the outlines and tags for the HIDDEN page's children
(`button2`, `label1`) landed exactly on top of the visible page's content. Combined with the name
tags being drawn INSIDE each control's top-left corner at the time, this produced:

- A gray "tabControl1" tag sitting precisely over the FIRST tab header's text, hiding the word
  "General" and leaving only "Advanced" legible - which is why the tab strip looked like "Advanced"
  was the active tab in every screenshot for hours. It never was.
- A "button2" tag and a "label1" tag plus an empty dashed outline over `tabPage1`'s content, which
  is why the body looked like it held tabPage2's controls.
- The "overlapping garbled text" (`"button2 ton on / General"`) - a phantom "button2" tag on top
  of the real, word-wrapped "Button on General".

Both of the user-reported symptoms that finally cracked it follow directly:
- *"I can't select label1, tabPage2 gets selected instead"* - the `label1` being clicked was a
  phantom outline for a control on the hidden page. The child process's `FindDeepest` hit-test
  correctly checks `child.Visible` and so refuses to resolve to it, falling back to the enclosing
  `TabPage`. The hit-test was right; the thing on screen was a lie.
- *"I selected button2 but can't move it"* - `button2` is on the hidden page, so `moveThumb` was
  positioned over a control that is not on screen.

**Fix**: new `DesignerComponentInfo.IsVisible`, populated server-side from `Control.Visible` (whose
getter already folds in the whole parent chain via `GetVisibleCore`, so a control on a non-selected
`TabPage` reports false without any TabControl-specific logic), with a guard that reports `true`
for everything if the offscreen root form itself reports invisible - otherwise the flag would carry
no information and the client would hide ALL overlays. Client now filters on it in
`UpdateDesignGuides`, in both `OnMouseLeftButtonDown` local bounds checks, and in the
marquee-intersection pass. Covered by assertions in
`ChildHost_SelectTab_SwitchesActivePageWithoutPersistingOrCreatingAnUndoStep` (both before and
after a tab switch, plus a "tabControl1 stays visible" check so a default-false regression cannot
silently hide every overlay). 69/69 Microsoft, 68/68 Libre.

**Process lesson worth more than the fix.** Hours went into the wrong layer - message-pump settle
sequences, `TabPage.Visible` normalisation, `Invalidate`/`Update` before capture, manual
erase-and-repaint of the TabControl's display area, and a long hunt for a `WM_PRINTCLIENT`
visibility quirk - because the *rendered bitmap* was assumed guilty from the first screenshot and
never independently verified. Two things would have caught it immediately:
1. **Read the actual pixels before theorising.** One zoom on the button's own text ("General" vs
   "Advanced") settled in seconds what days of server-side reasoning could not.
2. **Distinguish server bitmap from client overlay.** The design surface is a server-rendered
   bitmap with WPF adorners drawn ON TOP; "what I see" is the composite. When something looks
   wrong, first establish WHICH layer it came from - e.g. by temporarily hiding the guides canvas,
   or saving the raw `PngBase64` frame to disk and viewing it alone. Every failed fix above was
   applied to the layer that was already correct.

Also worth noting: `IsAdornerSource`-style guards made the same class of mistake in the input path -
a click was attributed to an adorner drawn on top rather than to what the user was aiming at. The
tab-header click fix and the drill-into-a-child fix are both instances of "the overlay is not the
thing".

## Click arbitration extracted and unit-tested (2026-09-05, later same day)

Deciding who owns a left press on the design surface - an adorner glyph drawn over the selection, a
component underneath it, or empty canvas - produced THREE regressions in a row in a single
afternoon, each one caused by the fix for the previous:

1. Bailing out on "the press came from an adorner" before checking for a tab-header hit made tab
   headers unclickable once the TabControl was selected (its move thumb covers the header strip,
   which is inside the control's own bounding rect).
2. Fixing that by drilling through whenever the press landed on ANY component's bounds broke
   move-dragging outright - the selected component's own bounds contain the press, so the arbiter
   tore the move thumb's mouse capture away before it saw a single drag delta.
3. Fixing THAT by ignoring only the selection itself still broke move-dragging for every NESTED
   control, because each of the selection's ANCESTORS contains the press too. Only top-level
   controls kept working, since their sole containing ancestor is the design root, which was
   already excluded - which is precisely what made the bug look arbitrary in use.

The common cause was not carelessness but the feedback loop: this logic lived inline in
`RemoteFormsDesignerControl.OnMouseLeftButtonDown`, so the ONLY way to exercise any of it was
build → deploy three layers → launch → click a specific pixel by hand. Nothing about it was
reachable from a test.

Extracted to `Designer.Presentation/DesignSurfaceClickArbiter.cs` as a pure function over a
`DesignSurfaceClickCandidate` list (name, parent, bounds, visibility - projected from whatever
component model the calling designer has, so WinUI/WPF surfaces can adopt it) returning
`{ Action, ReleaseAdornerCapture }`. The distinction that actually matters, and that all three bugs
missed, is **"is something MORE SPECIFIC than the current selection under the pointer?"** - i.e. a
candidate that is neither the selection nor one of its ancestors - not "does some other component
contain the point".

Covered by 20 cases in `tests/OpenDevelop.Base.Tests/DesignSurfaceClickArbiterTests.cs`, which run
in ~0.7s with no app boot: one per regression above, the overlapping-hidden-page cases that made
these so hard to see by eye, plus degenerate input (stale/deleted selection name, cyclic Parent
chain - which would hang the UI thread inside a mouse handler - empty candidate list, ordinal-vs-
case-insensitive name matching). The fixture deliberately mirrors `tests/fixtures/TabControlFixture`
with both pages sharing bounds, because that overlap is the whole difficulty.

### Edge coverage added for the TabControl/strip RPCs

`ChildHost_TabRpcs_ToleratePlausiblyStaleInputWithoutFaultingTheHost` pins the deliberate
asymmetry between the two new RPCs: `design/select-tab` is FORGIVING (out-of-range index, negative
index, an elementId that is not a TabControl at all → accepted no-op), because the client derives
its tab index from `TabHeaderBounds` captured in an earlier frame and a click racing a tab
add/remove legitimately arrives stale; `design/invoke-verb` is STRICT (bad index or unknown id →
error), because its index comes from a `list-verbs` response the caller just made. It also asserts
the child process stays usable after every rejection - it is shared by every open document in the
session, so a fault there takes them all down.

`ChildHost_ReorderToolStripItem_ClampsAnOutOfRangeDropIndex` pins `Math.Clamp` on the drop index
(dropping past the last item is a normal gesture from an insertion-line drag, not an error) AND
that the rewritten `Items.Add` order matches the clamped live order - an off-by-one there writes
designer code that disagrees with what the user sees.

`ChildHost_IsVisible_ReflectsTheRenderNotTheShadowedDesignTimeVisibleProperty` documents a
distinction discovered by writing the test: setting `Visible=false` at design time does NOT hide a
control on the surface, because WinForms designers SHADOW that property (recording it for runtime
while keeping the control selectable - real VS behaviour), so `Control.Visible` still returns true
and the control correctly keeps its designer overlays. A non-selected TabPage is genuinely
different: the TabControl really hides that page's window. That is why phantom overlays appeared
for tab pages and nowhere else, and why "fixing" `IsVisible` to read the shadowed property would
silently drop overlays for every control a user set `Visible=false` on.

### Two backend divergences the new IsVisible tests exposed

Writing `ChildHost_IsVisible_ReflectsTheRenderNotTheShadowedDesignTimeVisibleProperty` found two
real differences between the hosts, both invisible until a test asked the question on BOTH:

1. **Design-time `Visible` shadowing is Microsoft-only.** Real WinForms' `ControlDesigner` shadows
   `Visible`: setting it false records the value for runtime but leaves the control on the design
   surface, selectable, with its overlays - real VS behaviour. LibreWinForms' portable fork has no
   such shadowing, so the property takes effect immediately and the control genuinely vanishes from
   the rendered frame, reachable only from the Document Outline pad. A real, user-visible parity
   gap; the test now pins BOTH behaviours per backend rather than asserting one away, so whichever
   side changes gets noticed. Closing the gap would mean implementing shadowing in the portable
   fork's ControlDesigner - not attempted here.
2. **`Control.Visible`'s getter does not fold in the parent chain on Libre.** Real WinForms'
   getter recurses to the root (`GetVisibleCore`), so a control inside a hidden container reports
   false without its own flag being touched. The portable fork returns only the control's own flag.
   `IsEffectivelyVisible` originally leaned on that folding, which meant that on the Libre backend
   every child of a hidden container still reported visible - i.e. the phantom-overlay bug was
   still live there for any hidden container, even after being fixed for TabPages on Microsoft.
   Fixed by walking the parent chain explicitly (up to but excluding the design root, whose own
   flag says nothing about its children), which is correct on both hosts and removes a hidden
   dependency on a WinForms implementation detail.

Both were found in minutes by a test that simply ran the same assertion against both backends -
after the same class of bug had cost hours to find by eye on one backend.

## Cross-designer audit: the same phantom-overlay bug in WPF and WinUI (2026-09-05, later same day)

Having established that the WinForms "wrong tab is rendered" saga was actually unfiltered CLIENT
OVERLAYS drawn for components that are not on screen, the other two out-of-process surfaces were
audited for the same defect. Both have the same architecture - a server-rendered bitmap with WPF
adorners composited on top - so the bug class transfers directly.

**Result: present in both, but confined to one place each** - their tab-order badge overlays
(`WpfSurfaceDesignerControl.UpdateTabOrderOverlay`, `UnoDesignRuntimeHost.RefreshTabOrderBadges`).
Each iterates every node of the reported element tree and positions a badge from that node's X/Y,
with no visibility filter - and `DesignerElementNode` carried no visibility field at all, so the
clients could not have filtered even if they had tried. Switch the tab-order view on over a
`TabControl` and the hidden tabs' badges stack on top of the visible tab's, attributing tab indices
to the wrong controls. Less severe than the WinForms case (an opt-in view rather than always-on
outlines and name tags) but the same defect, and the same "the picture is lying to you" failure
mode that made the WinForms one so expensive.

**The other half of the audit came back negative, which is worth recording**: neither surface has
an `IsAdornerSource`/move-thumb-over-the-selection construct, so there is nothing for
`DesignSurfaceClickArbiter` to consolidate there and no duplicated arbitration to go wrong. The
three click regressions were specific to the WinForms surface drawing a thumb across the whole
selection; the others hit-test directly.

Fix mirrors the WinForms one:
- `DesignerElementNode.IsVisible` (default `true`, so a host that does not set it changes nothing).
- Populated in all three element-tree builders. The two WPF hosts use `UIElement.IsVisible`, which
  is already WPF's EFFECTIVE visibility - it folds in every ancestor's `Visibility` - so a
  non-selected `TabItem`'s content reports false with no TabControl-specific code. The WinUI/Uno
  host needs an explicit fold (`IsEffectivelyVisible` walking `VisualTreeHelper.GetParent`), because
  WinUI has no `UIElement.IsVisible` and a collapsed element remains in the visual tree - the same
  reason the WinForms host walks the chain rather than trusting a getter (see the Libre divergence
  above).
- Both badge loops skip invisible nodes.
- `Tree_ReportsWhetherEachElementIsActuallyOnScreen` covers a collapsed container's child AND a
  non-selected `TabItem`'s child, in the suite that runs against both the LibreWPF and Microsoft
  WPF hosts.

## Closing the Libre Visible-shadowing gap - and a fourth unreliable visibility signal (2026-09-05, later same day)

The parity gap recorded above is now closed. LibreWinForms itself could not be changed (it is an
external package built from the librewpf checkout, not source in this repo), so the shadowing lives
in THIS host instead, `#if !MICROSOFT_WINFORMS`-gated - on the Microsoft backend the framework's own
`ControlDesigner` already shadows `Visible` and duplicating it would fight the real designer.

Three parts, all of which are needed for the behaviour to be self-consistent:
1. `SetProperty` records `Visible` into `shadowedVisible` and rewrites the source, WITHOUT applying
   it to the live control - so the control stays on the surface and selectable, as in real VS.
2. `AdoptVisibleShadowsFromLoadedSource` takes over controls that arrive already hidden from source
   (shows them, shadows their value). Without this half, shadowing would survive only until the
   document was reopened. `control.Visible == false` is an exact signal for "this control itself was
   assigned false" here precisely BECAUSE the portable fork's getter does not fold the parent chain.
3. `DescribeProperties` reports the shadowed value, or the Properties pad would show the user's own
   `Visible=false` edit snapping straight back to `True`.

`TabPage` and the design root are excluded from all of it: a TabControl drives its pages' `Visible`
itself, so "restoring" an unselected page would show every page at once and bring the
phantom-overlay bug straight back.

**Writing the load-time test then exposed a FOURTH unreliable visibility signal**: LibreWinForms'
`TabControl` never sets an unselected `TabPage`'s `Visible` flag at all (real WinForms clears it).
So on that backend the unselected page and everything on it still claimed to be visible - meaning
the phantom-overlay bug was STILL live there for tab controls, even after the parent-chain fix
earlier today. `IsEffectivelyVisible` now asks the TabControl which page is selected
(`TabPages.IndexOf` + `SelectedIndex`) instead of trusting the flag, which is true on both forks and
needs no flag at all.

Running tally of framework "is this visible" signals that could not be trusted in an out-of-process
designer:

| Host | Signal | Why it failed |
|---|---|---|
| WinForms (Libre) | `Control.Visible` | Getter does not fold the parent chain |
| WinForms (Libre) | `TabPage.Visible` | Portable `TabControl` never sets it for unselected pages |
| WPF (both) | `UIElement.IsVisible` | Also requires a live presentation source; offscreen ⇒ always false |
| WinUI/Uno | *(none exists)* | No `IsVisible` at all |

**The pleasing part**: the assertion that was split per backend when the gap existed is now merged
back into a single backend-agnostic one, and its comment says so - that the assertion needs no
`#if` IS the evidence the gap is closed. A new test covers the load-time half and pins the TabPage
exclusion. 73/73 Microsoft.

## The popup that could not be photographed (2026-09-05)

A WPF `ContextMenu` / smart-tag popup is invisible to both of DevFlow's observation channels. The
mechanics and the workflow rule now live in the repo `CLAUDE.md` ("DevFlow cannot see a WPF popup"),
because they apply to any popup in this app, not just the designer's. What belongs here is what it
cost and what it retroactively explained:

- Hours were spent on the smart tag believing the synthetic click kept missing the 16x16 glyph. The
  popup was never once in a screenshot, which is exactly what a *working* click also looks like. The
  click was almost certainly fine all along.
- The right-click menu was then declared unverifiable until the user simply looked at it and reported
  "a Delete menu appeared" - a single human observation settled what no amount of automation could.
- `list-verbs` is what actually explained that menu: `tabControl1` → `Add Tab`/`Remove Tab`,
  `tabPage1` → **empty**. The right-click had resolved to the page, whose designer publishes no
  verbs. That is a content question, and content questions never needed the popup.

The rule this produced: **verify the content through an RPC and let a human confirm the popup
appears.** Never infer "the popup opened" from a screenshot or a UI-tree node count.

## The designer context menus were declared all along — just orphaned (2026-09-06)

`FormsDesigner.addin` declares four designer context menus and has since the SharpDevelop days:
`ContextMenus/SelectionMenu`, `ContainerMenu`, `TraySelectionMenu`, `ComponentTrayMenu`. Nothing
built them any more — `grep "FormsDesigner/ContextMenus" src/ --include=*.cs` returned **zero
results**. The move out of process orphaned them, and the hand-written menu that replaced them
offered two items (verbs + Delete) against the 19 the declaration already described.

Wiring them back up is a small change with a large payoff, because the commands were ready:
`AbstractFormsDesignerCommand.Run()` already routes BringToFront / SendToBack / LockControls /
`TryExecuteRemoteLayout` to the remote designer and only falls back to the in-process
`IMenuCommandService` when none match. The right-click handler now calls
`MenuService.ShowContextMenu(remoteControl, this, path)` with `SelectionMenu` for a component and
`ContainerMenu` for the design root, and the AddIn extension point works again — an AddIn can
contribute an item by declaring it.

`DesignerVerbSubmenuBuilder` was the one piece needing a rewrite: it returned `ToolStripItem`s and
read verbs straight off an in-process `IMenuCommandService`. It now renders WPF `MenuItem`s from
entries the view content prepared. **The preparation must happen before the menu is shown**, because
`ShowContextMenu` expands menu builders synchronously while listing verbs is an RPC.

### Gather verbs from the component and its immediate container — not the whole chain

Walking the full ancestor chain was the first implementation and it was wrong in a way only the live
designer showed: right-clicking `button1` offered **Add Tab**, inherited from the `TabControl` two
levels up. Real VS does not do that. `DesignerVerbMenuPlanner.ContainerDepth = 2` stops the walk at
the immediate container, which matches VS exactly:

| Right-clicked | Add Tab / Remove Tab |
|---|---|
| `tabControl1` | yes (its own) |
| `tabPage1` | yes (from its container) |
| `button1` | **no** |
| `MainForm` | no (and gets ContainerMenu) |

Some container walk is still required: `list-verbs` on `tabPage1` returns an **empty** list, so
without it there is no way to add a tab by right-clicking the page area — the obvious gesture, since
a TabControl's pages cover nearly all of its surface.

## `od.forms-designer.describe-context-menu` — how to test an untestable menu

A WPF `ContextMenu` is invisible to DevFlow (see the popup section above), so the menu is split at
the popup boundary: `describe-context-menu <component>` builds it through **exactly the same path**
the right-click uses, minus the opening, and returns the item labels. That is what caught the
`button1`/"Add Tab" leak, which no amount of screenshotting could have shown.

Building it hit two environment traps that are not designer-specific and are therefore written up in
the repo `CLAUDE.md` instead: a DevFlow action deadlocking on `GetAwaiter().GetResult()` (actions run
on the UI thread), and `ICSharpCode.Designer.Presentation.dll` existing in nine deployed copies so
that a single-AddIn build leaves the app loading a stale one. Both cost a debugging round here; read
those two `CLAUDE.md` sections before touching a shared Designer assembly or adding an action.

### The name label has no TabPage special case — twice now

A `TabPage`'s name label sits above its own bounds like every other component's, which puts it on the
TabControl's tab strip, overlapping the active header's text. Two attempts to avoid that were both
rejected as worse than the overlap: drawing it inside the page body (which is what the user noticed -
"tabPage1 是唯一在它内部的"), then sliding it right past the last header. A design-time name tag
overlapping a tab header is not worth a special case; consistency across components is what matters.
The code says so at the placement site, so this does not get "fixed" a third time.

## The designer's Cut/Copy/Paste/Delete were dead — a WPF surface misses the WinForms bridge (2026-09-06)

Wiring up the declared context menus (above) delivered 19 items, of which **four did not work**.
`od.forms-designer.describe-context-menu` was extended to report each item's real enabled state, and
it named them immediately:

```
Cu_t (disabled)   _Copy (disabled)   _Paste (disabled)   _Delete (disabled)
```

Every other item in the same menu was live. **A list of menu labels alone looks perfectly healthy** -
this is the argument for reporting enabled state in any menu-describing diagnostic.

**Root cause.** Items declared with `command="Cut"` resolve through
`MenuService.GetKnownCommand` to `ApplicationCommands.Cut`, a WPF `RoutedUICommand` (the dictionary is
populated by reflecting over `ApplicationCommands`/`NavigationCommands`, so any property name there
resolves). A RoutedCommand is inert without a `CommandBinding` somewhere up the tree, and the only
binding for these in the whole app is in `SDWindowsFormsHost`, which bridges them to
`IClipboardHandler`. The old in-process designer was a WinForms control hosted inside that, so it was
covered for free. **The out-of-process surface is plain WPF and routes straight past it**, finds no
binding, and reports `CanExecute = false`.

`FormsDesignerViewContent` had implemented `IClipboardHandler` in full the whole time -
`EnableCut`/`Cut`/`Copy`/`Paste`/`Delete` and a `remoteClipboard` that deep-copies components and
orders them by depth on paste. Nothing was calling it. `BindClipboardCommands` now adds the five
`CommandBinding`s to the design surface.

**Anything that assumed `SDWindowsFormsHost` is a candidate for the same bug.** That host is where
WinForms-era plumbing was attached, and a WPF replacement view silently loses all of it.

### Verifying it needed a routed-command action, not a keystroke

The first check was a synthetic `Ctrl+C`, and Paste stayed disabled - which looked like the binding
had failed. It had not: under `OD_TEST_MODE=1` the window does not take focus, so the keystroke went
somewhere else entirely. **Do not verify a command binding with synthetic keyboard input in test
mode.** `od.forms-designer.routed-command <cut|copy|paste|delete|selectall|undo|redo|help>` instead executes the real
routed command against the surface, which is what exercises the binding:

| Step | Result |
|---|---|
| select `button1` | Paste `(disabled)` - clipboard empty |
| `routed-command copy` | `canExecute: true, executed: true` |
| re-describe | **Paste now enabled** - proves Execute reached `IClipboardHandler.Copy` |
| `routed-command paste` | `canExecute: true, executed: true` |
| component tree | `button3` present under `tabPage1` |
| `od.file.save-all` | source gains `private ... button3` and `button3 = new Button()` |

The source check is only valid **after** a save: designer edits land in the in-memory document, so
reading the file off disk before saving shows nothing and looks like a source-sync bug. (Remember to
restore the fixture afterwards - this test leaves a `button3` in `TabControlFixture`.)

### Auditing the rest of the bridge: Undo/Redo were dead too (2026-09-06)

Having found the mechanism, the right next move was to read `SDWindowsFormsHost` and see what **else**
it bridges, rather than waiting for the next symptom. It binds ten commands to four interfaces:

| Commands | Interface | Implemented by FormsDesignerViewContent? |
|---|---|---|
| Cut, Copy, Paste, Delete, SelectAll | `IClipboardHandler` | yes — was dead, now bound |
| **Undo, Redo** | `IUndoHandler` | **yes — was dead, now bound** |
| Help | `IContextHelpProvider` | yes — was dead, now bound |
| Print, PrintPreview | `IPrintable` | **no** — deliberately left unbound |

So `Ctrl+Z` in the designer did nothing, even though `IUndoHandler` was implemented in full on top of
its own `remoteUndo`/`remoteRedo` document-snapshot stacks. `Print` is left out on purpose: binding a
command whose interface is not implemented would produce a live-looking menu item that cannot work.

Verified through the routed command, which is what proves the binding rather than the handler:

| Step | canExecute | Component tree |
|---|---|---|
| fresh designer, `undo` | **false** (nothing to undo) | — |
| `copy`, then `paste` | true, executed | `button3` appears |
| `undo` | **true** (state flipped correctly) | `button3` gone |
| `redo` | true | `button3` back |
| `undo` | true | `button3` gone — fixture clean again |

The `canExecute` flipping false→true across the paste is the part that matters: it shows the binding
is really consulting `EnableUndo` and not just reporting a constant.

## The component tray's two menus, and the "has a parent" bug they exposed (2026-09-06)

The last two orphaned menus - `ContextMenus/TraySelectionMenu` and `ContextMenus/ComponentTrayMenu` -
are now built too, from a `TrayContextMenuRequested` event the tray raises. Tray components (Timer,
ImageList, ToolTip - anything with no on-form representation) are reachable *only* there, so until
now they had no context menu at all while every control on the surface had one. The entry handler
selects first and then asks for the menu, matching the surface; the tray background gets its own menu
(registered on `trayRegion`, so the empty space below a short row counts), and marks the event
handled. The surface's own right-click handler sees the press too - it is registered with
`handledEventsToo` - but bails on `IsOutsideDesignSurface`, which already treats the tray as chrome.

### "Has a parent" is not the same test as "is not the design root"

Building the menu immediately exposed a real bug behind it: on a tray component **Cut, Copy and
Delete were all disabled**, and after fixing that, Delete reported `executed: true` and *did nothing*.

Three predicates were written as `SelectedRemoteComponent()?.Parent?.Length > 0`, and a fourth as a
`.Where(component => !String.IsNullOrEmpty(component.Parent))` filter inside
`SelectedRemoteComponents()`. The intent in every case was "not the design root" - the form itself
cannot be cut. But **a tray component has no parent control, because it is not on the form**, so the
parent test excluded those as well. The enable predicates said "no" and, once they said "yes", the
selection helper still filtered the component out and the delete silently became a no-op.

Both now read `component.IsTrayComponent || component.Parent?.Length > 0`, via a single
`SelectionIsRemovable` property so the four copies cannot drift apart again. Verified: select
`timer1` → `routed-command delete` → tray drops to `imageList1, toolTip1` → `undo` → all three back.

**The "executed: true but nothing happened" step is the lesson.** A command reporting success only
means `CanExecute` passed and `Execute` ran without throwing. Always assert the *effect* - here the
component tree - not the command's own return.

### Two tray items were removed rather than left looking alive

`ComponentTrayMenu` also declared "Line up icons" and "Show large icons". Both reach
`System.Windows.Forms.Design.ComponentTray` through `FormsDesignerViewContent.Host`, which **returns
null by design** out of process (the real `IDesignerHost` is in the child). So `ShowLargeIcons` did
nothing, and `LineUpIcons` dereferenced that null - which `AbstractFormsDesignerCommand.Run`'s catch
turned into an exception dialog. Neither means anything for the replacement tray either: it is a
`WrapPanel`, already reflowing, with one icon size. Commented out `MVP: removed`-style with that
reasoning, on the same principle that keeps `IPrintable` unbound - never offer a menu item that
cannot work.

`AbstractFormsDesignerCommand.Run` also gained a guard for the null `Host`, logging a warning instead
of dereferencing it. That covers **every** declared command with no remote equivalent yet, not just
this one, and turns a would-be crash dialog into a diagnosable log line.

**Note that `describe-context-menu` reporting an item as enabled does NOT mean clicking it is safe.**
`AbstractFormsDesignerCommand` derives enablement from the menu-command plumbing while executing down
an entirely different path, so an item can read enabled and still fault. The enabled state proves a
binding exists, nothing more.
