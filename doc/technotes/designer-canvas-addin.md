# Designer canvas addin: one canvas for every UI framework

Status: **phases 1-6 and GTK implemented (2026-09-29)** - WinUI, Uno, ProGPU WinUI, MAUI, WPF,
WinForms, GTK 4 and MewUI design on the shared canvas (MewUI renders on macOS only, see
[MewUI](#mewui)). See [Progress](#progress).
Follows on from [`designer-common.md`](designer-common.md), whose Part IV already lists the zoom
state machine as duplicated.

## Problem

Every designer grew its own design canvas on top of the `DesignerCanvas` shell (toolbar + status
bar, `ICSharpCode.SharpDevelop.Widgets`) and the `Designer.Presentation` primitives (viewport,
frame presenter, selection adorners, gridlines, snap calculator):

| Canvas | Lines | Shape |
|---|---|---|
| WinForms `RemoteFormsDesignerControl` | 2793 | view + owns its DDP client |
| WPF `WpfSurfaceDesignerControl` | 2414 | view + owns its DDP client |
| WinUI/Uno `UnoDesignSurfaceControl` (+ ~2000 lines of `UnoDesignRuntimeHost`) | 1233 | pure view; host does the gesture maths |
| MAUI `MauiDesignCanvas` (MAUI-Designer repo) | 416 | pure view |
| Gtk, MewUI | - | bare shell around a native preview |
| ProGPU WinUI | - | **no canvas at all** (no toolbar, zoom, selection, handles) |

The same features are implemented three or four times - the zoom/Fit state machine, frame
placement (`Max(0, Origin) + Pan`), marquee selection, drag threshold + capture, the inline text
editor (Enter/Esc/LostFocus), Ctrl+Z/Y, scroller-chrome filtering, tab-order badges, Grid row and
column guides - and they drift: WinForms resizes only from "se", Uno alone has pan and Ctrl+wheel
zoom, MAUI alone has device sizes plus clipboard, ProGPU has nothing.

`ICSharpCode.Designer.Presentation.dll` is also not a host assembly: about nine addin folders carry
private copies, and the MAUI addin works only because one of them happens to be loaded first.

## The shape

A standalone addin, **`ICSharpCode.DesignerCanvas`** (`src/AddIns/DisplayBindings/DesignerCanvas/`), owns the
whole design surface. A UI addin declares `<Dependency addin="ICSharpCode.DesignerCanvas"/>`,
imports `$ICSharpCode.DesignerCanvas/ICSharpCode.DesignerCanvas.dll`, and supplies **only a
backend**. `Designer.Presentation` moves into it (one copy, loaded from one place).

```
UI addin (WinForms, WPF, WinUI, Uno, ProGPU, MAUI, Gtk, MewUI, ...)
   └─ IDesignCanvasBackend        renders, answers hit tests, applies edits
          ▲  frames + element tree         │ intents (move, resize, text, command...)
          │                                ▼
ICSharpCode.DesignerCanvas addin
   DesignSurface  (the canvas: toolbar, viewport, frame, adorners, gestures, keys, overlays)
```

### The canvas (`DesignSurface`)

A pure view, the Uno/MAUI shape, with the superset of today's features:

- **Presentation:** `DesignerRenderFrame` (BGRA32 or PNG, `Dpi`, `Sequence` with stale-frame drop).
- **Viewport:** one zoom state machine (absolute zoom, Fit as its own mode, 100% default), pan
  (Space+drag, scroller), Ctrl+wheel zoom at the pointer, device design sizes, theme, gridlines,
  show names.
- **Selection:** outline + 8 handles, secondary/multi-selection, marquee, Shift/Ctrl, the pick chain
  (click-through to ancestors), selection labels.
- **Gestures:** drag-move, drag-resize from any handle, group drag, snap guides, reorder, nudge
  (arrows / Shift or Ctrl = 10).
- **Editing overlays:** inline text editor, Grid row/column guides, tab-order badges, visual-state
  loading overlay.
- **Commands and keys:** a context menu built from the backend's verbs (`DesignerVerbMenuPlanner`),
  Delete, Ctrl+C/X/V, Ctrl+Z/Y/Shift+Z, Esc (select parent), Tab (cycle), F2 (rename).
- **Toolbox drop,** the component tray (the shell's, which WinForms stops re-implementing).
- **Diagnostics** for DevFlow: surface geometry, screen anchors, frame profile - one set of
  actions instead of one per designer.

Every feature is on by capability, never by framework: the backend reports what it supports
(DDP's `IDesignHostCapabilities` / capability interfaces), and the canvas shows only that.

### The backend contract (`IDesignCanvasBackend`)

Framework-neutral and deliberately small; everything else is capability interfaces the canvas
feature-detects, the pattern DDP already uses (`IDesignHostBounds`, `IDesignHostHitTesting`, ...):

- `DesignerSessionState` updates (frame + `DesignerElementNode` tree with design-unit bounds),
- hit test (point → element path), pick chain,
- intents to apply: set bounds / group move, add element (drop), delete, rename, set text,
  context command, theme, visual state, design size, grid track size, undo/redo.

An out-of-process backend is a thin adapter over its DDP client (WinForms, WPF, Uno, Microsoft
WinUI, MAUI). An in-process one renders itself (ProGPU: offscreen WebGPU frame + its WinUI tree's
bounds). Either way the canvas never knows which framework it shows.

### Framework-specific parts stay in the backend's addin, as extensions

What genuinely differs is added through extension points of the canvas, not forks of it: extra
toolbar items, overlay layers, extra keyboard bindings, custom adorners. Today's cases:

- **WinForms:** ToolStrip/MenuStrip "Type Here" and popup overlays, smart tags, TabControl header
  switching, lock state, UIA peers.
- **WPF:** Menu/ContextMenu tray editing; align / distribute / match size (as verbs).
- **WinUI/Uno:** visual states (the shell already has them).

## Migration, in phases (each one shippable and tested on its own)

1. **Create the addin** with `DesignSurface` extracted from `UnoDesignSurfaceControl` (the most
   complete pure view) and the gesture maths now in `UnoDesignRuntimeHost`; move
   `Designer.Presentation` into it.
2. **ProGPU WinUI** gets a backend (in-process render → frame + tree) and so, for the first time,
   the full canvas. This is the bug that started this.
3. **Uno and Microsoft WinUI** switch to it; `UnoDesignSurfaceControl` and the canvas half of
   `UnoDesignRuntimeHost` are deleted. The `IWinUIXaml*` view interfaces collapse into the
   canvas's own API.
4. **MAUI** (MAUI-Designer repo): `MauiDesignCanvas` is deleted.
5. **WPF:** `WpfSurfaceDesignerControl` becomes a backend over `WpfSurfaceHostClient` plus the
   Menu/ContextMenu tray extension.
6. **WinForms:** the largest; `RemoteFormsDesignerControl` becomes a backend plus the
   ToolStrip / smart-tag / tab-header extensions.
7. **Gtk, MewUI** (and CoreWF's workflow designer) adopt the canvas where they render frames.

Each phase keeps that designer's integration tests green; phases 1-3 are verified with the WinUI,
Uno and ProGPU designer suites, 5 with the WPF designer suites, 6 with the WinForms suites.

## Progress

### What the addin is

- `src/AddIns/DisplayBindings/DesignerCanvas/`, identity `ICSharpCode.DesignerCanvas`, namespace
  `ICSharpCode.SharpDevelop.Designer.Surface`, deployed to `AddIns/DisplayBindings/DesignerCanvas/`
  together with the one `ICSharpCode.Designer.Presentation.dll` its consumers load.
- `DesignSurface` - the view (toolbar, viewport, frame, adorners, gestures, keys, overlays), a
  `DesignerCanvas`. Unsealed: a designer whose surface must itself be the tab content (WPF) derives
  from it; the others host it.
- `DesignSurfaceController` - the backend-neutral half: applies a `DesignerSessionState`, indexes the
  tree, and turns gestures into intents (selection, single/group drag with snapping, handle resize,
  double-click, inline text, Grid guides, nudge, undo/redo, context commands). Keyed by x:Name
  (`DesignSurfaceKeying.Name`, the WinUI designers) or by element Id (`DesignSurfaceKeying.Id`; an Id
  may be `""`, WPF's document root). `RestoreSelection(keys)` redraws a selection the designer owns
  without raising `SelectionChanged` again.
- `IDesignCanvasBackend.HitTest` - the only thing the canvas asks a backend.
- Extension points: `ExtensionLayer` (backend chrome in content coordinates; re-place on
  `ViewportChanged`, map with `DesignToContentPoint`), `SetContextCommands`, `FrameBackground` (an
  opaque page behind a frame whose root has no background), `ShowSelection(..., label)`,
  `ScrollStatus`.
- A consumer references the canvas and `Designer.Presentation` with `Private="false"` and its
  `.addin` declares `<Dependency addin="ICSharpCode.DesignerCanvas" requirePreload="true"/>` plus
  `$ICSharpCode.DesignerCanvas/` imports of both dlls. A private copy of Presentation in a consumer's
  folder gives the canvas's types two identities; delete any a build leaves behind.

### Per designer

| Designer | State | Verified by |
|---|---|---|
| WinUI / Uno (`UnoDesignRuntimeHost`) | on the canvas | the WinUI/Uno designer suite (13), `WinUIXamlDesigner_ResizeDrag_*` |
| ProGPU WinUI (`ProGpuRuntimeHost`: offscreen WebGPU frame + `ProGpuDesignTree`) | on the canvas; `ProGpuWinUIHostControl` deleted | `ProGpuWinUIDesigner_UsesTheSharedDesignCanvas`, `ProGpuWinUIDesignerTests` |
| MAUI (MAUI-Designer repo) | on the canvas; `MauiDesignCanvas` deleted | the MAUI addin journey |
| WPF (`WpfSurfaceDesignerControl`) | on the canvas (derives from `DesignSurface`, Id-keyed); ContextMenu tray, "Type Here" hotspot and inline editor live in `ExtensionLayer` | the 7 LibreWPF designer tests, now un-gated off Windows |
| WinForms (`RemoteFormsDesignerControl`) | on the canvas (derives from `DesignSurface`, Name-keyed); ToolStrip editors, popups, smart tag, reorder and rename in `ExtensionLayer`; its own tray below the canvas | the 7 WinForms designer tests on LibreWinForms, `FormsMenuEditingTests`, `FormsDesigner.Host.Tests` |
| GTK 4 (`GtkDesignerViewContent`) | on the canvas (hosts `DesignSurface`, Id-keyed); native GSK/Cairo PNG frame; no resize handles, a drag reorders | `GtkDesignerTests`, `GtkDesignerIntegrationTests` |
| MewUI | migrated - real MewUI frame on macOS (see below) | - |
| CoreWF | not in this repository | - |

Things the WPF migration fixed in the canvas, which every designer now gets:

- A drag captures the mouse once it really starts (not at the press): a resize dragged past the
  design - over the status bar - never got its mouse-up and so never committed.
- Snapping excluded the dragged element by x:Name, so an Id-keyed designer snapped every drag to
  itself (zero delta); tab-order badges likewise now cover every keyed element.
- `ClearSelection` forgets the selected rect, so a re-layout no longer redraws a cleared selection.
- Keys typed into any text box on the canvas are text, not commands (Space no longer starts panning).

And one in WPF itself: a reload shows `DesignerCanvas`'s loading overlay on the surface and nothing
hid it again, so the design stayed dimmed ("Starting LibreWPF design host…") and every press landed
on the overlay. `Show` now ends it when a frame arrives.

### WinForms

- **Tree.** The host's own `Tree` carries parent-relative `Location`s, which the canvas cannot place,
  so the control builds the canvas snapshot from the flat `Components` list (`SurfaceX/SurfaceY` are
  already frame coordinates): one node per placed component, keyed by name, with `TabIndex` for the
  badges. Tray-only components (no parent) get no node, hence no outline.
- **Edits.** A committed canvas move becomes the designer's own selection move
  (`SelectionMoveRequested`), a resize `BoundsChanged` in parent-relative coordinates. A locked
  component (except the root's size) and a ToolStripItem - not a Control - snap back.
- **WinForms-only input,** through the canvas's extension points: `DesignPressInterceptor` (a press on
  a TabControl header switches the page), `HandlesUndoRedoKeys = false` (undo is an IDE command; the
  control keeps Delete, arrows, Tab, Esc, F2 and Ctrl+.), `ContextMenu = null` (right-click raises
  `ContextMenuRequested` for the host's verb menu), `SelectionStroke` (a locked component's outline).
- **Frames** may be PNG (`DesignSurface.SetRender` decodes either).
- `od.forms-designer.view "fit" | <zoom>` sets the view; `od.forms-designer.status` reports
  `lastPick` (the controller's pick diagnostic).
- `DesignSurfaceClickArbiter` (Designer.Presentation) and its 20 unit tests were deleted
  (2026-09-30). On the canvas the backend's hit test answers with the innermost document element
  (`DesignCanvasHit.PickPath`), so there is no press arbitration left for it to do. The canvas's
  own picking (`DesignSurfaceController.ResolveNameAtWithPath`) has no unit tests yet; it is
  covered only by the designers' integration tests.

### GTK 4

- The host renders the interface with GSK/Cairo and reports each object's bounds in frame
  coordinates, so the canvas takes the frame and a copy of the tree rooted at the real top-level
  object (not the `$interface` wrapper), with a tree path on every node for the hit test.
- GTK lays every widget out: `ResizeHandlesEnabled = false` (a new canvas switch, backed by
  `SelectionAdornerLayer.ShowHandles`), and a committed drag onto a sibling becomes the designer's
  reorder. The old hand-rolled hit layer's drop onto the native surface never worked from a real
  pointer drag (`GtkDesigner_DragToolboxItemOntoNativeDesignSurface_...` failed before); the canvas's
  drop, mapped through its own viewport, does.
- The WPF mock preview used when no native frame exists is gone; the diagnostic line says so.

### MewUI

`MewUIDesignerViewContent` is on `DesignSurface` (Id-keyed, no resize handles, drag = reorder among
siblings, like GTK). `MewUIRenderer` in the host instantiates the real Aprillz.MewUI 0.22 controls
for the MXAML tree and renders them offscreen; each node carries its arranged rect.

- **Rendering**: an unshown `Window`, explicit `Measure`/`Arrange` (the frame path does not lay out),
  then the internal `Window.RenderFrameToSurface` on an offscreen MewVG/Metal surface on macOS or
  GDI surface on Windows, and `TryReadPixels` into a BGRA `DesignerRenderFrame.Data`. Every
  non-public member is bound by name and a miss is reported as a diagnostic naming the member and
  the MewUI version.
- **Bounds** are `Element.Bounds`, which are **absolute** (root) coordinates, not parent-relative:
  measured, a Button inside a StackPanel with a 40px left margin reports X=40.
- **Attributes**: public properties by reflection; `Owner.Prop` attached properties (`Grid.Row`,
  `DockPanel.Dock`, `Canvas.Left`) through MewUI's static `Owner.SetProp(Element, value)`. Text is
  wrapped in a `Label` only for `Content`/`Header`, and for 0.12-style `Text` on content controls.
- **Ids**: an unnamed element gets a path id (`#0,2,1`); `Name` stays empty.
- **Hit test** runs locally on the client from the pushed bounds (GTK too): it is called on the UI
  thread from a pointer press and must not block on an RPC. `design/hit-test` still exists.
- A frame is cached by the document's canonical text.
- **macOS and Windows** have render backends (`MEWUI_RENDER` is set for both in the host project).
  Elsewhere the host sends the tree without a frame plus an Info diagnostic saying so.

## Open questions

- Whether `DesignerCanvas` (the shell, in Widgets) moves into the addin too, or stays in the host
  for non-designer users (the EDM designer has its own unrelated `DesignerCanvas`).
- Out-of-tree designers (MAUI, CoreWF) build against the addin through the Addin SDK: a
  `ProjectReference` with `Private=false` plus the `$Identity/` import, or a published package.
