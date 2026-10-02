using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Widgets;

using RenderResult = ICSharpCode.SharpDevelop.Designer.Remote.DesignerRenderFrame;

namespace ICSharpCode.SharpDevelop.Designer.Surface;

/// <summary>
/// The shared design surface every UI framework's designer uses (ICSharpCode.DesignerCanvas addin;
/// see doc/technotes/designer-canvas-addin.md). Shows the bitmap its backend
/// rendered (dpi-scaled pixels at the host display's scale, displayed at logical size)
/// inside a ScrollViewer with the shared designer toolbar (see <see cref="DesignerCanvas"/>),
/// draws a selection outline with resize handles over the picked element, supports
/// drag-to-move and drag-to-resize (the runtime turns the committed drag into source edits),
/// and translates pointer positions into design coordinates, so pick, drop and selection
/// always agree with the child's layout.
///
/// Viewport model: the design (pixelWidth x pixelHeight logical units) is shown at
/// eff = the fitted scale in Fit mode, otherwise the absolute zoom (1.0 = 100%), as in the WPF
/// and Forms canvases. Its on-screen (viewport-local) origin is
/// (originX + panX, originY + panY) where origin is the centered-fit offset, pan is
/// the user pan PLUS <see cref="CanvasMargin"/> (the empty-canvas margin is folded into
/// the pan by <see cref="CurrentViewport"/> so every conversion through the shared
/// viewport automatically accounts for it - <see cref="ApplyViewport"/> then places the
/// frame through that same viewport, and nothing adds the margin a second time).
/// The ScrollViewer adds scroll offsets on top; the design rect is placed
/// at (originX + panX + scrollX, ...) inside the scroll content.
/// </summary>
public class DesignSurface : DesignerCanvas
{
	public const double MinZoom = 0.1;
	public const double MaxZoom = 16.0;
	const double DragThreshold = 4;
	/// <summary>Height of the title bar drawn above a window's content, matching Visual Studio's
	/// design surface so a window reads the same here as it does there.</summary>
	public const double WindowCaptionHeight = 22;
	const double WindowBorderThickness = 1;
	/// <summary>Empty-canvas margin around the design bitmap, so the design surface never
	/// touches the scroll-viewport edge (the dotted EdgePattern reads as "outside the design"
	/// and leaves room for edge-drag to resize the page).</summary>
	public const double CanvasMargin = 32;

	/// <summary>Whether the chrome around the design root is drawn as a window. False for a bare
	/// content element, which then sits flush with the canvas edge the way a control rather than a
	/// window should.</summary>
	public bool RootIsWindow { get; private set; }

	/// <summary>The title shown in the window frame, normally the root element's name or type.</summary>
	public string WindowTitle { get; private set; } = "";

	/// <summary>Records whether the design root is a top-level window, from
	/// DesignerSessionState.RootIsWindow. A framework whose decorations live outside the element tree
	/// (a GTK window's title bar is drawn by the window manager, not by a widget) reports the frame
	/// size but cannot put the chrome into the pixels, so the canvas draws it instead - which is what
	/// makes a window read as a window on every framework, not only on those whose toolkit happens to
	/// paint its own decorations.</summary>
	public void SetRootWindow(bool isWindow, string title)
	{
		var newTitle = title ?? "";
		if (RootIsWindow == isWindow && WindowTitle == newTitle)
			return;
		RootIsWindow = isWindow;
		WindowTitle = newTitle;
		ApplyViewport();
	}

	static readonly Color SelectionColor = Color.FromRgb(0x00, 0x78, 0xD4);
	static readonly double[] ZoomPresets = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 4.0 };
	static readonly string[] ZoomLabels = { "Fit", "25%", "50%", "75%", "100%", "125%", "150%", "200%", "400%" };
	static readonly string[] SizePresetLabels = { "Auto", "Phone 390x844", "Tablet 768x1024", "Desktop 1280x720" };
	static readonly string[] HandleNames = { "nw", "n", "ne", "e", "se", "s", "sw", "w" };

	readonly DesignFramePresenter framePresenter = new(Stretch.Fill, snapsToDevicePixels: true);
	readonly Canvas overlay = new() {
		IsHitTestVisible = false,
		IsEnabled = false
	};
	readonly GridlineOverlay gridlineOverlay = new();
	readonly Border frameBackdrop = new() { IsHitTestVisible = false };
	// Drawn under the frame when the design root is a top-level window. The host cannot include the
	// chrome in its pixels - a GTK window's title bar is drawn by the window manager and is not part
	// of the widget tree at all - so the canvas draws it from RootIsWindow instead, which is what
	// makes a window look like a window on every framework rather than only those whose toolkit
	// paints its own decorations.
	readonly Border windowFrame = new() { IsHitTestVisible = false };
	readonly TextBlock windowCaption = new() {
		IsHitTestVisible = false,
		TextAlignment = TextAlignment.Center,
		VerticalAlignment = VerticalAlignment.Top
	};
	readonly Canvas viewportCanvas = new();
	readonly Canvas contentCanvas = new();
	// Marquee (rubber-band) selection, drawn in content coordinates so it scrolls with the design.
	readonly System.Windows.Shapes.Rectangle marqueeBox = new() {
		Stroke = Brushes.DodgerBlue,
		StrokeThickness = 1,
		Fill = new SolidColorBrush(Color.FromArgb(35, 30, 144, 255)),
		StrokeDashArray = new DoubleCollection { 3, 2 },
		IsHitTestVisible = false,
		Visibility = Visibility.Collapsed
	};
	bool marqueeActive;
	Point marqueeStartContent;
	readonly ScrollViewer scroller = new() {
		HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
		VerticalScrollBarVisibility = ScrollBarVisibility.Auto
	};
	readonly TextBox textEditor = new() {
		Visibility = Visibility.Collapsed,
		BorderBrush = new SolidColorBrush(SelectionColor),
		BorderThickness = new Thickness(1),
		Padding = new Thickness(2),
		AcceptsReturn = false,
		FontSize = 14
	};
	readonly SelectionAdornerLayer adornerLayer = new(HandleNames, new SolidColorBrush(SelectionColor));
	int pixelWidth;
	int pixelHeight;
	// Viewport state, the shared canvas contract (WPF/Forms/MAUI): Fit is its own MODE, and
	// otherwise zoomScale is ABSOLUTE (1.0 = 100%, the VS default). It used to be a factor on top
	// of the fit scale ("100%" = 1/fitScale, captured once), which tied every zoom level to the
	// fit and so to the window size - and to the scrollbars (see CurrentViewport).
	bool fitMode;
	double zoomScale = 1.0;
	double panX;
	double panY;
	Rect designSelection = Rect.Empty;
	// The selected element's key (null: nothing selected; the empty string is a valid key - an
	// Id-keyed design root) and the text its outline is labelled with.
	string? selectionName;
	string? selectionLabel;
	bool spacePanning;
	bool middlePanning;
	Point lastPanPoint;
	bool syncingZoomCombo;
	// Drag state.
	bool dragPossible;
	bool dragActive;
	string dragHandle;
	Point dragStartSurface;
	Cursor previousCursor;
	// The selection state at drag start, restored if a drag is interrupted (LibreWPF
	// occasionally loses the mouse-up, which would otherwise leave the preview rect stuck).
	Rect dragRestoreRect = Rect.Empty;
	string? dragRestoreName;
	string? dragRestoreLabel;
	// Inline text-edit state.
	bool textEditing;
	Rect textEditRect;
	// Manual double-click detection (LibreWPF does not populate ClickCount).
	DateTime lastPressUtc;
	Point lastPressPosition;

	public DesignSurface()
	{
		Capabilities = DesignerCanvasCapabilities.All;
		foreach (var label in ZoomLabels)
			ZoomCombo.Items.Add(label);
		// Default to 100% rather than Fit, matching VS's design surface.
		ZoomCombo.SelectedIndex = 4;
		syncingZoomCombo = true;
		zoomComboSyncIndex = ZoomCombo.SelectedIndex;
		syncingZoomCombo = false;
		ZoomCombo.SelectionChanged += OnZoomSelectionChanged;
		foreach (var label in SizePresetLabels)
			DesignSizeCombo.Items.Add(label);
		DesignSizeCombo.SelectedIndex = 0;
		DesignSizeCombo.SelectionChanged += OnSizePresetSelected;
		FitRequested += (_, _) => FitView();
		ThemeRequested += OnThemeSelected;
		VisualStateRequested += (_, request) => DesignVisualStateRequested?.Invoke(this, request);
		GridRequested += (_, enabled) => SetGridlines(enabled);
		ShowNamesRequested += (_, enabled) => adornerLayer.ShowNameLabel = enabled;
		textEditor.KeyDown += OnTextEditorKeyDown;
		textEditor.LostKeyboardFocus += OnTextEditorLostFocus;
		viewportCanvas.Children.Add(frameBackdrop);
		viewportCanvas.Children.Add(windowFrame);
		viewportCanvas.Children.Add(windowCaption);
		viewportCanvas.Children.Add(framePresenter.Visual);
		viewportCanvas.Children.Add(gridlineOverlay.Visual);
		viewportCanvas.Children.Add(overlay);
		viewportCanvas.Children.Add(textEditor);
		overlay.Children.Add(adornerLayer.Visual);
		contentCanvas.Children.Add(viewportCanvas);
		contentCanvas.Children.Add(ExtensionLayer);
		contentCanvas.Children.Add(marqueeBox);
		scroller.Content = contentCanvas;
		ContentHost.Content = scroller;
		ContextMenu = BuildContextMenu();
		Focusable = true;
		KeyDown += OnKeyDown;
		KeyUp += OnKeyUp;
		PreviewMouseWheel += OnPreviewMouseWheel;
		// Preview (tunneling) events: under LibreWPF the ScrollViewer swallows the bubbling
		// mouse events, so picking and panning are handled before any child does.
		PreviewMouseDown += OnMouseDown;
		PreviewMouseMove += OnMouseMove;
		PreviewMouseUp += OnMouseUp;
		PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
		scroller.ScrollChanged += OnScrollChanged;
		scroller.SizeChanged += (_, _) => ApplyViewport();

		ApplyDesignTheme(IsDarkTheme);
	}

	int zoomComboSyncIndex;

	/// <summary>Raised with a design-unit delta when the user nudges the selection with arrow keys.</summary>
	public event EventHandler<(double DX, double DY)> NudgeRequested;

	/// <summary>
	/// Surface geometry for the resize-drag integration tests: the rendered design bitmap
	/// bounds, the current selection outline bounds, the selected element's bounds and the
	/// (bottom-right) resize handle position, all in screen coordinates. The selection outline
	/// must coincide with the rendered element and the handle sit at its bottom-right corner
	/// before and after a resize drag - the smoke probe for the shared-canvas invariant.
	/// </summary>
	public DesignerSurfaceGeometry SurfaceGeometry()
	{
		var frame = DesignerSurfaceGeometryProbe.ScreenBoundsOf(framePresenter.Visual);
		Rect selection = default;
		if (!designSelection.IsEmpty)
		{
			// DesignToSurface yields content coordinates; the scroll offset is subtracted so
			// the selection maps to the viewport's own screen rectangle.
			selection = DesignerSurfaceGeometryProbe.DesignRectToScreen(CurrentViewport(), designSelection, scroller,
				new System.Windows.Vector(scroller.HorizontalOffset, scroller.VerticalOffset));
		}
		var handle = new Point(selection.X + selection.Width, selection.Y + selection.Height);
		return new DesignerSurfaceGeometry(frame, selection, handle, selection);
	}

	/// <summary>Raised when the user presses Ctrl+Z (undo: true) or Ctrl+Y / Ctrl+Shift+Z (undo: false).</summary>
	public event EventHandler<bool> UndoRedoRequested;

	/// <summary>Raised with the selected element when a context-menu command is invoked.</summary>
	public event EventHandler<(string Command, string Name)> ContextCommandRequested;

	ContextMenu BuildContextMenu()
	{
		var menu = new ContextMenu();
		AddContextItem(menu, "Copy", "copy");
		AddContextItem(menu, "Paste", "paste");
		menu.Items.Add(new Separator());
		AddContextItem(menu, "Delete", "delete");
		menu.Items.Add(new Separator());
		AddContextItem(menu, "Bring to Front", "bring-to-front");
		AddContextItem(menu, "Send to Back", "send-to-back");
		menu.Items.Add(new Separator());
		AddContextItem(menu, "Wrap in Grid", "wrap-grid");
		AddContextItem(menu, "Wrap in StackPanel", "wrap-stackpanel");
		menu.Opened += OnContextMenuOpened;
		return menu;
	}

	MenuItem AddContextItem(ContextMenu menu, string header, string command)
	{
		var item = new MenuItem { Header = header, Tag = command };
		item.Click += (_, _) => ContextCommandRequested?.Invoke(this, ((string)item.Tag, selectionName));
		menu.Items.Add(item);
		return item;
	}

	/// <summary>Paste never needs a selection; everything else does.</summary>
	void OnContextMenuOpened(object sender, RoutedEventArgs e)
	{
		var menu = (ContextMenu)sender;
		foreach (var item in menu.Items)
		{
			if (item is MenuItem menuItem)
				menuItem.IsEnabled = (string)menuItem.Tag == "paste" || selectionName != null;
		}
	}

	/// <summary>
	/// Raised with a surface-local point when the design surface is clicked (Ctrl held
	/// when multi-selecting). The runtime converts it to design coordinates (see
	/// <see cref="ToDesignPoint"/>) - passing the design point here would double-convert.
	/// </summary>
	public event EventHandler<(Vector2 Point, bool Ctrl)> SurfacePointerPressed;

	/// <summary>Resolves the named element under a surface-local point (pick semantics).</summary>
	public Func<Vector2, string> ElementResolver { get; set; }

	/// <summary>Raised when a drag on the design surface begins, with the element and handle.</summary>
	public event EventHandler<(string Name, string Handle)> SurfaceElementDragStarted;

	/// <summary>Raised during a drag with the cumulative surface delta from the drag start.</summary>
	public event EventHandler<(double DX, double DY)> SurfaceElementDragDelta;

	/// <summary>Raised when a drag ends (with the final cumulative surface delta).</summary>
	public event EventHandler<(double DX, double DY)> SurfaceElementDragCommitted;

	/// <summary>Raised on a double-click, with the surface-local point.</summary>
	public event EventHandler<Vector2> SurfaceElementDoubleClicked;

	/// <summary>Raised when the inline text editor commits (Enter or focus loss).</summary>
	public event EventHandler<string> TextEditCommitted;

	/// <summary>True while the inline text editor is active.</summary>
	public bool IsTextEditing => textEditing;

	/// <summary>Raised when the user toggles the Light/Dark theme, with "Light" or "Dark".</summary>
	public event EventHandler<string> DesignThemeRequested;

	/// <summary>Raised when the user picks a VisualState to preview; State is null for "(none)".</summary>
	public event EventHandler<(string Group, string? State)> DesignVisualStateRequested;

	/// <summary>Repopulates the toolbar's per-group state combos from the latest snapshot. The
	/// panel hides itself when the document declares no groups, which is the common case.</summary>
	public void SetVisualStateGroups(IReadOnlyList<DesignerVisualStateGroup> groups)
		=> SetVisualStateGroups((groups ?? Array.Empty<DesignerVisualStateGroup>())
			.Select(g => (g.Name, (IReadOnlyList<string>)g.States, g.CurrentState))
			.ToList());

	/// <summary>Syncs the theme toggle with the runtime (e.g. after the design reloads).
	/// The button reads as the theme a click would switch TO, so its text and chrome
	/// always describe the next action.</summary>
	public void SetTheme(string theme)
	{
		var dark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
		DesignTheme = theme;
		ApplyDesignTheme(dark);
	}

	/// <summary>The current design-theme name.</summary>
	public string ThemeState => DesignTheme;

	void OnThemeSelected(object sender, string theme)
	{
		DesignThemeRequested?.Invoke(this, theme);
	}

	/// <summary>Shows the inline text editor over the given design rect, pre-filled with
	/// <paramref name="text"/>. Committed via <see cref="TextEditCommitted"/> on Enter or
	/// focus loss; Escape cancels.
	/// </summary>
	public void BeginTextEdit(double x, double y, double width, double height, string text)
	{
		textEditRect = new Rect(x, y, width, height);
		textEditing = true;
		textEditor.Text = text ?? "";
		textEditor.Visibility = Visibility.Visible;
		LayoutTextEditor();
		textEditor.Focus();
		textEditor.SelectAll();
	}

	public bool HasRender { get; private set; }

	/// <summary>Current viewport state: the effective zoom (1.0 = 100%, whatever the mode) and pan in surface DIPs.</summary>
	public (double Zoom, double PanX, double PanY) Viewport => (EffectiveScale(), panX, panY);

	/// <summary>Whether the view is in Fit mode (the design follows the pane size).</summary>
	public bool IsFitMode => fitMode;

	/// <summary>The effective design-to-surface scale (fit scale x zoom).</summary>
	public double ViewportScale => EffectiveScale();

	/// <summary>The current selection rect in design coordinates (may be empty).</summary>
	public (double X, double Y, double Width, double Height) CurrentSelection =>
		designSelection.IsEmpty
			? (0, 0, 0, 0)
			: (designSelection.X, designSelection.Y, designSelection.Width, designSelection.Height);

	/// <summary>Sets an ABSOLUTE zoom (1.0 = 100%) and pan; leaves Fit mode.</summary>
	public void SetViewport(double zoom, double panX, double panY)
	{
		fitMode = false;
		zoomScale = Math.Clamp(zoom, MinZoom, MaxZoom);
		this.panX = panX;
		this.panY = panY;
		ApplyViewport();
	}

	/// <summary>Resets to the fitted, centered view.</summary>
	public void FitView()
	{
		fitMode = true;
		panX = 0;
		panY = 0;
		ApplyViewport();
		scroller.ScrollToHorizontalOffset(0);
		scroller.ScrollToVerticalOffset(0);
	}

	/// <summary>
	/// A layer for a backend's own design-time chrome (WPF's detached-ContextMenu tray and "Type
	/// Here" hotspots, WinForms' ToolStrip editors, ...). It scrolls with the design; place children
	/// in content coordinates (<see cref="DesignToContentPoint"/>) and re-place them on
	/// <see cref="ViewportChanged"/>. It sits above the selection adorners and takes input.
	/// </summary>
	public Canvas ExtensionLayer { get; } = new();

	/// <summary>Raised after every viewport change (zoom, fit, pan, new frame, resize), once the
	/// frame and adorners are laid out.</summary>
	public event EventHandler? ViewportChanged;

	/// <summary>Design-space point to <see cref="ExtensionLayer"/> (content) coordinates.</summary>
	public Point DesignToContentPoint(double x, double y)
	{
		var (sx, sy) = CurrentViewport().DesignToSurface(x, y);
		return new Point(sx, sy);
	}

	/// <summary>
	/// Offered every left press on the design (design-unit point) before the canvas interprets it;
	/// returning true consumes it. For framework chrome painted INTO the frame that is not an element
	/// of its own - a WinForms TabControl's tab headers.
	/// </summary>
	public Func<Point, bool>? DesignPressInterceptor { get; set; }

	/// <summary>Whether the selection shows (and drags by) resize handles. Off for a framework
	/// whose layout owns every element's bounds (GTK): a drag there can only move an element.</summary>
	public bool ResizeHandlesEnabled
	{
		get => adornerLayer.ShowHandles;
		set { adornerLayer.ShowHandles = value; LayoutSelection(); }
	}

	/// <summary>Whether Ctrl+Z / Ctrl+Y are handled here (<see cref="UndoRedoRequested"/>). A
	/// designer whose undo is an IDE command turns this off so the keys reach the shell.</summary>
	public bool HandlesUndoRedoKeys { get; set; } = true;

	/// <summary>The selection outline's color; null restores the canvas default (a designer shows
	/// a locked component in its own color).</summary>
	public Brush? SelectionStroke
	{
		get => selectionStroke;
		set => adornerLayer.SelectionStroke = (selectionStroke = value) ?? new SolidColorBrush(SelectionColor);
	}
	Brush? selectionStroke;

	/// <summary>Whether dragging from empty canvas (or from the design root) draws a selection
	/// marquee. On by default.</summary>
	public bool MarqueeEnabled { get; set; } = true;

	/// <summary>A marquee drag finished: the rectangle it covered, in design units.</summary>
	public event EventHandler<Rect>? MarqueeSelectionCommitted;

	/// <summary>Replaces the context menu's commands ("-" as the header adds a separator). The
	/// canvas raises <see cref="ContextCommandRequested"/> with the chosen command; a backend lists
	/// only what it supports. "paste" is the one command enabled without a selection.</summary>
	public void SetContextCommands(IEnumerable<(string Header, string Command)> commands)
	{
		var menu = new ContextMenu();
		foreach (var (header, command) in commands)
		{
			if (header == "-")
				menu.Items.Add(new Separator());
			else
				AddContextItem(menu, header, command);
		}
		menu.Opened += OnContextMenuOpened;
		ContextMenu = menu;
	}

	/// <summary>Design-space point to surface-local DIPs, honoring the viewport.</summary>
	public Point DesignToSurfacePoint(double x, double y)
	{
		var (sx, sy) = CurrentViewport().DesignToSurface(x, y);
		return new Point(sx - scroller.HorizontalOffset, sy - scroller.VerticalOffset);
	}

	/// <summary>Diagnostic-only: reports the screen origin of every candidate anchor for
	/// element-to-screen-point translation, to measure which one actually lines up with
	/// <see cref="SurfaceGeometry"/>'s own (verified-correct) frame origin.</summary>
	public string DiagnoseScreenAnchors()
	{
		var thisOrigin = PointToScreen(new Point(0, 0));
		var scrollerOrigin = scroller.PointToScreen(new Point(0, 0));
		var frameOrigin = framePresenter.Visual is FrameworkElement fe ? fe.PointToScreen(new Point(0, 0)) : new Point(double.NaN, double.NaN);
		var viewport = CurrentViewport();
		return $"this=({thisOrigin.X},{thisOrigin.Y}) scroller=({scrollerOrigin.X},{scrollerOrigin.Y}) framePresenter=({frameOrigin.X},{frameOrigin.Y}) scrollOffset=({scroller.HorizontalOffset},{scroller.VerticalOffset}) scrollerViewport=({scroller.ViewportWidth},{scroller.ViewportHeight}) pixelSize=({pixelWidth},{pixelHeight}) fit={fitMode} zoomScale={zoomScale} basis=({FitBasisWidth},{FitBasisHeight}) pan=({panX},{panY}) scale={viewport.Scale} origin=({viewport.OriginX},{viewport.OriginY})";
	}

	/// <summary>A DESIGN-space point (the same space <c>QueryElementBounds</c>/<c>nodesByName</c>
	/// report element positions in - NOT surface-local pixels, despite this method's callers
	/// originally assuming that) to real screen coordinates, honoring the current viewport/scroll.
	/// Reuses exactly <see cref="DesignToSurfacePoint"/> + <c>scroller.PointToScreen</c>, the same
	/// pair <see cref="SurfaceGeometry"/> uses via <see cref="DesignerSurfaceGeometryProbe.DesignRectToScreen"/>
	/// - NOT <c>PointToScreen</c> on <c>this</c>, which sits above <c>scroller</c> by the shared
	/// toolbar's height. Found live: <c>od.winui-designer.query-element-screen-bounds</c> (which
	/// calls this) was reporting a point ~26px into the toolbar area for an element that visually
	/// sits well inside the canvas, so every synthetic click driven from its numbers landed short
	/// of the actual design surface and never registered at all.</summary>
	public Point SurfacePointToScreen(double x, double y)
	{
		var surfacePoint = DesignToSurfacePoint(x, y);
		return scroller.PointToScreen(surfacePoint);
	}

	public void SetRender(RenderResult render)
	{
		if (render == null)
			return;
		var dpi = Math.Max(1.0, render.Dpi);
		if (string.IsNullOrEmpty(render.Data))
		{
			// A PNG frame (the WinForms hosts send one when they cannot hand over raw pixels).
			if (string.IsNullOrEmpty(render.PngBase64))
				return;
			var png = new BitmapImage();
			using (var stream = new System.IO.MemoryStream(Convert.FromBase64String(render.PngBase64)))
			{
				png.BeginInit();
				png.CacheOption = BitmapCacheOption.OnLoad;
				png.StreamSource = stream;
				png.EndInit();
			}
			png.Freeze();
			framePresenter.SetSource(png);
			pixelWidth = (int)Math.Round(render.Width / dpi);
			pixelHeight = (int)Math.Round(render.Height / dpi);
			HasRender = true;
			ApplyViewport();
			return;
		}
		var bytes = DesignerFrameCodec.DecodeBgra32(render);
		// The child sends raw BGRA32 (premultiplied) pixels from its RenderTargetBitmap - WPF's
		// BitmapImage/BitmapDecoder is a native WIC codec (wpfgfx_cor3) that does not exist under
		// LibreWPF on macOS, so present the pixels with the pure-managed BitmapSource.Create path.
		// The bitmap DPI carries the render scale so the Image keeps the logical design size
		// while showing dpi-scaled pixels.
		framePresenter.SetSource(BitmapSource.Create(render.Width, render.Height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null, bytes, render.Width * 4));
		pixelWidth = (int)Math.Round(render.Width / dpi);
		pixelHeight = (int)Math.Round(render.Height / dpi);
		HasRender = true;
		ApplyViewport();
	}

	/// <summary>Shows the selection outline for a design-space rectangle (logical units).</summary>
	/// <param name="label">The outline's caption, when it differs from the key (an Id-keyed
	/// designer labels by x:Name or type); defaults to <paramref name="name"/>.</param>
	public void ShowSelection(double x, double y, double width, double height, string? name, string? label = null)
	{
		designSelection = new Rect(x, y, Math.Max(0, width), Math.Max(0, height));
		selectionName = name;
		selectionLabel = label ?? name;
		LayoutSelection();
	}

	void LayoutSelection()
	{
		if (pixelWidth == 0 || pixelHeight == 0 || designSelection.IsEmpty)
		{
			adornerLayer.ClearSelection();
			return;
		}
		// The adorner layer reports handles only for a non-empty label.
		adornerLayer.ShowSelection(designSelection, CanvasLocalViewport(), string.IsNullOrEmpty(selectionLabel) ? " " : selectionLabel);
	}

	readonly Dictionary<string, Rectangle> secondaryBoxes = new(StringComparer.Ordinal);
	readonly Dictionary<string, (double X, double Y, double W, double H)> secondaryBounds = new(StringComparer.Ordinal);

	// Drag-snap alignment guides: a vertical or horizontal line shown while an element is
	// being dragged near another element's edge/centre.
	readonly List<Rectangle> snapGuides = new();

	/// <summary>Shows snap alignment guides at the given design positions
	/// ((isVertical, position) pairs); empty clears them.</summary>
	public void SetSnapGuides(IReadOnlyList<(bool IsVertical, double Position)> guides)
	{
		foreach (var guide in snapGuides)
			overlay.Children.Remove(guide);
		snapGuides.Clear();
		foreach (var (isVertical, position) in guides)
		{
			var guide = new Rectangle {
				Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x5D, 0x2A)),
				IsHitTestVisible = false,
				Tag = (isVertical, position)
			};
			snapGuides.Add(guide);
			overlay.Children.Add(guide);
			LayoutSnapGuideFromStored(guide);
		}
	}

	void LayoutSnapGuideFromStored(Rectangle guide)
	{
		if (guide.Tag is not (bool isVertical, double position))
			return;
		var scale = EffectiveScale();
		// Guides live inside viewportCanvas, whose own position carries origin+pan+margin -
		// map design units at the bare scale only (see CanvasLocalViewport).
		var width = viewportCanvas.Width;
		var height = viewportCanvas.Height;
		if (isVertical)
		{
			Canvas.SetLeft(guide, position * scale);
			Canvas.SetTop(guide, 0);
			guide.Width = 1;
			guide.Height = height;
		}
		else
		{
			Canvas.SetLeft(guide, 0);
			Canvas.SetTop(guide, position * scale);
			guide.Width = width;
			guide.Height = 1;
		}
	}

	// Grid row/column divider guides: shown while a Grid is selected, draggable to resize.
	readonly List<Rectangle> rowGuides = new();
	readonly List<Rectangle> colGuides = new();
	(double X, double Y, double W, double H) gridGuideRect;
	double[] gridRowOffsets = Array.Empty<double>();
	double[] gridColOffsets = Array.Empty<double>();
	bool gridGuidesHitTest;
	int gridGuideIndex = -1;
	bool gridGuideIsRow;
	string gridGuideName;
	Vector2 gridGuideStart;

	/// <summary>Shows the row/column divider guides over the given Grid (design-space rect
	/// plus divider offsets); pass empty offsets to hide.</summary>
	public void SetGridGuides(string name, double x, double y, double width, double height, double[] rowOffsets, double[] colOffsets)
	{
		foreach (var guide in rowGuides)
			overlay.Children.Remove(guide);
		foreach (var guide in colGuides)
			overlay.Children.Remove(guide);
		rowGuides.Clear();
		colGuides.Clear();
		gridGuideName = name;
		gridGuideRect = (x, y, width, height);
		gridRowOffsets = rowOffsets ?? Array.Empty<double>();
		gridColOffsets = colOffsets ?? Array.Empty<double>();
		if (gridRowOffsets.Length == 0 && gridColOffsets.Length == 0)
			return;
		for (var i = 1; i < gridRowOffsets.Length - 1; i++)
		{
			var guide = new Rectangle { Width = 1, Fill = new SolidColorBrush(Color.FromArgb(0xC0, 0x00, 0x78, 0xD4)), IsHitTestVisible = false };
			rowGuides.Add(guide);
			overlay.Children.Add(guide);
		}
		for (var i = 1; i < gridColOffsets.Length - 1; i++)
		{
			var guide = new Rectangle { Height = 1, Fill = new SolidColorBrush(Color.FromArgb(0xC0, 0x00, 0x78, 0xD4)), IsHitTestVisible = false };
			colGuides.Add(guide);
			overlay.Children.Add(guide);
		}
		LayoutGridGuides();
	}

	void LayoutGridGuides()
	{
		var scale = EffectiveScale();
		var (gx, gy, gw, gh) = gridGuideRect;
		// Guides live inside viewportCanvas - bare scale mapping only (see CanvasLocalViewport).
		var left = gx * scale;
		var top = gy * scale;
		for (var i = 0; i < rowGuides.Count; i++)
		{
			Canvas.SetLeft(rowGuides[i], left);
			Canvas.SetTop(rowGuides[i], top + gridRowOffsets[i + 1] * scale);
			rowGuides[i].Height = gw * scale;
		}
		for (var i = 0; i < colGuides.Count; i++)
		{
			Canvas.SetLeft(colGuides[i], left + gridColOffsets[i + 1] * scale);
			Canvas.SetTop(colGuides[i], top);
			colGuides[i].Width = gh * scale;
		}
	}

	/// <summary>Raised when a Grid row/column divider drag commits: (grid name, isRow, index, design position).</summary>
	public event EventHandler<(string Name, bool IsRow, int Index, double Position)> GridGuideDragCommitted;

	/// <summary>The design-space divider under a point, as (isRow, index), or null.</summary>
	(bool IsRow, int Index)? GridGuideAt(Vector2 designPoint)
	{
		if (gridRowOffsets.Length == 0 && gridColOffsets.Length == 0)
			return null;
		var (gx, gy, _, _) = gridGuideRect;
		var tolerance = 4 / EffectiveScale();
		for (var i = 1; i < gridColOffsets.Length - 1; i++)
		{
			if (Math.Abs(designPoint.X - (gx + gridColOffsets[i])) <= tolerance)
				return (false, i - 1);
		}
		for (var i = 1; i < gridRowOffsets.Length - 1; i++)
		{
			if (Math.Abs(designPoint.Y - (gy + gridRowOffsets[i])) <= tolerance)
				return (true, i - 1);
		}
		return null;
	}

	bool gridGuideDragActive;
	Vector2 gridGuideDragStart;

	void BeginGridGuideDrag(bool isRow, int index, Vector2 start)
	{
		gridGuideIsRow = isRow;
		gridGuideIndex = index;
		gridGuideDragActive = true;
		gridGuideDragStart = start;
	}

	void UpdateGridGuideDrag(Vector2 current)
	{
		if (!gridGuideDragActive || gridGuideName == null)
			return;
		var (gx, gy, gw, gh) = gridGuideRect;
		var offsets = gridGuideIsRow ? gridRowOffsets : gridColOffsets;
		if (gridGuideIndex + 1 >= offsets.Length)
			return;
		var originalOffset = offsets[gridGuideIndex + 1];
		var scale = EffectiveScale();
		if (gridGuideIsRow)
		{
			var y = (gy + originalOffset + (current.Y - gridGuideDragStart.Y)) * scale;
			if (gridGuideIndex < rowGuides.Count)
			{
				Canvas.SetTop(rowGuides[gridGuideIndex], y);
				rowGuides[gridGuideIndex].Height = gw * scale;
			}
		}
		else
		{
			var x = (gx + originalOffset + (current.X - gridGuideDragStart.X)) * scale;
			if (gridGuideIndex < colGuides.Count)
			{
				Canvas.SetLeft(colGuides[gridGuideIndex], x);
				colGuides[gridGuideIndex].Width = gh * scale;
			}
		}
	}

	void EndGridGuideDrag(Vector2 end)
	{
		gridGuideDragActive = false;
		if (gridGuideName == null)
			return;
		var (gx, gy, _, _) = gridGuideRect;
		var offsets = gridGuideIsRow ? gridRowOffsets : gridColOffsets;
		if (gridGuideIndex + 1 < offsets.Length)
		{
			var originalOffset = offsets[gridGuideIndex + 1];
			var position = gridGuideIsRow
				? gy + originalOffset + (end.Y - gridGuideDragStart.Y)
				: gx + originalOffset + (end.X - gridGuideDragStart.X);
			GridGuideDragCommitted?.Invoke(this, (gridGuideName, gridGuideIsRow, gridGuideIndex, position));
		}
		gridGuideIndex = -1;
	}

	/// <summary>Shows light dashed outlines for the secondary (multi-selected) elements.</summary>
	public void SetSecondarySelection(IReadOnlyList<(string Name, double X, double Y, double Width, double Height)> elements)
	{
		foreach (var box in secondaryBoxes.Values)
			overlay.Children.Remove(box);
		secondaryBoxes.Clear();
		secondaryBounds.Clear();
		foreach (var (name, x, y, width, height) in elements)
		{
			var box = new Rectangle {
				Stroke = new SolidColorBrush(SelectionColor),
				StrokeThickness = 1,
				StrokeDashArray = new DoubleCollection { 2, 2 },
				IsHitTestVisible = false
			};
			secondaryBoxes[name] = box;
			secondaryBounds[name] = (x, y, width, height);
			overlay.Children.Add(box);
			LayoutSecondaryBox(name);
		}
	}

	void LayoutSecondaryBox(string name)
	{
		if (!secondaryBoxes.TryGetValue(name, out var box) || !secondaryBounds.TryGetValue(name, out var b))
			return;
		var scale = EffectiveScale();
		// Boxes live inside viewportCanvas - bare scale mapping only (see CanvasLocalViewport).
		Canvas.SetLeft(box, b.X * scale);
		Canvas.SetTop(box, b.Y * scale);
		box.Width = b.W * scale;
		box.Height = b.H * scale;
	}

	readonly Dictionary<string, Border> tabOrderBadges = new(StringComparer.Ordinal);
	readonly Dictionary<string, (double X, double Y)> tabOrderBounds = new(StringComparer.Ordinal);

	/// <summary>Shows a small numbered badge near every element that reports a TabIndex -
	/// matching RemoteFormsDesignerControl's own tab-order badge overlay. Empty clears them
	/// (toggling the view off, or a tree rebuild while it's off).</summary>
	public void SetTabOrderBadges(IReadOnlyList<(string Name, double X, double Y, string TabIndex)> badges)
	{
		foreach (var badge in tabOrderBadges.Values)
			overlay.Children.Remove(badge);
		tabOrderBadges.Clear();
		tabOrderBounds.Clear();
		foreach (var (name, x, y, tabIndex) in badges)
		{
			var badge = new Border {
				Background = Brushes.RoyalBlue,
				CornerRadius = new CornerRadius(8),
				Padding = new Thickness(5, 1, 5, 1),
				IsHitTestVisible = false,
				Child = new TextBlock {
					Text = tabIndex, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11
				}
			};
			tabOrderBadges[name] = badge;
			tabOrderBounds[name] = (x, y);
			overlay.Children.Add(badge);
			LayoutTabOrderBadge(name);
		}
	}

	void LayoutTabOrderBadge(string name)
	{
		if (!tabOrderBadges.TryGetValue(name, out var badge) || !tabOrderBounds.TryGetValue(name, out var b))
			return;
		var scale = EffectiveScale();
		Canvas.SetLeft(badge, b.X * scale - 5);
		Canvas.SetTop(badge, b.Y * scale - 8);
	}

	public void ClearSelection()
	{
		designSelection = Rect.Empty;
		selectionName = null;
		selectionLabel = null;
		adornerLayer.ClearSelection();
	}

	/// <summary>The selected element's key, or null when nothing is selected.</summary>
	public string? SelectedKey => selectionName;

	/// <summary>
	/// Paints an opaque page behind the rendered frame, exactly its size. For a framework whose
	/// root has no background of its own (a WPF Window/UserControl), where the canvas pattern would
	/// otherwise show through the page. Null (the default) leaves the frame as rendered.
	/// </summary>
	public Brush? FrameBackground
	{
		get => frameBackdrop.Background;
		set => frameBackdrop.Background = value;
	}

	/// <summary>The scroll metrics of the design area, in surface pixels (tests, DevFlow).</summary>
	public DesignSurfaceScrollStatus ScrollStatus => new(
		scroller.ExtentWidth, scroller.ExtentHeight,
		scroller.ViewportWidth, scroller.ViewportHeight,
		scroller.HorizontalOffset, scroller.VerticalOffset,
		scroller.ScrollableWidth, scroller.ScrollableHeight);

	/// <summary>
	/// Translates a control-local WPF point (every caller passes <c>e.GetPosition(this)</c>,
	/// i.e. relative to THIS control, which includes the toolbar row above <see cref="scroller"/>)
	/// into design coordinates through the viewport. Points outside the design area map outside
	/// its bounds; the child's hit-test then reports nothing for them, so clicks on empty
	/// surface space resolve to the root.
	/// </summary>
	public Vector2 ToDesignPoint(Point point)
	{
		if (pixelWidth == 0 || pixelHeight == 0 || scroller.ViewportWidth == 0)
			return new Vector2((float)point.X, (float)point.Y);
		// `point` is relative to `this` (the whole control, toolbar included); the formula
		// below only adds the SCROLL offset, which silently assumed `point` was already
		// relative to `scroller` - `this`'s own origin sits above `scroller`'s by the
		// toolbar's height, a FIXED offset this was missing entirely. That made every mouse
		// gesture (clicks, drags, resize handles) resolve to a design-space point shifted
		// down by the toolbar's height: confirmed live by comparing od.winui-designer.
		// surface-geometry's reported (correct) handle screen position against a real
		// synthetic click there - HandleAt() never recognized it as a handle at all, so a
		// resize-handle drag silently fell back to a plain element-drag at whatever the
		// wrong point resolved to (observed: resizing PrimaryButton's handle actually dragged
		// RootStack, its parent, because the mis-shifted point landed on it instead).
		// TranslatePoint gives the correct scroller-relative point regardless of how the
		// toolbar/scroller are laid out, rather than hardcoding its height.
		var scrollerPoint = TranslatePoint(point, scroller);
		var (dx, dy) = CurrentViewport().SurfaceToDesign(scrollerPoint.X + scroller.HorizontalOffset, scrollerPoint.Y + scroller.VerticalOffset);
		return new Vector2((float)dx, (float)dy);
	}

	/// <summary>The shared design-space-to-surface coordinate math (see
	/// <see cref="DesignViewport"/>), computed from this control's own viewport size and
	/// zoom/pan state. Every layout/hit-test method that used to inline this math now goes
	/// through here - same formulas, one place. <see cref="CanvasMargin"/> is folded into the
	/// pan so every conversion through this viewport automatically accounts for the frame's
	/// empty-canvas offset - the ONLY place the margin is applied, matching
	/// <see cref="ApplyViewport"/> which positions the frame through this same viewport.
	/// <para>
	/// Computed against the scroller's OWN size, never its ViewportWidth/Height: the viewport
	/// shrinks by a scrollbar whenever the content overflows, so fitting to it let the scale
	/// decide whether scrollbars show, which then decided the scale - a tall design oscillated
	/// between two scales (measured with a 400x700 MAUI page: viewport 187/170, scale 1.0/0.909).
	/// The WPF canvas computes from its host size for the same reason.
	/// </para></summary>
	DesignViewport CurrentViewport()
		=> fitMode
			? DesignViewport.Fit(pixelWidth, pixelHeight, FitBasisWidth, FitBasisHeight, 1.0, panX + CanvasMargin, panY + CanvasMargin)
			: DesignViewport.Zoom(pixelWidth, pixelHeight, FitBasisWidth, FitBasisHeight, zoomScale, panX + CanvasMargin, panY + CanvasMargin);

	double FitBasisWidth => scroller.ActualWidth > 0 ? scroller.ActualWidth : scroller.ViewportWidth;
	double FitBasisHeight => scroller.ActualHeight > 0 ? scroller.ActualHeight : scroller.ViewportHeight;

	double EffectiveScale() => CurrentViewport().Scale;

	/// <summary>The viewport for elements that live INSIDE <see cref="viewportCanvas"/> (the
	/// selection adorner layer, snap/grid guides): the frame's Canvas position already carries
	/// origin+pan+margin, so overlays must map design units at the bare scale - re-applying
	/// the origin/pan would double-shift them off the rendered bitmap.</summary>
	DesignViewport CanvasLocalViewport()
	{
		var viewport = CurrentViewport();
		return DesignViewport.Scaled(viewport.DesignWidth, viewport.DesignHeight, viewport.Scale);
	}


	/// <summary>True when the pointer is over the toolbar, where pick/pan must not fire.</summary>
	bool IsOverToolbar(Point position) => position.Y <= 0;

	/// <summary>
	/// True when the pressed element lies inside the design canvas. Presses elsewhere (the
	/// ScrollViewer's scrollbars, the empty content area around the design) must pass through
	/// untouched, or the scrollbars would never receive the mouse-down and their thumbs could
	/// not be dragged.
	/// </summary>
	bool IsDesignInteraction(MouseButtonEventArgs e)
	{
		var source = e.OriginalSource as DependencyObject;
		while (source != null)
		{
			if (ReferenceEquals(source, viewportCanvas))
				return true;
			source = VisualTreeHelper.GetParent(source);
		}
		return false;
	}

	void OnScrollChanged(object sender, ScrollChangedEventArgs e) => ApplyViewport();

	/// <summary>Places the window chrome around the frame the host produced, or removes it and pulls
	/// the content back flush when the root is not a window. Both directions run on every call: the
	/// top offsets are sticky on the visuals, so returning early without the reset would leave a
	/// document that stopped being a window permanently offset by one caption.</summary>
	void LayoutWindowFrame(double contentWidth, double contentHeight)
	{
		var captionHeight = RootIsWindow ? WindowCaptionHeight : 0;
		foreach (var visual in new UIElement[] { framePresenter.Visual, gridlineOverlay.Visual, overlay, textEditor })
			Canvas.SetTop(visual, captionHeight);
		windowFrame.Visibility = RootIsWindow ? Visibility.Visible : Visibility.Collapsed;
		windowCaption.Visibility = RootIsWindow ? Visibility.Visible : Visibility.Collapsed;
		if (!RootIsWindow)
			return;
		windowFrame.Width = contentWidth;
		windowFrame.Height = contentHeight + WindowCaptionHeight;
		windowFrame.BorderThickness = new Thickness(WindowBorderThickness);
		windowFrame.BorderBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70));
		windowFrame.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
		Canvas.SetLeft(windowFrame, 0);
		Canvas.SetTop(windowFrame, 0);
		windowCaption.Width = contentWidth;
		windowCaption.Height = WindowCaptionHeight - 1;
		windowCaption.Text = WindowTitle;
		windowCaption.SetResourceReference(TextBlock.ForegroundProperty, "MutedForeground");
		Canvas.SetLeft(windowCaption, 0);
		Canvas.SetTop(windowCaption, WindowBorderThickness);
	}

	void ApplyViewport()
	{
		if (pixelWidth == 0 || pixelHeight == 0 || scroller.ViewportWidth == 0 || scroller.ViewportHeight == 0)
			return;
		var viewport = CurrentViewport();
		var scale = viewport.Scale;
		var (baseX, baseY) = (Math.Max(0, viewport.OriginX), Math.Max(0, viewport.OriginY));
		// Content spans the design at the current zoom; at fit the design is centered inside
		// the viewport-sized content (no scroll range), when zoomed in it is top-left
		// anchored so the scroll range covers the whole design. The design rect is placed at
		// a FIXED content position - the ScrollViewer moves it on screen as the scroll
		// offsets change, which is what makes the scrollbars actually scroll the canvas.
		// A fixed margin keeps the design bitmap off the edges, surrounded by the dotted
		// empty-canvas EdgePattern (see DesignerCanvas).
		contentCanvas.Width = Math.Max(scroller.ViewportWidth, pixelWidth * scale + CanvasMargin * 2);
		contentCanvas.Height = Math.Max(scroller.ViewportHeight, pixelHeight * scale + CanvasMargin * 2);
		// viewport.PanX/PanY already have CanvasMargin folded in (see CurrentViewport) - reading
		// the raw panX/panY fields here instead (as a previous pass did) placed the rendered
		// frame CanvasMargin short of where every other conversion (ToDesignPoint,
		// SurfaceGeometry, CanvasLocalViewport) expected it, so the bitmap, selection outline
		// and click hit-testing all disagreed by a fixed 32px offset.
		Canvas.SetLeft(viewportCanvas, baseX + viewport.PanX);
		Canvas.SetTop(viewportCanvas, baseY + viewport.PanY);
		viewportCanvas.Width = pixelWidth * scale;
		viewportCanvas.Height = pixelHeight * scale;
		frameBackdrop.Width = viewportCanvas.Width;
		frameBackdrop.Height = viewportCanvas.Height;
		LayoutWindowFrame(pixelWidth * scale, pixelHeight * scale);
		// The image must fill the design-size canvas: without explicit size it renders at
		// the bitmap's natural DIP size (1 design unit = 1 DIP), which at fit scale is about
		// 2x too large and drifts from the selection outline.
		framePresenter.Resize(CurrentViewport());
		gridlineOverlay.Visual.Width = viewportCanvas.Width;
		gridlineOverlay.Visual.Height = viewportCanvas.Height;
		gridlineOverlay.Update(viewportCanvas.Width, viewportCanvas.Height, scale, showGridlines);
		LayoutSelection();
		foreach (var name in secondaryBounds.Keys)
			LayoutSecondaryBox(name);
		foreach (var name in tabOrderBounds.Keys)
			LayoutTabOrderBadge(name);
		LayoutGridGuides();
		foreach (var guide in snapGuides)
			LayoutSnapGuideFromStored(guide);
		LayoutTextEditor();
		UpdateZoomCombo();
		ViewportChanged?.Invoke(this, EventArgs.Empty);
	}

	bool showGridlines;

	/// <summary>Whether the design-space gridlines are currently visible.</summary>
	public bool Gridlines => showGridlines;

	/// <summary>Shows or hides the design-space gridlines overlay.</summary>
	public void SetGridlines(bool show)
	{
		showGridlines = show;
		gridlineOverlay.Update(viewportCanvas.Width, viewportCanvas.Height, EffectiveScale(), showGridlines);
	}

	/// <summary>The resize handle under a design-space point, or null - delegates to the
	/// shared adorner layer (same tolerance/center-third-is-move logic as before).</summary>
	string HandleAt(Vector2 designPoint)
		=> adornerLayer.HandleAt(new Point(designPoint.X, designPoint.Y), CanvasLocalViewport());

	/// <summary>Places the inline text editor for the stored design rect at the current zoom.</summary>
	void LayoutTextEditor()
	{
		if (!textEditing || pixelWidth == 0)
			return;
		var scale = EffectiveScale();
		Canvas.SetLeft(textEditor, textEditRect.X * scale);
		Canvas.SetTop(textEditor, textEditRect.Y * scale);
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

	/// <summary>Closes the inline editor; <paramref name="commit"/> raises <see cref="TextEditCommitted"/>.</summary>
	public void EndTextEdit(bool commit)
	{
		if (!textEditing)
			return;
		var text = textEditor.Text;
		textEditing = false;
		textEditor.Visibility = Visibility.Collapsed;
		if (commit)
			TextEditCommitted?.Invoke(this, text);
	}

	/// <summary>Keeps the zoom dropdown in sync with the viewport: "Fit" in Fit mode, otherwise the
	/// preset nearest the absolute zoom.</summary>
	void UpdateZoomCombo()
	{
		if (ZoomCombo.Items.Count == 0)
			return;
		syncingZoomCombo = true;
		if (fitMode)
		{
			ZoomCombo.SelectedIndex = 0; // Fit
		}
		else
		{
			var best = 0;
			for (var i = 1; i < ZoomPresets.Length; i++)
			{
				if (Math.Abs(ZoomPresets[i] - zoomScale) < Math.Abs(ZoomPresets[best] - zoomScale))
					best = i;
			}
			ZoomCombo.SelectedIndex = best + 1;
		}
		syncingZoomCombo = false;
	}

	void OnZoomSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (syncingZoomCombo || ZoomCombo.SelectedIndex < 0)
			return;
		if (ZoomCombo.SelectedIndex == 0)
		{
			FitView();
			return;
		}
		fitMode = false;
		zoomScale = Math.Clamp(ZoomPresets[ZoomCombo.SelectedIndex - 1], MinZoom, MaxZoom);
		ApplyViewport();
	}

	/// <summary>Raised when the user picks a canvas size preset; arg is "phone"/"tablet"/"desktop"/"reset".</summary>
	public event EventHandler<string> SizePresetRequested;

	bool syncingSizeCombo;

	void OnSizePresetSelected(object sender, SelectionChangedEventArgs e)
	{
		if (syncingSizeCombo || DesignSizeCombo.SelectedIndex <= 0)
			return;
		SizePresetRequested?.Invoke(this, SizePresetKeys[DesignSizeCombo.SelectedIndex - 1]);
	}

	/// <summary>Syncs the size combo with the runtime (e.g. after a preset is applied).</summary>
	public void SetSizePreset(string preset)
	{
		syncingSizeCombo = true;
		var index = Array.IndexOf(SizePresetKeys, preset);
		DesignSizeCombo.SelectedIndex = index < 0 ? 0 : index + 1;
		syncingSizeCombo = false;
	}

	static readonly string[] SizePresetKeys = { "phone", "tablet", "desktop" };

	#region Viewport interactions

	void OnKeyDown(object sender, KeyEventArgs e)
	{
		// Keys typed into a text box on the canvas (the inline editor, a backend's own editors in
		// the extension layer) are text, not canvas commands: Space must not start panning.
		if (textEditing || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase)
			return;
		if (e.Key == Key.Space)
		{
			spacePanning = true;
			Cursor = Cursors.SizeAll;
			e.Handled = true;
			return;
		}
		// Undo/redo shortcuts (Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z) forward to the shell's history.
		if (HandlesUndoRedoKeys && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
		{
			if (e.Key == Key.Z && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
			{
				UndoRedoRequested?.Invoke(this, true);
				e.Handled = true;
				return;
			}
			if (e.Key == Key.Y || (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
			{
				UndoRedoRequested?.Invoke(this, false);
				e.Handled = true;
				return;
			}
		}
		// Delete removes the selection, through the same command the context menu raises.
		if (e.Key == Key.Delete && selectionName != null)
		{
			ContextCommandRequested?.Invoke(this, ("delete", selectionName));
			e.Handled = true;
			return;
		}
		// Arrow keys nudge the selection in design units (Ctrl = 10px step), raising the
		// request only when something is selected.
		if (selectionName != null)
		{
			var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10.0 : 1.0;
			var (dx, dy) = e.Key switch
			{
				Key.Left => (-step, 0.0),
				Key.Right => (step, 0.0),
				Key.Up => (0.0, -step),
				Key.Down => (0.0, step),
				_ => (0.0, 0.0)
			};
			if (dx != 0 || dy != 0)
			{
				NudgeRequested?.Invoke(this, (dx, dy));
				e.Handled = true;
			}
		}
	}

	void OnKeyUp(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Space)
		{
			spacePanning = false;
			Cursor = Cursors.Arrow;
			e.Handled = true;
		}
	}

	void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (HasRender && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
		{
			ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.1 : 1 / 1.1);
			e.Handled = true;
		}
		// Without Ctrl the ScrollViewer scrolls natively.
	}

	void OnMouseDown(object sender, MouseButtonEventArgs e)
	{
		Focus();
		if (textEditing || IsOverToolbar(e.GetPosition(this)) || !IsDesignInteraction(e))
			return;
		if (e.ChangedButton == MouseButton.Middle)
		{
			middlePanning = true;
			lastPanPoint = e.GetPosition(this);
			CaptureMouse();
			Cursor = Cursors.SizeAll;
			e.Handled = true;
		}
	}

	void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (!HasRender || textEditing)
			return;
		var position = e.GetPosition(this);
		if (IsOverToolbar(position) || !IsDesignInteraction(e))
			return;
		if (spacePanning || middlePanning)
		{
			lastPanPoint = position;
			middlePanning = true;
			CaptureMouse();
			Cursor = Cursors.SizeAll;
			e.Handled = true;
			return;
		}
		var now = DateTime.UtcNow;
		var isDoubleClick = now - lastPressUtc < TimeSpan.FromMilliseconds(500)
			&& Math.Abs(position.X - lastPressPosition.X) < 8
			&& Math.Abs(position.Y - lastPressPosition.Y) < 8;
		lastPressUtc = now;
		lastPressPosition = position;
		if (isDoubleClick)
		{
			SurfaceElementDoubleClicked?.Invoke(this, new Vector2((float)position.X, (float)position.Y));
			e.Handled = true;
			return;
		}
		// A press may become a click (pick on release) or a drag (move/resize). No mouse
		// capture: under LibreWPF, capturing stops subsequent pointer events from being
		// delivered, and the Preview handlers keep receiving them without capture.
		CancelStuckDrag();
		var designPoint = ToDesignPoint(position);
		if (DesignPressInterceptor?.Invoke(new Point(designPoint.X, designPoint.Y)) == true)
		{
			dragPossible = false;
			e.Handled = true;
			return;
		}
		if (GridGuideAt(designPoint) is { } guide)
		{
			// Pressing on a Grid divider starts a row/column resize, not an element drag.
			BeginGridGuideDrag(guide.IsRow, guide.Index, designPoint);
			dragPossible = false;
			dragActive = false;
			e.Handled = true;
			return;
		}
		dragStartSurface = position;
		dragHandle = HandleAt(designPoint) ?? "";
		dragPossible = true;
		dragActive = false;
		e.Handled = true;
	}

	/// <summary>Ends a drag whose mouse-up was never delivered, restoring the selection outline.</summary>
	void CancelStuckDrag()
	{
		if (!dragActive)
			return;
		dragActive = false;
		dragPossible = false;
		if (dragRestoreRect.IsEmpty)
			ClearSelection();
		else
			ShowSelection(dragRestoreRect.X, dragRestoreRect.Y, dragRestoreRect.Width, dragRestoreRect.Height, dragRestoreName, dragRestoreLabel);
	}

	void OnMouseMove(object sender, MouseEventArgs e)
	{
		if (textEditing)
			return;
		var position = e.GetPosition(this);
		if (middlePanning)
		{
			panX += position.X - lastPanPoint.X;
			panY += position.Y - lastPanPoint.Y;
			lastPanPoint = position;
			ApplyViewport();
			return;
		}
		if (gridGuideDragActive)
		{
			UpdateGridGuideDrag(ToDesignPoint(position));
			Cursor = gridGuideIsRow ? Cursors.SizeNS : Cursors.SizeWE;
			e.Handled = true;
			return;
		}
		if (marqueeActive)
		{
			var now = e.GetPosition(contentCanvas);
			Canvas.SetLeft(marqueeBox, Math.Min(now.X, marqueeStartContent.X));
			Canvas.SetTop(marqueeBox, Math.Min(now.Y, marqueeStartContent.Y));
			marqueeBox.Width = Math.Abs(now.X - marqueeStartContent.X);
			marqueeBox.Height = Math.Abs(now.Y - marqueeStartContent.Y);
			e.Handled = true;
			return;
		}
		if (dragPossible && !dragActive)
		{
			if (Math.Abs(position.X - dragStartSurface.X) < DragThreshold && Math.Abs(position.Y - dragStartSurface.Y) < DragThreshold)
				return;
			BeginDrag();
			if (!dragActive)
				return;
		}
		if (dragActive)
		{
			var dx = position.X - dragStartSurface.X;
			var dy = position.Y - dragStartSurface.Y;
			SurfaceElementDragDelta?.Invoke(this, (dx, dy));
			e.Handled = true;
			return;
		}
		UpdateCursor(position);
	}

	void OnMouseUp(object sender, MouseButtonEventArgs e)
	{
		if (middlePanning && e.ChangedButton is MouseButton.Middle or MouseButton.Left)
		{
			middlePanning = false;
			ReleaseMouseCapture();
			Cursor = spacePanning ? Cursors.SizeAll : Cursors.Arrow;
			e.Handled = true;
			return;
		}
		if (gridGuideDragActive)
		{
			EndGridGuideDrag(ToDesignPoint(e.GetPosition(this)));
			Cursor = Cursors.Arrow;
			e.Handled = true;
			return;
		}
		if (marqueeActive)
		{
			marqueeActive = false;
			marqueeBox.Visibility = Visibility.Collapsed;
			dragPossible = false;
			if (IsMouseCaptured)
				ReleaseMouseCapture();
			var end = e.GetPosition(contentCanvas);
			var viewportNow = CurrentViewport();
			var (x0, y0) = viewportNow.SurfaceToDesign(marqueeStartContent.X, marqueeStartContent.Y);
			var (x1, y1) = viewportNow.SurfaceToDesign(end.X, end.Y);
			MarqueeSelectionCommitted?.Invoke(this, new Rect(new Point(x0, y0), new Point(x1, y1)));
			e.Handled = true;
			return;
		}
		if (!dragPossible)
			return;
		var position = e.GetPosition(this);
		if (IsMouseCaptured)
			ReleaseMouseCapture();
		if (dragActive)
		{
			SurfaceElementDragCommitted?.Invoke(this, (position.X - dragStartSurface.X, position.Y - dragStartSurface.Y));
		}
		else
		{
			// A plain click: pick at the press point, carrying whether Ctrl was held.
			var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
			SurfacePointerPressed?.Invoke(this, (new Vector2((float)dragStartSurface.X, (float)dragStartSurface.Y), ctrl));
		}
		dragPossible = false;
		dragActive = false;
		Cursor = Cursors.Arrow;
		e.Handled = true;
	}

	void BeginDrag()
	{
		dragActive = true;
		// Capture only once the gesture is really a drag (not at the press): a drag may end
		// outside this control - past the design, over the status bar - and its mouse-up must
		// still come here, or the gesture never commits and the preview outline stays behind.
		CaptureMouse();
		dragRestoreRect = designSelection;
		dragRestoreName = selectionName;
		dragRestoreLabel = selectionLabel;
		string name = null;
		if (string.IsNullOrEmpty(dragHandle))
		{
			// Resolve what is under the press point; if it is the selected element, drag it.
			name = ElementResolver?.Invoke(new Vector2((float)dragStartSurface.X, (float)dragStartSurface.Y));
			if (name == null)
			{
				dragActive = false;
				if (MarqueeEnabled)
				{
					// Nothing draggable under the press (empty canvas, or the design root): select by
					// rubber band instead, as every desktop designer does.
					marqueeActive = true;
					marqueeStartContent = TranslatePoint(dragStartSurface, contentCanvas);
					marqueeBox.Width = marqueeBox.Height = 0;
					marqueeBox.Visibility = Visibility.Visible;
				}
				else
				{
					dragPossible = false;
					ReleaseMouseCapture();
				}
				return;
			}
		}
		else
		{
			name = selectionName;
		}
		previousCursor = Cursor;
		Cursor = Cursors.SizeAll;
		SurfaceElementDragStarted?.Invoke(this, (name, dragHandle));
	}

	void UpdateCursor(Point position)
	{
		if (!HasRender || dragPossible || gridGuideDragActive)
			return;
		var designPoint = ToDesignPoint(position);
		if (GridGuideAt(designPoint) is { } guide)
		{
			var guideCursor = guide.IsRow ? Cursors.SizeNS : Cursors.SizeWE;
			if (Cursor != guideCursor)
				Cursor = guideCursor;
			return;
		}
		var handle = HandleAt(designPoint);
		var cursor = handle switch
		{
			"n" or "s" => Cursors.SizeNS,
			"e" or "w" => Cursors.SizeWE,
			"nw" or "se" => Cursors.SizeNWSE,
			"ne" or "sw" => Cursors.SizeNESW,
			_ => Cursors.Arrow
		};
		if (Cursor != cursor)
			Cursor = cursor;
	}

	void ZoomAt(Point cursorSurface, double factor)
	{
		// Same folded-pan requirement as ApplyViewport: viewport.PanX/PanY (not the raw
		// panX/panY fields) already carry CanvasMargin, matching CurrentViewport()'s own
		// DesignToSurface/SurfaceToDesign math - otherwise the "point under the cursor"
		// this solves for drifts by CanvasMargin's worth of scale-dependent error on every zoom.
		var viewport = CurrentViewport();
		var (baseX, baseY) = (Math.Max(0, viewport.OriginX), Math.Max(0, viewport.OriginY));
		var designX = (cursorSurface.X - baseX - viewport.PanX + scroller.HorizontalOffset) / viewport.Scale;
		var designY = (cursorSurface.Y - baseY - viewport.PanY + scroller.VerticalOffset) / viewport.Scale;
		// Keep the scroll-content position stable while the scale changes, so the point
		// under the cursor does not jump and the scrollbar thumb stays put.
		var contentX = baseX + viewport.PanX + designX * viewport.Scale;
		var contentY = baseY + viewport.PanY + designY * viewport.Scale;
		// Zooming from Fit continues from the fitted scale, as an absolute zoom.
		zoomScale = Math.Clamp(viewport.Scale * factor, MinZoom, MaxZoom);
		fitMode = false;
		var viewport2 = CurrentViewport();
		var (baseX2, baseY2) = (Math.Max(0, viewport2.OriginX), Math.Max(0, viewport2.OriginY));
		// Solve for the folded pan, then strip CanvasMargin back out for the raw field CurrentViewport() re-adds.
		panX = contentX - baseX2 - designX * viewport2.Scale - CanvasMargin;
		panY = contentY - baseY2 - designY * viewport2.Scale - CanvasMargin;
		ApplyViewport();
	}

	#endregion
}

/// <summary>Scroll metrics of the design area, in surface pixels.</summary>
public sealed record DesignSurfaceScrollStatus(
	double ExtentWidth,
	double ExtentHeight,
	double ViewportWidth,
	double ViewportHeight,
	double HorizontalOffset,
	double VerticalOffset,
	double ScrollableWidth,
	double ScrollableHeight);
