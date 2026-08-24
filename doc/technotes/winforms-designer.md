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
produces a portable-painted PNG frame and performs child-side coordinate hit-testing back to
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
lose unsaved designer work. Unexpected process exit keeps the last frame visible under a
diagnostic overlay and offers an explicit restart that reconstructs the session from those parent
documents. RPC operations have a bounded timeout; a hung operation terminates the child process
tree and enters the same recovery path. Project file, target framework and output assembly
metadata travel with each snapshot, and the child loads project/custom-control assemblies in a
collectible dependency-resolving load context while keeping LibreWinForms/Drawing contracts in
the host context. The VB backend is implemented in the same
out-of-process child (see "VB.NET WinForms support" below); only the legacy in-process
fallback remains C#-only.

## Current Baseline

| Component | Location | Current Status |
|---|---|---|
| WinForms Designer | `src/AddIns/DisplayBindings/FormsDesigner/` | The out-of-process LibreWinForms host is the default C# path on macOS. It owns the real `DesignSurface`, project controls and dependencies; renders to PNG; supports selection, nested Toolbox drops, Properties, events, move/resize/delete, Undo/Redo, resources, save, timeout/crash recovery and restart. The VB backend runs in the same child for `.vb` files. |

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
