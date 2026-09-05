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
