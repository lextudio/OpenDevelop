# MewUI visual designer

## MXAML document model (.mxaml)

The designer's authoritative layout document is a dedicated XML dialect (.mxaml), replacing
the previous C#-syntax-tree approach. The Aprillz.MewUI runtime stays C#-first: generated
applications compile from a strict InitializeComponent partial that is GENERATED from the
.mxaml at build time (via the MewUIGen tool) and never exists as an editable file.

Key properties of the format:
- Root element `<Window>` carries `Class` (partial class name) and control attributes.
- Child elements nest by containment; only container types accept children.
- Attribute values are always strings; the property registry decides literal kind at
  generation time, eliminating the entire class of "numeric string breaks compilation" bugs.
- Whole-tree uniqueness validation catches duplicate names and invalid identifiers at parse
  time with line/column positions.

The engine lives in `LeXtudio.MewUI.Xaml` (dependency-free, System.Xml.Linq only) alongside
`MewUICSharpGenerator` for the build-time code emission. See
[`mxaml.md`](mxaml.md) for the full specification.

The workbench shell uses the common `Designer.Shell.DesignerSelectionController` for the MXAML
outline, stable-name selection restoration and `MewUIPropertyAdapter` recreation. Roslyn/MXAML
source generation and MewUI container semantics remain backend-owned.
Undo, Redo and Delete also pass through the common `DesignerCommandController`; MewUI retains the
MXAML/RPC mutation implementation while the shell owns command gating and re-entrancy.

## Implemented process boundary (2026-08-23)

The MewUI designer now follows the shared DDP process architecture. The in-process AddIn owns only
the OpenDevelop surface, Toolbox, Outline and Properties adapters. `MewUIDesigner.Host` exclusively
owns the Roslyn-backed generated-code model, edit history and serialization; the main process no
longer compiles or loads `MewUIDocumentEditor` or its Roslyn dependency for this designer. The
integration test asserts the child process and performs property edits, insertion, undo/redo and
save across the RPC boundary. The design view uses the same `DesignerCanvas` shell as WinForms,
WPF, WinUI/Uno and GTK 4. Its declared toolbar capabilities are exactly Zoom, Fit and Gridlines;
editing and lifecycle commands do not appear as fake canvas buttons.

Multiple MewUI windows now share one `MewUIDesigner.Host` process. `MewUIDesignerHostClient` is a
per-document lease with its own `DocumentId`; the shared connection owns the child process and every
mutation/flush/reorder RPC includes that `documentId`. The host stores a separate
`DocumentSession` for each generated file, including Roslyn editor state, version, file name and
undo history, and `session/close` removes only the closed document. MainWindow and SettingsWindow
therefore report the same host PID while source transforms and saves stay isolated.

`MewUIDesigner.Host` uses the common `DesignerChildHost` bootstrap and exits when either graceful
`shutdown` completes or the parent RPC transport disappears. Consequently an IDE crash/kill does
not strand a MewUI host as an orphan; normal multi-window sharing and the idle grace period remain
owned by the parent-side broker.

The shared process lifecycle is governed by
[`designer-common.md`](designer-common.md#shared-host-lifecycle-design-2026-08-23). The common
broker replaces the private static lease counter, retains an idle process for ten seconds,
coordinates one replacement after failure, and reopens every live generated-code document from
its latest parent snapshot. Explicit restart is pool-wide and restores sibling windows and unsaved
Roslyn transformations instead of invalidating their clients. MewUI has no native pixel phase
today, so asynchronous frames are dormant; its semantic WPF projection still obeys the same
version and recovery-generation checks.

Recovery notifications are marshalled as a whole to the WPF dispatcher. In particular,
`OutputChannel.Write` must not run on the broker worker while a workbench command synchronously
waits for recovery: the output channel is UI-affine and that ordering previously deadlocked the
two-window `terminate-host` scenario after the replacement process had already started. Recovery
also has a 45-second linked cancellation boundary so a failed replacement cannot occupy the UI
request indefinitely.

The two-window integration coverage verifies shared PID/distinct document ids, pad and edit
isolation, closing one document without affecting its sibling, forced shared-host recovery,
pool-wide explicit restart, unsaved generated-code preservation and independent saves. Status
automation exposes the same lifecycle identity fields as GTK even when render revisions are zero.

## Decision

MewUI is a C#-first UI framework (`Aprillz.MewUI`), so its designer must not translate the
document to XAML or treat a runtime object graph as the editable model. The C# syntax tree is the
authoritative document. Every toolbox insertion, deletion and Properties-pad change is a Roslyn
source transformation, is reparsed immediately, participates in undo/redo, marks the shared
`OpenedFile` dirty, and is saved through the normal multi-view document pipeline.

This follows the safety and consistency rules established by the WinForms, WPF and WinUI
designers while accounting for MewUI's different source language:

| Concern | Existing designers | MewUI implementation |
|---|---|---|
| Authority | `.Designer.cs` or XAML source | C# syntax tree |
| Preview model | child-owned runtime tree | safe WPF projection of the parsed MewUI tree |
| Selection | generated/session element id | syntax-span id, regenerated after every edit |
| Toolbox | source edit followed by reload | object creation inserted in `Children`/`Content` |
| Properties | source-backed adapter | source-backed `MewUIPropertyAdapter` |
| Undo/redo | source snapshots | C# text snapshots, reparsed on restore |
| Automation | `od.*-designer.*` DevFlow actions | `od.mewui-designer.*` |

The preview deliberately does not execute the user's top-level statements. MewUI applications can
perform arbitrary work during construction, so in-process execution would violate the designer
isolation invariant. The projection preserves hierarchy, control kind, text/content and selection;
unsupported custom controls remain visible as labelled containers instead of disappearing. This is
the safe-mode equivalent of the other designers' out-of-process boundary and keeps editing usable
even before a project has built.

## Window file convention

The supported design unit is deliberately the same shape developers already understand from
WinForms: one independently constructible partial Window split across behavior and generated
layout files.

`MainWindow.cs` is user-owned:

```csharp
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    void saveButton_Click() { /* application behavior */ }
}
```

`MainWindow.Designer.cs` is designer-owned:

```csharp
public partial class MainWindow
{
    private StackPanel rootPanel = null!;
    private Button saveButton = null!;

    private void InitializeComponent()
    {
        rootPanel = new StackPanel();
        saveButton = new Button();

        Title = "Main";
        WindowSize = WindowSize.Resizable(900, 700);
        saveButton.Content = "Save";
        saveButton.Click += saveButton_Click;

        rootPanel.Children(saveButton);
        Content = rootPanel;
    }
}
```

Consequences of this contract:

- `new MainWindow()` is itself a real MewUI `Window` and can be passed directly to
  `Application.Run`, `Show` or `ShowDialogAsync`.
- behavior, state and handler bodies never get regenerated;
- fields, element construction, Window properties and event hookups live in the generated part;
- opening `MainWindow.Designer.cs` directly remains a plain source edit; Design attaches only to
  `MainWindow.cs`;
- the designer registers both files in the same view transaction, marks only the generated file
  dirty, and saves source changes without rewriting the behavior part;
- field names are the stable component identities. Rename updates the declaration and every
  generated reference atomically; insert and delete create/remove both field and construction.

The earlier top-level-statement form remains ordinary valid MewUI code, but it is intentionally no
longer a designable document. It has no isolated Window type, no safe behavior/generated boundary,
and no unambiguous ownership for source generation. Projects migrate by extracting each top-level
Window into the two partial files above.

## Detection and attachment

`MewUIDesignerDisplayBinding` attaches only when the primary C# file contains a partial class that
derives from `Window`, its constructor invokes `InitializeComponent`, and the co-located
`Name.Designer.cs` contains the matching partial class and method. It rejects generated files and
top-level fluent code, so it does not steal ordinary C# or WinForms documents. The secondary view
registers both `OpenedFile` instances and external edits are reloaded through the normal workbench
lifecycle.

## Strict generated-code grammar

Unlike ordinary application C#, the generated part is a serialization format with one canonical
shape. The Roslyn backend accepts and emits these ordered sections:

1. instantiate every generated field with a standalone assignment;
2. assign Window and control properties with standalone statements;
3. attach events with standalone statements;
4. establish containment with `parent.Children(child, ...)`;
5. finish with `Content = rootField`.

For example:

```csharp
private void InitializeComponent()
{
    rootPanel = new StackPanel();
    heading = new Label();
    saveButton = new Button();

    heading.Text = "Hello";
    saveButton.Content = "Save";

    rootPanel.Children(heading, saveButton);
    Content = rootPanel;
}
```

Generated code does not use `this.`, object initializers, nested assignment, nested `new`, or fluent
property chains. This restriction makes every component and edit boundary unambiguous. A toolbox
insert adds a field declaration, a construction statement, optional default property statement,
and one parent relationship argument. The generated field name is the stable component identity;
renames and deletions update its complete generated relationship.

The initial catalog covers Window, common panels and content containers, Label/Button/TextBox,
selection controls, ranges, lists and Image. Unknown project controls are retained in the tree when
nested under a recognized control but are not synthesized from the toolbox until their metadata is
known.

## User experience

The Design view contributes all three standard designer pads:

- Toolbox: standard MewUI controls; double-click inserts into the selected/root container.
- Toolbox filtering uses the common `DesignerToolboxController`: control/category matching is
  case-insensitive, a hidden selection is cleared, and the preferred selection returns when the
  filter is cleared. The shared Tools pad supplies the visible search field and clear button;
  Enter and double-click both insert the selected item.
- Outline: the parsed source hierarchy; selecting a node synchronizes the Properties pad.
- Properties: identity, text/content, layout, appearance and enabled-state properties; edits land
  in C# source.

The surface supports click selection and the workbench Undo, Redo and Delete commands. Parse errors
are displayed without overwriting invalid source. Merely opening Design never reformats the file;
serialization occurs only after a designer mutation.

## Automation and coverage

Debug builds expose:

- `od.mewui-designer.status`
- `od.mewui-designer.select`
- `od.mewui-designer.toolbox.insert`
- `od.mewui-designer.toolbox.filter`
- `od.mewui-designer.set-property`
- `od.mewui-designer.delete`
- `od.mewui-designer.undo` / `redo`
- `od.mewui-designer.zoom` / `fit` / `gridlines`
- `od.mewui-designer.refresh` / `restart-host` / `show-source`

`MewUIDesigner.Tests` covers fluent-tree parsing, property round-trip, insertion/deletion,
undo/redo and invalid-source recovery. `tests/fixtures/MewUIFixture` is the real project fixture used
for full-workbench integration. `MewUIDesignerTests` additionally proves that the live Tools pad
hosts the MewUI catalogue, the live Outline pad hosts the parsed hierarchy, selection populates
the real Properties pad with `MewUIPropertyAdapter`, and Zoom/Fit/Gridlines work through the common
canvas. It also covers two independently designable windows, nested insertion, generated-file
save, delete/undo/redo with selection restoration, close/reopen with element reselection,
cross-file save safety, refresh, child-process restart, and reopening a second window against the
same live host process. As with all xUnit v3 projects in
this repository, execute tests with `dotnet run --project ... --`, not `dotnet test`.

MewUI's Outline/Properties synchronization now goes through the shared
`DesignerPadController`. Roslyn tree rebuilds, stable-id reselection, Outline commits and
`MewUIPropertyAdapter` rebinding therefore follow the same shell contract as GTK 4, including the
Outline notification re-entry guard. Toolbox-filter automation uses the common
`DesignerDevFlowResults` JSON envelope; the cross-designer contract and lifecycle stress gate are
documented in [`designer-common.md`](designer-common.md#shared-shell-contracts-completed-2026-08-24).

MewUI uses the same ordered multi-selection contract: Ctrl-click toggles an element,
`od.mewui-designer.multi-select` replaces the set, Outline follows the primary element and the
Properties pad receives their common editable property intersection through
`DesignerMultiPropertyAdapter`, which broadcasts edits to each `MewUIPropertyAdapter`. Tree
regeneration restores the complete set by generated field id rather than retaining only the first
element. Integration drives the realized shared Properties-pad `PropertyItem` while two elements
are selected and verifies the broadcast value in generated MXAML; selection automation returns
the same ordered `selectedIds`/`primarySelectedId`/`selectionCount` schema as the other four
designers.

Undo, Redo and Delete now use the shared `DesignerCommandController`. The isolated MewUI document
session tracks its undo/redo depths and publishes them with every state response, giving routed
commands, `IUndoHandler` and automation one authoritative enablement result.

MewUI events now use the same Properties-pad event contract as WinForms, WinUI and GTK. The child
publishes the catalogued events and existing handlers in `DesignerElementNode.Events`, implements
versioned `design/set-event` with `MxamlDocument.SetEvent`, and flushes the resulting event
attribute to canonical MXAML. `MewUIPropertyAdapter` supplies `IPropertyGridEventSource` and
`IEventBindingHost`; integration binds `Loaded` through the real Properties-pad selected object and
asserts the saved `Loaded="heading_Loaded"` source.

MewUI intentionally implements neither `IDesignHostBounds` nor `IDesignHostHitTesting` today.
Its host owns a canonical source model, not framework-native arranged geometry; advertising either
operation would turn a missing layout contract into a runtime exception. The shell must
feature-detect these optional capabilities until MewUI has authoritative design-space bounds.

Current deliberate gaps: the preview is a safe WPF semantic projection rather than MewUI-native
pixels. Fit is measured from the realized viewport and projected content. Child reorder is a
Roslyn transformation of the canonical `Children(...)` relationship and is integration-covered;
free-form pointer positioning is not yet implemented. Code-behind method-body creation remains a
Roslyn-side follow-up; event attribute creation and binding are implemented. These must not be
described as covered by the passing integration test.

## Future runtime fidelity

The current safe projection is intentionally project-code-free. A future exact-pixel renderer may
be added as a fourth DDP child backend once MewUI exposes a supported off-screen render/bootstrap
contract. That child must consume source snapshots and return neutral DTOs; it must never become the
document authority. The existing source editor, pad integration, automation contract and tests do
not depend on such a renderer and therefore remain valid.
