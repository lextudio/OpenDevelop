# In-process designer baselines: inventory and migration analysis

Original analysis date: 2026-09-30. Read-only investigation; no production code was changed.

**Status reviewed: 2026-10-09.** The contract/lifecycle work described below has since landed:
every child returns a versioned `HostHandshake`; `session/open` and `session/update` use the common
`DesignerDocumentRequest { Snapshot, Viewport }`; recovery retains the last accepted viewport; and
the common add/delete/theme/state mutation fields now have one spelling. This review updates the
plan to distinguish that completed protocol foundation from the remaining product-parity work.

## Purpose

OpenDevelop is moving all five of its UI designers to an **out-of-process** architecture: a child
host process instantiates the real framework controls, renders them to a bitmap, and pushes a
snapshot; the IDE shows that bitmap plus an interactive overlay. The question this document answers
is: **for each framework, what did the original in-process designer have that we have not ported,
and where does that capability belong in the new architecture?**

The three original implementations are still in the repository, as git submodules. They are the
maturity benchmark. This is a file-by-file inventory of all four baseline trees, with every piece
classified by destination.

## Baselines

| Framework | Baseline | Location | Size | Status |
|---|---|---|---|---|
| **WPF** | `lextudio/WpfDesigner` @ `7cf36c6` | `externals/vscode-wpf/external/WpfDesigner` | 358 `.cs`; `WpfDesign.Designer` alone is 206 | analysed in full below |
| **WinForms** | SharpDevelop's in-process WinForms designer + the shared in-process designer library | **git history only** — see correction 3 and §9.1 | initial inventory complete | resource/complex-editor parity remains |
| **WinForms (secondary)** | `mono/monodevelop` @ `ba01d2d6d3`, `MonoDevelop.DesignerSupport` | `externals/monodevelop/main/src/addins/MonoDevelop.DesignerSupport` | 84 `.cs` | analysed; partial value only |
| **WinForms (secondary)** | Stetic — the GTK# designer, `MonoDevelop.GtkCore` | `externals/monodevelop/main/src/addins/MonoDevelop.GtkCore` | 258 `.cs` / ~49 800 lines | analysed; three transferable ideas |
| **WinUI** | `lextudio/xamlstudio` @ `d711d64` | `externals/xamlstudio` | 135 `.cs` / ~14 600 lines | analysed |

The WPF and WinUI baselines are open source and present as submodules. The WinForms baseline is in
repository history rather than a checkout; its focused initial inventory is complete in correction 3
and section 9. The remaining Forms-specific parity work is capability-gated complex/resource editing.

For scale, the shared canvas that currently serves all five out-of-process designers is
**3 files / 2282 lines**: `src/AddIns/DisplayBindings/DesignerCanvas/{DesignSurface,DesignSurfaceController,IDesignCanvasBackend}.cs`.

## The classification yardstick

Two rules decide every classification below.

1. **The snapshot is the single source of truth.** After every change the backend pushes a snapshot
   (a node tree with bounds in design coordinates, plus a rendered frame). The client must answer
   hit-testing, selection, dragging, alignment and keyboard navigation **locally** from that
   snapshot. Round-trips to the backend are only for infrequent, user-initiated actions — opening a
   dialog, enumerating a collection, browsing a file.
2. **Mutations are the only write path.** The client never patches text. It sends a mutation
   (`SetProperty` / `Insert` / `Remove` / `Move` / `Rename` / `SetDesignSize`); the backend applies it
   to its own document model and returns a new snapshot. Undo/redo is the backend's native stack.

Therefore:

- **CANVAS** — pure client-side UI, no live control needed. Goes into the shared `DesignerCanvas`,
  benefits all five designers.
- **BACKEND** — needs the real document model, control catalog, type/metadata lookup, rendering, or
  source generation. Stays per-framework.
- **DISCARD** — obsolete, demo/test-only, GTK/AppKit/UWP-era cruft, or superseded.
- **HAVE** — the current out-of-process code already does this or better. The existing file is named
  in every case.

---

## Corrections to earlier assumptions

These four corrections changed the conclusions materially and are stated first because the rest of
the document depends on them.

### 1. The in-process WPF designer was never lost

An earlier conclusion in this work stream held that OpenDevelop's in-process WPF designer had been
replaced and was only recoverable as a reduced "resurrected" copy at `9ca4d90d0f` (185 files). Both
parts were wrong. The full original is a submodule: `externals/vscode-wpf/external/WpfDesigner`
(`lextudio/WpfDesigner`, branch `opendevelop`, pinned at `7cf36c6`), **358 files**. It is present in
the working tree, not just in history. Two branches exist (`opendevelop` and `vscode`, 358 files
each, differing only in top-level extras), so either is a valid reference.

`WpfViewContent.cs` in the current tree *is* just a 651-line shell — but the shell no longer hosts
the original implementation because that implementation was never removed, only stopped being used
in favour of the shell.

### 2. xamlstudio is a previewer, not a designer

A repo-wide search for `DragDrop | SnapToGrid | AlignmentLine | UndoRedo | CanUndo | BringIntoView |
DragDelta` returns **zero** designer hits in xamlstudio. There is no drag, no resize, no snap, no
alignment guides, no undo/redo, no keyboard navigation, no outline tree, no component tray. Selection
is "click a transparent `UserControl` laid over the element" (`Controls/ModifySelectorAdorner.xaml.cs:40`).
Its single editing gesture — inline `TextBlock` text — **hand-patches the XAML source string**
(`Views/Document.Design.xaml.cs:246-267`), which rule 2 forbids outright.

Our `WpfSurfaceDesignerControl` and `RemoteFormsDesignerControl` are far past it on interaction. Its
value is a narrow and genuinely useful layer: **XAML text tolerance and error/binding telemetry**,
not the canvas. Sections 4.3 reflects this.

### 3. The WinForms baseline is SharpDevelop's, not MonoDevelop's — and it is in git history

**This correction supersedes an earlier, wrong conclusion in this work stream.** A first pass
concluded that "MonoDevelop's WinForms designer shipped as closed-source `MonoDevelop.Designer.dll`
and is not in the tree, so the WinForms baseline is thin". Every clause of that was either about the
wrong project or the wrong location.

- OpenDevelop is a **SharpDevelop** fork. SharpDevelop's WinForms designer is open source, and it
  lived in this repository until it was replaced.
- The in-process design surface was **not** in the `FormsDesigner` project at all. It was the
  **shared in-process designer library** (`ICSharpCode.Designer` — `DesignSurface`, `DesignPanel`,
  `AdornerLayer`, `DesignItem`, the property grid, the toolbox), used by *both* the WinForms and the
  WPF designers. That is why the `WpfDesigner` submodule reads as familiar: it is the same lineage.
- That library is **no longer in the working tree**. `src/Main/Designer/` now contains only
  `Designer.Remote`, `Designer.Presentation`, `Designer.Server` and `Designer.Shell`, and **no file
  declares `class DesignSurface`, `class DesignPanel`, `class DesignControl` or `class AdornerLayer`
  any more**. The shell/pieces that were kept are exactly the framework-neutral parts
  (`DesignerCommandController`, `DesignerSelectionController`, `SelectionAdornerLayer`,
  `SnapGuideCalculator`, …) that section 1 credits as "already ours".
- The in-process removal boundary is **`af32ec7e Remove in-process legacy designer`**; its parent
  `40e32d7c` still exposes the complete FormsDesigner addin through `git ls-tree -r af32ec7e^`.
  It follows the replacement commit for WinForms, **`95339845b4 Implement out-of-process WinForms
  designer`**. Section 9.1 records the resulting focused inventory.
- `OpenDevelop-5.5.8` is **not** a pre-port baseline for WinForms either: it already contains
  `Project/Src/OutOfProcess/RemoteFormsDesignerControl.cs`.

**Consequence for this report.** Section 3 below is *not* the WinForms baseline. It is a secondary
reference whose value survives the correction: MonoDevelop's toolbox abstraction is genuinely richer
than ours, and Stetic independently solved three problems we have not (session state across a child
crash, property values that live in the model rather than the control, and transaction coalescing).
The actual SharpDevelop in-process WinForms designer and its shared design library have now received
the focused inventory in section 9.1. Its result is that capability-gated complex/resource editors,
rather than restoring a live in-process object model, are the remaining Forms-specific parity work.

The three findings below that concern *our own* position relative to MonoDevelop — that our
`snap`-style alignment engine has no counterpart in MonoDevelop or Stetic, that our event-handler
binding is ahead, and that our toolbox UI is more capable — all stand, because they compare our code
against what is present in those trees rather than against an absent baseline.

### 4. Stetic shipped in-process, and its out-of-process mode is the inverse of ours

`GuiBuilderService.cs:70-71` and `:118` hardcode `IsolationMode.None`, bypassing even the static
field that holds it. **Shipped MonoDevelop always ran Stetic fully in-process.** The isolation
machinery (`IsolatedApplication`, `ApplicationBackendController`, `GuiDispatchServerSink`) compiles
and is never executed.

Worse, the out-of-process path that *was* implemented is architecturally the opposite of what we are
building. `PluggableWidget.cs:143-149` creates a `Gtk.Socket` in the client and a `Gtk.Plug` in the
backend, reparenting the backend's **live GTK widget tree** into the IDE window over the **X11
plug/socket protocol**. There is no snapshot, no bounds, no overlay; the client holds a live
`MarshalByRefObject` proxy for every model object and asks it questions.

Stetic is therefore **not** a precedent for snapshot-plus-overlay. It is a precedent for a narrower
set of things: process isolation of untrusted assemblies, code-generation isolation, session state
capture/restore across a child restart, and the frontend/backend command seam.

---

## 1. Where we already stand

Before the baselines, the honest inventory of the current out-of-process code, because several
baseline features turn out to be already ours.

`src/Main/ICSharpCode.SharpDevelop/Designer.Presentation/` already factors the interaction maths out
of every backend: `SnapGuideCalculator`, `GridlineOverlay`, `ReorderGestureCalculator`,
`DesignSurfaceClickArbiter`, `DesignerSurfaceGeometry`, `SelectionAdornerLayer`,
`DesignFramePresenter`, `DesignerVerbMenuPlanner`. `Designer.Shell` adds `DesignerSelectionController`,
`DesignerPadController`, `DesignerCommandController` and `XmlToolboxDropPlanner`; every designer's
Toolbox is the shared `SharedToolbox` (see designer-common.md).

Per-designer capability declarations (`DesignerCanvasCapabilities`, 9 flags: `Zoom | Fit | Gridlines |
Theme | ShowNames | DesignSize | StatusBar | VisualStates | ComponentTray`):

| Designer | Declared | Count |
|---|---|---|
| WinUI ProGPU | `All & ~VisualStates` | 8 |
| WPF | `Zoom\|Fit\|Gridlines\|ShowNames\|StatusBar` (+`Theme` when the document declares themes) | 5–6 |
| Forms | `Zoom\|Fit\|ShowNames\|StatusBar` | 4 |
| GTK | `Zoom\|Fit\|Gridlines` | 3 |
| MewUI | `Zoom\|Fit\|Gridlines` | 3 |

The capability ladder tracks the process model exactly: **WinUI's ProGPU host is the only in-process
backend, and it is the only one that gets the full set**, because it can answer hit tests without an
RPC. That is the clearest evidence that the gap is architectural, not effort.

## 2. WPF baseline — the richest, and mostly portable

`WpfDesign.Designer/Project/Controls/` (33 files) + `Controls/Thumbs/` (6 files) is the highest-value
region in the entire exercise: 39 files, of which **22 are CANVAS, 8 BACKEND, 7 DISCARD, 2 HAVE**,
and the CANVAS set is ~2400 lines of pure WPF UI with no model dependency.

What is there, and where each piece lands:

| Area | Files | Destination | Note |
|---|---|---|---|
| Adorner algebra | `Adorners/{AdornerPlacement,RelativePlacement,AdornerPanel}.cs`, `AdornerProvider`, `AdornerProviderClasses` | **CANVAS** | `RelativePlacement`'s 14 knobs place an overlay *relative to the content's size*, so a thumb stays a thumb at any zoom. `AdornerOrder` (`Background` 100 → `BeforeForeground` 400) is a five-layer z-order. **This is the single most reusable idea in the baseline.** |
| Adorner layer itself | `Controls/AdornerLayer.cs` (333) | **DISCARD as a port** | It positions via `TransformToAncestor` on a live tree, digs into `GeneralTransformGroup`, and contains a WPF-`Canvas` zero-size hack. The snapshot makes all three unnecessary. Port the algebra, not the layer. |
| Thumbs | `Thumbs/{DesignerThumb,ResizeThumb,RotateThumb,PointThumb,MultiPointThumb,UserControlPointsObjectThumb}.cs` | **CANVAS** | **Every thumb is design-coordinate-only.** `ResizeThumb` needs only a `Size`; the live-control dependency lives entirely in the *extensions* that pair a thumb with a transform or geometry. `RotateThumb` and `MultiPointThumb` are capabilities we do not have at all. |
| Margin handles | `Controls/MarginHandle.cs` (354) + `MarginHandleExtension` | **CANVAS + BACKEND (split)** | The handle is a pure bound control; only the commit needs the model. `DecideVisiblity` (`:191`) and the `HorizontalAlignment`-driven line/stub swap (`:216-239`) encode real design decisions. |
| Canvas position handles | `Controls/CanvasPositionHandle.cs` (225) | **CANVAS + BACKEND (split)** | Same shape, but the *which-container* and *how-to-read* policies are hard casts to WPF `Canvas`. |
| Grid rail | `Controls/GridAdorner.cs` (672) | **CANVAS (rail/splitters/drawing) + BACKEND (split maths)** | Cleanest split in the codebase. `OnRender` (`:85-120`) draws from `ColumnDefinition.Offset/ActualWidth` — numbers DDP already ships as `DesignerGridTrackInfo`. `OnMouseLeftButtonDown` (`:265-363`) is a document mutation: `design/split-grid-track` is now implemented for WPF. The shared canvas keeps blue divider drags for resizing; holding Ctrl over a selected Grid reveals amber mid-track rails and Ctrl-click splits a row (Ctrl+Shift-click splits a column). |
| In-place editor | `Controls/InPlaceEditor.cs` (160) + `InPlaceEditorExtension` | **CANVAS (UI) + BACKEND (commit)** | A `RichTextBox` overlay. At minimum port the Enter / Shift+Enter / Escape contract (`:70-77`) and the font-property commit (`:105-127`), which needs no rich text. |
| Property editor widgets | `Controls/{NumericUpDown,ColorPicker,ColorHelper,Picker,EnterTextBox,ClearableTextBox,NullableComboBox}.cs` | **CANVAS** | `ColorHelper` (110 lines) and `ColorPicker`'s three-way HSV/RGB/hex sync are pure value maths. `NumericUpDown`'s drag-scrub (`:114-143`) is the good part. |
| Property editors | `PropertyGrid/Editors/**` (25 files, ~5000 lines) | **CANVAS** | `NumberEditor` alone is registered for 21 types. See section 5 item 1 — this is the biggest parity gap in the whole port. |
| Zoom controls | `ZoomControl`, `ZoomScrollViewer`, `ZoomButtons` | **HAVE** | `DesignSurface` has the zoom state machine, pan, and a `CanvasMargin`. Two small ideas worth stealing: `RoundToOneIfClose` (kills zoom drift at 100%) and `EnableHorizontalWheelSupport`. |
| Snap engine | `Extensions/SnaplinePlacementBehavior.cs` (534) | **CANVAS (maths + rendering) / BACKEND (inputs)** | See below. |
| Raster grid | `Extensions/RasterPlacementBehavior.cs` (173) | **CANVAS** | ~50 lines of real logic, pure maths. Simplest win in the directory. |
| Collection editor + ChooseClass | `PropertyGrid/Editors/CollectionEditor.*`, `Services/ChooseClass*` | **CANVAS (UI) / BACKEND (enumeration)** | The one place a round-trip is *correct*. |
| `WindowClone` / `PageClone` | 252 + 184 | **BACKEND** | They exist only so the instantiated `Window` is not a top-level OS window. A client that only sees a bitmap never had that problem. |
| Line/Path/Polyline drawing | `Extensions/{DrawLine,DrawPath,DrawPolyLine,PathHandler,LineHandler,PolyLineHandler,LineExtensionBase,UserControlPointsObject}*` (~2400 lines) | **DISCARD** | A *drawing tool*, not a designer surface. New DDP verb per shape, real geometry in the backend, no analogue in the other four designers. |
| XAML DOM | `WpfDesign.XamlDom/**` (32 files, 6972 lines) | **BACKEND** | Already the right shape; the existing WPF host runs the same model. |
| `MouseHorizontalWheelEnabler` | 407 lines | **DISCARD** | `WindowInteropHelper` + `SetWindowsHookEx` on a `user32` HWND. There is no window, and the project runs on macOS via LibreWPF. |

### The snap engine, and the one thing not to copy

`SnaplinePlacementBehavior` is the best candidate for the shared canvas: ~250 lines of geometry with
no model access once the *inputs* are rects. It contributes four capabilities `SnapGuideCalculator`
(68 lines) does not have:

- **text baselines** (`GetBaseline`, `:463`)
- a **margin-inflated map** (`SnaplineMargin` 8, `SnaplineAccuracy` 5, `:55`/`:62`)
- `RequireOverlap` — distinguishing soft from firm edges, so a resize only snaps the moving edge
- `Group` — a baseline only snaps to another baseline (`:488`)
- paired white-solid + orange-dashed guide rendering with dash alignment across segments (`:433`)

But **do not port its `Snap` loop** (`:476-493`). It is *last-wins*, not nearest-match: it overwrites
`delta` on every qualifying pair, so among several lines within 5 px the last in list order wins, and
`BuildMaps` inserts edges in sibling-tree order — the outcome depends on document order.
`SnapGuideCalculator.ApplySnap` already takes the genuinely nearest candidate. Port the concepts onto
the nearest-match rule.

`GetBaseline` is the one borderline case: it needs a text-layout result, not a type. It cannot be
answered client-side, but it is also not a backend-specific concept. Either add a `BaselineOffset`
field to `DesignerElementNode` (one field, exactly in the spirit of `DesignerGridTrackInfo`) or drop
baseline snapping. **It should be added** — it is the only snapline feature WinUI genuinely needs,
since WinUI has no margin to snap to.

### The gesture arbitration is already solved, and better

`ClickOrDragMouseGesture` exists because one left-press means two things — "select" and "start
dragging" — and they are indistinguishable at the press. It defers the drag decision until the
pointer passes `SystemParameters.MinimumHorizontalDragDistance`.

`DesignSurface` does not use a gesture-object hierarchy, but it solves the identical conflict, and
in three places it is **better** than the baseline:

1. **Capture timing.** `DesignSurface.cs:1350-1353` captures the mouse only inside `BeginDrag`, not
   at the press, because a drag may end outside the control and its mouse-up must still arrive.
   `MouseGestureBase.Start` captures at the press — the WPF migration hit that as a bug.
2. **A stuck-drag escape hatch.** `CancelStuckDrag` (`:1232-1243`) restores pre-drag state on every
   new press, with a comment that LibreWPF "occasionally loses the mouse-up".
3. **Double-click is detected independently of the drag machinery** (`:1194-1198`), so a double-click
   can never be misread as a failed drag. The baseline needs a `user32` P/Invoke for
   `GetDoubleClickTime` with a 500 ms fallback off Windows.

One behaviour the baseline has and the canvas lacks: `setSelectionIfNotMoving` — deferring the
selection change so that Ctrl-clicking an already-selected item collapses a multi-selection *only if
the user does not drag*. Small, and worth porting.

## 3. Secondary reference: MonoDevelop and Stetic

> **Not the WinForms baseline.** See correction 3 — the real one is SharpDevelop's, in git history,
> and has the focused initial inventory in section 9.1. What follows is retained because two specific,
> transferable findings came out of it, and because three comparisons against it are sound.

### 3.1 `MonoDevelop.DesignerSupport` (84 files)

The valuable part is the **toolbox abstraction**, which is more general than ours on two axes:

1. **Two-sided declarative filtering.** `ToolboxService.FilterPermitted` (`:510-580`) matches
   `ToolboxItemFilterAttribute` `Require`/`Prevent`/`Custom` on *both* the item and the consumer. Our
   `SharedToolbox` filters by `Scope` string equality. The richer vocabulary is documented in the
   BCL, so third-party control authors can already emit it.
2. **Non-control items in a designer toolbox.** `ITextToolboxNode` + `TextToolboxNode` +
   `CodeTemplateToolboxProvider` mean one toolbox pad serves the text editor and the designer, and a
   snippet drops into either.

Plus two things we lack entirely:

- **`IToolboxCustomizer`** lets the active document restyle the toolbox pad — reorder its own
  categories (`SetCategoryPriority`), hide the "add items" button. Cheap to add.
- **`LoaderContext.LoadItemsIsolated`** (`IToolboxLoader.cs:100-113`) loads an untrusted control
  assembly **in a child process** and brings results back as XML text, never as live objects.
  `ExternalLoader : RemoteProcessObject` carries a no-op `Ping()` (`:149-151`) that
  `CreateExternalLoader` probes on every reuse, respawning on failure (`:87-94`). Our
  `TypeDiscoveryService` scans **in-process**. Stetic independently arrived at the better answer —
  `CecilToolboxItemLoader` / `CecilWidgetLibrary` read **Cecil metadata only, never loading or
  instantiating the assembly**.

On the property grid: MonoDevelop's WinForms grid had **no working event-handler binding**. The
`PropertyDescriptorEventInfo` type is a stub, never constructed anywhere in the tree, and the
`UITypeEditor` support is commented out with a TODO. Ours (`EventBindingService`,
`IEventBindingHost` on `RemoteComponentPropertyProxy`) is ahead. The one transferable idea is
`ComponentModelTarget` + `GetPropertiesForProviders` (`ComponentModelEditorProvider.cs:94-112`):
the grid edits a *set* of proxy objects, with an explicit descriptor-shape dispatch table deciding
enum / flags / file-path / standard-values from a descriptor alone, with no live control.

Alignment and snapping: **MonoDevelop has neither.** Zero hits for `ShowGrid`/`SnapToGrid`/
`GridSize`/`AlignLeft` in `DesignerSupport` or `MonoDevelop.GtkCore`. Our `SnapGuideCalculator` +
`DesignerOptionService` (8×8 grid default, snap-lines exclusivity rule) is ahead. Worth stating
plainly so nobody goes looking for it.

### 3.2 Stetic (258 files) — architectural lessons, mostly negative

**The undo model is strictly worse than ours, and this is worth saying explicitly.** Stetic's is a
snapshot-diff scheme: `UndoManager` holds one `XmlDocument` of per-object status elements;
`DiffGenerator` (319 lines) walks children matched by a monotonic `undoId` and emits
`ChildDiff`/`PropertyDiff` records in which **every property value is compared as a string**; undo
recomputes the inverse by diffing *again* rather than storing it. Compare:

| | Stetic snapshot-diff | Our mutation → snapshot |
|---|---|---|
| Write amplification | Full status doc per changed object, stringly-typed | One serialization per mutation |
| Identity | `undoId` counter, reassigned on every reload — undo records dangle across a child restart | Stable `Path`/`Id` in `DesignerElementNode` |
| Redo | Re-diffs to synthesise the inverse; can diverge | Authoritative — the backend returns the true new state |
| Type safety | All values are `string` | `DesignerPropertyInfo.Kind` tags: `Boolean`/`Number`/`Enum`/`Brush`/… |
| Coalescing | `AtomicChange` — **a good idea** | Not present; each mutation is a record |
| A no-op edit | Still writes and diffs the subtree | — |

**The one thing to steal is `AtomicChangeTracker`** (`UndoManager.cs:251-314`): a re-entrant counter
whose outermost dispose fires coalesced change events, and which explicitly permits the pending list
to grow *while* firing, so a change handler that mutates another widget folds into the same
transaction. That is transaction coalescing, and we do not have it.

**`IDiffAdaptor` (31 lines) is the right seam** and the reusable idea: it lets one diff algorithm run
over the live model, an XML status tree, and the action tree, with subclasses supplying only
`GetChildAdaptor`.

**Three genuinely transferable ideas:**

1. **Session state survives a child crash.** `WidgetDesigner.SaveStatus`/`LoadStatus` return
   `{ XmlDocument, UndoQueue }` across the boundary, and on restore every undo record is re-pointed at
   the new manager (`WidgetEditSession.cs:374-393`). Failure handling is three-layered: silently
   respawn on unexpected exit, marshal teardown to the GUI thread, and swallow `RemotingException` on
   proxy calls because a GUI-thread-dispatched callback may already be dead. This maps directly onto
   our `SharedDesignerHostPool` / `SharedDesignerHostRecovery`, and we do **not** currently have the
   state-carrying part.
2. **`CecilPropertyDescriptor` — the property value lives in the model, never in the control.**
   `CecilPropertyDescriptor.cs:88-127` stores third-party control property values in a `Hashtable` on
   the wrapper's `ExtendedData`; `GetRuntimeValue` returns null and `SetRuntimeValue` is a no-op. This
   is exactly the shape an out-of-process snapshot designer needs, and we do not have it.
3. **Code generation is forked only when it must be.** `GuiBuilderService.GenerateSteticCode` (`:444`)
   runs codegen in-process *only if every widget library is codegen-capable*; otherwise it spawns a
   `CodeGeneratorProcess`. The decision is "run it here only when it provably cannot touch a
   third-party assembly". Our `FormsDesigner` codegen should adopt the same test.

**And one answer that is better than ours by accident:** Stetic does not merge generated code back.
The generated file is written whole with a "do not modify" header, and the user's own file is instead
*reconciled against the model* — `CodeBinder.UpdateBindings` (`:116-125`) walks the design tree and
**deletes every signal whose handler method no longer exists in the class**, with the compiler as
arbiter. Renames go through `RenameRefactoring.Rename`, not text patching.

**Confirmations of earlier negative findings:** Stetic has no snap-to-grid, no alignment commands, no
z-order commands and no guides overlay. `libsteticui/Grid.cs:7` is `internal class Grid : Gtk.Container`
— a two-column label/editor *property table* (`AppendPair`, `:126`), not a design grid. The design
surface (`WidgetDesignerBackend.cs:536-720`) is a selection outline plus eight resize handles and
nothing else. A prior claim that `libsteticui/Project.cs` runs out-of-process via
`GuiBuilderService.cs:636` is **true but misleading**: `:636` is the code-generation fork, not the
designer, and the designer path is in-process.

**A naming trap for anyone porting from it:** `ProjectViewBackend`, `PaletteBackend`,
`WidgetDesignerBackend`, `WidgetPropertyTreeBackend` and `SignalsEditorBackend` are **client-side**
widgets bound to a *remote* session. "Backend" there means "fed by the backend", not "the backend".
Only `ProjectBackend`, `ApplicationBackend`, `WidgetEditSession` and `Component` are server-side.

## 4. xamlstudio — a XAML text-tolerance layer

### 4.1 It mixes two incompatible XAML stacks

`XamlRenderService.*` uses `Microsoft.UI.Xaml` (WinUI 2); `XamlXmlTreeCoordinator.cs`,
`Helpers/VisualStateWatcher.cs`, `Models/XamlBindingInfo.cs` use `Windows.UI.Xaml` (UWP system XAML);
`Views/Document.Design.xaml.cs` imports `CommunityToolkit.WinUI` while `Views/Document.xaml.cs`
imports `Microsoft.Graphics.Canvas`. The render service passes an `Microsoft.UI.Xaml.FrameworkElement`
outward while the coordinator casts the same object to a `Windows.UI.Xaml.DependencyObject`
(`Views/Document.xaml.cs:378`) — which only type-checks because WinUI 2 type-forwards.

Consequence: `XamlXmlTreeCoordinator` **cannot compile against WinUI 3 at all**, naming
`Control.BackgroundSizing`, `Grid.BackgroundSizing`, `StackPanel.BackgroundSizing`,
`ListViewBase.SemanticZoomOwner` and `ListViewBase.IsZoomedInView`, all UWP-only.

### 4.2 There is no snapshot, and there are no pixels

`XamlRenderService.cs:91,95` calls `XamlReader.Load` / `LoadWithInitialTemplateValidation`. There is
no parse-to-abstract-tree mode; it constructs real controls, applies templates, runs layout.
`XamlRenderResultContext` has **no bounds, no pixels, no sequence, no tree** — and one field that
cannot cross a process boundary at all: `public object Element { get; internal set; }` (`:42`).

Rasterisation exists only in the app shell, not the Toolkit: `Views/Document.xaml.cs:581-602` uses
UWP `RenderTargetBitmap` + `DisplayInformation.GetForCurrentView()` + `Window.Current.Bounds` +
`CanvasBitmap.CreateFromBytes`, to feed the Windows share sheet with hard-coded dimensions and a
`// TODO: Bug need to get specific for specific size ones...`. It is a share-a-screenshot path, not
design rendering.

The Toolkit's model therefore does **not** map onto `DesignerRenderFrame`, and must not be made to.
The mapping is already implemented on our side: Toolkit → live element → `DesignHost.FinishLayoutAsync`
(measure/arrange) → `BuildTree` (bounds) → `RenderAsync` (pixels) → `DesignerRenderFrame`. The
Toolkit contributes only the first step, and that is the one needing a real XAML runtime.

A partial port is already in the tree and is the right starting point:
`WinUIXamlDesigner/XamlStudio.Toolkit.ProGPU/Port/` — 4 files, same namespaces, deliberately unforked.
It has already replaced the one call that cannot work outside a real XAML runtime
(`XamlReader.Load` → `IProGpuXamlExecutor.MaterializeAsync`, `ProGpuXamlRenderService.cs:55`) and
dropped `Package.Current.InstalledLocation`.

### 4.3 What is genuinely worth taking

- **Line-length-preserving XAML repair.** `XamlRenderSettings.KeepSuggestedContentSameLength` plus
  `.ParsePre.cs:131-136` keeps suggested content the same length so **error positions still map to
  the user's text**. Our hosts rewrite XAML through seven repair passes and report no error positions
  at all. This is the most directly portable idea in the repo.
- **XAML error ranges.** `XamlExceptionRange` is a framework-neutral `{ Message, Line, StartCol,
  EndCol }`, with a second constructor deriving the end column by scanning for the next non-word
  character. **We have no equivalent** — grepping `Errors|ErrorMessage` in `DesignHost.cs` returns
  nothing, so our WinUI hosts do not surface load errors to the client.
- **Binding telemetry.** `XamlBindingInfo` (`NotBound`/`Successful`/`ConversionError`) +
  `XamlBindingWrapperManager` + `XamlBindingWrapperConverter` + `Views/Binding.xaml.cs` is a
  per-binding success/conversion-error debugger. A real feature we do not have; backend-only, since it
  needs a binding engine.
- **`d:DesignData` mock data** (`.ParsePost.cs:53-90` + `.Helpers.cs:37-69`).
- **Template-part enumeration** — nowhere implemented, and our protocol already has the hook
  (`DesignerElementNode.IsDesignable`, "False for template parts / non-source nodes"). The WinUI
  hosts do not populate it. Small, well-scoped gap.

### 4.4 Visual states: we are ahead, and the real WinUI gap is elsewhere

`ProGpuRuntimeHost.cs:94` is the only place that disables `VisualStates`, which reads as if visual
states were unimplemented. They are not: the out-of-process hosts implement them —
`WinUIXamlDesigner.UnoHost/DesignHost.cs:816-838` (`CollectVisualStateGroups`), `:920` (force a
state), `:1524` (populate the snapshot), surfaced by `UnoDesignRuntimeHost.cs:131-132` and
`DesignerCanvas/DesignSurface.cs:283-284`. `DesignHost.cs` is source-linked into the Microsoft host
too. xamlstudio's version (`VisualStateWatcher.cs:17-62`) is strictly worse: it flattens to one
dictionary and calls `GoToState` on a `Control`, so it cannot preview states on a `Page` or
`UserControl` — the common WinUI root. Ours reports per **group** (`DesignerVisualStateGroup`),
which is correct because groups are orthogonal.

`ComponentTray` is now present for WinUI, but deliberately source-driven rather than a transport
protocol field. After each snapshot, `WinUIXamlDesignerViewContent` compares named source elements
with the rendered tree and projects source-only, non-resource/non-template objects (for example a
named `ContextFlyout`) into the shared tray. Its ID is a document-local sibling-index path, so
clicking an item selects its source-backed Properties adapter without inventing a visual selection
rectangle. This works in both the Uno/Microsoft out-of-process runtime and ProGPU; a snapshot cannot
erase it because the source projection is re-applied after every render. Selected tray objects now
also support the normal F2 rename and Delete commands as source edits: rename deliberately reloads
the document instead of addressing a nonexistent visual node, while delete removes the selected
source element and refreshes the tray. The Toolbox now also has source-owned `ContextFlyout`,
`Flyout`, `CommandBarFlyout`, and menu components: dropping a flyout onto its compatible named
control authors the named popup under that control's property element; selecting a `MenuFlyout` or
`MenuFlyoutSubItem` in the tray makes it the explicit target for `MenuFlyoutItem`,
`ToggleMenuFlyoutItem`, and nested `MenuFlyoutSubItem` creation. All select their source-backed
result in the tray and keep undo/redo entirely in the document model.
Further component-specific creation remains separate work; source-order moves, paste, wrapping
and Outline re-parenting now commit through the document editor as one undoable change rather than
being transient preview-only mutations.
The Outline resolves unnamed runtime nodes through the runtime pick chain and gives source-fallback
nodes a document-local sibling-index identity. Source-only paths are never passed back as runtime
geometry paths, so selecting or re-parenting an unnamed source element never needs to author an
artificial `x:Name` or risk a stale overlay.
basic visibility, selection, rename, deletion and this first property-owned creation flow need no
child write capability. Deleting that tray flyout removes its now-empty single-value property
element in the same undo entry, so deletion restores the exact pre-insertion source shape.

---

## 5. What to port, ranked

Ranked by (a) parity gap closed per line, (b) genuinely missing rather than re-implemented,
(c) framework-neutral so one implementation serves all five, (d) verifiability.

| # | Item | Source | Why |
|---|---|---|---|
| **1** | **Property editors keyed on `DesignerPropertyInfo.Kind`** | `PropertyGrid/Editors/{NumberEditor,BoolEditor,ComboBoxEditor,TextBoxEditor}.*`, `Controls/{NumericUpDown,EnterTextBox,ClearableTextBox,NullableComboBox}.cs` | **Scalar baseline delivered.** WPF maps Boolean, Number, Enum, Point, Size, Rect, Thickness, Color, Brush and Uri to native descriptor types; GTK/MewUI map their supported booleans/numbers/enums (and GTK color), with protocol `AllowedValues` driving enum drop-downs. `ICustomTypeDescriptor.GetEditor()` being null is intentional: Xceed chooses editors from the descriptor type/converter. The remaining gap is safe structured editors for nested XAML/reference values, brush graphs and collections, not the generic scalar wire contract. |
| **2** | **Adorner placement algebra** | `Adorners/{RelativePlacement,AdornerPlacement}.cs`, `AdornerOrder` | The first reusable slice is now landed: `SelectionAdornerPlacementCalculator` maps snapshot rectangles, label placement and enabled resize anchors through `DesignViewport`, and owns the center-third move-zone hit rule. Further overlays should consume this primitive instead of re-implementing scale/origin arithmetic. |
| **3** | **Snapline concepts on a nearest-match rule** | `SnaplinePlacementBehavior` (concepts only), `RasterPlacementBehavior` | Adds baselines, `RequireOverlap`, `Group`, margin-inflated map, paired guide rendering. Basic sibling edge/centre snapping now calls `SetSnapGuides` through `DesignSurfaceController`; this phase enriches that working baseline rather than adding its first caller. |
| **4** | **Margin / position handles** | `Controls/{MarginHandle,CanvasPositionHandle}.cs` (UI half) | WPF now publishes structured Grid Margin and Canvas-position insets under v5, and the shared canvas renders draggable L/T/R/B edge readouts with dashed rulers (including zero-distance stubs) that track selection, zoom and pan. Nonzero rulers end in an arrow following their measured direction, including negative overflow; a zero-distance ruler keeps the legacy compact circular stub. These endpoint marks are presentational, leaving the labeled readout as the only drag/click target. The WPF-only commit preserves effective Canvas anchors (including Right/Bottom, margins and overflow) in one native undo group. Clicking a Grid Margin readout toggles that edge between its explicit alignment anchor and Stretch while preserving the rendered bounds. As in the legacy designer, the affordance is intentionally suppressed for non-identity Layout/RenderTransform because visual-edge coordinates cannot safely round-trip to Canvas attached properties. |
| **5** | **Grid rail + a `split-grid-track` verb** | `Controls/GridAdorner.cs` (rail/splitter half) | **WPF delivered:** explicit measured tracks can be split through an atomic host mutation; Ctrl exposes amber insertion rails, Ctrl-click inserts a row and Ctrl+Shift-click a column. The rail's `GridUnitSelector` is delivered as Grid Row/Column → Fixed/Star/Auto context-menu commands (v22). WinUI and Forms still need comparable track metadata/mutations before they can opt in. |
| **6** | **In-place text editor** | `Controls/InPlaceEditor.cs` | **WPF key contract delivered (2026-10-12)**; the font commit remains. Port the Enter/Shift+Enter/Escape contract and the font commit first; rich text (`FormatedTextEditor`, 845 lines of XAML) is a later optional step. `DesignSurface.BeginTextEdit` (`:310`) is plain-text already. |
| **7** | **`CecilPropertyDescriptor` side-store** | `CecilPropertyDescriptor.cs:88-127` | Property values live in the model, never in the control. Exactly the out-of-process shape; we have no equivalent. |
| **8** | **Cecil-based / process-isolated toolbox scanning** | `CecilToolboxItemLoader`, `CecilWidgetLibrary`, `IToolboxLoader.LoadItemsIsolated` + ping-and-respawn | Closes a real robustness gap: `TypeDiscoveryService` currently loads and instantiates third-party control assemblies **in the IDE process**. |
| **9** | **Session state capture/restore across child restart** | `WidgetEditSession.SaveState`/`RestoreState` | The one idea that maps directly onto `SharedDesignerHostPool`, which currently has recovery without state. |
| **10** | **Property transactions (`AtomicChange`)** | `UndoManager.AtomicChangeTracker` | Coalescing so one drag is one undo step. We have no equivalent. |
| **11** | **Collection editor + ChooseClass dialog** | `PropertyGrid/Editors/CollectionEditor.*`, `Services/ChooseClass*` | The one place a round-trip is *correct*; also the model for any future dialog RPC. |
| **12** | **XAML error ranges + line-length-preserving repair** | `XamlExceptionRange`, `KeepSuggestedContentSameLength` | Our WinUI hosts report no load errors at all. Medium effort, real diagnostic value. |
| **13** | **`FocusNavigator`** | `WpfDesign.Designer/Project/FocusNavigator.cs` | **Delivered:** `DesignerFocusNavigator` uses snapshot pre-order traversal for Tab/Shift+Tab primary selection, skips hidden/non-designable/unselectable nodes, wraps at both ends and tolerates a stale selection or malformed cycle. `DesignSurface` owns the key gesture while the controller retains each backend's keying/selection policy. |
| **14** | **Context-menu shell** | the 9 `*ContextMenu.xaml` | **WPF Align/Order/Wrap/Unwrap delivered (2026-10-12)**; Edit Style remains. One boilerplate template; we already have `SetContextCommands` (`:416`) + `DesignerVerbMenuPlanner`. Port content, not the `ExtensionServer` registration. |
| **15** | **Toolbox: two-sided filtering + document customization** | `ToolboxService.FilterPermitted`, `IToolboxCustomizer`, `ToolboxProvider`'s provenance re-categorisation | `Require`/`Prevent`/`Custom` beats `Scope` equality. **Document category priority is now delivered:** `DesignerToolboxScope` applies an owner-scoped category-priority map to the shared pad while retaining registration order inside a category; GTK and MewUI put Containers first for blank-document authoring. Two-sided declarative filtering still needs metadata provenance from both the item and active designer. |

Deliberately excluded from the top 15: `AdornerLayer` itself (rewrite, not port), `EditorManager`'s
registry (superseded by `Kind`), the Line/Path drawing family (a different product),
`WindowClone`/`PageClone` (backend problem by construction), `FormatedTextEditor` (after plain text
works), and all of xamlstudio's app shell.

## 6. Do not attempt

Ordered by how much time each will cost.

1. **`AdornerLayer` as a port.** Positioning is `TransformToAncestor` on a live tree, plus a
   `GeneralTransformGroup` dig, plus a WPF-`Canvas` zero-size hack. The snapshot makes all three
   unnecessary. Port the algebra (item 2), leave the layer.
2. **`SnaplinePlacementBehavior.Snap` verbatim.** Last-wins, document-order-dependent. We already
   have nearest-match.
3. **`EditorManager`'s resolution order.** Two inherited defects: the assignable scan at `:58-62`
   iterates a `Dictionary` (unspecified order, so two registrations resolve arbitrarily), and `:78`
   sets `IsNullable` by reflective name lookup. Switch on `Kind` instead.
4. **The Line/Path/Polyline/Arrow drawing family** (~2400 lines, ~30% of the WPF `Extensions`
   directory). Also note `PointTrackerPlacementSupport` takes a live `Shape`, so the thumbs leak into
   the backend regardless.
5. **`WindowClone` / `PageClone` / `CustomInstanceFactory` / `PanelInstanceFactory`.**
6. **`MouseHorizontalWheelEnabler`** (407 lines) — needs a `user32` HWND; there is none, and the
   project runs on macOS.
7. **`FormatedTextEditor`** before plain inline text works — it converts a live `TextBlock`'s
   `Inlines` to and from a `RichTextBox`'s `FlowDocument`, a separate feature.
8. **`DesignerContextMenu` + `DesignItemBinding`** — model-bound; `DesignerMultiPropertyAdapter` and
   `ICustomTypeDescriptor` already replaced them.
9. **xamlstudio's `XamlXmlTreeCoordinator` in any form.** It cannot compile on WinUI 3, and it
   *infers* the XML↔element mapping by BFS and attribute comparison, where `DesignHost.BuildTree`
   *asserts* it via a child-index `Path`. Porting it would be a strict regression plus a build error.
10. **`XamlReader.Load` as the "headless" story.** It needs a dispatcher, an `Application` context
    and (for templates) a loaded PRI/resource graph. `MicrosoftHost/Program.cs` +
    `HeadlessDispatcher.cs` already construct that world deliberately; do not inherit xamlstudio's
    assumption that the ambient process *is* the app.
11. **xamlstudio's rasterisation** (`RenderTargetBitmap` + `DisplayInformation.GetForCurrentView()` +
    `Window.Current.Bounds`).
12. **`AppAssemblyInfo.LoadAssembliesAsync`'s discovery** — it enumerates
    `Package.Current.InstalledLocation`, which throws outside an app container and is meaningless for
    a designed project. Our `Port/AppAssemblyInfo.cs` already does the right thing.
13. **xamlstudio's singleton registries** (`XamlBindingWrapperManager`, `AppAssemblyInfo`,
    `XamlAutocompleteService`) — process-wide mutable state keyed by a process-local `int`. Under
    multiple sessions and a host pool that outlives the IDE, that is a cross-session leak.
14. **xamlstudio's error-message string parsing** — `XamlRenderService.cs:100-141` and
    `.ParseXml.cs:52-60` scrape `Line:`/`Position:` out of exception text with two *different*
    regexes for two different formats. Port the `XamlExceptionRange` shape; get positions from
    `XDocument.Parse(…, LoadOptions.SetLineInfo)`, which is already used at `.ParseXml.cs:44`.
15. **xamlstudio's text-patching edit gestures** and its Monaco autocomplete and `ResourceViewer`
    (all UWP) and `Microsoft.Toolkit.Future` wholesale (26 files, a superseded pre-release fork).
16. **Stetic's remoting transport and its snapshot-diff undo.** .NET Remoting was removed from .NET
    Core; the diff model is strictly worse than ours (§3.2).
17. **Stetic's `libstetic/editor/`** (52 files) — every editor there renders a real widget to work
    (`ImageSelector` shows the actual image, `Color` reads `Gdk.Color`, `WidgetSelector` instantiates a
    control). None of that works against a bitmap. `ThemedIconList.cs` alone is 1018 lines of GTK-2
    icon-theme archaeology.
18. **Stetic's `ObjectReader`/`ObjectWriter` as a serialization framework** — they are 38 lines each
    and only exist as subclass extension points (`UndoWriter`/`UndoReader` are the only subclasses).
    The real per-type serialization is spread across ~60 overrides in `wrapper/`.
19. **MonoDevelop's `RemoteDesignerProcess`** — X11 `Gtk.Plug` over .NET Remoting, unreferenced dead
    code from the MD 1.0 era, and impossible on macOS. The *principles* (crashable child, explicit
    `ExceptionOccurred`/`RecoverFromException`, degraded-state UI) are worth keeping; the code is not.
20. **MonoDevelop's `Mac*` classes and GTK `ToolboxWidget`/`Toolbox`** — AppKit/GTK-2. Our
    Xceed-based `SharedToolbox` is more capable.

## 7. Next-phase plan (reviewed 2026-10-09)

The protocol foundation is no longer the critical path. The next work should improve an ordinary
designer session first, then remove the remaining architectural hazards. Each phase remains small
enough to verify in the affected host tests plus one focused integration journey.

| Phase | Content | Definition of done |
|---|---|---|
| **C0 — complete** | Contract versioning, common handshake, `DesignerDocumentRequest`, lifecycle viewport preservation, and common add/delete/theme/state fields | A stale child is rejected at handshake; every backend accepts the same document envelope; a recovered shared child reopens an explicit viewport and survives a source-only update. The focused Uno host recovery test covers this contract. |
| **C1 — implementation landed; LibreWPF journey verified** | **Snapshot-local hit testing** | `DesignSurfaceController` now answers from the latest `DesignerElementNode` tree for every bitmap-based backend; it no longer asks an adapter to synchronously hit-test. Forms' `Task.Run(...).GetAwaiter().GetResult()` and WinUI's UI-thread two-second wait are removed. `DesignerSnapshotHitTester` is now a pure protocol-level calculator with coverage for reverse paint order, hidden nodes, and non-designable/template-part wrappers; the explicit v7 `IsTemplatePart` flag remains a selection veto even if an incomplete snapshot accidentally marks that visual designable. Its answer therefore has no possible child-host dependency. The WPF DevFlow acceptance journey terminates the shared child through the IDE's ordinary process lifecycle, immediately selects from the last accepted snapshot, then requires a replacement PID, recovery count and restored selection geometry. This introduces no test RPC to the production protocol. Its fixture resolves to LibreWPF on macOS and Microsoft WPF on Windows; the lower-level LibreWPF and LibreWinForms shared-host recovery tests pass on macOS, and the full LibreWPF IDE journey passed on macOS on 2026-10-09. The Microsoft-WPF leg remains a Windows verification item. |
| **C2 — in progress** | **Property editor registry by `DesignerPropertyInfo.Kind`** | WPF and Forms now report native scalar kinds (Boolean, Number, Enum, Color/Brush, URI and geometry) where their existing converter can round-trip them; both report enum `AllowedValues`, and both Properties-pad adapters render those as constrained drop-downs. Forms also exposes a minimal, host-declared `ComponentReference` editor through that same constrained-value path: properties such as `AcceptButton` offer only compatible named sibling components (never the root form), serialize C#/VB references, and reset back to no authored assignment. The actual **LibreWinForms** Properties-pad journey now also proves that an `AcceptButton` edit, undo, redo and reset each flush the expected source while keeping the referenced Button constructed and parented; focused child-host coverage locks the same acceptance/rejection/reset contract for both C# and VB. MewUI's Properties adapter is now likewise snapshot-driven rather than a hand-maintained row list: every host-declared attribute is surfaced from `DesignerPropertyInfo`, with native bool/number editors and protocol enum choices; established fundamental rows remain as a fallback so users can add attributes absent from source, and MewUI color/Thickness text is deliberately not rewritten through WPF formatters. WPF has focused brush/Thickness edit-to-source coverage and now implements the optional `design/reset-property` contract: a reset removes the authored local value through `DesignItemProperty.Reset()`, preserving inherited/default semantics rather than guessing a CLR default. Its LibreWPF Properties-pad journey proves the reset is one history entry: undo restores the authored XAML value and redo removes it again after source flush. Its **Uno-host** WinUI journey proves the same reset→undo→redo source-history sequence for `PrimaryButton.Content`; the Microsoft WinUI host has not yet run that journey. Forms has edit/reset/source-flush coverage for enum and Padding. Its LibreWinForms resource round-trip test also locks an image resource to the opaque `[binary]`, read-only `Unsupported` contract on macOS, so that display token can never be submitted as editable text. Every snapshot-driven adapter (WPF, Forms, GTK and MewUI) enforces `Unsupported`/`Reference`/`ReadOnly` kinds as read-only even if a child accidentally omits the redundant boolean, making `Kind` a real cross-process edit boundary; GTK/MewUI additionally update their local snapshot only after an accepted mutation reply. The Forms, WPF and WinUI Properties-pad DevFlow seams also return the actual outcome: they reject read-only descriptors and read binding validation on the `PropertyItem` target, so a child refusal cannot be reported as a successful edit. WPF now exposes an existing `Binding` through narrow, nested-model editors for `Path`, `Mode`, `UpdateSourceTrigger` and `ElementName`: each mutation preserves the existing Binding object and flushes real markup rather than routing brace text through the scalar setter. `ElementName` is offered only when the Binding has neither an explicit `Source` nor `RelativeSource`, so it cannot silently overwrite another source form; clearing it restores the ordinary DataContext source. Other nested design-model values remain read-only `Reference` values; full Binding graphs, collections and object editors remain future work. WinUI retains its richer source-backed adapter (enum, Thickness, color and resource-key paths) rather than being downgraded to snapshot values. Unsupported/complex values stay read-only rather than silently stringified. Remaining: run the same journeys on Windows with the Microsoft WPF, Microsoft WinUI and Microsoft WinForms backends; add collection/ChooseClass/image dialogs only where a backend advertises them. |
| **C3 — in progress** | **Layout affordances and snapshot enrichment** | Shared move and resize snapping now select the nearest matching edge/centre among the dragged element's **immediate siblings** and require orthogonal overlap, so guides cannot attach to an unrelated row/column or nested container. The shared controller forms one single-parent multi-selection batch: it emits the primary element and only selected nodes with the same direct parent, avoiding cross-parent batches for backends that cannot apply them. Its snap filter removes those moving roots and their indexed descendants from the stationary sibling bounds. Measured text baselines form a separate guide group: an in-range baseline takes precedence over coincident box edges, while an unavailable baseline never prevents ordinary edge/centre snapping. The shared raster-grid move primitive is available behind an explicit `SnapToGrid` switch; WinForms now consumes its existing ShowGrid/SnapToGrid/UseSnapLines/GridSize preferences in the OOP canvas, while other frameworks remain visual-only until they opt in. `SelectionAdornerPlacementCalculator` now provides the snapshot-to-overlay placement algebra for primary/secondary selection rectangles, labels, resize anchors and the center-third move zone; `SelectionAdornerLayer` is its first consumer. The common tree now carries `LayoutMode` (the parent container's actual layout contract), nullable measured `ZIndex`, nullable local `BaselineOffset` for text-capable controls, measured `GridTracks`, and nullable `LayoutInsets` under protocol **v5**: known Canvas/Form/Panel, Grid/TableLayout, Stack/Box, Dock, Wrap, Flow/ToolStrip and content hosts are classified; unrecognised containers remain `Unknown`, never falsely advertised as freely movable. WPF publishes Grid-child Margin insets and measured Canvas-child edge distances, with child-host coverage for both; the shared canvas consumes them as draggable L/T/R/B readouts with visible dashed rulers and zero-distance stubs that remain aligned through selection, zoom and pan. A WPF-only `design/set-layout-inset` mutation maps the committed edge back to Margin or the effective Canvas anchor in one native undo group, preserving Right/Bottom source forms, margin-box coordinates and negative overflow distances; clicking a Grid edge readout routes to `design/toggle-layout-inset-anchor`, changing only the source representation between the edge alignment and Stretch while preserving its measured rectangle. Transformed elements deliberately withhold the affordance instead of attempting an invalid visual-to-layout conversion. Other hosts leave insets null until they can state equivalent semantics; the shared inset readouts are visibly bordered drag handles, with Grid-only anchor-toggle guidance. WPF and both WinUI hosts populate z-index/baseline; WPF has coverage for an explicit `Canvas.ZIndex`, positive text baseline, and node tracks matching its established `query-grid-guides` RPC. WPF's atomic `design/split-grid-track` mutation splits a measured explicit track, preserves its `GridLength` unit, and uses each child’s measured occupied region to choose whether it moves with the new second half or extends across it. The canvas now connects that mutation to a discoverable rail gesture: Ctrl reveals amber mid-track insertion rails, Ctrl-click splits a row and Ctrl+Shift-click a column; blue divider drags continue to resize, with horizontal row rails now spanning the Grid width and vertical column rails its height during both layout and drag. Forms currently leaves z-index/baseline null rather than guessing from control enumeration; WinUI keeps its source-editor guide path until it can publish measured tracks. GTK/Mew/front-end projections preserve the common scalar facts. **Node-shape additions must be released with a protocol-version/host-pool generation update:** the multi-TFM WPF host graph can otherwise resolve an older `Designer.Remote` assembly even when source is newer. Do not port the live-tree `AdornerLayer`. |
| **C4 — in progress** | **Complete mutation and session semantics** | Converge remaining backend-specific edits toward a typed common mutation envelope while keeping true runtime-only operations optional. **Selection is deliberately not a child-to-host notification:** C1's snapshot-local hit testing and `DesignerSelectionController` make the shell the single selection owner; stable node ids already restore the selection, Outline and Properties after every rebuilt snapshot/recovery. The unused `DesignerSelectionChanged` DTO was removed rather than creating a second selection authority. All five designers now expose visible F2 rename where the selected element has a mutable identity: WPF reuses `design/rename` and its existing Roslyn code-behind field sync; WinUI keeps its source editor authoritative and incrementally refreshes the child; GTK/MewUI invoke their host-owned source mutations. GTK/MewUI now propagate the host's `Accepted` result through edit, reset, add/delete, reorder, rename and undo/redo: a rejected mutation rebuilds the displayed rejection but neither dirties the document nor changes the local Properties-pad snapshot; GTK also suppresses code-behind generation after a rejected signal bind. WPF now publishes the design model's browsable XAML events and implements optional `design/set-event`: users can bind or clear an existing handler name without executing project code or generating a code-behind method. The Properties pad adapter exposes those descriptors, and its `od.wpf-designer.properties-pad.bind-event` acceptance seam exercises the same binding convention as a user double-click, writing only the XAML attribute. Its fixture resolves to LibreWPF on macOS and Microsoft WPF on Windows; both the LibreWPF child-host round trip and the visible Properties-pad-to-source-flush journey passed on macOS on 2026-10-09. Automatic handler generation remains deferred until child-to-host code-behind editing has a transactional contract. WinForms already commits a group move through one `design/apply-layout` transaction; WPF group drags/layout commands now use the optional atomic `design/set-bounds-batch` mutation, so the host validates every target before a single `PlacementOperation.Commit` and the shell records exactly one undo entry. WPF nudge now uses this same path for both arrow-key input and `od.wpf-designer.nudge`, rather than falsely reporting that a working canvas operation is unsupported. **WinForms multi-selection delete is now atomic under protocol v8:** the client sends one `design/delete-elements` request; the host prevalidates every requested component, reduces a parent/child selection to outer roots while collecting the complete descendant set for source rewrite, and restores the authoritative designer source then recreates the surface if any destruction or rewrite step fails. The focused host regression test covers a root placed after a valid selection (which must leave the valid component intact) and parent/child deletion (which must remove the whole subtree). WPF's deliberately source-authoritative whole-document undo/redo stack is now surfaced consistently by both `od.wpf-designer.status` and its dedicated actions, rather than the general status endpoint falsely reporting it unavailable because the child has no live-tree history RPC. Shared-child recovery now also replays child-local active design state: WPF restores the last successfully selected project theme; WinUI/Uno restores its selected theme and each successfully forced visual-state group after reopening the authoritative document snapshot. A focused WPF host test kills the child and proves that the recovered frame remains on the selected theme. Remaining work: verify the same WPF journeys with the Microsoft backend on Windows; then add transactional WPF code-behind handler generation only if independently desired; add an appbin-backed WinUI fixture to assert visual-state replay end-to-end (the source-only fixture cannot enumerate state groups). |
| **C5 — diagnostic foundation in progress** | **Isolation and diagnostics hardening** | WPF project-toolbox discovery now reads PE metadata with `PEReader`/`MetadataReader`, follows only metadata type references to identify public concrete `FrameworkElement` descendants, and sends the resulting `DesignerToolboxItemInfo` directly to the child on drag. It no longer calls `TypeResolutionService.LoadAssembly()` or creates a third-party runtime `Type` in the IDE process; type resolution and construction stay in the disposable design host. `DesignerDiagnostic` already has a start `Line`/`Column`; WPF parser failures populate it and the Uno host extracts it from its load exception, so do not reintroduce a parallel error DTO. The common DTO now also has optional `FileName`/`EndColumn`; WPF parser failures populate both. WinUI retains those fields from the child runtime through host/view, DevFlow diagnostics and goto-error results; WPF creates the Error List task against the reported file (and honors Warning severity) instead of always pinning it to the primary document. Diagnostic navigation now respects that file identity: WinUI resolves a relative diagnostic path against the designed document and opens that source file rather than always switching to the active XAML source view; WPF passes DDP's 1-based column directly to `SDTask` instead of shifting it one character left. WinUI now carries `PrimaryFileName` through `session/open`/`session/update` and internal theme/state reloads; RPC coverage locks both a nonvisual-root diagnostic and malformed-XAML rejection to the source file. The malformed-XAML test also caught and fixed a host bug where the exception path returned `Accepted=true`; it now rejects while keeping the child alive. Protocol **v6** carries conservative binding telemetry: WPF publishes only design-model bindings as `Declared`, never a fabricated runtime-success/error state that would require evaluating user converters or a non-existent design DataContext; a focused child-host test covers it. Protocol **v7** further makes WinUI's existing source-reachability classification explicit as `IsTemplatePart`, so consumers can distinguish source markup from template-generated visuals without inferring it from a generic non-designable flag. WinUI's `d:DesignData` transform is now RPC-tested as a child-only render aid: placeholder items and `d:DesignWidth`/`d:DesignHeight` alter the preview, while flush returns the untouched source with its design-time markup and no injected `<String>` values. The source-only Uno fixture on macOS cannot presently validate a live tree without an appbin, so `IsTemplatePart` has host compilation coverage rather than a misleading null-tree assertion. Next: richer host-specific binding states only where an isolated runtime can substantiate them, then a separately specified design-data contract for any backend that does not already have this source-preserving transform. |
| **R1 — complete** | **Inventory the pre-port WinForms designer and shared designer library** (section 9.1) | The focused historical inventory identifies `af32ec7e^` as the pre-removal source and re-ranks Forms work toward capability-gated complex/resource editors; it does not block C1/C2. |

**C2 Binding source-form update (2026-10-13).** The WPF Properties pad now also exposes a
bounded `Binding.RelativeSource` choice: empty, `Self` and `TemplatedParent`. It and `ElementName`
are offered only where XamlDom proves `Binding.Source` genuinely unset; runtime `Binding.Source`
can normalize inline and `x:Static` source forms to an apparent null, so neither editor may infer
that the source is absent. Explicit Source, `FindAncestor`, `PreviousData` and every other
non-losslessly-representable form stay absent and direct RPC calls are rejected. Choosing `Self` or
`TemplatedParent` clears the mutually exclusive `ElementName`.

**C2 Binding Source capability (2026-10-13).** Protocol **v31** now adds that stronger, narrow
contract: `Binding.Source` is a resource picker only where the nested XamlDom property proves it is
unset or an authored `{StaticResource key}`. The host reads that syntax rather than inferring from
the runtime `Binding.Source`; inline object, `x:Static`, dynamic-resource and other forms stay
absent from the pad and are rejected by `design/set-binding-source`. Changing to a resource clears
the mutually exclusive `ElementName`/`RelativeSource`; clearing returns to the ordinary DataContext
source. Focused child-host coverage locks static-resource replace/reset, ElementName replacement,
and preservation of rejected inline/`x:Static` markup from every source-choice editor.

**C2 Binding option update (2026-10-11).** The WPF Properties pad now also exposes the nested
`Binding.NotifyOnValidationError` boolean. Enabling it writes the real Binding option; disabling it
resets the nested design property so no explicit false value remains in source. It follows the same
versioned child-host path as the other narrow Binding editors.

**C2 Binding validation update (2026-10-11).** The WPF Properties pad also exposes
`ValidatesOnDataErrors`, `ValidatesOnExceptions`, and `ValidatesOnNotifyDataErrors` through a
strictly whitelisted nested-Binding RPC. The host rejects any other requested member; disabling a
validation option resets its nested property instead of serializing a redundant false value.

**C2 Binding delay update (2026-10-11).** WPF also exposes `Binding.Delay` as a native numeric
Properties-pad row. Its versioned, Binding-only mutation rejects negative durations, while zero
resets the nested design property so the default delay is not redundantly serialized.
The row is an `Int32` descriptor (integer editor), so a fractional value is rejected rather than
rounded on the way to the child.

**C2 Binding text options (2026-10-11).** Protocol **v16** adds WPF's optional, whitelisted
`design/set-binding-text-option` for `StringFormat`, `FallbackValue` and `TargetNullValue`. An empty
value resets the nested property. A row is offered only while the authored value is unset or plain
text; a nested markup value such as `FallbackValue={x:Static ...}` stays read-only and the host
rejects a text overwrite.

Building it exposed pre-existing DDP round-trip corruption, fixed in the WpfDesigner submodule's
XamlDom. A parsed markup-extension text argument already in `{}` form was escaped a second time; an
escaped leading brace (`StringFormat=\{0:N2\}`) lost its literal marker and was re-parsed as a nested
extension; and the printer escaped braces only for string-typed properties. Consequently any rewrite
of a Binding containing `StringFormat='{}{0:N2}'` (even a Mode edit) saved `\{\}\{\}\{0:N2\}`, which
WPF reads as the wrong format and which grows on every edit; DDP's own `\{0:N2\}` output failed to
reload at all; and an object-typed literal such as `TargetNullValue="{0}"` was written as a nested
extension. Parsed arguments now enter XamlDom through `XamlTextValue.FromAttributeText`, while
converter output keeps the literal-escaping constructor. Host tests lock five accepted escape forms
through a Mode rewrite and reload, a positional argument of a custom extension without a default
constructor, and `TargetNullValue` literals `{}{0}` and `{0}`. The submodule fix is reviewed but not
yet committed.

`ConverterParameter` is now a fourth whitelisted text option. Adding it found one more printer
defect: values were quoted only when they contained a space, so `ConverterParameter=a,b` or the
common `StringFormat={0:#,0}` was split at the comma and the Binding failed to reload. The printer
now quotes any value containing a space, comma, `=` or quote, escaping embedded `'`, and the
tokenizer's quoted branch processes escapes sequentially so a quoted value may end in an escaped
backslash. The WpfDesigner NUnit project discovers no tests on macOS, so these cases are covered by
the OpenDevelop host round-trip test.

**C2 nested-row correction (2026-10-11).** The first visible-pad journey for these rows found that
none of them had ever committed: Xceed binds each `PropertyItem` with `new Binding(descriptor.Name)`,
so a dotted protocol name such as `Text.Mode` was parsed as a property path and the edit was
silently dropped while the pad reported success. The WPF descriptor now uses a binding-safe name
(`.` → `_`) and keeps the protocol name for RPCs and DevFlow lookup. The LibreWPF
`PropertiesPad_BindingOptions_EditDelayModeAndStringFormat` journey proves `Text.Delay` and `Text.Mode`
both reach saved XAML. The same hazard existed for authored attached attributes in the WinUI
(`Grid.Row`) and MewUI (`DockPanel.Dock`) adapters; both now apply the same binding-safe descriptor
name while keeping the dotted label and source attribute. `WinUIDesigner_PropertiesPadEditsAttachedProperty`
and `MewUIDesigner_PropertiesPadEditsDottedAttachedAttribute` fail without the change and pass with
it. WinForms (CLR property names) and GTK (PascalCased GIR names) cannot produce dotted names.

Mutation acceptance is a shell-side invariant, not merely a host detail: GTK/MewUI now make all
ordinary edit paths return the host's `Accepted` result before dirtying or changing their local
Properties-pad snapshot, and WPF's adapter rejects before entering `OnPropertyEdited`. A failed
signal bind therefore cannot create a GTK code-behind method or show a handler that the child did
not accept.

**C4 update (2026-10-11).** The earlier C4 table text describing WinForms multi-selection delete
as non-atomic is superseded. Under protocol **v8**, one `design/delete-elements` request
prevalidates every identifier before the first `DestroyComponent`, reduces selected
parents/children to outer roots, removes the complete logical control/ToolStrip subtree from source
deepest-first, and uses one designer transaction. If a live-model or source-rewrite step fails, the
host restores the original source and rebuilds from that authority before returning rejection. The
v8 handshake prevents a newly deployed client from sending this array form to an old pooled child.
A focused LibreWinForms host test proves a root rejection retains both source and live state, then
proves selected parent/child declarations disappear together.

**C2 verification update (2026-10-09).** The macOS Uno journey now selects `PrimaryButton` in the
shared Properties pad, resets its authored `Content` through that live `PropertyItem` descriptor,
saves, and verifies that the named element remains while the XAML attribute is removed. The test
restores its copied fixture in `finally`; it proves reset is a source edit rather than a runtime-only
preview change.

The LibreWinForms journey also now verifies the visible `Text` descriptor end to end: after its
Properties-pad edit commits, the refreshed accepted snapshot exposes Reset; reset removes the
`label1.Text` source assignment. This prevents the fixed client-side `Text` proxy from masking the
remote descriptor's serialisation and reset semantics.

**C3 compatibility note (2026-10-09).** WPF always publishes its native text baseline. WinUI
hosts publish `BaselineOffset` only when the active compatible runtime exposes that optional
`TextBlock` member; otherwise they send null rather than failing a design session or inventing a
measurement. Consumers must continue to treat the field as nullable telemetry.

**C3 update (2026-10-11).** The shared Uno/WinUI `BuildTree` now publishes measured `GridTracks`
for every live `Grid`: `RowDefinition.ActualHeight` and `ColumnDefinition.ActualWidth`, with
cumulative offsets computed in the host. This is deliberately layout telemetry, not a source
interpretation: the focused Uno child-host test shows that its root grid is measured to the active
320px viewport, so a star column reports its actual 190px extent rather than an extent inferred
from the document's `Width` attribute. An empty definition collection remains an accepted Grid
with zero explicitly declared tracks. The Microsoft-host leg remains a Windows verification item.

**C3 follow-up (2026-10-11).** The WinUI designer now consumes those snapshot tracks when drawing
Grid divider guides: it converts each measured track start plus the measured far edge into the
shared canvas boundary form. The former XAML `Auto`/star approximation remains only as a fallback
for ProGPU or an unavailable/older runtime that does not publish `GridTracks`; it is no longer the
normal Uno/WinUI path. Runtime telemetry is accepted only for one unambiguous source-backed Grid
with the selected name; a duplicate x:Name in a template/data-template namescope suppresses the
guides rather than combining one instance's bounds or tracks with another's source definitions.

The resize commit now reuses those same guide boundaries to calculate the edited row or column
size. This keeps a drag over an `Auto`, star, or constrained track from looking correct on the
surface yet serializing a size reconstructed from a different source-only approximation.
The canvas reports a released divider in design-canvas coordinates, so the commit also removes the
selected Grid's measured X/Y origin before comparing it with its Grid-local track boundaries; this
is what makes the same interaction round-trip for a nested Grid rather than only a root Grid.

**C3 page-edge update (2026-10-11).** WinUI and ProGPU now opt into the shared canvas's preview
frame right, bottom and corner drag affordance. Its hit target stays six screen pixels wide at every
zoom level; an overlapping selected-element resize thumb wins, so the new affordance cannot steal a
normal element resize. Releasing a real drag updates only the runtime's configured design size and
re-renders; it deliberately does not author a root `Width`/`Height`, so pages that declare their own
size keep source authority. The edge-hit calculator has focused unit coverage for all edges, zoom
scaling and out-of-range rejection.

**C3 Canvas-position correction (2026-10-11).** WinUI canvas drags now preserve Canvas's actual
attached-property contract: a child with `Canvas.Left`/`Canvas.Top` moves those values, while a
child deliberately anchored with `Canvas.Right`/`Canvas.Bottom` updates the opposite distance in
the inverse direction. The shared drag path no longer creates a `Margin` on Canvas children; normal
layout containers retain the existing alignment-aware Margin behaviour. The same source edit path
serves both single-element and same-parent multi-selection drags.

**C3 WPF placement toggles (2026-10-11).** The in-process WPF designer's zoom bar offered raster
placement with a width and snapline placement (`DesignPanel.UseRasterPlacement`, `RasterWidth`,
`UseSnaplinePlacement`; defaults off, 5, on). The shared toolbar now has an opt-in `Snapping`
capability with the same three controls, bound to the canvas controller's existing `SnapToGrid`,
`GridCellSize` and `SnapToGuides`; WPF opts in and persists them as `WpfDesigner.*` preferences.
The gridlines overlay now draws at the controller's raster cell rather than a fixed 20 units, so the
lines mark the positions a raster drag snaps to (this also corrects WinForms, whose grid size was
previously used for snapping but not drawing). `Toolbar_SnappingControls_DriveTheDragSettingsAndPersist`
covers the defaults, the toolbar-to-controller binding, gridline spacing and persistence.

**C2 WinForms string collections, host slice (2026-10-12).** The in-process WinForms designer's most
used collection editor - a ListBox/ComboBox/CheckedListBox `Items` string list - now has an
out-of-process host contract under protocol **v21**. The Forms host publishes the same read-only
`ItemsCollection` row as WPF when the control has no `DataSource` and every item is a string, and
its optional `design/set-items` replaces the live collection in one designer transaction. Unlike WPF,
the Forms host patches the designer source rather than re-serializing components, so the mutation
also rewrites `InitializeComponent`: every `<id>.Items.Add/AddRange` statement is replaced by one
`Items.AddRange(new object[] { ... })` (`New Object() {...}` in VB) at the first replaced position,
or after the control's own last statement when the source had none; clearing removes them.
**IDE slice:** the dialog, its edit script and the commit rule moved out of the WPF add-in into
`Designer.Presentation` (`CollectionItemsEditing`, `IItemsCollectionEditorHost`), so the WPF and Forms
`ITypeEditor`s are thin shims over one implementation. The Forms Properties-pad proxy attaches the
editor to an `ItemsCollection` row and commits through `design/set-items` as one designer undo step;
`od.forms-designer.properties-pad.edit-items` drives the real row button with the shared script.

**C4 Cut, Copy and Paste (2026-10-13).** The out-of-process WPF surface had no clipboard at all;
the in-process designer's `XamlEditOperations` copied each selected element as a XAML snippet and
pasted into the selection or its nearest accepting ancestor. Protocol **v27** adds WPF's optional
`design/copy-elements` (read-only: one snippet per element, an element whose ancestor is copied
travels inside it, never the root) and `design/paste-elements` (parse with DDP's `ParseSnippet`, walk
up from the target to a content collection that accepts every element - UI elements only into a
Panel - or an empty single content; one undo group; `CreatedElementIds`). Unlike in-process, a pasted
`x:Name` the document already uses is renamed (`ok` -> `ok1`, `label1` -> `label2`), since a
duplicate name breaks the document. The IDE owns the clipboard: the snippets go to the system
clipboard as text (so they also paste into a XAML editor) and are kept for a multi-element round trip;
other XAML text pastes as one snippet. Cut is copy then the existing delete (one undo step). The
surface binds `ApplicationCommands` Cut/Copy/Paste - focus-routed, as the Forms designer does, so the
XAML source pane keeps its own text clipboard - and its context menu gains the three items;
`od.wpf-designer.clipboard` runs those bound commands. Protocol **v30** adds Canvas-only `Paste Here`:
the context-menu design point is converted to Canvas-local coordinates, and the host anchors the pasted
bounds there while preserving multi-item Canvas offsets, uniquifying names and recording one undo group.
Ordinary Paste retains its target/ancestor fallback semantics. Protocol **v35** extends Paste Here
to a Grid through `design/paste-elements-into-cell`: the innermost Canvas or Grid under the context
point is the target, and over a Grid the host assigns every pasted element the cell whose measured
row/column contain the Grid-local point (row/column 0 resets the attached property; spans, margins
and alignment stay as copied; an outside point is refused, never clamped), in one undo group.
Stack/Dock/Wrap panels still offer only ordinary Paste, because a point does not define their order
or dock side. Host and IDE tests: `ChildHost_PastesIntoTheMeasuredGridCellUnderThePoint` and
`Clipboard_PasteHerePlacesIntoTheGridCellUnderTheContextPoint`.

**C4 Duplicate (2026-10-13).** WPF now offers Duplicate in the surface context menu and through
Ctrl+D. It copies the selected same-parent roots through the existing read-only host serializer and
pastes them into that collection parent in one host undo group, so it does not touch the system
clipboard or ever duplicate a container into itself. Protocol **v28** adds the WPF-host-authoritative
`DesignerElementNode.HasContentCollection` fact: full single-content parents (for example `Window`,
`Border` and `Viewbox`) are deliberately unavailable rather than inferred from a type-name/layout
heuristic, because ordinary clipboard Paste may walk upward from them while Duplicate must never
change the selected roots' parent. Cross-parent and parent/child selections, and Duplicate during
inline text editing, are disabled; the created copies receive the host's ordinary unique names and
become the new selection.

**C4 no lost edits after a save (2026-10-12).** In-process edits were never refused; out of process a
WPF edit made just after a save or a text edit could be rejected as a stale base version and silently
dropped. The IDE pushes the new source with `session/update` at a new version, asynchronously, and the
canvas kept using its last state's version until that reply arrived; the WPF child dispatches requests
without ordering, so a mutation in that window could not simply be sent at the new version either.
`WpfSurfaceDesignerControl.RequireVersion` - now the base version of every canvas, context-menu and
Properties-pad mutation - waits up to 5 s for an in-flight update (its reply completes off the
dispatcher) and uses the version in that reply when the host accepted it; past the bound the edit goes
out at the known version and, at worst, is refused visibly as before. The same review found the first
edit after a load could not be undone: the surface showed (and accepted edits) before the undo
baseline was flushed, so that baseline is now taken before `Show`. The Outline toggle journey, which
hit the stale version once, no longer retries.

**C4 Outline drag and drop (2026-10-12).** The in-process WPF Outline moved elements by dragging
rows (`OutlineNodeBase.Insert`): into the target's content property, after a sibling in a collection
or as the single content. Protocol **v26** adds WPF's optional `design/move-into(elementIds, target,
after)`, one undo group that reports the moved element's new path. It is deliberately stricter than
in-process: a full single-content target is refused (in-process silently replaced its content), as
are moving the root, into itself or a descendant, a single content of the wrong type, and a
non-UIElement into a Panel. The shared `DocumentOutlineControl` gains an opt-in `AllowElementMoves`
with row drag and drop - the top/bottom quarter of a row inserts before/after it in its parent, the
middle drops into it after its last child, highlighting the target - raising `MoveRequested`;
`od.wpf-designer.outline-move` takes the same `RequestMove` path. Protocol **v29** adds atomic
`design/clone-into(elementId, target, after)` for Ctrl-drag copy: it keeps the source, uniqueifies
names, honors the exact target and insertion rail, selects the clone, and rejects self/descendant or
full single-content targets. `od.wpf-designer.outline-copy` takes the same `RequestCopy` path.
Ctrl-selecting Outline rows keeps the shared canvas selection in sync and a row drag moves the
selected outer roots through one `design/move-into` transaction; nested selected children travel
with their parent, while Ctrl-copy remains deliberately single-row. Moving a child now clears
authored Grid/Canvas/Dock attached layout properties
that are not meaningful under its new parent, in the same undo group, while preserving the new
parent's own layout property family.

**C4 Outline design-time flags (2026-10-12).** The in-process WPF Outline had an eye and a lock
toggle per row, writing `d:IsHidden` (DDP hides the element at design time only) and `d:IsLocked`
(the surface's hit test skips the element and its children). Protocol **v25** adds nullable
`DesignerElementNode.IsDesignTimeHidden/IsDesignTimeLocked` - null where a backend has no such flag,
so other designers' outlines are unchanged - and WPF's optional `design/set-design-time-flag`, which
sets the attribute or resets it on clear. The shared `DocumentOutlineControl` shows the two toggles
(VS 2026 HideMember and Lock/Unlock glyphs, faint while unset) only for nodes that carry the flags and
raises `DesignTimeFlagToggled`; `DesignerSnapshotHitTester` skips a locked subtree, so a click falls
through to what is behind it while the Outline can still select it. `od.wpf-designer.outline-toggle`
clicks the real row toggle.

**C2 brush editor (2026-10-12).** In-process WPF had DDP's BrushEditor (solid/linear/radial with
stops); the OOP host published any element-syntax brush - every gradient - as a read-only
`Reference`, so a gradient could be neither edited nor created. The editor is modelled on Visual
Studio/Blend's: brush-kind tabs (No brush, Solid, Gradient), a gradient slider whose stops are added
by clicking, moved by dragging and removed by dragging off, linear/radial, reverse and the selected
stop's offset, one color editor (saturation/value field, hue slider, R/G/B/A, hex, initial/current
chips) for the solid color or the selected stop, and an advanced section (opacity, start/end or
center/origin/radii, mapping mode, spread method). **Host slice:** protocol **v23** adds
`DesignerPropertyInfo.Brush` (`DesignerBrushInfo` with those settings and its stops) and optional
`design/set-brush`. The host describes a brush only when the editor can round-trip it - a transform
or non-default color interpolation keeps it a read-only reference - also for an attribute-text,
unset (its effective value) or `{x:Null}` brush, and writes only non-default settings (a solid as
attribute text, "No brush" as `{x:Null}`, a gradient as a property element) in one change group.
**IDE slice:** `BrushEditModel`/`BrushEditing`/`BrushEditorDialog` live in `Designer.Presentation`
beside the items editor; the WPF row (`WpfBrushEditor`) shows a swatch and a "…" button and commits
through `IBrushEditorHost`. `od.wpf-designer.properties-pad.edit-brush` drives the real button with
an edit script. **Resources tab:** protocol **v24** adds a `Resource` brush kind (a
`{StaticResource key}` reference, which the host now describes instead of leaving a read-only
reference) and `DesignerPropertyInfo.BrushResourceKeys`, the in-scope Brush resource keys (own,
ancestors', merged and application dictionaries - the converter-key walk, generalized); the dialog's
Resources tab lists them and `design/set-brush` accepts only one of them. Not yet: the eyedropper,
color resources and system resources, tile brushes, other color spaces, and the on-canvas gradient
and brush-transform tools.

**C4 WinForms Toolbox components (2026-10-12).** The WinForms Toolbox catalog lists Timer, ToolTip,
ImageList, ErrorProvider, BackgroundWorker and the other non-visual components, but the child's
`design/add-element` resolved only `Control` types, so dropping any of them failed with
"Unsupported WinForms control type". A creatable non-Control `IComponent` marked
`[DesignTimeVisible(true)]` - the tray classifier's own rule, which keeps out ToolStripItem,
ColumnHeader and DataGridViewColumn, components that exist only inside their owner - now joins the
component tray as the in-process drop did: it is created in one designer transaction, and the source gains its
field and creation statement - into `components` when the type has an `(IContainer)` constructor,
declaring the field and initializing `components` first when the form has none (C# and VB). The
loader already read such tray components back. Project components (non-Control types in the
project's own assemblies) can now be added the same way, and `FormsProjectToolbox` lists them
(public concrete `Component`/`IComponent` types with a default constructor). The metadata walk reads
`[ToolboxItem]` and `[DesignTimeVisible]` as inherited attributes, nearest declaration first, as
TypeDescriptor does: a project subclass of a `[DesignTimeVisible(false)]` component (or of
`ToolStripItem`) or of a `[ToolboxItem(false)]` control stays out of the Toolbox unless it
re-declares the attribute.

**C5 WinForms project controls in the Toolbox (2026-10-12).** The in-process designer's
`CustomComponentsSideTab` listed the project's own components; the OOP Toolbox offered only the
fixed WinForms catalog, although the Forms child could already resolve a project control by name.
`FormsProjectToolbox` now adds, one category per assembly, the public concrete `Control` types with
a public parameterless constructor from the project output and its copy-local references (the same
assemblies the snapshot passes the child), skipping `[ToolboxItem(false)]` and `Form` subclasses.
Framework and runtime copy-local assemblies (`System.*`, `Microsoft.*`, LibreWinForms/LibreWPF...)
are ignored, as the in-process tab ignored the GAC, so the catalog is not repeated; an assembly is
rescanned only when its timestamp changes. This decision is `WinFormsProjectControlFinder`, in
FormsDesigner.Remote so host tests cover it; `FormsProjectToolbox` only builds the items.
Discovery is metadata-only: the WPF PE-metadata walker moved to `Designer.Remote` as
`ToolboxMetadataDiscovery` with a base-type parameter, constructor and `ToolboxItem(false)` flags,
so the IDE never loads a project assembly. Each item is a `ToolboxItem` carrying only `TypeName`;
the drop and click paths already used nothing else. Items are added when a form is opened; the
shared Toolbox has no removal, so a rebuilt assembly contributes only new controls.

**C2 attached layout properties (2026-10-12).** As in the in-process Properties pad, a WPF element
now shows the attached layout properties its parent panel defines - `Grid.Row/Column/RowSpan/
ColumnSpan`, `Canvas.Left/Top/Right/Bottom` or `DockPanel.Dock` - in a Layout category, named as
XAML spells them. They edit and reset through the ordinary `design/set-property` and
`design/reset-property`, which now both resolve that `Owner.Member` form; the Properties-pad adapter
already carried dotted names through Xceed's binding-safe row names. No protocol change.

**C2 quick property commands (2026-10-12).** The in-process `QuickOperationMenuExtension` (the
small per-element menu) offered Horizontal/Vertical Alignment, StackPanel Orientation and DockPanel
Dock as one-click property edits. These are now WPF context-menu submenus for a single selection,
built on the existing `design/set-property`: the snapshot's current value is disabled, Orientation
needs a StackPanel and Dock a DockPanel parent. `design/set-property` now accepts a
`System.Windows.Controls` attached property by its XAML name (`DockPanel.Dock`), and an unknown
property name is a rejection rather than an RPC exception. Its "Edit Items" entry is the collection
editor above; "Edit Rows/Columns" (DDP's `FlatCollectionEditor`) remain covered by the rail's split,
resize and unit commands. The in-place editor's "font commit" was reviewed and is not a gap: it
writes back the RichTextBox's own font, which is copied from the TextBlock and never changed by the
plain-text editor. `ChooseClassServiceBase` has no caller in DDP, so there is nothing to port.

**C3 Grid track units (2026-10-12).** The in-process Grid rail's `GridUnitSelector` (Fixed/Star/Auto
for the hovered row or column) is now a pair of WPF context-menu submenus, Grid Row and Grid Column,
acting on the selected Grid's track under the right-click point. Protocol **v22** adds the authored
`Unit`/`Value` to `DesignerGridTrackInfo`, so the canvas disables the track's current unit from the
snapshot alone, and the optional `design/set-grid-track-unit` mutation. The host keeps the numeric
part as the in-process designer does, but leaving Auto (whose value is a meaningless 1) takes the
measured size for Fixed and `1*` for Star rather than writing a 1-pixel track. `DesignSurface`

records the context menu's design point (null when opened from the keyboard);
`od.wpf-designer.context-menu-point` sets it relative to the selection for tests. WinUI still reads
its tracks from source and leaves `Unit` null.

**C3 WinForms TableLayout boundary (2026-10-13).** The first safe host slice is now landed:
`TableLayoutPanel` tree nodes publish the live child’s measured `GetRowHeights`/`GetColumnWidths`
and cumulative offsets. They deliberately leave authored unit/value empty: WinForms creates default
`Absolute` styles at runtime even where Designer.cs has no style declaration, so live style data is
not proof of a safe source write. Forms still remains out of the shared Grid-guide affordance because
its client projection does not yet carry host-tree `GridTracks` into `CanvasSnapshot`; more
importantly, a divider is not a control Bounds mutation. The next slice must add an explicit
host-owned track mutation which proves and rewrites the matching designer source style collection in
one transaction. Do not show draggable rails until both halves exist:
the current canvas emits a commit on release, and a visual-only rail would falsely imply a saved
layout edit. This is separate from WinForms' existing pixel-grid/snap preference support.

**C3 WinForms TableLayout style mutation (2026-10-13).** The host half now exists. Protocol **v32**
adds the Forms host's optional `design/set-table-layout-track(elementId, isRow, index, sizeType,
value)`. It is accepted only when every C# `InitializeComponent` statement that mentions the
table's `RowStyles`/`ColumnStyles` is a plain `Add(new RowStyle/ColumnStyle(...))` and their count
equals the live style count, so the requested index provably names one authored statement; a
`RemoveAt`/`Clear`/indexer, an initializer, or runtime-only default styles are rejected without
touching source or live state. The proven statement is rewritten in place (`AutoSize` without a
value; `Absolute`/`Percent` with a finite non-negative float), then the live style changes in one
designer transaction. (VB was initially rejected; see the client-half note.) Host tests cover the edit, re-measured tracks,
flushed source and each rejection.

**Client half (2026-10-13).** Selecting a single, unlocked TableLayoutPanel now shows the shared
canvas's blue divider guides from the host tree's measured `GridTracks`, placed at the component's
surface bounds; any other selection clears them. Releasing a divider sends protocol **v33**'s
`design/resize-table-layout-track(elementId, isRow, index, size)`, one designer undo step. As in
the in-process TableLayoutPanel designer, two neighbouring `Percent` tracks keep their summed
percentage and are redistributed in proportion to the dragged measured sizes (rounded to 0.01);
otherwise the track before the divider becomes `Absolute` at the dragged size. Both styles share
one proof, source rewrite and transaction with `design/set-table-layout-track`; a refused edit (unprovable style list) shows the host's error and the guides return to
the measured layout. Forms opts out of the shared Ctrl insertion rails (`GridTrackSplitEnabled`),
because it has no split mutation and Ctrl-click must stay a multi-selection gesture.
`od.forms-designer.table-layout-guides` reports the shown guides and
`od.forms-designer.table-layout-divider` releases a divider through the same commit path; the
LibreWinForms journey `WinFormsDesigner_TableLayoutDividerWritesTheAuthoredColumnStyle` proves the
guides, the re-measured divider, the saved `ColumnStyle` and undo; a host test locks the Percent
redistribution and the Absolute fallback. VB designer source is now proven and rewritten the
same way (`Me.<id>.ColumnStyles.Add(New ColumnStyle(...))`, untouched statements keep their `50.0!`
literals); `ChildHost_ResizesVisualBasicTableLayoutStylesInPlace` covers the Percent and Absolute
paths and a `Clear()` rejection.

**Track add/remove, host slice (2026-10-13).** Protocol **v34** adds
`design/add-table-layout-track(elementId, isRow)` and `design/remove-table-layout-track(elementId,
isRow)`, for C# and VB. Both require the style-list proof plus exactly one integer-literal
`RowCount`/`ColumnCount` assignment equal to the live count, with one style per track. Add appends a
track copying the last style's SizeType and length, after the last style statement; remove deletes
the last track only while two or more exist and no child's cell or span reaches it, so no control is
moved or deleted. The Microsoft TableLayoutPanelDesigner's own Add/Remove Row/Column verbs change
only the live table (verb sync rewrites added/removed components, not Count/styles), so the IDE must
not expose them as source edits. **Menu slice:** right-clicking a TableLayoutPanel now offers Add
Column, Add Row, Remove Last Column and Remove Last Row, each one undoable source edit through those
RPCs (a refusal shows the host's reason); the designer's same-named live-only verbs, and "Edit Rows
and Columns...", are dropped for TableLayoutPanel owners. `od.forms-designer.context-menu-entry`
invokes a built entry by label; the LibreWinForms TableLayout journey adds a row from the menu,
saves `RowCount = 2` with a second style, and undoes it. Not yet: insertion before an arbitrary
track, removing an occupied track, and a rows/columns dialog.

**TableLayout loader fix (2026-10-13).** The snapshot loader called only one-argument `Add`, so a
child authored as `table.Controls.Add(child, column, row)` auto-flowed into the next free cell at
design time, and `SetColumn/SetRow/SetCellPosition/SetColumnSpan/SetRowSpan` were ignored: a
designed TableLayoutPanel could show its controls in the wrong cells and spans. The C# and VB loaders
now apply that overload and those calls; `ChildHost_LoadsTableLayoutCellPositionsAndSpans` fails
without the fix.

**Middle-track insert/remove, host slice (2026-10-13).** Protocol **v36** adds
`design/insert-table-layout-track(elementId, isRow, index)` and
`design/remove-table-layout-track-at(elementId, isRow, index)`, for C# and VB. Insert copies track
`index`'s style before it; children starting at or after it move one cell on, and a child spanning
across it grows by one, as the in-process designer's Insert did. Remove is refused while any child's
cell or span covers the track; children after it move one cell back. Besides the track-list proof,
the host proves every child's placement: each live child has exactly one `Controls.Add(child,
column, row)` with integer literals, at most one literal `SetColumnSpan`/`SetRowSpan`, the literals
equal the live cell and span, and no other placement statement (`SetColumn`, `SetRow`,
`SetCellPosition`, an unpositioned `Add`, `AddRange`) mentions the table. Only those literals, the
Count literal and one style statement are rewritten, in one designer transaction. **IDE slice:**
the Forms surface now records the right-click design point (`ContextMenuDesignPoint`), and a
TableLayoutPanel's menu adds Insert Column Before / Remove This Column and Insert Row Before /
Remove This Row for the measured track under it (absent when the menu was not opened over the
table); a refusal shows the host's reason. `od.forms-designer.context-menu-point` sets the point
relative to a component for automation, and the LibreWinForms TableLayout journey inserts and then
removes a column at a pointer-chosen position.

**C4 event handler generation (2026-10-12).** Double-clicking a WPF event in the Properties pad now
matches the in-process designer (`AbstractEventHandlerService`): the XAML attribute is bound through
`design/set-event` if the event is unhandled, then the IDE creates the method in `<file>.xaml.cs`
(the `x:Class` class, else the file's first class) and opens it; a bound event opens its existing
method, and an existing method is never duplicated. The isolated host still never touches project
code: generation is IDE-side Roslyn (`WpfCodeBehindHandlers`), formats only the inserted member,
edits the open editor's buffer when the file is open (its own undo stack) and otherwise rewrites the
file preserving its UTF-8 BOM. The argument type comes from the host's delegate type
(`EventHandlerSignature`: `XxxEventHandler` takes `XxxEventArgs`, `EventHandler<T>` takes `T`).
This supersedes the earlier "attribute only, generation deferred" decision; the XAML edit and the
code-behind edit stay on separate document undo stacks, as in-process.

**C2 collection editor, slice 1 (2026-10-12).** Section 5 item 11 starts with the narrowest
useful collection: the plain items of a WPF ListBox, ComboBox or ListView. Protocol **v20** adds
`DesignerPropertyInfo.CollectionItems` and the optional `design/set-items`. The host publishes a
read-only `Items` row (EditorKind `ItemsCollection`, Kind `Collection`) only when there is no
`ItemsSource` and every item is the control's own container (`ListBoxItem`/`ComboBoxItem`/
`ListViewItem`) holding plain text or nothing; anything else stays non-editable. `design/set-items`
takes the whole edited list, each entry naming its previous index (or -1 for a new container), so
moved items keep their `x:Name` and other attributes, omitted items are removed, and the change is
one undo group. **Slice 2:** the WPF Properties-pad descriptor attaches an Xceed `ITypeEditor` to an
`ItemsCollection` row (the WinForms `ResourceImage` pattern): the row shows the item count and a
"…" button opening an IDE dialog (add, remove, move up/down, rename) over the framework-neutral
`CollectionItemsEditModel`, which tracks each entry's original index and commits only a changed
list, as one undoable `design/set-items`. `od.wpf-designer.properties-pad.edit-items` clicks the real
button and applies an edit script to the same model in place of the modal window. The editor reaches its
host through the row's own `PropertyItem.Instance` rather than a visual-tree walk to the grid, which
failed when the row was not realized. **Slice 3:** the same row and
dialog edit a TabControl's page headers: each editable control maps to its item container and text
property (`Content` for list items, `Header` for `TabItem`), so moving a page keeps its body content
and only header text changes; a page whose header is an element rather than text keeps the
control non-editable. Edit Style (the
remaining in-process context command) is deferred: it would serialize the child runtime's default
theme style, which under LibreWPF is not the Microsoft theme the project ships with.

**C4 context menu (2026-10-12).** The WPF design surface's context menu now carries the in-process
ArrangeItems and RightClick menus: an Align submenu (left/center/right/top/middle/bottom to the
primary selection, enabled for two or more selected elements, reusing the existing atomic bounds
batch) and an Order submenu (Bring to Front, Bring Forward, Send Backward, Send to Back, enabled for
one element). Protocol **v18** adds the optional host-owned `design/set-z-order`, which reorders the
parent's collection and returns `MovedElementId`, because WPF ids are tree paths and the moved
element must stay selected under its new id. The shared `SetContextCommands` gained `Group/Item`
submenus and an optional per-command enablement callback; `od.wpf-designer.context-commands` and
`od.wpf-designer.context-command` exercise the popup menu semantically. Wrap/unwrap (Canvas, Grid,
Viewbox) and Edit Style were then the remaining in-process context commands.

**C4 wrap/unwrap (2026-10-12).** Protocol **v19** adds the optional host mutations
`design/wrap-elements` (siblings into a new Canvas, Grid or one-child Viewbox, keeping their rendered
placement; the new container is returned as `CreatedElementId` and selected) and
`design/unwrap-element` (a Grid or Canvas, keeping its children in place), each one undo group. They
call DDP's `ModelTools` like the in-process Wrap/Unwrap menus, with two corrections: wrapping uses
`doInsert: false` and inserts the container itself, because `ModelTools`' final
`Selection.SetSelectedComponents` raises in-process selection adorners that need a DesignPanel the
headless host lacks; and `UnwrapItemsFromContainer` measured the container in its own placement
(always 0,0), so unwrapped children lost the container's offset, also in the in-process designer.
The WpfDesigner submodule fix measures the container in its parent's placement and adds the offset
to Grid margins (right/bottom-anchored children keep DDP's existing approximation). Edit Style is
the remaining in-process context command.

**C2 Binding converter (2026-10-12).** Protocol **v17** adds WPF's optional
`design/set-binding-converter`. A Binding's `Converter` row is a drop-down of the string keys of
IValueConverter resources visible to the element (its own and ancestors' `Resources`, merged
dictionaries, then the application's), written as `Converter={StaticResource key}`; an empty choice
resets it. The row exists only while the converter is unset or already a StaticResource reference,
and the host rejects keys outside that scope, so an inline converter object or another markup form
is never overwritten.

**C2 inline-editor keys (2026-10-12).** Section 2's `InPlaceEditor` contract is ported to the WPF
inline editor through `InlineTextEditKeys`: Enter commits, Escape cancels, and Shift+Enter inserts a
newline when the edited value is a TextBlock's `Text` (elsewhere it commits like Enter), with the
same key hint as the in-process tooltip. The macOS journey opens the editor with a native
double-click, holds a native Shift, and saves `Text="Line1&#xA;Line2"`. It first presses the
toolbar Fit (new `od.designer-canvas.fit`) and requires the target inside the visible viewport,
because a view restored from an earlier journey on the same file could leave it panned out of view.
`CtrlPointerGestures` still fails occasionally (about 1 in 8 class runs with DevFlow 0.3.1, down from
2 in 4) at its deselect step; that residual cause is not yet identified.

**C3 zoom rounding (2026-10-12).** Section 2's `RoundToOneIfClose` is ported as
`ZoomStepCalculator`: a Ctrl+wheel zoom step within 0.001 of 100% now lands on exactly 1.0, so
stepping in and back out restores a 1:1 frame instead of `1.0000000000000004`. Its unit tests await
`LeXtudio.DevFlow.Agent.Core` 0.3.1 appearing on nuget.org, without which restore fails.

**C3 Fit correction (2026-10-11).** The shared canvas's Fit scaled the frame to the whole scroller
and then offset it by the 32-unit `CanvasMargin`, so a fitted design overflowed the right and bottom
edges by that margin and its bottom-right resize handle was clipped. Fit now scales into the extent
left after the margin on both sides, so every backend's fitted frame is fully visible. The surface
geometry probe also reports the visible `viewport`, `handleVisible` and the frame's zoom-independent
`designSize`; the WinForms resize journey falls back from 50% to Fit when a split Design/Source pane
hides the handle and judges growth in design units (a fitted form is re-scaled, so its screen size
barely changes), and the MewUI journey asserts that a fitted frame fits the viewport rather than an
arbitrary minimum scale. `od.designer-canvas.*` now falls back to the only open designer canvas
when activation has moved to another document (a WPF load can activate App.xaml), and the WPF
snapping journey closes all document views first so a view left by another journey is not reported.
`CtrlPointerGestures_DistinguishClickCollapseFromGroupDrag`'s intermittent macOS failure was not a
visibility problem (both targets are inside the viewport). DevFlow holds Ctrl and presses the mouse
in separate CliclickSharp processes, and its mouse events carried no modifier flags, so LibreWPF's
`Keyboard.Modifiers` lost Ctrl at the mouse-down (observed: `Control` before the press, `None`
after it) and the canvas, which reads Ctrl at mouse-up, saw a plain click. The fix belongs to
wpf-labs' cliclick-sharp (mouse events now carry the session's held modifiers); with that build from
a local DevFlow package the journey passed in every class run. The journey also confirms through
`od.keyboard.modifiers` that the IDE sees the held key before clicking, so a missed key-down is
reported as such instead of as a selection mismatch.

**C3 WinUI Canvas insets (2026-10-11).** The Uno host now publishes measured Canvas child edge
distances as `CanvasPosition`, and the shared L/T/R/B labels are a real source-edit path rather
than a read-only overlay: a released edge label converts its measured distance into a Canvas move
delta, preserving the source's Left/Right and Top/Bottom anchoring form. A binding/resource
expression remains untouched because it cannot be safely offset as a numeric source edit.

**C3 WinUI Grid track split (2026-10-11).** The Uno/WinUI surface now consumes the shared
Ctrl/Ctrl+Shift insertion rails when its snapshot supplies measured tracks. Splitting inserts a
source `RowDefinition` or `ColumnDefinition` at the real measured position, preserves pixel/star
units (materialising `Auto` to its measured pixels), and adjusts affected `Grid.Row`/`Column` and
span attributes in the same document-history edit. ProGPU deliberately does not opt in: its
source-only guide approximation is insufficient evidence for a safe structural mutation.

### Why this order

`C1` removes a correctness and responsiveness hazard in every click path. `C2` then closes the
largest day-to-day parity gap: the protocol already carries typed property metadata, but the shell
does not use it to provide editors. `C3` makes layout work feel like a designer rather than a bitmap
editor. `C4` turns the now-common protocol into consistent user-visible editing semantics. `C5`
hardens the boundary that motivated process isolation in the first place.

Framework-specific work belongs inside these phases rather than becoming a competing roadmap:
WinForms needs complete resize parity; WPF needs event binding and richer keyboard/multi-selection
interaction; WinUI needs component-specific tray mutations and edge-drag page resizing. GTK and MewUI should receive
the shared C1/C2/C3 capabilities before new framework-specific surface features.

## 8. Uncertainties

Stated plainly, because the report's value depends on reliability rather than confidence.

- **Nothing was compiled or run.** The "cannot compile on WinUI 3" claim about
  `XamlXmlTreeCoordinator` rests on the UWP-only API names it references; a transitive `using:` alias
  could in principle paper over one, though not five.
- **Non-WPF files were classified from their type/member surface and extension attributes**, not by
  reading them end to end. The WPF `Controls/`, adornment, gesture, placement, property-grid and
  XAML round-trip paths *were* read in full.
- **`GetBaseline` needs a text-layout result.** Whether WinUI's layout can report one per node from
  inside the host is unverified; it may force dropping baseline snapping for that framework.
- **xamlstudio's `.xaml` files were not inventoried** (only the 135 `.cs`). Visual-state
  definitions, `Styles/VSCode.xaml` and `Package.appxmanifest` may carry designer-relevant behaviour
  not seen here.
- **`XamlRenderServiceTests.Resources.cs` / `.Bindings.cs` were enumerated, not read** — they are
  characterised here as a spec for binding-telemetry and resource-injection behaviour, unverified.
- **Whether the Microsoft host populates `VisualStateGroups` in practice is unverified** — the code
  is present via source-linking, but the host was not run. The source-driven ComponentTray path
  compiles in the common host surface but still needs a live Microsoft-host interaction check.
- **xamlstudio's property-grid internals** were read only around the visual-state block; the "already
  have it" verdict rests on the existence of `WinUIXamlElementPropertyAdapter.cs`, which was not
  opened.
- **The Stetic `AtomicChange` and session-restore mechanics were read from source, not exercised.**

## 9. Outstanding work

### 9.1 WinForms in-process baseline — initial inventory complete

The actual removal boundary is `af32ec7e` (**Remove in-process legacy designer**), whose parent is
`40e32d7c`. This follows the out-of-process WinForms port (`95339845b4`); `git ls-tree -r
af32ec7e^` exposes the complete legacy FormsDesigner tree without touching the active worktree.
The original investigation's `class DesignSurface` search was a false lead: the current shared canvas
was introduced later (`01d58df2`), while the legacy implementation lived under the FormsDesigner
addin rather than `src/Main/Designer/`.

The focused inventory changes the ranking materially:

| Legacy capability | Evidence at `af32ec7e^` | Current OOP disposition |
|---|---|---|
| Keyboard select/move/resize/nudge/delete and ToolStrip-specific keyboard routing | `FormKeyHandler` maps arrows, Shift+arrows, Ctrl+arrows, Tab, Delete, and defers active template editors to WinForms' keyboard service | The shared canvas has nudge, tab-order and context/ToolStrip editing paths; preserve focused interaction checks rather than re-porting a WinForms message filter. |
| Grid/snap preferences | `GridOptionsPanel` persists ShowGrid, SnapToGrid, SnapLines and X/Y grid size | Now consumed by the OOP Forms canvas; C3's cross-framework guide work is additive, not a parity repair. |
| Undo | `FormsDesignerUndoEngine` wraps real designer transactions | OOP Forms uses child transactions/source rewrite and WPF uses source-authoritative document undo. Keep the atomic-operation tests; do not recreate the legacy live-object engine in the IDE. |
| Resource, image, collection and type-selection editors; sidebar/custom component catalogue | Concrete `ImageResourceEditor`, resource serializers/stores, `TypeDiscoveryService`, `AddComponentsDialog` and sidebar classes were live files | This is the strongest remaining *Forms-specific* parity area. C2 should prioritize advertised resource/image/collection/ChooseClass editors behind capabilities, not blanket text serialization. |
| Event handler generation | `Services/EventBindingService.cs` contains an entirely commented-out class, including method-name generation and ShowCode hooks | **Not a working legacy baseline.** The current safe XAML event-attribute binding is already ahead for WPF. Transactional WPF code-behind method generation remains a separately justified enhancement, not a parity prerequisite. |

Thus the next cross-framework order is: retain C1 recovery validation; finish C2's capability-gated
complex/resource editors; continue C3 measured layout interaction only where the host can write the
container's real contract; then pursue optional code-behind event generation as a new feature with a
source transaction boundary. The old `IComponentChangeService`/`DesignerWidget` arrangement is not
an architecture to restore: it is precisely the in-process target-type boundary the OOP cutover
removed.

#### Resource-editor boundary (2026-10-09)

The initial C2 follow-up confirms that `ImageResourceEditor` and its project-resource dialog still
live in the IDE-side FormsDesigner addin, but the OOP Forms child deliberately reports `Image` as
the opaque, read-only `Unsupported` value `[binary]`. This is correct today: the child has neither
the IDE's `ProjectResourceService` nor authority to mutate project resources, run the resource
generator, or write source files. Do not make `[binary]` an editable string to recover the old UI.

The current document boundary reinforces that constraint: `CreateRemoteSnapshot` includes resource
files as base64 snapshots for the child to read, while `SynchronizeRemoteEdits` intentionally
ignores `Resource` entries returned by `session/flush`. A child-originated resource write therefore
cannot accidentally reach disk today. The new path must preserve that directionality: the IDE edits
the `ResourceStore`/project model first, then sends the resulting complete source-and-resource
snapshot back to the child for reload.

The next implementation must instead be an IDE-owned, capability-gated request: the host declares
that a selected property has a supported resource/image/collection editor; the IDE opens its own
editor against the authoritative project model; and the resulting source/resource edit returns as
one versioned document update for the child to reload. Definition of done: unsupported runtimes keep
the current read-only token; a supported image edit updates the resource plus designer source as one
undoable IDE operation; child processes never gain file-write or project-service access; and tests
cover both the supported round trip and the safe unsupported fallback.

Implementation order: first add optional resource-origin metadata to `DesignerPropertyInfo` only
when the Forms loader can prove that a property came from a resource key in a snapshotted file;
then have the IDE offer the editor exclusively for that capability, capture source/resource before
images, and apply the edited resource plus any required reference rewrite as one undo record before
issuing `session/update`. Do not infer an origin from `[binary]`, a property name, or a converter
string.

The first capability value is now `ResourceImage`: Forms emits it only for an `Image` or `Icon`
property with proven `ResourceFileName` and `ResourceKey`; scalar resource values intentionally
leave it empty. The IDE renders an explicit replace button in that property's row; it is an
IDE-editor eligibility signal, not permission for the child to mutate resources. The LibreWinForms
integration journey now uses actual PNG and ICO payloads to prove both a child control's `Image`
and the root Form's `$this.Icon` replace, save, undo and redo independently, including the
invariant that undoing Icon leaves the preceding image replacement intact.

### 9.2 Tooling notes for whoever continues this

- **`timeout` does not exist on macOS** (it is `gtimeout` from coreutils). Several `git log` queries
  during this analysis returned empty *because of that*, not because of history simplification. Any
  apparently-empty history result on this machine should be re-run without `timeout` before being
  believed. This cost real time and nearly produced a second wrong conclusion.
- **`git log --diff-filter=A -- <file>` can return nothing** for files that plainly exist in history.
  Fall back to `git ls-tree -r <ref> --name-only` and read the file at a known ref directly, which is
  what resolved the `95339845b4` boundary.
- The three baselines are **submodules with two branches each**; `lextudio/WpfDesigner` has
  `opendevelop` (pinned by OpenDevelop) and `vscode` (standalone checkout) at 358 files each, so
  either ref is a valid reference and a file-count comparison will not distinguish them.
- `OpenDevelop-5.5.8` (`3559c44614`) is a separate full clone. It is **post**-port for both WPF and
  WinForms and is not a pre-port baseline for either.
