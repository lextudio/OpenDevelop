#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.WpfDesign.SurfaceHost;

namespace ICSharpCode.WpfDesign.AddIn.OutOfProcess
{
	/// <summary>
	/// Phase 1 of the WPF designer's DDP cutover (see doc/technotes/wpf-designer.md and
	/// designer-common.md's "Relationship to the existing implementations" table): a
	/// <see cref="DesignerCanvas"/>-hosted surface driven entirely through
	/// <see cref="WpfSurfaceHostClient"/>/<see cref="IDesignHostClient"/> instead of a live
	/// in-process <c>DesignSurface</c>, mirroring <c>RemoteFormsDesignerControl</c>/
	/// <c>UnoDesignSurfaceControl</c>.
	///
	/// Originally scoped down from those two to single-element click-to-select only; since then
	/// drag/resize, toolbox drop, one keyboard command, Ctrl-click multi-select and
	/// marquee-select have all landed (see <see cref="ToggleMultiSelect"/>/
	/// <see cref="SelectWithinMarquee"/>) - group mouse-drag-move and a broader keyboard command
	/// set are still later work. It is also NOT YET
	/// wired into <c>WpfViewContent</c>; swapping it in would regress the live in-process
	/// designer's interactivity before phase 2+ (see below) lands, so it stays a standalone,
	/// independently testable class until it has enough parity not to be a regression.
	///
	/// Selection outlines here are drawn from the element tree's reported bounds
	/// (<see cref="DesignerElementNode.X"/>/Y/Width/Height) and do line up with the rendered
	/// frame: the coordinate mismatch this class was originally written against turned out to be
	/// the child arranging its root into a hardcoded 800x600 viewport while rendering a texture
	/// sized to the root itself, and is fixed in <c>WpfSurfaceHostService.RebuildTreeAndRender</c>
	/// (see wpf-designer.md's "Coordinate mismatch: SOLVED", plus the
	/// <c>RenderedContent_LandsExactlyAtTheBoundsTheElementTreeReports</c> regression test).
	/// </summary>
	public sealed class WpfSurfaceDesignerControl : DesignerCanvas
	{
		readonly WpfSurfaceHostClient client;
		readonly Grid designSurface = new Grid();
		// Stretch.Fill (like UnoDesignSurfaceControl, the other backend with working zoom), NOT
		// Stretch.None: DesignFramePresenter.Resize sets the Image's Width/Height to
		// DesignWidth/Height * viewport.Scale, but with Stretch.None the Image draws the bitmap at
		// its natural pixel size regardless, so zooming scaled the coordinate math and the
		// selection adorners while the picture itself stayed put - confirmed live (at Fit, the
		// element was laid out 462 wide while the bitmap still rendered 297 wide). At the default
		// scale of 1 the two are identical (Width == the bitmap's own 96-DPI size), so this only
		// changes behavior once zoom/fit is actually used.
		readonly DesignFramePresenter framePresenter = new(Stretch.Fill,
			horizontalAlignment: HorizontalAlignment.Left, verticalAlignment: VerticalAlignment.Top);
		// All eight handles, like the WinUI/Uno surface - the WPF backend supports a real
		// container-aware move/resize through design/set-bounds (PlacementOperation on the child
		// side), unlike WinForms which only ever shows "se". The label must be non-empty for
		// SelectionAdornerLayer.HandleAt to report handles at all, so selection always passes one.
		static readonly string[] HandleNames = { "nw", "n", "ne", "e", "se", "s", "sw", "w" };

		readonly SelectionAdornerLayer adornerLayer = new(HandleNames, Brushes.DodgerBlue);

		DesignerSessionState? state;
		DesignViewport viewport = DesignViewport.Identity(0, 0);
		string? selectedPath;

		// Multi-select: `selectedPath` stays the single "primary" selection (the one shown with
		// resize handles and driving single-element drag/resize, unchanged); every other
		// multi-selected element's path lives here and is drawn as a dashed secondary outline via
		// SelectionAdornerLayer.SetSecondarySelection - matching UnoDesignSurfaceControl's own
		// primary/secondary split. No group mouse-drag yet (see doc/technotes designer parity
		// notes) - group delete reuses the already-batch design/delete-elements RPC.
		readonly HashSet<string> secondarySelection = new(StringComparer.Ordinal);

		// Marquee (rubber-band) select: resolved entirely client-side against the already-known
		// element tree (state.Tree carries every node's X/Y/Width/Height from the last render),
		// no RPC needed - matching RemoteFormsDesignerControl's own local intersect-test approach.
		// Pending vs. active mirrors the gesture threshold pattern: a press over empty space could
		// still just be a plain click, resolved at mouse-up.
		readonly Canvas marqueeOverlay = new Canvas { IsHitTestVisible = false };
		readonly Rectangle marqueeBorder = new Rectangle {
			Stroke = Brushes.DodgerBlue, StrokeThickness = 1,
			Fill = new SolidColorBrush(Color.FromArgb(35, 30, 144, 255)),
			StrokeDashArray = new DoubleCollection { 3, 2 },
			IsHitTestVisible = false,
			Visibility = Visibility.Collapsed
		};
		bool marqueePending;
		bool marqueeActive;
		Point marqueeStartSurface;

		/// <summary>The document root's tree path/id. Paths are built root-first with the root
		/// itself at "" (<c>WpfSurfaceHostService.BuildNode</c>), so this is the empty string -
		/// which is why "no selection" is represented by null here and never by "".</summary>
		const string RootElementId = "";

		/// <summary>Empty space kept on every side of the design inside the canvas, so the root
		/// element's own resize handles are reachable and DesignerCanvas's tiled "EdgePattern"
		/// background is visible around the page.</summary>
		const double CanvasPadding = 24;

		// Zoom/Fit toolbar state, matching RemoteFormsDesignerControl's own identical fields and
		// presets exactly - the shared DesignerCanvas toolbar chrome is meant to look and behave
		// identically across all three designers (its own top-of-file doc comment). Default is
		// fitMode=false, zoomScale=1.0, which Show() below special-cases to the exact same
		// DesignViewport.Identity(...) this control already used before this feature existed - so
		// a document that's never had its zoom touched renders bit-for-bit identically to before,
		// preserving every screen-coordinate assumption the resize-drag/geometry integration tests
		// depend on (doc/technotes/wpf-designer.md's coordinate-mismatch history).
		static readonly double[] ZoomPresets = { 0.25, 0.5, 0.75, 1.0, 1.5, 2.0 };
		static readonly string[] ZoomLabels = { "Fit", "25%", "50%", "75%", "100%", "150%", "200%" };
		bool fitMode;
		double zoomScale = 1.0;

		// Gridlines: plain Line children of a Canvas over the rendered frame (see GridlineOverlay -
		// a tiled DrawingBrush Background does not render under LibreWPF-on-macOS even though the
		// property assignment succeeds). IsHitTestVisible=false on the overlay is load-bearing:
		// this sits above the frame image, and a hit-testable overlay would swallow the mouse
		// input the resize/drag gestures depend on.
		readonly GridlineOverlay gridOverlay = new GridlineOverlay();
		bool showGridlines;

		// Drag-snap alignment guides (see SnapGuideCalculator): a vertical or horizontal line
		// shown while an element is being dragged near another element's edge/centre, matching
		// UnoDesignSurfaceControl's own guide overlay/rendering.
		// HorizontalAlignment/VerticalAlignment=Left/Top for the same reason GridlineOverlay.Visual
		// needs them: designSurface is a Grid, which would otherwise center this Canvas instead of
		// pinning it to the Margin offset.
		readonly Canvas snapGuideOverlay = new Canvas {
			IsHitTestVisible = false,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top
		};
		readonly List<Rectangle> snapGuides = new();

		// Tab order view: a small numbered badge near every element that reports a TabIndex
		// property (WpfSurfaceHostService.BuildProperties already reflects it onto the wire for
		// every Control-derived element - no protocol change needed), matching
		// RemoteFormsDesignerControl's own tab-order badge overlay.
		readonly Canvas tabOrderOverlay = new Canvas {
			IsHitTestVisible = false,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top
		};
		bool showTabOrder;

		// Inline text editing: double-click an element with a plain-string Text/Content property
		// to edit it directly on the design surface, matching UnoDesignSurfaceControl's own
		// BeginTextEdit/EndTextEdit - ported without the manual double-click-timing hack that
		// needed, since native WPF (unlike the Uno designer's LibreWPF host) reports
		// MouseButtonEventArgs.ClickCount reliably.
		readonly TextBox textEditor = new TextBox {
			Visibility = Visibility.Collapsed,
			BorderBrush = Brushes.DodgerBlue,
			BorderThickness = new Thickness(1),
			Padding = new Thickness(2),
			AcceptsReturn = false,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top
		};
		bool textEditing;
		Rect textEditRect;
		string? textEditElementId;
		string? textEditPropertyName;

		// Grid row/column drag guides (design/query-grid-guides, design/set-grid-track-size):
		// shown whenever the current selection is a Grid, refreshed on every selection change and
		// after every edit, matching WinUIXamlDesignerViewContent's own RefreshGridGuides calls.
		// Ported from UnoDesignSurfaceControl's SetGridGuides/GridGuideAt/BeginGridGuideDrag/
		// UpdateGridGuideDrag/EndGridGuideDrag - same math, this backend's own overlay/RPC shapes.
		readonly Canvas gridGuideOverlay = new Canvas {
			IsHitTestVisible = false,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top
		};
		readonly List<Rectangle> gridRowGuides = new();
		readonly List<Rectangle> gridColGuides = new();
		string? gridGuideElementId;
		Rect gridGuideRect;
		double[] gridRowOffsets = Array.Empty<double>();
		double[] gridColOffsets = Array.Empty<double>();
		bool gridGuideDragPending;
		bool gridGuideDragIsRow;
		int gridGuideDragIndex;
		Point gridGuideDragStart;

		// Gesture state. A drag only starts once the pointer has moved past a small threshold, so
		// an ordinary click still selects without nudging the element (same guard both shipped
		// backends use). The adorner is updated locally during the drag and the result committed
		// with a single design/set-bounds on mouse-up - never one RPC per mouse-move.
		const double DragThreshold = 3;
		Point gestureStart;
		Rect gestureOriginalBounds;
		string? gestureHandle;
		bool gesturePending;
		bool gestureActive;

		public WpfSurfaceDesignerControl(WpfSurfaceHostClient client, string backendName)
		{
			this.client = client ?? throw new ArgumentNullException(nameof(client));
			BackendName = backendName;

			designSurface.Children.Add(framePresenter.Visual);
			// Between the frame and the adorners: gridlines draw over the design, selection
			// outlines/handles draw over the gridlines.
			designSurface.Children.Add(gridOverlay.Visual);
			designSurface.Children.Add(snapGuideOverlay);
			designSurface.Children.Add(tabOrderOverlay);
			designSurface.Children.Add(gridGuideOverlay);
			designSurface.Children.Add(adornerLayer.Visual);
			marqueeOverlay.Children.Add(marqueeBorder);
			designSurface.Children.Add(marqueeOverlay);
			textEditor.KeyDown += OnTextEditorKeyDown;
			textEditor.LostKeyboardFocus += OnTextEditorLostFocus;
			designSurface.Children.Add(textEditor);
			ContentHost.Content = designSurface;

			// Zoom, Fit and gridlines are all really implemented for this backend (zoomScale/
			// fitMode/showGridlines and Show()'s viewport + overlay sizing below), following
			// RemoteFormsDesignerControl's zoom/fit approach and UnoDesignSurfaceControl's
			// host-side-only gridlines approach. Two toolbar entries stay hidden because they are
			// genuinely other backends' concepts, not unfinished work here:
			//  - design-size (device presets like "Phone 390x844") only means something for a
			//    WinUI/Uno page with no intrinsic size; a WPF Window/UserControl carries its own
			//    design size in the XAML, so there is nothing for a preset to override. Hidden for
			//    WinForms for the same reason.
			//  - design theme: WPF has no framework-level theme API to switch
			//    (unlike WinUI/Uno's RequestedTheme, which its child host can just flip), so
			//    whether the combo is shown at all is per-project, driven by
			//    DesignerSessionState.DesignThemes (see Show's own handling) - starts hidden
			//    here (no document is open yet) and Show() reveals it once a project that
			//    embeds themes is actually loaded.
			Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit |
				DesignerCanvasCapabilities.Gridlines | DesignerCanvasCapabilities.ShowNames |
				DesignerCanvasCapabilities.StatusBar;
			StatusText = "Starting WPF design host…";
			foreach (var label in ZoomLabels)
				ZoomCombo.Items.Add(label);
			ZoomCombo.SelectedIndex = 4; // "100%"
			ZoomChanged += (_, _) => {
				var index = ZoomCombo.SelectedIndex;
				if (index <= 0) {
					fitMode = true;
				} else {
					fitMode = false;
					zoomScale = ZoomPresets[index - 1];
				}
				RebuildViewport();
			};
			FitRequested += (_, _) => { fitMode = true; RebuildViewport(); };
			GridRequested += (_, enabled) => SetGridlines(enabled);
			ShowNamesRequested += (_, enabled) => adornerLayer.ShowNameLabel = enabled;
			// ThemeRequested carries the chosen theme name directly; CommitTheme is blocking
			// for the same reason CommitDelete/CommitBounds are (see CommitDelete's own doc
			// comment: this runs on the UI thread already inside a routed event, so a plain
			// blocking call is simpler and safe here).
			ThemeRequested += (_, theme) => CommitTheme(theme);

			Focusable = true;
			AllowDrop = true;
			// Preview (tunneling) events, not the bubbling MouseLeftButtonDown/Move/Up ones -
			// matching UnoDesignSurfaceControl's own identical fix and its doc comment: under
			// LibreWPF, an ancestor (its own comment names a ScrollViewer, but the exact swallowing
			// element wasn't identified for this control specifically) swallows the bubbling mouse
			// events before they ever reach a child, so real synthetic mouse drags (resize-handle
			// press, toolbox drop) landed nowhere - confirmed via temporary diagnostics showing zero
			// hits in these handlers during a real press/drag/release even after window activation,
			// and by the sibling WinUI designer's equivalent resize-drag test passing where this
			// one didn't, using the same interaction shape but Preview* events (wpf-designer.md's
			// "Decisive follow-up" entry documents the full investigation).
			PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
			PreviewMouseMove += OnMouseMove;
			PreviewMouseLeftButtonUp += OnMouseLeftButtonUp;
			DragOver += OnDragOver;
			Drop += OnDrop;
		}

		public DesignerSessionState? State => state;

		/// <summary>Opens a document from a host-owned snapshot. Does NOT render the returned
		/// frame itself - see the note on <see cref="Show"/> for why every caller of this and the
		/// mutation methods below must call it explicitly instead.</summary>
		public async Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
		{
			state = await client.OpenAsync(snapshot, cancellationToken).ConfigureAwait(false);
			return state;
		}

		/// <summary>Delivers newer source (a host-side edit or external change). Does NOT render -
		/// see <see cref="Show"/>.</summary>
		public async Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
		{
			state = await client.UpdateAsync(snapshot, cancellationToken).ConfigureAwait(false);
			return state;
		}

		/// <summary>Renders <paramref name="newState"/>'s frame and re-places the selection
		/// adorner. Deliberately a separate step the caller invokes explicitly, rather than a
		/// continuation this class's own async RPC wrappers run after their own await.
		///
		/// EVERY caller of this method blocks via <c>.GetAwaiter().GetResult()</c> and calls this
		/// directly afterward, on the same thread - including the mouse-gesture/toolbox-drop/
		/// Delete-key handlers (<c>CommitBounds</c>/<c>CommitDrop</c>/<c>CommitDelete</c>/
		/// <c>HitTestAndSelect</c>), which are always already on the dispatcher thread by
		/// construction (they run inside WPF routed-event handlers). This used to be two
		/// different patterns - those four also existed as fire-and-forget async methods that
		/// awaited with <c>ConfigureAwait(true)</c>, trusting the WPF dispatcher's
		/// SynchronizationContext to resume their continuation back on the same thread - but that
		/// was proven unreliable live: a real drag-resize genuinely committed and rendered
		/// (confirmed via <c>od.wpf-designer.surface-geometry</c> showing the correct new size),
		/// yet the continuation after the await resumed on a thread-pool thread instead of the
		/// dispatcher thread (verified via <c>Dispatcher.Thread.ManagedThreadId</c> differing from
		/// <c>Environment.CurrentManagedThreadId</c> at that point), so touching WPF objects
		/// afterward threw a cross-thread <c>InvalidOperationException</c> that the fire-and-forget
		/// wrapper silently suppressed - <see cref="DocumentChanged"/> never reached
		/// <c>WpfViewContent</c>, so the file was never marked dirty even though the edit had
		/// genuinely applied and would be silently lost if the user closed without touching
		/// anything else. Blocking needs no SynchronizationContext capture at all - <c>GetResult()</c>
		/// simply returns control to whichever thread called it - so switching every one of these
		/// four to a plain blocking call removes the whole class of risk, matching the same
		/// already-proven-reliable pattern <c>WpfViewContent.LoadInternal</c>/
		/// <c>WpfDesignDevFlowActions</c> use for their own blocking calls into this class
		/// (doc/technotes/wpf-designer.md's cutover-completion entry has the original deadlock
		/// this design was built to avoid - that deadlock only applied to callers already blocked
		/// waiting for a captured-context continuation, which none of these four ever are).</summary>
		internal void Show(DesignerSessionState newState)
		{
			state = newState;
			// Reflects whatever THIS document reported, every time - a session/open for a
			// The theme combo lists exactly the themes the project's assembly embeds (its
			// themes/*.xaml resources); a project without any embedded theme hides the combo.
			// Opening a different document that does embed them (or a design/theme response
			// confirming them again) shows and repopulates it.
			Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit |
				DesignerCanvasCapabilities.Gridlines | DesignerCanvasCapabilities.ShowNames |
				DesignerCanvasCapabilities.StatusBar |
				(newState.DesignThemes.Length > 0 ? DesignerCanvasCapabilities.Theme : DesignerCanvasCapabilities.None);
			if (Capabilities.HasFlag(DesignerCanvasCapabilities.Theme))
			{
				SetDesignThemes(newState.DesignThemes);
			}
			var render = newState.Render;
			if (render == null || string.IsNullOrEmpty(render.Data) || render.Width <= 0 || render.Height <= 0)
			{
				StatusText = "WPF design host: nothing rendered yet.";
				framePresenter.Clear();
				viewport = DesignViewport.Identity(0, 0);
				adornerLayer.ClearSelection();
				adornerLayer.ClearSecondarySelection();
				gridOverlay.Visual.Width = gridOverlay.Visual.Height = 0;
				gridOverlay.Update(0, 0, viewport.Scale, showGridlines);
				SetSnapGuides(Array.Empty<(bool, double)>());
				tabOrderOverlay.Children.Clear();
				EndTextEdit(commit: false);
				SetGridGuideOverlay(null, default, Array.Empty<double>(), Array.Empty<double>());
				return;
			}

			var pixels = DesignerFrameCodec.DecodeBgra32(render);
			framePresenter.SetSource(BitmapSource.Create(render.Width, render.Height, 96, 96, PixelFormats.Bgra32, null, pixels, render.Width * 4));
			StatusText = $"Rendered by WPF design host ({render.Width}×{render.Height}).";
			// The design is centered inside the canvas with at least CanvasPadding of empty space
			// on every side, never flush against the top-left corner. Two reasons, both real:
			// the ROOT element's own resize handles are drawn just outside its bounds, so with no
			// margin the top/left ones fall outside the surface and can neither be seen nor
			// grabbed (which is what makes drag-resizing the whole Window/UserControl work); and
			// the surrounding empty area is what shows DesignerCanvas's tiled "EdgePattern"
			// background, visually separating "the page" from "around the page".
			// Implemented by centering inside an inset area and shifting everything back out by
			// the same padding through the viewport's own pan - so the frame bitmap, the gridline
			// overlay and every DesignToSurface-based adorner all move together and stay aligned.
			var availableWidth = Math.Max(0, ContentHost.ActualWidth - 2 * CanvasPadding);
			var availableHeight = Math.Max(0, ContentHost.ActualHeight - 2 * CanvasPadding);
			viewport = fitMode
				? DesignViewport.Fit(render.Width, render.Height, availableWidth, availableHeight, 1.0, CanvasPadding, CanvasPadding)
				: DesignViewport.Zoom(render.Width, render.Height, availableWidth, availableHeight, zoomScale, CanvasPadding, CanvasPadding);
			framePresenter.Resize(viewport);
			// The rendered frame must sit at the viewport's own base (origin + pan) - exactly where
			// the DesignToSurface-based adorners/hit-testing already assume it is - otherwise the
			// selection outline and the bitmap drift apart the moment Scale/Origin isn't the
			// (1, 0, 0) identity case, matching RemoteFormsDesignerControl's own identical fix for
			// the same reason.
			framePresenter.Visual.Margin = new Thickness(
				Math.Max(0, viewport.OriginX) + viewport.PanX,
				Math.Max(0, viewport.OriginY) + viewport.PanY, 0, 0);
			// Gridlines cover exactly the rendered frame (same size and placement), and their cell
			// size follows the current zoom.
			gridOverlay.Visual.Width = framePresenter.Visual.Width;
			gridOverlay.Visual.Height = framePresenter.Visual.Height;
			gridOverlay.Visual.Margin = framePresenter.Visual.Margin;
			gridOverlay.Update(framePresenter.Visual.Width, framePresenter.Visual.Height, viewport.Scale, showGridlines);
			snapGuideOverlay.Width = framePresenter.Visual.Width;
			snapGuideOverlay.Height = framePresenter.Visual.Height;
			snapGuideOverlay.Margin = framePresenter.Visual.Margin;
			tabOrderOverlay.Width = framePresenter.Visual.Width;
			tabOrderOverlay.Height = framePresenter.Visual.Height;
			tabOrderOverlay.Margin = framePresenter.Visual.Margin;
			UpdateTabOrderOverlay();
			LayoutTextEditor();
			gridGuideOverlay.Width = framePresenter.Visual.Width;
			gridGuideOverlay.Height = framePresenter.Visual.Height;
			gridGuideOverlay.Margin = framePresenter.Visual.Margin;
			LayoutGridGuides();

			RestoreSelection();
		}

		/// <summary>Re-derives the viewport from the current toolbar Zoom/Fit selection and
		/// re-presents the last-known state - the toolbar's own event handlers call this instead of
		/// re-fetching from the child, since a zoom/fit change is purely a local presentation change
		/// (matching RemoteFormsDesignerControl.RebuildViewport exactly).</summary>
		void RebuildViewport()
		{
			if (state != null)
				Show(state);
		}

		/// <summary>Gridlines toggle state (the toolbar's own grid button drives this).</summary>
		public bool Gridlines => showGridlines;

		/// <summary>Shows/hides the design-space gridlines over the rendered frame.</summary>
		public void SetGridlines(bool show)
		{
			showGridlines = show;
			gridOverlay.Update(framePresenter.Visual.Width, framePresenter.Visual.Height, viewport.Scale, showGridlines);
		}

		void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (state is not { } currentState)
				return;
			var point = e.GetPosition(designSurface);
			// These are Preview (tunneling) handlers on the whole control, so they also see presses
			// on the shared toolbar sitting above the design surface. Ignore those outright:
			// otherwise a press on the Zoom/Fit/grid chrome runs the hit-test path below with a
			// negative design-space Y, which resolves to nothing and silently clears the user's
			// selection (observed live - clicking Fit dropped the current selection).
			if (point.X < 0 || point.Y < 0 || point.X > designSurface.ActualWidth || point.Y > designSurface.ActualHeight)
				return;
			Focus();
			var (designX, designY) = viewport.SurfaceToDesign(point.X, point.Y);
			var designPoint = new Point(designX, designY);

			// Double-click starts inline text editing if the hit element has a plain-string
			// Text/Content property (see ResolveTextPropertyName) - real WPF (unlike the Uno
			// designer's LibreWPF host) reports ClickCount reliably, so no manual timestamp/
			// position double-click detection is needed here.
			if (e.ClickCount == 2)
			{
				var hit = client.HitTestAsync(currentState.Version, designX, designY).GetAwaiter().GetResult();
				if (hit.Hit && state?.Tree != null && FindNodeByPath(state.Tree, hit.PickPath) is { } hitNode
					&& ResolveTextPropertyName(hitNode) is { } propertyName)
				{
					var currentValue = hitNode.Properties.First(p => p.Name == propertyName).Value;
					BeginTextEdit(hitNode.Id, propertyName, hitNode.X, hitNode.Y, hitNode.Width, hitNode.Height, currentValue);
				}
				return;
			}

			// Pressing on a Grid divider starts a row/column resize, not an element drag/move -
			// checked before the selected-element move/resize logic below, matching
			// UnoDesignSurfaceControl's own "GridGuideAt wins" ordering.
			if (GridGuideAt(designPoint) is { } guide)
			{
				BeginGridGuideDrag(guide.IsRow, guide.Index, designPoint);
				return;
			}

			var selected = SelectedNode;
			if (selected != null)
			{
				// A press on one of the current selection's resize handles starts a resize without
				// re-hit-testing - the handles deliberately sit outside the element's own bounds,
				// so hit-testing there would resolve the parent (or nothing) and drop the selection.
				if (adornerLayer.HandleAt(designPoint, viewport) is { } handle)
				{
					BeginGesture(designPoint, selected, handle);
					return;
				}
				// A press inside the already-selected element starts a move; anything else re-picks.
				// Excluded for the ROOT: its rect covers the whole design, so treating a press
				// inside it as a move would make every child unreachable once the root is selected
				// (and the root cannot be moved anyway - it has no container to be positioned in,
				// so design/set-bounds falls back to a size-only change). Resize handles are still
				// honored for the root above, which is what makes drag-resizing the page work.
				if (selected.Id != RootElementId
					&& new Rect(selected.X, selected.Y, selected.Width, selected.Height).Contains(designPoint))
				{
					BeginGesture(designPoint, selected, handle: null);
					return;
				}
			}

			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
			{
				ToggleMultiSelect(currentState.Version, designX, designY);
				return;
			}

			// Neither a handle press nor a click inside the current primary selection: could be a
			// plain click that re-picks a different element, or the start of a marquee-select drag
			// over empty space - resolved at mouse-up depending on whether the drag threshold was
			// exceeded (matching RemoteFormsDesignerControl's own marqueeStart/marqueeSelecting
			// pattern), so this does NOT call HitTestAndSelect immediately.
			marqueePending = true;
			marqueeActive = false;
			marqueeStartSurface = point;
			CaptureMouse();
		}

		void BeginGesture(Point designPoint, DesignerElementNode node, string? handle)
		{
			gestureStart = designPoint;
			gestureOriginalBounds = new Rect(node.X, node.Y, node.Width, node.Height);
			gestureHandle = handle;
			gesturePending = true;
			gestureActive = false;
			CaptureMouse();
		}

		void OnMouseMove(object sender, MouseEventArgs e)
		{
			if (gridGuideDragPending && e.LeftButton == MouseButtonState.Pressed)
			{
				var (gx, gy) = viewport.SurfaceToDesign(e.GetPosition(designSurface).X, e.GetPosition(designSurface).Y);
				UpdateGridGuideDrag(new Point(gx, gy));
				return;
			}
			if (marqueePending && e.LeftButton == MouseButtonState.Pressed)
			{
				UpdateMarquee(e.GetPosition(designSurface));
				return;
			}
			if (!gesturePending || e.LeftButton != MouseButtonState.Pressed)
				return;
			var point = e.GetPosition(designSurface);
			var (designX, designY) = viewport.SurfaceToDesign(point.X, point.Y);
			var dx = designX - gestureStart.X;
			var dy = designY - gestureStart.Y;

			if (!gestureActive)
			{
				// Threshold is in surface pixels, so a zoomed-out canvas doesn't need a huge
				// physical movement before a drag starts.
				if (Math.Abs(dx * viewport.Scale) < DragThreshold && Math.Abs(dy * viewport.Scale) < DragThreshold)
					return;
				gestureActive = true;
			}

			// Snap the dragged element's edges/centre to nearby siblings' edges/centres and show
			// alignment guides while dragging (move only - resizes are not snapped), matching
			// UnoDesignSurfaceControl's own ApplySnap behavior.
			if (gestureHandle == null)
			{
				var (snapDx, snapDy, guides) = SnapGuideCalculator.ApplySnap(
					(gestureOriginalBounds.X, gestureOriginalBounds.Y, gestureOriginalBounds.Width, gestureOriginalBounds.Height),
					dx, dy, SiblingBounds());
				dx = snapDx;
				dy = snapDy;
				SetSnapGuides(guides);
			}
			else
			{
				SetSnapGuides(Array.Empty<(bool, double)>());
			}
			lastGestureDx = dx;
			lastGestureDy = dy;

			adornerLayer.ShowSelection(ApplyGesture(dx, dy), viewport, SelectedNode?.Name ?? SelectedNode?.Type);
		}

		// The last (possibly snap-corrected) delta shown by OnMouseMove, so the committed bounds
		// on mouse-up match what the user actually saw rather than re-deriving an unsnapped delta
		// from the raw release position.
		double lastGestureDx;
		double lastGestureDy;

		/// <summary>Every other element's design-space bounds, for <see cref="SnapGuideCalculator"/>
		/// to snap the dragged element against.</summary>
		IEnumerable<(double X, double Y, double Width, double Height)> SiblingBounds()
		{
			if (state?.Tree == null)
				yield break;
			foreach (var node in FlattenTree(state.Tree))
			{
				if (node.Path == selectedPath)
					continue;
				yield return (node.X, node.Y, node.Width, node.Height);
			}
		}

		static IEnumerable<DesignerElementNode> FlattenTree(DesignerElementNode node)
		{
			yield return node;
			foreach (var child in node.Children)
			{
				foreach (var descendant in FlattenTree(child))
					yield return descendant;
			}
		}

		void UpdateMarquee(Point pointInSurface)
		{
			if (!marqueeActive)
			{
				if (Math.Abs(pointInSurface.X - marqueeStartSurface.X) < DragThreshold
					&& Math.Abs(pointInSurface.Y - marqueeStartSurface.Y) < DragThreshold)
					return;
				marqueeActive = true;
				marqueeBorder.Visibility = Visibility.Visible;
			}
			var left = Math.Min(pointInSurface.X, marqueeStartSurface.X);
			var top = Math.Min(pointInSurface.Y, marqueeStartSurface.Y);
			marqueeBorder.Width = Math.Abs(pointInSurface.X - marqueeStartSurface.X);
			marqueeBorder.Height = Math.Abs(pointInSurface.Y - marqueeStartSurface.Y);
			Canvas.SetLeft(marqueeBorder, left);
			Canvas.SetTop(marqueeBorder, top);
		}

		/// <summary>Resolves a completed marquee drag or falls back to a plain single-element
		/// click when the drag threshold was never exceeded.</summary>
		void FinishMarquee(Point pointInSurface)
		{
			var wasActive = marqueeActive;
			marqueePending = false;
			marqueeActive = false;
			marqueeBorder.Visibility = Visibility.Collapsed;
			ReleaseMouseCapture();
			if (!wasActive)
			{
				if (state is { } currentState)
				{
					var (designX, designY) = viewport.SurfaceToDesign(pointInSurface.X, pointInSurface.Y);
					HitTestAndSelect(currentState.Version, designX, designY);
				}
				return;
			}
			SelectWithinMarquee(pointInSurface);
		}

		/// <summary>Selects every element (excluding the root) whose bounds intersect the marquee
		/// rectangle - resolved entirely client-side against the already-known element tree, no
		/// RPC needed, matching RemoteFormsDesignerControl's own local intersect-test marquee.</summary>
		void SelectWithinMarquee(Point endPointInSurface)
		{
			secondarySelection.Clear();
			var (x0, y0) = viewport.SurfaceToDesign(marqueeStartSurface.X, marqueeStartSurface.Y);
			var (x1, y1) = viewport.SurfaceToDesign(endPointInSurface.X, endPointInSurface.Y);
			var marqueeRect = new Rect(new Point(x0, y0), new Point(x1, y1));
			var matches = new List<string>();
			if (state?.Tree != null)
			{
				foreach (var node in FlattenTree(state.Tree))
				{
					if (node.Path == RootElementId)
						continue;
					if (marqueeRect.IntersectsWith(new Rect(node.X, node.Y, node.Width, node.Height)))
						matches.Add(node.Path);
				}
			}
			selectedPath = matches.Count > 0 ? matches[0] : null;
			for (var i = 1; i < matches.Count; i++)
				secondarySelection.Add(matches[i]);
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			RestoreSelection();
		}

		/// <summary>Ctrl-click: toggles the hit-tested element's membership in the multi-selection
		/// (primary + secondary), matching UnoDesignRuntimeHost's own Ctrl-click toggle behavior.
		/// The root is excluded - it can't sensibly join a multi-selection group.</summary>
		void ToggleMultiSelect(long baseVersion, double designX, double designY)
		{
			var hit = client.HitTestAsync(baseVersion, designX, designY).GetAwaiter().GetResult();
			if (!hit.Hit || hit.PickPath == RootElementId)
				return;
			var path = hit.PickPath;
			if (path == selectedPath)
			{
				selectedPath = secondarySelection.Count > 0 ? PopFirst(secondarySelection) : null;
			}
			else if (secondarySelection.Contains(path))
			{
				secondarySelection.Remove(path);
			}
			else if (selectedPath == null)
			{
				selectedPath = path;
			}
			else
			{
				secondarySelection.Add(path);
			}
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			RestoreSelection();
		}

		static string PopFirst(HashSet<string> set)
		{
			var first = set.First();
			set.Remove(first);
			return first;
		}

		/// <summary>Shows snap alignment guides at the given design positions
		/// ((isVertical, position) pairs); empty clears them.</summary>
		void SetSnapGuides(IReadOnlyList<(bool IsVertical, double Position)> guides)
		{
			foreach (var guide in snapGuides)
				snapGuideOverlay.Children.Remove(guide);
			snapGuides.Clear();
			foreach (var (isVertical, position) in guides)
			{
				var guide = new Rectangle {
					Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x5D, 0x2A)),
					IsHitTestVisible = false
				};
				snapGuides.Add(guide);
				snapGuideOverlay.Children.Add(guide);
				if (isVertical)
				{
					Canvas.SetLeft(guide, position * viewport.Scale);
					Canvas.SetTop(guide, 0);
					guide.Width = 1;
					guide.Height = snapGuideOverlay.Height;
				}
				else
				{
					Canvas.SetLeft(guide, 0);
					Canvas.SetTop(guide, position * viewport.Scale);
					guide.Width = snapGuideOverlay.Width;
					guide.Height = 1;
				}
			}
		}

		/// <summary>Whether the tab-order badge overlay is currently shown.</summary>
		public bool ShowTabOrder => showTabOrder;

		/// <summary>Toggles the tab-order badge overlay (matching
		/// <c>RemoteFormsDesignerControl.SetTabOrderMode</c>'s own toggle).</summary>
		public void SetTabOrderMode(bool show)
		{
			showTabOrder = show;
			UpdateTabOrderOverlay();
		}

		/// <summary>Draws a small numbered badge near every element that reports a TabIndex
		/// property - <c>WpfSurfaceHostService.BuildProperties</c> already reflects it onto the
		/// wire for every Control-derived element (designer-common.md's Properties pad plumbing),
		/// so this needs no protocol change, just reading <see cref="DesignerElementNode.Properties"/>.
		/// An element with no TabIndex property (e.g. a non-Control panel) shows no badge.</summary>
		void UpdateTabOrderOverlay()
		{
			tabOrderOverlay.Children.Clear();
			if (!showTabOrder || state?.Tree == null)
				return;
			foreach (var node in FlattenTree(state.Tree))
			{
				var tabIndex = node.Properties?.FirstOrDefault(p => p.Name == "TabIndex")?.Value;
				if (string.IsNullOrEmpty(tabIndex))
					continue;
				var badge = new Border {
					Background = Brushes.RoyalBlue,
					CornerRadius = new CornerRadius(8),
					Padding = new Thickness(5, 1, 5, 1),
					Child = new TextBlock {
						Text = tabIndex, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11
					}
				};
				var (x, y) = (node.X * viewport.Scale, node.Y * viewport.Scale);
				Canvas.SetLeft(badge, x - 5);
				Canvas.SetTop(badge, y - 8);
				tabOrderOverlay.Children.Add(badge);
			}
		}

		#region Grid row/column drag guides

		/// <summary>Re-queries and re-shows the Grid guide overlay for the current selection -
		/// hides it when nothing/a non-Grid is selected. Blocking for the same reason every other
		/// query/mutation RPC on this control is.</summary>
		void RefreshGridGuides(DesignerElementNode? node)
		{
			if (node == null || node.Type != "Grid" || state == null)
			{
				SetGridGuideOverlay(null, default, Array.Empty<double>(), Array.Empty<double>());
				return;
			}
			var guides = client.QueryGridGuidesAsync(state.Version, node.Id).GetAwaiter().GetResult();
			if (!guides.Accepted)
			{
				SetGridGuideOverlay(null, default, Array.Empty<double>(), Array.Empty<double>());
				return;
			}
			SetGridGuideOverlay(node.Id, new Rect(node.X, node.Y, node.Width, node.Height),
				guides.RowTracks.Select(t => t.Offset).ToArray(),
				guides.ColumnTracks.Select(t => t.Offset).ToArray());
		}

		/// <summary>Shows draggable divider lines at every INTERIOR row/column boundary (i.e. not
		/// the Grid's own top/left edge or its far bottom/right edge - a Grid with N rows has N-1
		/// interior dividers) over the given Grid's design rect. Empty offsets hide the overlay.</summary>
		void SetGridGuideOverlay(string? elementId, Rect gridRect, double[] rowOffsets, double[] colOffsets)
		{
			foreach (var guide in gridRowGuides)
				gridGuideOverlay.Children.Remove(guide);
			foreach (var guide in gridColGuides)
				gridGuideOverlay.Children.Remove(guide);
			gridRowGuides.Clear();
			gridColGuides.Clear();
			gridGuideElementId = elementId;
			gridGuideRect = gridRect;
			gridRowOffsets = rowOffsets;
			gridColOffsets = colOffsets;
			for (var i = 1; i < gridRowOffsets.Length; i++)
			{
				var guide = new Rectangle { Height = 1, Fill = Brushes.DodgerBlue, IsHitTestVisible = false };
				gridRowGuides.Add(guide);
				gridGuideOverlay.Children.Add(guide);
			}
			for (var i = 1; i < gridColOffsets.Length; i++)
			{
				var guide = new Rectangle { Width = 1, Fill = Brushes.DodgerBlue, IsHitTestVisible = false };
				gridColGuides.Add(guide);
				gridGuideOverlay.Children.Add(guide);
			}
			LayoutGridGuides();
		}

		void LayoutGridGuides()
		{
			var scale = viewport.Scale;
			var left = gridGuideRect.X * scale;
			var top = gridGuideRect.Y * scale;
			for (var i = 0; i < gridRowGuides.Count; i++)
			{
				Canvas.SetLeft(gridRowGuides[i], left);
				Canvas.SetTop(gridRowGuides[i], top + gridRowOffsets[i + 1] * scale);
				gridRowGuides[i].Width = gridGuideRect.Width * scale;
			}
			for (var i = 0; i < gridColGuides.Count; i++)
			{
				Canvas.SetLeft(gridColGuides[i], left + gridColOffsets[i + 1] * scale);
				Canvas.SetTop(gridColGuides[i], top);
				gridColGuides[i].Height = gridGuideRect.Height * scale;
			}
		}

		/// <summary>The design-space divider (row/column boundary) under a point, as
		/// (isRow, index), or null - "index" is the row/column BEFORE the divider, matching
		/// <c>design/set-grid-track-size</c>'s own indexing.</summary>
		(bool IsRow, int Index)? GridGuideAt(Point designPoint)
		{
			if (gridGuideElementId == null)
				return null;
			var tolerance = 4 / viewport.Scale;
			for (var i = 1; i < gridColOffsets.Length; i++)
			{
				if (Math.Abs(designPoint.X - (gridGuideRect.X + gridColOffsets[i])) <= tolerance)
					return (false, i - 1);
			}
			for (var i = 1; i < gridRowOffsets.Length; i++)
			{
				if (Math.Abs(designPoint.Y - (gridGuideRect.Y + gridRowOffsets[i])) <= tolerance)
					return (true, i - 1);
			}
			return null;
		}

		void BeginGridGuideDrag(bool isRow, int index, Point start)
		{
			gridGuideDragPending = true;
			gridGuideDragIsRow = isRow;
			gridGuideDragIndex = index;
			gridGuideDragStart = start;
			CaptureMouse();
		}

		void UpdateGridGuideDrag(Point current)
		{
			var offsets = gridGuideDragIsRow ? gridRowOffsets : gridColOffsets;
			if (gridGuideDragIndex + 1 >= offsets.Length)
				return;
			var originalOffset = offsets[gridGuideDragIndex + 1];
			var scale = viewport.Scale;
			if (gridGuideDragIsRow)
			{
				var y = (gridGuideRect.Y + originalOffset + (current.Y - gridGuideDragStart.Y)) * scale;
				if (gridGuideDragIndex < gridRowGuides.Count)
					Canvas.SetTop(gridRowGuides[gridGuideDragIndex], y);
			}
			else
			{
				var x = (gridGuideRect.X + originalOffset + (current.X - gridGuideDragStart.X)) * scale;
				if (gridGuideDragIndex < gridColGuides.Count)
					Canvas.SetLeft(gridColGuides[gridGuideDragIndex], x);
			}
		}

		/// <summary>Commits the completed divider drag: the new size for the row/column BEFORE the
		/// divider is (new divider position) - (that row/column's own start offset), matching
		/// WinUIXamlDesignerViewContent.SetGridLength's identical math.</summary>
		void EndGridGuideDrag(Point end)
		{
			gridGuideDragPending = false;
			ReleaseMouseCapture();
			if (gridGuideElementId is not { } elementId || state == null)
				return;
			var offsets = gridGuideDragIsRow ? gridRowOffsets : gridColOffsets;
			if (gridGuideDragIndex + 1 >= offsets.Length)
				return;
			var originalOffset = offsets[gridGuideDragIndex + 1];
			var newDividerPosition = gridGuideDragIsRow
				? gridGuideRect.Y + originalOffset + (end.Y - gridGuideDragStart.Y)
				: gridGuideRect.X + originalOffset + (end.X - gridGuideDragStart.X);
			var newSize = Math.Max(1, newDividerPosition - offsets[gridGuideDragIndex]);
			var result = client.SetGridTrackSizeAsync(state.Version, elementId, gridGuideDragIsRow, gridGuideDragIndex, newSize)
				.GetAwaiter().GetResult();
			Show(result);
			DocumentChanged?.Invoke(this, result);
		}

		#endregion

		#region Inline text editing (double-click)

		/// <summary>The element's Text/Content property, if it holds a plain string value safe to
		/// edit inline - "Text" is preferred (TextBlock/TextBox/etc.), then "Content" only when
		/// its <see cref="DesignerPropertyInfo.Kind"/> is "String" (never when Content holds a
		/// nested visual/DesignItem, which a ContentControl's Content commonly does). Returns null
		/// (silently no-op on double-click) when neither applies, e.g. a layout panel.</summary>
		static string? ResolveTextPropertyName(DesignerElementNode node)
		{
			bool IsEditableString(DesignerPropertyInfo p) => !p.IsReadOnly && p.Kind == "String";
			if (node.Properties.FirstOrDefault(p => p.Name == "Text" && IsEditableString(p)) != null)
				return "Text";
			if (node.Properties.FirstOrDefault(p => p.Name == "Content" && IsEditableString(p)) != null)
				return "Content";
			return null;
		}

		/// <summary>Shows the inline text editor over the given design rect, pre-filled with
		/// <paramref name="text"/>. Committed via Enter or focus loss; Escape cancels - matching
		/// UnoDesignSurfaceControl's own BeginTextEdit/EndTextEdit.</summary>
		void BeginTextEdit(string elementId, string propertyName, double x, double y, double width, double height, string text)
		{
			textEditElementId = elementId;
			textEditPropertyName = propertyName;
			textEditRect = new Rect(x, y, width, height);
			textEditing = true;
			textEditor.Text = text ?? "";
			textEditor.Visibility = Visibility.Visible;
			LayoutTextEditor();
			textEditor.Focus();
			textEditor.SelectAll();
		}

		void LayoutTextEditor()
		{
			if (!textEditing)
				return;
			var scale = viewport.Scale;
			textEditor.Margin = new Thickness(
				framePresenter.Visual.Margin.Left + textEditRect.X * scale,
				framePresenter.Visual.Margin.Top + textEditRect.Y * scale, 0, 0);
			textEditor.Width = textEditRect.Width * scale;
			textEditor.Height = textEditRect.Height * scale;
			textEditor.FontSize = 14 * scale;
		}

		void OnTextEditorKeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Enter)
			{
				EndTextEdit(commit: true);
				e.Handled = true;
			}
			else if (e.Key == Key.Escape)
			{
				EndTextEdit(commit: false);
				e.Handled = true;
			}
		}

		void OnTextEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
		{
			if (textEditing)
				EndTextEdit(commit: true);
		}

		/// <summary>Blocking for the same reason <see cref="CommitBounds"/> is - see its own doc
		/// comment.</summary>
		void EndTextEdit(bool commit)
		{
			if (!textEditing)
				return;
			var text = textEditor.Text;
			var elementId = textEditElementId!;
			var propertyName = textEditPropertyName!;
			textEditing = false;
			textEditElementId = null;
			textEditPropertyName = null;
			textEditor.Visibility = Visibility.Collapsed;
			if (!commit || state == null)
				return;
			var result = client.SetPropertyAsync(RequireVersion(), elementId, propertyName, text).GetAwaiter().GetResult();
			Show(result);
			DocumentChanged?.Invoke(this, result);
		}

		#endregion

		/// <summary>The dragged bounds for a pointer delta: a null handle moves the whole element,
		/// a named handle drags that edge/corner and leaves the opposite one anchored. Sizes are
		/// clamped non-negative so dragging a handle past the opposite edge collapses rather than
		/// producing an inverted rect.</summary>
		Rect ApplyGesture(double dx, double dy)
		{
			var r = gestureOriginalBounds;
			if (gestureHandle == null)
				return new Rect(r.X + dx, r.Y + dy, r.Width, r.Height);

			double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
			if (gestureHandle.Contains('w')) left += dx;
			if (gestureHandle.Contains('e')) right += dx;
			if (gestureHandle.Contains('n')) top += dy;
			if (gestureHandle.Contains('s')) bottom += dy;
			return new Rect(Math.Min(left, right), Math.Min(top, bottom),
				Math.Abs(right - left), Math.Abs(bottom - top));
		}

		void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (gridGuideDragPending)
			{
				var (gx, gy) = viewport.SurfaceToDesign(e.GetPosition(designSurface).X, e.GetPosition(designSurface).Y);
				EndGridGuideDrag(new Point(gx, gy));
				return;
			}
			if (marqueePending)
			{
				FinishMarquee(e.GetPosition(designSurface));
				return;
			}
			if (!gesturePending)
				return;
			var wasActive = gestureActive;
			gesturePending = false;
			gestureActive = false;
			ReleaseMouseCapture();
			SetSnapGuides(Array.Empty<(bool, double)>());
			if (!wasActive || selectedPath == null || state == null)
			{
				RestoreSelection();
				return;
			}
			// One RPC for the whole gesture; Show() re-renders and re-places the adorner from the
			// authoritative post-edit tree, so a rejected/adjusted placement snaps back correctly.
			// Uses the last (possibly snap-corrected) delta from OnMouseMove rather than
			// re-deriving an unsnapped one from the release position.
			var bounds = ApplyGesture(lastGestureDx, lastGestureDy);
			CommitBounds(selectedPath, bounds);
		}

		/// <summary>Commits one drag-resize/move gesture. Deliberately BLOCKING
		/// (<c>.GetAwaiter().GetResult()</c>), called directly from the mouse-up handler that is
		/// always already on the dispatcher thread - NOT an async method awaited with
		/// <c>ConfigureAwait(true)</c> from a fire-and-forget wrapper, which this used to be.
		/// That pattern was proven unreliable live: a real drag committed and visibly resized the
		/// element (confirmed via <c>od.wpf-designer.surface-geometry</c>), but the continuation
		/// after the await resumed on a thread-pool thread instead of the dispatcher thread it was
		/// captured from (verified via <c>Dispatcher.Thread.ManagedThreadId</c> vs
		/// <c>Environment.CurrentManagedThreadId</c> differing), so touching WPF objects afterward
		/// threw a cross-thread <c>InvalidOperationException</c> that <c>FireAndForget</c> silently
		/// suppressed - <see cref="DocumentChanged"/> never reached <c>WpfViewContent</c>, so the
		/// file was never marked dirty even though the resize had genuinely applied and would be
		/// silently lost if the user closed without touching anything else first. Blocking here
		/// needs no SynchronizationContext capture at all: <c>GetResult()</c> simply returns
		/// control to whichever thread called it, which by construction already IS the dispatcher
		/// thread for every caller of this method.</summary>
		void CommitBounds(string elementId, Rect bounds)
		{
			var result = SetBoundsAsync(elementId, bounds.X, bounds.Y, bounds.Width, bounds.Height).GetAwaiter().GetResult();
			Show(result);
			DocumentChanged?.Invoke(this, result);
		}

		void OnDragOver(object sender, DragEventArgs e)
		{
			e.Effects = state != null && ToolboxItemOf(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
			e.Handled = true;
		}

		void OnDrop(object sender, DragEventArgs e)
		{
			if (state == null || ToolboxItemOf(e.Data) is not { } item)
				return;
			e.Handled = true;
			var point = e.GetPosition(designSurface);
			var (designX, designY) = viewport.SurfaceToDesign(point.X, point.Y);
			// Drop onto whatever element is under the pointer, falling back to the document root -
			// the child decides whether that parent actually accepts a new child there and rejects
			// the operation if not, so no container knowledge is duplicated on this side.
			CommitDrop(item, designX, designY);
		}

		/// <summary>Blocking for the same reason <see cref="CommitBounds"/> is - see its own doc
		/// comment.</summary>
		void CommitDrop(DesignerToolboxItemInfo item, double designX, double designY)
		{
			var hit = client.HitTestAsync(state!.Version, designX, designY).GetAwaiter().GetResult();
			var parentId = string.IsNullOrEmpty(hit.PickPath) ? OutlineRoot?.Id ?? "" : hit.PickPath;
			var result = AddElementAsync(parentId, item, proposedName: "", designX, designY).GetAwaiter().GetResult();
			Show(result);
			// A toolbox drop should select the element it just created, matching what a real drop
			// onto every other designer backend already does (and what a real end user dragging a
			// control expects: the Properties pad immediately shows what was just dropped, with no
			// extra click). CreatedElementId is only meaningful on this exact response - see its
			// own doc comment for why WPF can't just look the element up by name afterward like
			// WinForms/WinUI do.
			if (result.Accepted && result.CreatedElementId != null)
				selectedPath = result.CreatedElementId;
			secondarySelection.Clear();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			DocumentChanged?.Invoke(this, result);
		}

		static DesignerToolboxItemInfo? ToolboxItemOf(IDataObject data)
			=> data?.GetDataPresent(typeof(DesignerToolboxItemInfo)) == true
				? data.GetData(typeof(DesignerToolboxItemInfo)) as DesignerToolboxItemInfo
				: null;

		/// <summary>Blocking for the same reason <see cref="CommitBounds"/> is - see its own doc
		/// comment.</summary>
		void HitTestAndSelect(long baseVersion, double designX, double designY)
		{
			var hit = client.HitTestAsync(baseVersion, designX, designY).GetAwaiter().GetResult();
			// Hit, not PickPath emptiness: the root's own path is "" (see DesignerHitTestResult.Hit),
			// so testing PickPath here made clicking the Window/UserControl clear the selection
			// instead of selecting it - the root could never be selected, and therefore never
			// resized by dragging its handles.
			selectedPath = hit.Hit ? hit.PickPath : null;
			// A plain (non-marquee, non-Ctrl) click always replaces the whole selection, matching
			// every other designer's click semantics.
			secondarySelection.Clear();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			RestoreSelection();
		}

		void RestoreSelection()
		{
			var node = SelectedNode;
			if (node == null)
				adornerLayer.ClearSelection();
			else
				adornerLayer.ShowSelection(new Rect(node.X, node.Y, node.Width, node.Height), viewport,
					node.Name ?? node.Type);
			UpdateSecondarySelectionAdorners();
			RefreshGridGuides(node);
		}

		void UpdateSecondarySelectionAdorners()
		{
			if (secondarySelection.Count == 0 || state?.Tree == null)
			{
				adornerLayer.ClearSecondarySelection();
				return;
			}
			var boxes = new List<(string, Rect)>();
			foreach (var path in secondarySelection)
			{
				if (FindNodeByPath(state.Tree, path) is { } node)
					boxes.Add((path, new Rect(node.X, node.Y, node.Width, node.Height)));
			}
			adornerLayer.SetSecondarySelection(boxes, viewport);
		}

		DesignerElementNode? SelectedNode =>
			selectedPath != null && state?.Tree != null ? FindNodeByPath(state.Tree, selectedPath) : null;

		/// <summary>The current selection's rendered bounds in screen coordinates, or null when
		/// nothing is selected - for DevFlow's resize-drag smoke probe
		/// (<c>od.wpf-designer.surface-geometry</c>), the out-of-process equivalent of
		/// <c>WpfViewContent.SurfaceGeometry</c>'s old direct <c>FrameworkElement.PointToScreen</c>
		/// call on the live visual. Converts through the same <see cref="DesignViewport"/> the
		/// selection outline itself is drawn with, then maps the design surface's own origin to
		/// the screen once via <see cref="UIElement.PointToScreen"/>.</summary>
		public Rect? ScreenBoundsOfSelected() => ScreenBoundsOf(SelectedNode);

		/// <summary>The resize-drag smoke probe shared by all three designers
		/// (<c>od.&lt;x&gt;-designer.surface-geometry</c>): the rendered design bitmap bounds,
		/// the current selection outline bounds, the bottom-right resize handle position and
		/// the selected element's own bounds, all in screen coordinates.</summary>
		public DesignerSurfaceGeometry SurfaceGeometry()
		{
			var frame = DesignerSurfaceGeometryProbe.ScreenBoundsOf(framePresenter.Visual);
			var selection = ScreenBoundsOfSelected() ?? default;
			var handle = new Point(selection.X + selection.Width, selection.Y + selection.Height);
			return new DesignerSurfaceGeometry(frame, selection, handle, selection);
		}

		/// <summary>Screen-coordinate bounds for an arbitrary tree node (found by
		/// <see cref="DesignerElementNode.Id"/>/tree path), not just the current selection - for
		/// DevFlow's <c>od.wpf-designer.query-element-screen-bounds</c> probe, which asks for a
		/// named element regardless of what's selected.</summary>
		public Rect? ScreenBoundsOf(string? elementId)
			=> elementId != null && state?.Tree != null ? ScreenBoundsOf(FindNodeByPath(state.Tree, elementId)) : null;

		Rect? ScreenBoundsOf(DesignerElementNode? node)
		{
			if (node == null || !IsVisible)
				return null;
			var (left, top) = viewport.DesignToSurface(node.X, node.Y);
			var (right, bottom) = viewport.DesignToSurface(node.X + node.Width, node.Y + node.Height);
			var topLeft = designSurface.PointToScreen(new Point(left, top));
			var bottomRight = designSurface.PointToScreen(new Point(right, bottom));
			return new Rect(topLeft, bottomRight);
		}

		/// <summary>Finds a node by its <c>x:Name</c>/component name (depth-first), for DevFlow
		/// probes that address elements by name rather than by tree path - the same lookup
		/// <c>WpfSurfaceHostRpcTests.FindByName</c> already uses against the raw DTO.</summary>
		public DesignerElementNode? FindNodeByName(string name)
			=> state?.Tree is { } tree
				// Falls back to matching the TYPE name for unnamed elements, and only if no
				// x:Name matched first. Without it the document root is unreachable by name: a
				// Window/UserControl normally carries no x:Name, so the Outline pad lists it by
				// type (DocumentOutlineControl shows Name ?? Type) and there was no way to ask
				// for "the UserControl" - which is exactly how the WinForms designer's own
				// root-resize path selects its form. First match wins, so an unnamed element
				// deeper in the tree can shadow a same-typed one; unambiguous for the root, which
				// is visited first.
				? FindNodeByNameCore(tree, name) ?? FindNodeByTypeCore(tree, name)
				: null;

		static DesignerElementNode? FindNodeByNameCore(DesignerElementNode node, string name)
		{
			if (node.Name == name)
				return node;
			foreach (var child in node.Children)
			{
				if (FindNodeByNameCore(child, name) is { } found)
					return found;
			}
			return null;
		}

		static DesignerElementNode? FindNodeByTypeCore(DesignerElementNode node, string typeName)
		{
			if (node.Name == null && node.Type == typeName)
				return node;
			foreach (var child in node.Children)
			{
				if (FindNodeByTypeCore(child, typeName) is { } found)
					return found;
			}
			return null;
		}

		/// <summary>An <see cref="ICustomTypeDescriptor"/> for the shared Properties pad, backed
		/// by the currently-selected element's DDP property list (see
		/// <see cref="WpfSurfaceElementPropertyAdapter"/>), or null when nothing is selected.
		/// A fresh adapter is returned on every access rather than cached, since the underlying
		/// <see cref="DesignerElementNode"/> instance changes on every re-render.</summary>
		public WpfSurfaceElementPropertyAdapter? SelectedPropertyAdapter =>
			SelectedNode is { } node
				? new WpfSurfaceElementPropertyAdapter(client, () => state!.Version, node, OnPropertyEdited)
				: null;
		public object[] SelectedPropertyAdapters => state?.Tree == null ? Array.Empty<object>() : SelectedElementIds
			.Select(id => FindNodeByPath(state.Tree, id))
			.Where(node => node != null)
			.Select(node => (object)new WpfSurfaceElementPropertyAdapter(client, () => state!.Version, node!, OnPropertyEdited))
			.ToArray();

		/// <summary>Renders a property edit's resulting state, then raises <see cref="SelectionChanged"/>
		/// so <c>WpfViewContent.OnSelectionChanged</c> (its only subscriber) re-checks the document
		/// version and marks the file dirty - the same "refresh dependent UI, then reconcile dirty
		/// state" signal every other mutation path here already raises (see
		/// <c>CommitBounds</c>/<c>CommitDrop</c>/<c>CommitDelete</c>). A raw
		/// <c>Show</c> callback (this method's previous wiring) rendered the edit but never told
		/// <c>WpfViewContent</c> a mutation happened at all, so editing a property through the
		/// Properties pad silently never dirtied the document.</summary>
		void OnPropertyEdited(DesignerSessionState newState)
		{
			Show(newState);
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			DocumentChanged?.Invoke(this, newState);
		}

		static DesignerElementNode? FindNodeByPath(DesignerElementNode node, string path)
		{
			if (node.Path == path)
				return node;
			foreach (var child in node.Children)
			{
				if (FindNodeByPath(child, path) is { } found)
					return found;
			}
			return null;
		}

		public event EventHandler? SelectionChanged;

		/// <summary>Raised after an actual accepted mutation (bounds/add/delete/rename/property
		/// edit) commits and renders - distinct from <see cref="SelectionChanged"/>, which also
		/// fires for a plain selection with no mutation at all (<see cref="SelectElementId"/>,
		/// <c>HitTestAndSelect</c>). <c>WpfViewContent</c> uses this, not
		/// <see cref="DesignerSessionState.Version"/> comparisons, to decide when to mark the file
		/// dirty: the DDP's <c>state.Version</c> is only ever bumped by <c>session/open</c>/
		/// <c>session/update</c> (see <c>WpfSurfaceHostService.NewState</c>, which always echoes
		/// back the caller's own <c>baseVersion</c> unchanged) - every mutation RPC's returned
		/// version is identical to the version before it, so a version-based "did anything change"
		/// check can never fire. Bumping that wire-level version is a separate, larger change (it
		/// would need matching updates in <c>WpfSurfaceHostRpcTests</c> and the other two
		/// designers' backends, which share the same DDP contract); this event sidesteps the need
		/// for it entirely on the host side.</summary>
		public event EventHandler<DesignerSessionState>? DocumentChanged;

		/// <summary>Raises <see cref="DocumentChanged"/> for a mutation committed by a blocking
		/// caller outside this class (<c>WpfDesignDevFlowActions.DropToolboxItem</c>) - a plain
		/// event can only be invoked from its declaring type, so this is that seam.</summary>
		internal void NotifyDocumentChanged(DesignerSessionState newState) => DocumentChanged?.Invoke(this, newState);

		/// <summary>The currently selected element's id (its tree path), or null.</summary>
		public string? SelectedElementId => selectedPath;
		public IReadOnlyList<string> SelectedElementIds => selectedPath == null
			? Array.Empty<string>()
			: new[] { selectedPath }.Concat(secondarySelection).ToArray();

		/// <summary>The current element tree, ready to hand straight to
		/// <see cref="ICSharpCode.SharpDevelop.Widgets.DocumentOutlineControl.SetRoot"/> - unlike
		/// WinForms (whose flat <c>DesignerComponentInfo</c> list needs
		/// <c>FormsDesignerViewContent.BuildOutlineTree</c> to become a tree first), WPF's own DDP
		/// shape already IS the <see cref="DesignerElementNode"/> tree the Outline pad wants, with
		/// no conversion needed (designer-common.md's Document Outline section).</summary>
		public DesignerElementNode? OutlineRoot => state?.Tree;

		/// <summary>Outline pad -> design surface: selects the given element id (a tree path,
		/// see <see cref="DesignerElementNode.Path"/>) without a round trip to the child - the
		/// bounds needed to draw the selection outline are already in the current tree, matching
		/// how <c>RemoteFormsDesignerControl.SelectComponent</c> selects locally from data the
		/// state already carries rather than re-querying the child.</summary>
		/// <summary>Selects an element by tree path, or clears the selection when
		/// <paramref name="id"/> is null. The empty string is NOT "no selection" - it is the
		/// document root's own id (see <see cref="DesignerHitTestResult.Hit"/>), so passing it
		/// selects the Window/UserControl itself; treating it as "clear" here previously made the
		/// root unselectable from the Outline pad too.</summary>
		public void SelectElementId(string? id)
		{
			selectedPath = id;
			secondarySelection.Clear();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			RestoreSelection();
		}

		/// <summary>Sets the multi-selection to exactly the named elements (by
		/// <see cref="DesignerElementNode.Name"/>, resolved via <see cref="FindNodeByName"/>) -
		/// the out-of-process host's own equivalent of <c>od.wpf-designer.multi-select</c>, driving
		/// the same primary/secondary state Ctrl-click and marquee-select already populate.
		/// Unresolvable names are silently skipped, matching a stale-name query returning fewer
		/// elements rather than throwing.</summary>
		public void SetMultiSelection(IReadOnlyList<string> names)
		{
			var paths = names
				.Select(FindNodeByName)
				.Where(node => node != null)
				.Select(node => node!.Path)
				.ToList();
			selectedPath = paths.Count > 0 ? paths[0] : null;
			secondarySelection.Clear();
			for (var i = 1; i < paths.Count; i++)
				secondarySelection.Add(paths[i]);
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			RestoreSelection();
		}

		#region Remaining mutations (design/set-bounds, design/add-element, design/delete-elements, design/rename)

		// Callable as RPCs, matching designer-common.md's "wire the protocol first, presentation
		// later" convergence order - the same shape WinForms landed `design/rename` in before any
		// UI called it ("no existing 'rename an already-named element' call site... landed as a
		// ready-to-use capability only"). None of these have interactive UI yet (drag/resize,
		// toolbox drop) - that is real future work building on top of these, not attempted here,
		// since it needs the coordinate-mismatch bug fixed first to place drag handles/drop
		// targets correctly against the rendered frame.

		/// <summary>Moves/resizes the given element (<c>design/set-bounds</c>). Coordinates are
		/// design units, matching <see cref="DesignerElementNode.X"/>/Y/Width/Height. Does NOT
		/// render the result - see <see cref="Show"/>.</summary>
		public async Task<DesignerSessionState> SetBoundsAsync(string elementId, double x, double y, double width, double height, CancellationToken cancellationToken = default)
		{
			state = await client.SetBoundsAsync(RequireVersion(), elementId, x, y, width, height, cancellationToken).ConfigureAwait(false);
			return state;
		}

		/// <summary>Inserts a new element under <paramref name="parentId"/> (<c>design/add-element</c>,
		/// a toolbox drop in spirit). Does not change the current selection - the caller decides
		/// whether to select the new element (its id isn't known ahead of the call). Does NOT
		/// render the result - see <see cref="Show"/>.</summary>
		public async Task<DesignerSessionState> AddElementAsync(string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, CancellationToken cancellationToken = default)
		{
			state = await client.AddElementAsync(RequireVersion(), parentId, item, proposedName, x, y, cancellationToken).ConfigureAwait(false);
			return state;
		}

		/// <summary>Removes the whole selection - primary plus every secondary multi-selected
		/// element (<c>design/delete-elements</c>, already batch-capable, so no new RPC was needed
		/// to support this). Clears the selection first - the deleted elements' paths are no
		/// longer meaningful once the tree is rebuilt (sibling indices can shift), matching
		/// <c>RemoteFormsDesignerControl</c>'s own "delete clears selection" behavior. Does NOT
		/// raise <see cref="SelectionChanged"/> or render the result - see <see cref="Show"/>; the
		/// caller raises the event and renders once back on the correct thread (this method's own
		/// await cannot safely do either, see <see cref="Show"/>'s remarks).</summary>
		public async Task<DesignerSessionState?> DeleteSelectedAsync(CancellationToken cancellationToken = default)
		{
			if (selectedPath is not { } id)
				return null;
			var ids = new List<string> { id };
			ids.AddRange(secondarySelection);
			selectedPath = null;
			secondarySelection.Clear();
			state = await client.DeleteElementsAsync(RequireVersion(), ids.ToArray(), cancellationToken).ConfigureAwait(false);
			return state;
		}

		/// <summary>Renames the currently selected element (<c>design/rename</c>). A rename does
		/// not restructure the tree, so the current selection is kept. Does NOT render the result -
		/// see <see cref="Show"/>.</summary>
		public async Task<DesignerSessionState?> RenameSelectedAsync(string newName, CancellationToken cancellationToken = default)
		{
			if (selectedPath is not { } id)
				return null;
			state = await client.RenameAsync(RequireVersion(), id, newName, cancellationToken).ConfigureAwait(false);
			return state;
		}

		/// <summary>Switches the design-time theme by name (<c>design/theme</c>) - a no-op
		/// (Accepted = false) when the current project embeds no such theme. Does NOT render
		/// the result - see <see cref="Show"/>.</summary>
		public async Task<DesignerSessionState> SetThemeAsync(string theme, CancellationToken cancellationToken = default)
		{
			state = await client.SetThemeAsync(RequireVersion(), theme, cancellationToken).ConfigureAwait(false);
			return state;
		}

		long RequireVersion() => state?.Version
			?? throw new InvalidOperationException("No document is open - call OpenAsync first.");

		#endregion

		/// <summary>Delete removes the selection; Ctrl+Z/Ctrl+Y raise <see cref="UndoRedoRequested"/>
		/// for <c>WpfViewContent</c> to handle (matching WinUIXamlDesignerViewContent's own
		/// Ctrl+Z/Ctrl+Y -&gt; UndoRedoRequested wiring) - undo/redo is whole-document text
		/// snapshot/restore (see WpfViewContent.Undo/Redo's own doc comment), not something this
		/// surface-only control can do by itself. Full command routing beyond these, matching
		/// <c>RemoteFormsDesignerControl.OnKeyDown</c>'s broader set, is later work.</summary>
		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.Key == Key.Delete && selectedPath != null)
			{
				e.Handled = true;
				CommitDelete();
			}
			else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
			{
				e.Handled = true;
				UndoRedoRequested?.Invoke(this, true);
			}
			else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control)
			{
				e.Handled = true;
				UndoRedoRequested?.Invoke(this, false);
			}
		}

		/// <summary>Raised on Ctrl+Z (true = undo) / Ctrl+Y (false = redo) - <c>WpfViewContent</c>
		/// is the only subscriber, since it owns the undo/redo text-snapshot stacks.</summary>
		public event EventHandler<bool>? UndoRedoRequested;

		/// <summary>Blocking for the same reason <see cref="CommitBounds"/> is - see its own doc
		/// comment.</summary>
		void CommitDelete()
		{
			var result = DeleteSelectedAsync().GetAwaiter().GetResult();
			if (result == null)
				return;
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			Show(result);
			DocumentChanged?.Invoke(this, result);
		}

		/// <summary>Blocking for the same reason <see cref="CommitDelete"/> is.</summary>
		void CommitTheme(string theme)
		{
			if (state == null)
				return;
			var result = SetThemeAsync(theme).GetAwaiter().GetResult();
			Show(result);
			DocumentChanged?.Invoke(this, result);
		}

		#region Align / distribute / match-size (od.wpf-designer.align/.distribute/.match-size)

		/// <summary>The current multi-selection's paths and CURRENT bounds, primary first
		/// (<see cref="selectedPath"/>) then every <see cref="secondarySelection"/> member - the
		/// snapshot align/distribute/match-size all work from. Empty when fewer than two elements
		/// are selected or no tree is loaded.</summary>
		List<(string Path, Rect Bounds)> SelectedBoundsForLayout()
		{
			var result = new List<(string, Rect)>();
			if (state?.Tree == null)
				return result;
			if (selectedPath is { } primary && FindNodeByPath(state.Tree, primary) is { } primaryNode)
				result.Add((primary, new Rect(primaryNode.X, primaryNode.Y, primaryNode.Width, primaryNode.Height)));
			foreach (var path in secondarySelection)
			{
				if (FindNodeByPath(state.Tree, path) is { } node)
					result.Add((path, new Rect(node.X, node.Y, node.Width, node.Height)));
			}
			return result;
		}

		/// <summary>Commits a batch of bounds edits with a single re-render/DocumentChanged at the
		/// end - safe to compute every target rect up front from one snapshot (unlike delete,
		/// which reshuffles sibling-index-derived paths, a bounds-only edit changes no element's
		/// position in the tree, so every other element's path and pre-computed target rect stay
		/// valid across the whole loop).</summary>
		void CommitBoundsForEach(IReadOnlyList<(string Path, Rect NewBounds)> edits)
		{
			if (edits.Count == 0 || state == null)
				return;
			var result = state;
			foreach (var (path, bounds) in edits)
				result = SetBoundsAsync(path, bounds.X, bounds.Y, bounds.Width, bounds.Height).GetAwaiter().GetResult();
			Show(result);
			DocumentChanged?.Invoke(this, result);
		}

		/// <summary>Aligns every other selected element's edge/center to the PRIMARY selection's
		/// (matching UnoDesignRuntimeHost.AlignSelection's own "primary is the anchor" semantics).
		/// mode: "left"/"center"/"right" (horizontal) or "top"/"middle"/"bottom" (vertical).</summary>
		public void AlignSelection(string mode)
		{
			var selected = SelectedBoundsForLayout();
			if (selected.Count < 2)
				return;
			var anchor = selected[0].Bounds;
			var edits = new List<(string, Rect)>();
			foreach (var (path, bounds) in selected.Skip(1))
			{
				var rect = bounds;
				switch (mode)
				{
					case "left": rect.X = anchor.X; break;
					case "center": rect.X = anchor.X + anchor.Width / 2 - bounds.Width / 2; break;
					case "right": rect.X = anchor.X + anchor.Width - bounds.Width; break;
					case "top": rect.Y = anchor.Y; break;
					case "middle": rect.Y = anchor.Y + anchor.Height / 2 - bounds.Height / 2; break;
					case "bottom": rect.Y = anchor.Y + anchor.Height - bounds.Height; break;
					default: continue;
				}
				edits.Add((path, rect));
			}
			CommitBoundsForEach(edits);
		}

		/// <summary>Equal-CENTER spacing (not equal-gap) along <paramref name="axis"/>
		/// ("horizontal"/"vertical"), matching UnoDesignRuntimeHost.DistributeSelection: orders the
		/// selection by center coordinate, steps evenly between the two outer elements' centers,
		/// and moves only the interior elements - requires at least 3 selected elements.</summary>
		public void DistributeSelection(string axis)
		{
			var selected = SelectedBoundsForLayout();
			if (selected.Count < 3)
				return;
			var horizontal = axis == "horizontal";
			var ordered = selected
				.Select(item => (item.Path, item.Bounds,
					Center: horizontal ? item.Bounds.X + item.Bounds.Width / 2 : item.Bounds.Y + item.Bounds.Height / 2))
				.OrderBy(item => item.Center)
				.ToList();
			var min = ordered[0].Center;
			var max = ordered[^1].Center;
			var step = (max - min) / (ordered.Count - 1);
			var edits = new List<(string, Rect)>();
			for (var i = 1; i < ordered.Count - 1; i++)
			{
				var target = min + step * i;
				var rect = ordered[i].Bounds;
				if (horizontal)
					rect.X = target - rect.Width / 2;
				else
					rect.Y = target - rect.Height / 2;
				edits.Add((ordered[i].Path, rect));
			}
			CommitBoundsForEach(edits);
		}

		/// <summary>Resizes every other selected element to match the PRIMARY selection's
		/// width/height/both, keeping each element's own X/Y - matching
		/// UnoDesignRuntimeHost.MatchSizeSelection. mode: "width"/"height"/"both".</summary>
		public void MatchSizeSelection(string mode)
		{
			var selected = SelectedBoundsForLayout();
			if (selected.Count < 2)
				return;
			var anchor = selected[0].Bounds;
			var edits = new List<(string, Rect)>();
			foreach (var (path, bounds) in selected.Skip(1))
			{
				var rect = bounds;
				if (mode is "width" or "both")
					rect.Width = anchor.Width;
				if (mode is "height" or "both")
					rect.Height = anchor.Height;
				edits.Add((path, rect));
			}
			CommitBoundsForEach(edits);
		}

		#endregion
	}
}
