#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
	/// Deliberately scoped down from those two: this control can open a session, show the
	/// rendered frame, and click-to-select a single element via <c>design/hit-test</c>. It does
	/// NOT yet implement drag/resize, marquee-select, toolbox drop, keyboard commands, or
	/// multi-select - those are later phases once this foundation is proven. It is also NOT YET
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

		// Gridlines: a tiled DrawingBrush over the rendered frame, sized in design units so the
		// cells scale with zoom - the same host-side-only approach UnoDesignSurfaceControl uses
		// (CreateGridBrush/UpdateGridBrush/SetGridlines), needing no child/protocol support at all.
		// IsHitTestVisible=false is load-bearing: this sits above the frame image, and a
		// hit-testable overlay with a non-null Background would swallow the mouse input the
		// resize/drag gestures depend on.
		const double GridCellSize = 20;
		readonly DrawingBrush gridBrush = CreateGridBrush();
		readonly Grid gridOverlay = new Grid {
			IsHitTestVisible = false,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top
		};
		bool showGridlines;

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

		public WpfSurfaceDesignerControl(WpfSurfaceHostClient client)
		{
			this.client = client ?? throw new ArgumentNullException(nameof(client));

			designSurface.Children.Add(framePresenter.Visual);
			// Between the frame and the adorners: gridlines draw over the design, selection
			// outlines/handles draw over the gridlines.
			designSurface.Children.Add(gridOverlay);
			designSurface.Children.Add(adornerLayer.Visual);
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
			//  - design theme (Light/Dark) is a WinUI concept its child host re-renders against
			//    (UnoDesignSurfaceControl raises DesignThemeRequested for exactly that). WPF has no
			//    equivalent built-in design-time theme for the engine to switch, so a toggle here
			//    would be inert chrome with nothing behind it - deliberately not shown rather than
			//    shown-and-dead.
			ShowDesignSize = false;
			ShowTheme = false;
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
			var render = newState.Render;
			if (render == null || string.IsNullOrEmpty(render.Data) || render.Width <= 0 || render.Height <= 0)
			{
				framePresenter.Clear();
				viewport = DesignViewport.Identity(0, 0);
				adornerLayer.ClearSelection();
				gridOverlay.Width = gridOverlay.Height = 0;
				return;
			}

			var pixels = DecodeFrame(render.Data);
			framePresenter.SetSource(BitmapSource.Create(render.Width, render.Height, 96, 96, PixelFormats.Bgra32, null, pixels, render.Width * 4));
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
			gridOverlay.Width = framePresenter.Visual.Width;
			gridOverlay.Height = framePresenter.Visual.Height;
			gridOverlay.Margin = framePresenter.Visual.Margin;
			UpdateGridBrush(viewport.Scale);

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
			gridOverlay.Background = show ? gridBrush : null;
			UpdateGridBrush(viewport.Scale);
		}

		static DrawingBrush CreateGridBrush()
		{
			var group = new DrawingGroup();
			var linePen = new Pen(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), 1);
			group.Children.Add(new GeometryDrawing(null, linePen, new LineGeometry(new Point(0, 0), new Point(0, GridCellSize))));
			group.Children.Add(new GeometryDrawing(null, linePen, new LineGeometry(new Point(0, 0), new Point(GridCellSize, 0))));
			return new DrawingBrush(group) {
				TileMode = TileMode.Tile,
				ViewportUnits = BrushMappingMode.Absolute,
				Viewport = new Rect(0, 0, GridCellSize, GridCellSize)
			};
		}

		/// <summary>Keeps the grid cells a fixed size in DESIGN units, so they scale with zoom
		/// (a 20-unit cell stays 20 design units, drawn larger when zoomed in).</summary>
		void UpdateGridBrush(double scale)
		{
			if (showGridlines)
				gridBrush.Viewport = new Rect(0, 0, GridCellSize * scale, GridCellSize * scale);
		}

		/// <summary>Deflate+base64 decode matching the wire shape every DDP backend's
		/// <c>DesignerRenderFrame.Data</c> uses (see designer-common.md's Surface section) -
		/// duplicated per backend by design (each backend also owns its own pixel format
		/// decode), matching WinUI/Uno's <c>RenderCodec.Decode</c>.</summary>
		static byte[] DecodeFrame(string data)
		{
			var compressed = Convert.FromBase64String(data);
			using var input = new MemoryStream(compressed);
			using var deflate = new DeflateStream(input, CompressionMode.Decompress);
			using var output = new MemoryStream();
			deflate.CopyTo(output);
			return output.ToArray();
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

			HitTestAndSelect(currentState.Version, designX, designY);
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

			adornerLayer.ShowSelection(ApplyGesture(dx, dy), viewport, SelectedNode?.Name ?? SelectedNode?.Type);
		}

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
			if (!gesturePending)
				return;
			var wasActive = gestureActive;
			var point = e.GetPosition(designSurface);
			var (designX, designY) = viewport.SurfaceToDesign(point.X, point.Y);
			gesturePending = false;
			gestureActive = false;
			ReleaseMouseCapture();
			if (!wasActive || selectedPath == null || state == null)
			{
				RestoreSelection();
				return;
			}
			// One RPC for the whole gesture; Show() re-renders and re-places the adorner from the
			// authoritative post-edit tree, so a rejected/adjusted placement snaps back correctly.
			var bounds = ApplyGesture(designX - gestureStart.X, designY - gestureStart.Y);
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
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			RestoreSelection();
		}

		void RestoreSelection()
		{
			var node = SelectedNode;
			if (node == null)
			{
				adornerLayer.ClearSelection();
				return;
			}
			adornerLayer.ShowSelection(new Rect(node.X, node.Y, node.Width, node.Height), viewport,
				node.Name ?? node.Type);
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

		/// <summary>Removes the current selection (<c>design/delete-elements</c>). Clears the
		/// selection first - the deleted element's path is no longer meaningful once the tree is
		/// rebuilt (sibling indices can shift), matching <c>RemoteFormsDesignerControl</c>'s own
		/// "delete clears selection" behavior. Does NOT raise <see cref="SelectionChanged"/> or
		/// render the result - see <see cref="Show"/>; the caller raises the event and renders
		/// once back on the correct thread (this method's own await cannot safely do either, see
		/// <see cref="Show"/>'s remarks).</summary>
		public async Task<DesignerSessionState?> DeleteSelectedAsync(CancellationToken cancellationToken = default)
		{
			if (selectedPath is not { } id)
				return null;
			selectedPath = null;
			state = await client.DeleteElementsAsync(RequireVersion(), new[] { id }, cancellationToken).ConfigureAwait(false);
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

		long RequireVersion() => state?.Version
			?? throw new InvalidOperationException("No document is open - call OpenAsync first.");

		#endregion

		/// <summary>Removes the current selection when Delete is pressed - the one keyboard
		/// command this control implements so far (full command routing, matching
		/// <c>RemoteFormsDesignerControl.OnKeyDown</c>'s broader set, is later work).</summary>
		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.Key == Key.Delete && selectedPath != null)
			{
				e.Handled = true;
				CommitDelete();
			}
		}

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
	}
}
