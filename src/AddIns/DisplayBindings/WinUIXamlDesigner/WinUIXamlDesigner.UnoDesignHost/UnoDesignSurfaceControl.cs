using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>
/// WPF-side design surface for the out-of-process Uno host: shows the bitmap the child
/// rendered (dpi-scaled pixels at the host display's scale, displayed at logical size)
/// inside a ScrollViewer with a zoom toolbar, draws a selection outline with resize
/// handles over the picked element, supports drag-to-move and drag-to-resize (the runtime
/// turns the committed drag into source edits), and translates pointer positions into
/// design coordinates, so pick, drop and selection always agree with the child's layout.
///
/// Viewport model: the design (pixelWidth x pixelHeight logical units) is shown at
/// eff = fitScale x zoomFactor. Its on-screen (viewport-local) origin is
/// (originX + panX, originY + panY) where origin is the centered-fit offset and pan is
/// the user pan. The ScrollViewer adds scroll offsets on top; the design rect is placed
/// at (originX + panX + scrollX, ...) inside the scroll content.
/// </summary>
public sealed class UnoDesignSurfaceControl : ContentControl
{
	public const double MinZoom = 0.1;
	public const double MaxZoom = 16.0;
	const double DragThreshold = 4;
	const double HandleSize = 7;

	static readonly Color SelectionColor = Color.FromRgb(0x00, 0x78, 0xD4);
	static readonly double[] ZoomPresets = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 4.0 };
	static readonly string[] ZoomLabels = { "Fit", "25%", "50%", "75%", "100%", "125%", "150%", "200%", "400%" };
	static readonly string[] SizePresetLabels = { "Auto", "Phone 390x844", "Tablet 768x1024", "Desktop 1280x720" };
	static readonly string[] HandleNames = { "nw", "n", "ne", "e", "se", "s", "sw", "w" };

	// Toolbar chrome follows the design theme, so the surface's own controls stay legible
	// (and visually consistent) whether the design is Light or Dark.
	static readonly Brush ToolbarDarkBackground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
	static readonly Brush ToolbarDarkForeground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
	static readonly Brush ToolbarDarkButtonBackground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
	static readonly Brush ToolbarCheckedBackground = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));

	// The chrome around the design bitmap uses a dotted pattern - same idea as VS's design
	// surface - so empty space is clearly distinguishable from the design's own background
	// (a plain colour frame would be mistaken for the page's background at a glance).
	static readonly Brush CanvasLightPattern = CreateCanvasPattern(Color.FromRgb(0xE8, 0xE8, 0xE8), Color.FromRgb(0xC8, 0xC8, 0xC8));
	static readonly Brush CanvasDarkPattern = CreateCanvasPattern(Color.FromRgb(0x1E, 0x1E, 0x1E), Color.FromRgb(0x2E, 0x2E, 0x2E));

	static DrawingBrush CreateCanvasPattern(Color baseColor, Color dotColor)
	{
		var group = new DrawingGroup();
		group.Children.Add(new GeometryDrawing(new SolidColorBrush(baseColor), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
		group.Children.Add(new GeometryDrawing(new SolidColorBrush(dotColor), null, new EllipseGeometry(new Point(4, 4), 1, 1)));
		return new DrawingBrush(group) {
			TileMode = TileMode.Tile,
			Viewport = new Rect(0, 0, 8, 8),
			ViewportUnits = BrushMappingMode.Absolute
		};
	}

	readonly Image image = new() {
		Stretch = Stretch.Fill,
		SnapsToDevicePixels = true
	};
	readonly Canvas overlay = new() {
		IsHitTestVisible = false,
		IsEnabled = false
	};
	readonly Canvas viewportCanvas = new();
	readonly Canvas contentCanvas = new();
	readonly ScrollViewer scroller = new() {
		HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
		VerticalScrollBarVisibility = ScrollBarVisibility.Auto
	};
	readonly ComboBox zoomCombo = new() { Width = 84, Margin = new Thickness(4, 2, 4, 2) };
	readonly ComboBox sizeCombo = new() { Width = 92, Margin = new Thickness(0, 2, 4, 2), ToolTip = "Design canvas size preset (for pages without an explicit size)" };
	readonly Button fitButton = new() {
		Content = "Fit",
		Margin = new Thickness(0, 2, 4, 2),
		Padding = new Thickness(8, 1, 8, 1)
	};
	readonly ToggleButton themeButton = new() {
		Content = "Dark",
		Margin = new Thickness(0, 2, 4, 2),
		Padding = new Thickness(8, 1, 8, 1),
		ToolTip = "Switch the design surface between Light and Dark theme"
	};
	readonly ToggleButton gridButton = new() {
		Content = "Grid",
		Margin = new Thickness(0, 2, 4, 2),
		Padding = new Thickness(8, 1, 8, 1),
		ToolTip = "Show design-space gridlines on the surface"
	};
	readonly TextBox textEditor = new() {
		Visibility = Visibility.Collapsed,
		BorderBrush = new SolidColorBrush(SelectionColor),
		BorderThickness = new Thickness(1),
		Padding = new Thickness(2),
		AcceptsReturn = false,
		FontSize = 14
	};
	readonly StackPanel toolbar = new() {
		Orientation = Orientation.Horizontal
	};
	readonly DockPanel root = new();
	readonly Rectangle selectionBox = new() {
		Stroke = new SolidColorBrush(SelectionColor),
		StrokeThickness = 1.5,
		StrokeDashArray = new DoubleCollection { 4, 2 },
		IsHitTestVisible = false,
		Visibility = Visibility.Collapsed
	};
	readonly TextBlock selectionLabel = new() {
		Background = new SolidColorBrush(SelectionColor),
		Foreground = Brushes.White,
		FontSize = 10,
		Padding = new Thickness(3, 1, 3, 1),
		IsHitTestVisible = false,
		Visibility = Visibility.Collapsed
	};
	readonly Dictionary<string, Rectangle> handles = new(StringComparer.Ordinal);
	int pixelWidth;
	int pixelHeight;
	// Viewport state: zoomFactor 1.0 = the design fits the surface; panX/panY is the user
	// pan on top of the centered fit.
	double zoomFactor = 1.0;
	double panX;
	double panY;
	Rect designSelection;
	string selectionName;
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
	Rect dragRestoreRect;
	string dragRestoreName;
	// Inline text-edit state.
	bool textEditing;
	Rect textEditRect;
	// Manual double-click detection (LibreWPF does not populate ClickCount).
	DateTime lastPressUtc;
	Point lastPressPosition;

	public UnoDesignSurfaceControl()
	{
		Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
		foreach (var label in ZoomLabels)
			zoomCombo.Items.Add(label);
		zoomCombo.SelectedIndex = 0;
		zoomCombo.SelectionChanged += OnZoomSelectionChanged;
		foreach (var label in SizePresetLabels)
			sizeCombo.Items.Add(label);
		sizeCombo.SelectedIndex = 0;
		sizeCombo.SelectionChanged += OnSizePresetSelected;
		fitButton.Click += (_, _) => FitView();
		themeButton.Click += OnThemeToggle;
		textEditor.KeyDown += OnTextEditorKeyDown;
		textEditor.LostKeyboardFocus += OnTextEditorLostFocus;
		toolbar.Children.Add(zoomCombo);
		toolbar.Children.Add(sizeCombo);
		toolbar.Children.Add(fitButton);
		toolbar.Children.Add(gridButton);
		toolbar.Children.Add(themeButton);
		gridButton.Click += (_, _) => SetGridlines(gridButton.IsChecked == true);
		ApplyToolbarTheme(dark: false);
		DockPanel.SetDock(toolbar, Dock.Top);
		foreach (var name in HandleNames)
		{
			var handle = new Rectangle {
				Width = HandleSize,
				Height = HandleSize,
				Fill = Brushes.White,
				Stroke = new SolidColorBrush(SelectionColor),
				StrokeThickness = 1,
				IsHitTestVisible = false,
				Visibility = Visibility.Collapsed
			};
			handles[name] = handle;
			overlay.Children.Add(handle);
		}
		viewportCanvas.Children.Add(image);
		viewportCanvas.Children.Add(overlay);
		viewportCanvas.Children.Add(textEditor);
		overlay.Children.Add(selectionBox);
		overlay.Children.Add(selectionLabel);
		contentCanvas.Children.Add(viewportCanvas);
		scroller.Content = contentCanvas;
		ContextMenu = BuildContextMenu();
		root.Children.Add(toolbar);
		root.Children.Add(scroller);
		Content = root;
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
	}

	/// <summary>Raised with a design-unit delta when the user nudges the selection with arrow keys.</summary>
	public event EventHandler<(double DX, double DY)> NudgeRequested;

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
				menuItem.IsEnabled = (string)menuItem.Tag == "paste" || !string.IsNullOrEmpty(selectionName);
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
	public event EventHandler<string> ThemeRequested;

	/// <summary>Syncs the theme toggle with the runtime (e.g. after the design reloads).
	/// The button reads as the theme a click would switch TO, so its text and chrome
	/// always describe the next action.</summary>
	public void SetTheme(string theme)
	{
		var dark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
		syncingTheme = true;
		themeButton.IsChecked = dark;
		syncingTheme = false;
		themeButton.Content = dark ? "Light" : "Dark";
		themeButton.ToolTip = dark
			? "Switch the design surface to Light theme"
			: "Switch the design surface to Dark theme";
		ApplyToolbarTheme(dark);
	}

	/// <summary>Switches the toolbar chrome (backgrounds, button and text colors) between the
	/// Light and Dark design themes, and highlights the theme button while Dark is active.</summary>
	void ApplyToolbarTheme(bool dark)
	{
		toolbar.Background = dark ? ToolbarDarkBackground : null;
		// The chrome around the design bitmap uses a dotted pattern so empty space reads
		// as "outside the design", not as the design's own background.
		Background = dark ? CanvasDarkPattern : CanvasLightPattern;
		scroller.Background = dark ? CanvasDarkPattern : CanvasLightPattern;
		var fg = dark ? ToolbarDarkForeground : SystemColors.ControlTextBrush;
		zoomCombo.Foreground = fg;
		fitButton.Foreground = fg;
		fitButton.Background = dark ? ToolbarDarkButtonBackground : null;
		themeButton.Foreground = fg;
		themeButton.Background = dark
			? (themeButton.IsChecked == true ? ToolbarCheckedBackground : ToolbarDarkButtonBackground)
			: null;
	}

	/// <summary>The current toggle state: "Light" or "Dark".</summary>
	public string ThemeState => themeButton.IsChecked == true ? "Dark" : "Light";

	bool syncingTheme;

	void OnThemeToggle(object sender, RoutedEventArgs e)
	{
		if (syncingTheme)
			return;
		ThemeRequested?.Invoke(this, themeButton.IsChecked == true ? "Dark" : "Light");
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

	/// <summary>Current viewport state (zoom 1.0 = fit; pan in surface DIPs).</summary>
	public (double Zoom, double PanX, double PanY) Viewport => (zoomFactor, panX, panY);

	/// <summary>The effective design-to-surface scale (fit scale x zoom).</summary>
	public double ViewportScale => EffectiveScale();

	/// <summary>The current selection rect in design coordinates (may be empty).</summary>
	public (double X, double Y, double Width, double Height) CurrentSelection =>
		designSelection.IsEmpty
			? (0, 0, 0, 0)
			: (designSelection.X, designSelection.Y, designSelection.Width, designSelection.Height);

	public void SetViewport(double zoom, double panX, double panY)
	{
		zoomFactor = Math.Clamp(zoom, MinZoom, MaxZoom);
		this.panX = panX;
		this.panY = panY;
		ApplyViewport();
	}

	/// <summary>Resets to the fitted, centered view.</summary>
	public void FitView()
	{
		zoomFactor = 1.0;
		panX = 0;
		panY = 0;
		ApplyViewport();
		scroller.ScrollToHorizontalOffset(0);
		scroller.ScrollToVerticalOffset(0);
	}

	/// <summary>Design-space point to surface-local DIPs, honoring the viewport.</summary>
	public Point DesignToSurfacePoint(double x, double y)
	{
		var (originX, originY, scale) = ViewportParams();
		var (baseX, baseY) = (Math.Max(0, originX), Math.Max(0, originY));
		return new Point(
			baseX + panX - scroller.HorizontalOffset + x * scale,
			toolbar.ActualHeight + baseY + panY - scroller.VerticalOffset + y * scale);
	}

	public void SetRender(RenderResult render)
	{
		if (string.IsNullOrEmpty(render?.Data))
			return;
		var bytes = RenderCodec.Decode(render.Data);
		if (render.Width <= 0 || render.Height <= 0)
			return;
		var dpi = Math.Max(1.0, render.Dpi);
		// The child sends raw BGRA32 (premultiplied) pixels from its RenderTargetBitmap - WPF's
		// BitmapImage/BitmapDecoder is a native WIC codec (wpfgfx_cor3) that does not exist under
		// LibreWPF on macOS, so present the pixels with the pure-managed BitmapSource.Create path.
		// The bitmap DPI carries the render scale so the Image keeps the logical design size
		// while showing dpi-scaled pixels.
		image.Source = BitmapSource.Create(render.Width, render.Height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32, null, bytes, render.Width * 4);
		pixelWidth = (int)Math.Round(render.Width / dpi);
		pixelHeight = (int)Math.Round(render.Height / dpi);
		HasRender = true;
		ApplyViewport();
	}

	/// <summary>Shows the selection outline for a design-space rectangle (logical units).</summary>
	public void ShowSelection(double x, double y, double width, double height, string name)
	{
		designSelection = new Rect(x, y, width, height);
		selectionName = name ?? "";
		LayoutSelection();
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
		var (originX, originY, scale) = ViewportParams();
		var width = Math.Max(scroller.ViewportWidth, pixelWidth * scale);
		var height = Math.Max(scroller.ViewportHeight, pixelHeight * scale);
		if (isVertical)
		{
			Canvas.SetLeft(guide, originX + position * scale + panX);
			Canvas.SetTop(guide, 0);
			guide.Width = 1;
			guide.Height = height;
		}
		else
		{
			Canvas.SetLeft(guide, 0);
			Canvas.SetTop(guide, originY + position * scale + panY);
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
		var (originX, originY, scale) = ViewportParams();
		var (gx, gy, gw, gh) = gridGuideRect;
		var left = originX + gx * scale + panX;
		var top = originY + gy * scale + panY;
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
		var (originX, originY, scale) = ViewportParams();
		var offsets = gridGuideIsRow ? gridRowOffsets : gridColOffsets;
		if (gridGuideIndex + 1 >= offsets.Length)
			return;
		var originalOffset = offsets[gridGuideIndex + 1];
		if (gridGuideIsRow)
		{
			var y = originY + (gy + originalOffset + (current.Y - gridGuideDragStart.Y)) * scale + panY;
			if (gridGuideIndex < rowGuides.Count)
			{
				Canvas.SetTop(rowGuides[gridGuideIndex], y);
				rowGuides[gridGuideIndex].Height = gw * scale;
			}
		}
		else
		{
			var x = originX + (gx + originalOffset + (current.X - gridGuideDragStart.X)) * scale + panX;
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
		var (originX, originY, scale) = ViewportParams();
		Canvas.SetLeft(box, originX + b.X * scale + panX);
		Canvas.SetTop(box, originY + b.Y * scale + panY);
		box.Width = b.W * scale;
		box.Height = b.H * scale;
	}

	public void ClearSelection()
	{
		selectionBox.Visibility = Visibility.Collapsed;
		selectionLabel.Visibility = Visibility.Collapsed;
		foreach (var handle in handles.Values)
			handle.Visibility = Visibility.Collapsed;
	}

	/// <summary>
	/// Translates a control-local WPF point into design coordinates through the viewport.
	/// Points outside the design area map outside its bounds; the child's hit-test then
	/// reports nothing for them, so clicks on empty surface space resolve to the root.
	/// </summary>
	public Vector2 ToDesignPoint(Point point)
	{
		if (pixelWidth == 0 || pixelHeight == 0 || scroller.ViewportWidth == 0)
			return new Vector2((float)point.X, (float)point.Y);
		var (originX, originY, scale) = ViewportParams();
		var viewportY = point.Y - toolbar.ActualHeight;
		var (baseX, baseY) = (Math.Max(0, originX), Math.Max(0, originY));
		return new Vector2(
			(float)((point.X - baseX - panX + scroller.HorizontalOffset) / scale),
			(float)((viewportY - baseY - panY + scroller.VerticalOffset) / scale));
	}

	double EffectiveScale()
	{
		if (pixelWidth == 0 || pixelHeight == 0 || scroller.ViewportWidth == 0 || scroller.ViewportHeight == 0)
			return 1.0;
		return Math.Min(scroller.ViewportWidth / pixelWidth, scroller.ViewportHeight / pixelHeight) * zoomFactor;
	}

	(double OriginX, double OriginY, double Scale) ViewportParams()
	{
		var scale = EffectiveScale();
		return ((scroller.ViewportWidth - pixelWidth * scale) / 2,
			(scroller.ViewportHeight - pixelHeight * scale) / 2, scale);
	}

	/// <summary>True when the pointer is over the toolbar, where pick/pan must not fire.</summary>
	bool IsOverToolbar(Point position) => position.Y <= toolbar.ActualHeight;

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

	void ApplyViewport()
	{
		if (pixelWidth == 0 || pixelHeight == 0 || scroller.ViewportWidth == 0 || scroller.ViewportHeight == 0)
			return;
		var (originX, originY, scale) = ViewportParams();
		var (baseX, baseY) = (Math.Max(0, originX), Math.Max(0, originY));
		// Content spans the design at the current zoom; at fit the design is centered inside
		// the viewport-sized content (no scroll range), when zoomed in it is top-left
		// anchored so the scroll range covers the whole design. The design rect is placed at
		// a FIXED content position - the ScrollViewer moves it on screen as the scroll
		// offsets change, which is what makes the scrollbars actually scroll the canvas.
		contentCanvas.Width = Math.Max(scroller.ViewportWidth, pixelWidth * scale);
		contentCanvas.Height = Math.Max(scroller.ViewportHeight, pixelHeight * scale);
		Canvas.SetLeft(viewportCanvas, baseX + panX);
		Canvas.SetTop(viewportCanvas, baseY + panY);
		viewportCanvas.Width = pixelWidth * scale;
		viewportCanvas.Height = pixelHeight * scale;
		// The image must fill the design-size canvas: without explicit size it renders at
		// the bitmap's natural DIP size (1 design unit = 1 DIP), which at fit scale is about
		// 2x too large and drifts from the selection outline.
		image.Width = viewportCanvas.Width;
		image.Height = viewportCanvas.Height;
		UpdateGridBrush(scale);
		LayoutSelection();
		foreach (var name in secondaryBounds.Keys)
			LayoutSecondaryBox(name);
		LayoutGridGuides();
		foreach (var guide in snapGuides)
			LayoutSnapGuideFromStored(guide);
		LayoutTextEditor();
		UpdateZoomCombo();
	}

	/// <summary>Design-space gridline spacing in design units (shown when gridlines are on).</summary>
	const double GridCellSize = 20;

	readonly DrawingBrush gridBrush = CreateGridBrush();

	bool showGridlines;

	/// <summary>Whether the design-space gridlines are currently visible.</summary>
	public bool Gridlines => showGridlines;

	/// <summary>Shows or hides the design-space gridlines overlay.</summary>
	public void SetGridlines(bool show)
	{
		showGridlines = show;
		overlay.Background = show ? gridBrush : null;
		UpdateGridBrush(EffectiveScale());
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

	void UpdateGridBrush(double scale)
	{
		if (showGridlines)
			gridBrush.Viewport = new Rect(0, 0, GridCellSize * scale, GridCellSize * scale);
	}

	/// <summary>Places the outline, its name label and the resize handles, in design units
	/// scaled to the viewport.</summary>
	void LayoutSelection()
	{
		if (pixelWidth == 0 || pixelHeight == 0 || designSelection.IsEmpty)
		{
			ClearSelection();
			return;
		}
		var scale = EffectiveScale();
		var x = designSelection.X * scale;
		var y = designSelection.Y * scale;
		var w = designSelection.Width * scale;
		var h = designSelection.Height * scale;

		Canvas.SetLeft(selectionBox, x);
		Canvas.SetTop(selectionBox, y);
		selectionBox.Width = w;
		selectionBox.Height = h;
		selectionBox.Visibility = Visibility.Visible;

		Canvas.SetLeft(selectionLabel, x);
		Canvas.SetTop(selectionLabel, Math.Max(0, y - 17));
		selectionLabel.Text = selectionName;
		selectionLabel.Visibility = string.IsNullOrEmpty(selectionName) ? Visibility.Collapsed : Visibility.Visible;

		foreach (var (name, (hx, hy)) in HandlePositions())
		{
			var handle = handles[name];
			Canvas.SetLeft(handle, hx * scale - HandleSize / 2);
			Canvas.SetTop(handle, hy * scale - HandleSize / 2);
			handle.Visibility = Visibility.Visible;
		}
	}

	/// <summary>The eight resize-handle anchor points in design coordinates.</summary>
	IEnumerable<(string Name, (double X, double Y))> HandlePositions()
	{
		var (x, y) = (designSelection.X, designSelection.Y);
		var (w, h) = (designSelection.Width, designSelection.Height);
		var (cx, cy) = (x + w / 2, y + h / 2);
		yield return ("nw", (x, y));
		yield return ("n", (cx, y));
		yield return ("ne", (x + w, y));
		yield return ("e", (x + w, cy));
		yield return ("se", (x + w, y + h));
		yield return ("s", (cx, y + h));
		yield return ("sw", (x, y + h));
		yield return ("w", (x, cy));
	}

	/// <summary>The resize handle under a design-space point, or null.</summary>
	string HandleAt(Vector2 designPoint)
	{
		if (designSelection.IsEmpty || string.IsNullOrEmpty(selectionName))
			return null;
		var scale = EffectiveScale();
		var tolerance = (HandleSize / 2 + 2) / scale;
		// Presses in the central third of the element are always a move: on small elements
		// the resize anchors sit right on top of the centre, and treating such presses as
		// resize starts makes a plain drag resize (or crash on tiny elements).
		var (x, y) = (designSelection.X, designSelection.Y);
		var (w, h) = (designSelection.Width, designSelection.Height);
		var (cx, cy) = (x + w / 2, y + h / 2);
		if (Math.Abs(designPoint.X - cx) < w / 3 && Math.Abs(designPoint.Y - cy) < h / 3)
			return null;
		foreach (var (name, (hx, hy)) in HandlePositions())
		{
			if (Math.Abs(designPoint.X - hx) <= tolerance && Math.Abs(designPoint.Y - hy) <= tolerance)
				return name;
		}
		return null;
	}

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

	void EndTextEdit(bool commit)
	{
		if (!textEditing)
			return;
		var text = textEditor.Text;
		textEditing = false;
		textEditor.Visibility = Visibility.Collapsed;
		if (commit)
			TextEditCommitted?.Invoke(this, text);
	}

	/// <summary>Keeps the zoom dropdown in sync with the current zoom factor.</summary>
	void UpdateZoomCombo()
	{
		if (zoomCombo.Items.Count == 0)
			return;
		syncingZoomCombo = true;
		var effective = EffectiveScale();
		if (Math.Abs(zoomFactor - 1.0) < 0.02)
		{
			zoomCombo.SelectedIndex = 0; // Fit
		}
		else
		{
			var best = -1;
			var bestDistance = double.MaxValue;
			for (var i = 0; i < ZoomPresets.Length; i++)
			{
				var distance = Math.Abs(ZoomPresets[i] - effective);
				if (distance < bestDistance)
				{
					bestDistance = distance;
					best = i;
				}
			}
			zoomCombo.SelectedIndex = best + 1;
		}
		syncingZoomCombo = false;
	}

	void OnZoomSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (syncingZoomCombo || zoomCombo.SelectedIndex <= 0)
		{
			if (!syncingZoomCombo && zoomCombo.SelectedIndex == 0)
				FitView();
			return;
		}
		var fitScale = EffectiveScale() / zoomFactor;
		var preset = ZoomPresets[zoomCombo.SelectedIndex - 1];
		zoomFactor = Math.Clamp(preset / fitScale, MinZoom, MaxZoom);
		ApplyViewport();
	}

	/// <summary>Raised when the user picks a canvas size preset; arg is "phone"/"tablet"/"desktop"/"reset".</summary>
	public event EventHandler<string> SizePresetRequested;

	bool syncingSizeCombo;

	void OnSizePresetSelected(object sender, SelectionChangedEventArgs e)
	{
		if (syncingSizeCombo || sizeCombo.SelectedIndex <= 0)
			return;
		SizePresetRequested?.Invoke(this, SizePresetKeys[sizeCombo.SelectedIndex - 1]);
	}

	/// <summary>Syncs the size combo with the runtime (e.g. after a preset is applied).</summary>
	public void SetSizePreset(string preset)
	{
		syncingSizeCombo = true;
		var index = Array.IndexOf(SizePresetKeys, preset);
		sizeCombo.SelectedIndex = index < 0 ? 0 : index + 1;
		syncingSizeCombo = false;
	}

	static readonly string[] SizePresetKeys = { "phone", "tablet", "desktop" };

	#region Viewport interactions

	void OnKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Space)
		{
			spacePanning = true;
			Cursor = Cursors.SizeAll;
			e.Handled = true;
			return;
		}
		// Undo/redo shortcuts (Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z) forward to the shell's history.
		if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
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
		// Arrow keys nudge the selection in design units (Ctrl = 10px step), raising the
		// request only when something is selected.
		if (!string.IsNullOrEmpty(selectionName))
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
		ShowSelection(dragRestoreRect.X, dragRestoreRect.Y, dragRestoreRect.Width, dragRestoreRect.Height, dragRestoreName);
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
		if (!dragPossible)
			return;
		var position = e.GetPosition(this);
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
		dragRestoreRect = designSelection;
		dragRestoreName = selectionName;
		string name = null;
		if (string.IsNullOrEmpty(dragHandle))
		{
			// Resolve what is under the press point; if it is the selected element, drag it.
			name = ElementResolver?.Invoke(new Vector2((float)dragStartSurface.X, (float)dragStartSurface.Y));
			if (name == null)
			{
				dragActive = false;
				dragPossible = false;
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
		var (originX, originY, scale) = ViewportParams();
		var viewportY = cursorSurface.Y - toolbar.ActualHeight;
		var (baseX, baseY) = (Math.Max(0, originX), Math.Max(0, originY));
		var designX = (cursorSurface.X - baseX - panX + scroller.HorizontalOffset) / scale;
		var designY = (viewportY - baseY - panY + scroller.VerticalOffset) / scale;
		// Keep the scroll-content position stable while the scale changes, so the point
		// under the cursor does not jump and the scrollbar thumb stays put.
		var contentX = baseX + panX + designX * scale;
		var contentY = baseY + panY + designY * scale;
		zoomFactor = Math.Clamp(zoomFactor * factor, MinZoom, MaxZoom);
		var (originX2, originY2, scale2) = ViewportParams();
		var (baseX2, baseY2) = (Math.Max(0, originX2), Math.Max(0, originY2));
		panX = contentX - baseX2 - designX * scale2;
		panY = contentY - baseY2 - designY * scale2;
		ApplyViewport();
	}

	#endregion
}
