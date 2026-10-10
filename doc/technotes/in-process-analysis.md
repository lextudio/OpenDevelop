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
| **WinForms** | SharpDevelop's in-process WinForms designer + the shared in-process designer library | **git history only** — see correction 3 | not yet inventoried | **outstanding** |
| **WinForms (secondary)** | `mono/monodevelop` @ `ba01d2d6d3`, `MonoDevelop.DesignerSupport` | `externals/monodevelop/main/src/addins/MonoDevelop.DesignerSupport` | 84 `.cs` | analysed; partial value only |
| **WinForms (secondary)** | Stetic — the GTK# designer, `MonoDevelop.GtkCore` | `externals/monodevelop/main/src/addins/MonoDevelop.GtkCore` | 258 `.cs` / ~49 800 lines | analysed; three transferable ideas |
| **WinUI** | `lextudio/xamlstudio` @ `d711d64` | `externals/xamlstudio` | 135 `.cs` / ~14 600 lines | analysed |

The WPF and WinUI baselines are open source and present as submodules. **The WinForms baseline is not**,
and that is the single largest gap in this report — see correction 3 and section 9.

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
- The replacement commit for WinForms is **`95339845b4 Implement out-of-process WinForms designer`**.
  Its parent `feec3616f4` still carries `Designer/Project/FormsDesigner.csproj`. At that commit the
  shared library had *already* been reduced to out-of-process projects only, so the in-process
  surface was removed by an **earlier** commit than the WinForms port — the exact commit has not yet
  been identified.
- `OpenDevelop-5.5.8` is **not** a pre-port baseline for WinForms either: it already contains
  `Project/Src/OutOfProcess/RemoteFormsDesignerControl.cs`.

**Consequence for this report.** Section 3 below is *not* the WinForms baseline. It is a secondary
reference whose value survives the correction: MonoDevelop's toolbox abstraction is genuinely richer
than ours, and Stetic independently solved three problems we have not (session state across a child
crash, property values that live in the model rather than the control, and transaction coalescing).
But **the actual SharpDevelop in-process WinForms designer and its shared design library have not
been inventoried.** That is now the highest-priority follow-up, and section 9 records it.

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
| WinUI ProGPU | `All & ~VisualStates & ~ComponentTray` | 7 |
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
> and is not yet inventoried (section 9.1). What follows is retained because two specific,
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

**The genuine WinUI capability gap is `ComponentTray`**, and it is WinUI-side only:
`TrayComponents` / `IsTrayComponent` is populated solely by
`WpfDesign.SurfaceHost/WpfSurfaceHostService.cs:1809-1826,1946` and
`FormsDesigner/Host/DesignerHostService.cs:2080,2107,2214`. Nothing in `WinUIXamlDesigner` sets it.

---

## 5. What to port, ranked

Ranked by (a) parity gap closed per line, (b) genuinely missing rather than re-implemented,
(c) framework-neutral so one implementation serves all five, (d) verifiability.

| # | Item | Source | Why |
|---|---|---|---|
| **1** | **Property editors keyed on `DesignerPropertyInfo.Kind`** | `PropertyGrid/Editors/{NumberEditor,BoolEditor,ComboBoxEditor,TextBoxEditor}.*`, `Controls/{NumericUpDown,EnterTextBox,ClearableTextBox,NullableComboBox}.cs` | **The largest gap in the port.** `WpfSurfaceElementPropertyAdapter.cs:78-82` maps `Kind` to only `bool`/`double`/`string`, and `GetEditor` returns `null` (`:51`). DDP already defines `"Boolean"\|"Number"\|"Enum"\|"Rect"\|"Brush"\|"Color"\|"Uri"\|"Reference"` (`DesignerProtocol.cs:398`) — the wire is ready, the UI is not. GTK and MewUI have the same hole. |
| **2** | **Adorner placement algebra** | `Adorners/{RelativePlacement,AdornerPlacement}.cs`, `AdornerOrder` | The first reusable slice is now landed: `SelectionAdornerPlacementCalculator` maps snapshot rectangles, label placement and enabled resize anchors through `DesignViewport`, and owns the center-third move-zone hit rule. Further overlays should consume this primitive instead of re-implementing scale/origin arithmetic. |
| **3** | **Snapline concepts on a nearest-match rule** | `SnaplinePlacementBehavior` (concepts only), `RasterPlacementBehavior` | Adds baselines, `RequireOverlap`, `Group`, margin-inflated map, paired guide rendering. Basic sibling edge/centre snapping now calls `SetSnapGuides` through `DesignSurfaceController`; this phase enriches that working baseline rather than adding its first caller. |
| **4** | **Margin / position handles** | `Controls/{MarginHandle,CanvasPositionHandle}.cs` (UI half) | WPF now publishes structured Grid Margin and Canvas-position insets under v5, and the shared canvas renders draggable L/T/R/B edge readouts with dashed rulers (including zero-distance stubs) that track selection, zoom and pan. The WPF-only commit preserves effective Canvas anchors (including Right/Bottom, margins and overflow) in one native undo group. Clicking a Grid Margin readout toggles that edge between its explicit alignment anchor and Stretch while preserving the rendered bounds. As in the legacy designer, the affordance is intentionally suppressed for non-identity Layout/RenderTransform because visual-edge coordinates cannot safely round-trip to Canvas attached properties. The remaining parity work is endpoint-handle styling. |
| **5** | **Grid rail + a `split-grid-track` verb** | `Controls/GridAdorner.cs` (rail/splitter half) | **WPF delivered:** explicit measured tracks can be split through an atomic host mutation; Ctrl exposes amber insertion rails, Ctrl-click inserts a row and Ctrl+Shift-click a column. WinUI and Forms still need comparable track metadata/mutations before they can opt in. |
| **6** | **In-place text editor** | `Controls/InPlaceEditor.cs` | Port the Enter/Shift+Enter/Escape contract and the font commit first; rich text (`FormatedTextEditor`, 845 lines of XAML) is a later optional step. `DesignSurface.BeginTextEdit` (`:310`) is plain-text already. |
| **7** | **`CecilPropertyDescriptor` side-store** | `CecilPropertyDescriptor.cs:88-127` | Property values live in the model, never in the control. Exactly the out-of-process shape; we have no equivalent. |
| **8** | **Cecil-based / process-isolated toolbox scanning** | `CecilToolboxItemLoader`, `CecilWidgetLibrary`, `IToolboxLoader.LoadItemsIsolated` + ping-and-respawn | Closes a real robustness gap: `TypeDiscoveryService` currently loads and instantiates third-party control assemblies **in the IDE process**. |
| **9** | **Session state capture/restore across child restart** | `WidgetEditSession.SaveState`/`RestoreState` | The one idea that maps directly onto `SharedDesignerHostPool`, which currently has recovery without state. |
| **10** | **Property transactions (`AtomicChange`)** | `UndoManager.AtomicChangeTracker` | Coalescing so one drag is one undo step. We have no equivalent. |
| **11** | **Collection editor + ChooseClass dialog** | `PropertyGrid/Editors/CollectionEditor.*`, `Services/ChooseClass*` | The one place a round-trip is *correct*; also the model for any future dialog RPC. |
| **12** | **XAML error ranges + line-length-preserving repair** | `XamlExceptionRange`, `KeepSuggestedContentSameLength` | Our WinUI hosts report no load errors at all. Medium effort, real diagnostic value. |
| **13** | **`FocusNavigator`** | `WpfDesign.Designer/Project/FocusNavigator.cs` | 229 lines that reduce to ~35 against `DesignerElementNode.Children` + `IsDesignable` + `IsVisible`. Spec already exists as a test. |
| **14** | **Context-menu shell** | the 9 `*ContextMenu.xaml` | One boilerplate template; we already have `SetContextCommands` (`:416`) + `DesignerVerbMenuPlanner`. Port content, not the `ExtensionServer` registration. |
| **15** | **Toolbox: two-sided filtering + document customization** | `ToolboxService.FilterPermitted`, `IToolboxCustomizer`, `ToolboxProvider`'s provenance re-categorisation | `Require`/`Prevent`/`Custom` beats `Scope` equality; per-designer category priority is cheap. |

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
| **C2 — in progress** | **Property editor registry by `DesignerPropertyInfo.Kind`** | WPF and Forms now report native scalar kinds (Boolean, Number, Enum, Color/Brush, URI and geometry) where their existing converter can round-trip them; both report enum `AllowedValues`, and both Properties-pad adapters render those as constrained drop-downs. MewUI's Properties adapter is now likewise snapshot-driven rather than a hand-maintained row list: every host-declared attribute is surfaced from `DesignerPropertyInfo`, with native bool/number editors and protocol enum choices; established fundamental rows remain as a fallback so users can add attributes absent from source, and MewUI color/Thickness text is deliberately not rewritten through WPF formatters. WPF has focused brush/Thickness edit-to-source coverage and now implements the optional `design/reset-property` contract: a reset removes the authored local value through `DesignItemProperty.Reset()`, preserving inherited/default semantics rather than guessing a CLR default. Forms has edit/reset/source-flush coverage for enum and Padding. Its LibreWinForms resource round-trip test also locks an image resource to the opaque `[binary]`, read-only `Unsupported` contract on macOS, so that display token can never be submitted as editable text. Every snapshot-driven adapter (WPF, Forms, GTK and MewUI) enforces `Unsupported`/`Reference`/`ReadOnly` kinds as read-only even if a child accidentally omits the redundant boolean, making `Kind` a real cross-process edit boundary; GTK/MewUI additionally update their local snapshot only after an accepted mutation reply. The Forms, WPF and WinUI Properties-pad DevFlow seams also return the actual outcome: they reject read-only descriptors and read binding validation on the `PropertyItem` target, so a child refusal cannot be reported as a successful edit. WPF also distinguishes nested design-model values such as `Binding` as read-only `Reference` rather than falsely calling them unsupported or serializing a runtime display string; a structured reference editor remains future work. WinUI retains its richer source-backed adapter (enum, Thickness, color and resource-key paths) rather than being downgraded to snapshot values. Unsupported/complex values stay read-only rather than silently stringified. Add collection/ChooseClass/image dialogs only where a backend advertises them. Verify a property edit, reset where supported, undo, and source flush for Forms, WPF and WinUI. |
| **C3 — in progress** | **Layout affordances and snapshot enrichment** | Shared move and resize snapping now select the globally nearest matching edge/centre and require orthogonal overlap, so guides cannot attach to an unrelated row/column. The shared raster-grid move primitive is available behind an explicit `SnapToGrid` switch; WinForms now consumes its existing ShowGrid/SnapToGrid/UseSnapLines/GridSize preferences in the OOP canvas, while other frameworks remain visual-only until they opt in. `SelectionAdornerPlacementCalculator` now provides the snapshot-to-overlay placement algebra for primary/secondary selection rectangles, labels, resize anchors and the center-third move zone; `SelectionAdornerLayer` is its first consumer. The common tree now carries `LayoutMode` (the parent container's actual layout contract), nullable measured `ZIndex`, nullable local `BaselineOffset` for text-capable controls, measured `GridTracks`, and nullable `LayoutInsets` under protocol **v5**: known Canvas/Form/Panel, Grid/TableLayout, Stack/Box, Dock, Wrap, Flow/ToolStrip and content hosts are classified; unrecognised containers remain `Unknown`, never falsely advertised as freely movable. WPF publishes Grid-child Margin insets and measured Canvas-child edge distances, with child-host coverage for both; the shared canvas consumes them as draggable L/T/R/B readouts with visible dashed rulers and zero-distance stubs that remain aligned through selection, zoom and pan. A WPF-only `design/set-layout-inset` mutation maps the committed edge back to Margin or the effective Canvas anchor in one native undo group, preserving Right/Bottom source forms, margin-box coordinates and negative overflow distances; clicking a Grid edge readout routes to `design/toggle-layout-inset-anchor`, changing only the source representation between the edge alignment and Stretch while preserving its measured rectangle. Transformed elements deliberately withhold the affordance instead of attempting an invalid visual-to-layout conversion. Other hosts leave insets null until they can state equivalent semantics; the shared inset readouts are visibly bordered drag handles, with Grid-only anchor-toggle guidance. WPF and both WinUI hosts populate z-index/baseline; WPF has coverage for an explicit `Canvas.ZIndex`, positive text baseline, and node tracks matching its established `query-grid-guides` RPC. WPF's atomic `design/split-grid-track` mutation splits a measured explicit track, preserves its `GridLength` unit, and uses each child’s measured occupied region to choose whether it moves with the new second half or extends across it. The canvas now connects that mutation to a discoverable rail gesture: Ctrl reveals amber mid-track insertion rails, Ctrl-click splits a row and Ctrl+Shift-click a column; blue divider drags continue to resize, with horizontal row rails now spanning the Grid width and vertical column rails its height during both layout and drag. Forms currently leaves z-index/baseline null rather than guessing from control enumeration; WinUI keeps its source-editor guide path until it can publish measured tracks. GTK/Mew/front-end projections preserve the common scalar facts. **Node-shape additions must be released with a protocol-version/host-pool generation update:** the multi-TFM WPF host graph can otherwise resolve an older `Designer.Remote` assembly even when source is newer. Do not port the live-tree `AdornerLayer`. |
| **C4 — in progress** | **Complete mutation and session semantics** | Converge remaining backend-specific edits toward a typed common mutation envelope while keeping true runtime-only operations optional. **Selection is deliberately not a child-to-host notification:** C1's snapshot-local hit testing and `DesignerSelectionController` make the shell the single selection owner; stable node ids already restore the selection, Outline and Properties after every rebuilt snapshot/recovery. The unused `DesignerSelectionChanged` DTO was removed rather than creating a second selection authority. All five designers now expose visible F2 rename where the selected element has a mutable identity: WPF reuses `design/rename` and its existing Roslyn code-behind field sync; WinUI keeps its source editor authoritative and incrementally refreshes the child; GTK/MewUI invoke their host-owned source mutations. GTK/MewUI now propagate the host's `Accepted` result through edit, reset, add/delete, reorder, rename and undo/redo: a rejected mutation rebuilds the displayed rejection but neither dirties the document nor changes the local Properties-pad snapshot; GTK also suppresses code-behind generation after a rejected signal bind. WPF now publishes the design model's browsable XAML events and implements optional `design/set-event`: users can bind or clear an existing handler name without executing project code or generating a code-behind method. The Properties pad adapter exposes those descriptors, and its `od.wpf-designer.properties-pad.bind-event` acceptance seam exercises the same binding convention as a user double-click, writing only the XAML attribute. Its fixture resolves to LibreWPF on macOS and Microsoft WPF on Windows; both the LibreWPF child-host round trip and the visible Properties-pad-to-source-flush journey passed on macOS on 2026-10-09. Automatic handler generation remains deferred until child-to-host code-behind editing has a transactional contract. WinForms already commits a group move through one `design/apply-layout` transaction; WPF group drags/layout commands now use the optional atomic `design/set-bounds-batch` mutation, so the host validates every target before a single `PlacementOperation.Commit` and the shell records exactly one undo entry. WPF nudge now uses this same path for both arrow-key input and `od.wpf-designer.nudge`, rather than falsely reporting that a working canvas operation is unsupported. WPF's deliberately source-authoritative whole-document undo/redo stack is now surfaced consistently by both `od.wpf-designer.status` and its dedicated actions, rather than the general status endpoint falsely reporting it unavailable because the child has no live-tree history RPC. Shared-child recovery now also replays child-local active design state: WPF restores the last successfully selected project theme; WinUI/Uno restores its selected theme and each successfully forced visual-state group after reopening the authoritative document snapshot. A focused WPF host test kills the child and proves that the recovered frame remains on the selected theme. Remaining work: verify the same WPF journeys with the Microsoft backend on Windows; then add transactional WPF code-behind handler generation only if independently desired; add an appbin-backed WinUI fixture to assert visual-state replay end-to-end (the source-only fixture cannot enumerate state groups). |
| **C5 — diagnostic foundation in progress** | **Isolation and diagnostics hardening** | WPF project-toolbox discovery now reads PE metadata with `PEReader`/`MetadataReader`, follows only metadata type references to identify public concrete `FrameworkElement` descendants, and sends the resulting `DesignerToolboxItemInfo` directly to the child on drag. It no longer calls `TypeResolutionService.LoadAssembly()` or creates a third-party runtime `Type` in the IDE process; type resolution and construction stay in the disposable design host. `DesignerDiagnostic` already has a start `Line`/`Column`; WPF parser failures populate it and the Uno host extracts it from its load exception, so do not reintroduce a parallel error DTO. The common DTO now also has optional `FileName`/`EndColumn`; WPF parser failures populate both. WinUI retains those fields from the child runtime through host/view, DevFlow diagnostics and goto-error results; WPF creates the Error List task against the reported file (and honors Warning severity) instead of always pinning it to the primary document. Diagnostic navigation now respects that file identity: WinUI resolves a relative diagnostic path against the designed document and opens that source file rather than always switching to the active XAML source view; WPF passes DDP's 1-based column directly to `SDTask` instead of shifting it one character left. WinUI now carries `PrimaryFileName` through `session/open`/`session/update` and internal theme/state reloads; RPC coverage locks both a nonvisual-root diagnostic and malformed-XAML rejection to the source file. The malformed-XAML test also caught and fixed a host bug where the exception path returned `Accepted=true`; it now rejects while keeping the child alive. Protocol **v6** carries conservative binding telemetry: WPF publishes only design-model bindings as `Declared`, never a fabricated runtime-success/error state that would require evaluating user converters or a non-existent design DataContext; a focused child-host test covers it. Protocol **v7** further makes WinUI's existing source-reachability classification explicit as `IsTemplatePart`, so consumers can distinguish source markup from template-generated visuals without inferring it from a generic non-designable flag. WinUI's `d:DesignData` transform is now RPC-tested as a child-only render aid: placeholder items and `d:DesignWidth`/`d:DesignHeight` alter the preview, while flush returns the untouched source with its design-time markup and no injected `<String>` values. The source-only Uno fixture on macOS cannot presently validate a live tree without an appbin, so `IsTemplatePart` has host compilation coverage rather than a misleading null-tree assertion. Next: richer host-specific binding states only where an isolated runtime can substantiate them, then a separately specified design-data contract for any backend that does not already have this source-preserving transform. |
| **R1 — parallel research** | **Inventory the pre-port WinForms designer and shared designer library** (section 9.1) | Classify the real SharpDevelop baseline and re-rank WinForms-specific work. This is required before claiming full parity, but it should not block C1/C2: their deficiencies are directly observable in the current OOP implementation. |

Mutation acceptance is a shell-side invariant, not merely a host detail: GTK/MewUI now make all
ordinary edit paths return the host's `Accepted` result before dirtying or changing their local
Properties-pad snapshot, and WPF's adapter rejects before entering `OnPropertyEdited`. A failed
signal bind therefore cannot create a GTK code-behind method or show a handler that the child did
not accept.

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

### Why this order

`C1` removes a correctness and responsiveness hazard in every click path. `C2` then closes the
largest day-to-day parity gap: the protocol already carries typed property metadata, but the shell
does not use it to provide editors. `C3` makes layout work feel like a designer rather than a bitmap
editor. `C4` turns the now-common protocol into consistent user-visible editing semantics. `C5`
hardens the boundary that motivated process isolation in the first place.

Framework-specific work belongs inside these phases rather than becoming a competing roadmap:
WinForms needs complete resize parity; WPF needs event binding and richer keyboard/multi-selection
interaction; WinUI needs Component Tray and edge-drag page resizing. GTK and MewUI should receive
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
  is present via source-linking, but the host was not run. The same applies to `ComponentTray`,
  where only the absence of any writer in `WinUIXamlDesigner/` was verified.
- **xamlstudio's property-grid internals** were read only around the visual-state block; the "already
  have it" verdict rests on the existence of `WinUIXamlElementPropertyAdapter.cs`, which was not
  opened.
- **The Stetic `AtomicChange` and session-restore mechanics were read from source, not exercised.**

## 9. Outstanding work

### 9.1 WinForms in-process baseline — initial inventory complete

The actual removal boundary is `af32ec7e` (**Remove in-process legacy designer**), whose parent is
`40e32d7c`. This is earlier than the out-of-process WinForms port (`95339845b4`); `git ls-tree -r
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
IDE-editor eligibility signal, not permission for the child to mutate resources.

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
