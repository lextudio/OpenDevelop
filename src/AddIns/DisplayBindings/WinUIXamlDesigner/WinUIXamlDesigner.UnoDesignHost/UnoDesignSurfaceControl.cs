using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
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
	static readonly string[] HandleNames = { "nw", "n", "ne", "e", "se", "s", "sw", "w" };

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
	readonly Button fitButton = new() {
		Content = "Fit",
		Margin = new Thickness(0, 2, 4, 2),
		Padding = new Thickness(8, 1, 8, 1)
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
		fitButton.Click += (_, _) => FitView();
		textEditor.KeyDown += OnTextEditorKeyDown;
		textEditor.LostKeyboardFocus += OnTextEditorLostFocus;
		toolbar.Children.Add(zoomCombo);
		toolbar.Children.Add(fitButton);
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

	/// <summary>
	/// Raised with a surface-local point when the design surface is clicked. The runtime
	/// converts it to design coordinates (see <see cref="ToDesignPoint"/>) - passing the
	/// design point here would double-convert.
	/// </summary>
	public event EventHandler<Vector2> SurfacePointerPressed;

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

	/// <summary>
	/// Shows the inline text editor over the given design rect, pre-filled with
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
	}

	/// <summary>Design-space point to surface-local DIPs, honoring the viewport.</summary>
	public Point DesignToSurfacePoint(double x, double y)
	{
		var (originX, originY, scale) = ViewportParams();
		return new Point(originX + panX + x * scale, toolbar.ActualHeight + originY + panY + y * scale);
	}

	public void SetRender(RenderResult render)
	{
		if (string.IsNullOrEmpty(render?.Data))
			return;
		var bytes = Convert.FromBase64String(render.Data);
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
		return new Vector2(
			(float)((point.X - originX - panX) / scale),
			(float)((viewportY - originY - panY) / scale));
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

	void OnScrollChanged(object sender, ScrollChangedEventArgs e) => ApplyViewport();

	void ApplyViewport()
	{
		if (pixelWidth == 0 || pixelHeight == 0 || scroller.ViewportWidth == 0 || scroller.ViewportHeight == 0)
			return;
		var (originX, originY, scale) = ViewportParams();
		contentCanvas.Width = Math.Max(scroller.ViewportWidth, pixelWidth * scale);
		contentCanvas.Height = Math.Max(scroller.ViewportHeight, pixelHeight * scale);
		// The design rect's scroll-content position: its screen position (origin + pan) plus
		// the scroll offset; the scroll offset cancels for the on-screen position.
		Canvas.SetLeft(viewportCanvas, originX + panX + scroller.HorizontalOffset);
		Canvas.SetTop(viewportCanvas, originY + panY + scroller.VerticalOffset);
		viewportCanvas.Width = pixelWidth * scale;
		viewportCanvas.Height = pixelHeight * scale;
		// The image must fill the design-size canvas: without explicit size it renders at
		// the bitmap's natural DIP size (1 design unit = 1 DIP), which at fit scale is about
		// 2x too large and drifts from the selection outline.
		image.Width = viewportCanvas.Width;
		image.Height = viewportCanvas.Height;
		LayoutSelection();
		LayoutTextEditor();
		UpdateZoomCombo();
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

	#region Viewport interactions

	void OnKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Space)
		{
			spacePanning = true;
			Cursor = Cursors.SizeAll;
			e.Handled = true;
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
		if (textEditing || IsOverToolbar(e.GetPosition(this)))
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
		if (IsOverToolbar(position))
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
		dragStartSurface = position;
		dragHandle = HandleAt(ToDesignPoint(position)) ?? "";
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
		if (middlePanning)
		{
			var position = e.GetPosition(this);
			panX += position.X - lastPanPoint.X;
			panY += position.Y - lastPanPoint.Y;
			lastPanPoint = position;
			ApplyViewport();
			return;
		}
		if (dragPossible && !dragActive)
		{
			var position = e.GetPosition(this);
			if (Math.Abs(position.X - dragStartSurface.X) < DragThreshold && Math.Abs(position.Y - dragStartSurface.Y) < DragThreshold)
				return;
			BeginDrag();
			if (!dragActive)
				return;
		}
		if (dragActive)
		{
			var position = e.GetPosition(this);
			var dx = position.X - dragStartSurface.X;
			var dy = position.Y - dragStartSurface.Y;
			SurfaceElementDragDelta?.Invoke(this, (dx, dy));
			e.Handled = true;
			return;
		}
		UpdateCursor(e.GetPosition(this));
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
		if (!dragPossible)
			return;
		var position = e.GetPosition(this);
		if (dragActive)
		{
			SurfaceElementDragCommitted?.Invoke(this, (position.X - dragStartSurface.X, position.Y - dragStartSurface.Y));
		}
		else
		{
			// A plain click: pick at the press point.
			SurfacePointerPressed?.Invoke(this, new Vector2((float)dragStartSurface.X, (float)dragStartSurface.Y));
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
		if (!HasRender || dragPossible)
			return;
		var handle = HandleAt(ToDesignPoint(position));
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
		var designX = (cursorSurface.X - originX - panX) / scale;
		var designY = (viewportY - originY - panY) / scale;
		// Keep the scroll-content position stable while the scale changes, so the point
		// under the cursor does not jump.
		var contentX = originX + panX + designX * scale + scroller.HorizontalOffset;
		var contentY = originY + panY + designY * scale + scroller.VerticalOffset;
		zoomFactor = Math.Clamp(zoomFactor * factor, MinZoom, MaxZoom);
		var (originX2, originY2, scale2) = ViewportParams();
		panX = cursorSurface.X - originX2 - designX * scale2;
		panY = viewportY - originY2 - designY * scale2;
		ApplyViewport();
		scroller.ScrollToHorizontalOffset(contentX - (originX2 + panX + designX * scale2));
		scroller.ScrollToVerticalOffset(contentY - (originY2 + panY + designY * scale2));
	}

	#endregion
}
