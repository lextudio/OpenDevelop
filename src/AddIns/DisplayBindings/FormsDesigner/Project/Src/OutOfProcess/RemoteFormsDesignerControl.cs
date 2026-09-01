using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Drawing.Design;

using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Widgets;

namespace ICSharpCode.FormsDesigner.OutOfProcess
{
	sealed class RemoteFormsDesignerControl : DesignerCanvas
	{
		readonly FormsDesignerHostClient client;
		readonly Grid designSurface = new Grid();
		readonly Canvas scrollContent = new Canvas();
		readonly ScrollViewer scroller = new() {
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		};
		// Stretch.Fill (matching WpfSurfaceDesignerControl/UnoDesignSurfaceControl exactly): the
		// bitmap must scale to fill framePresenter.Visual's Width/Height, which is all Resize()
		// actually changes on zoom - Stretch.None would keep showing the bitmap at its native
		// pixel size regardless of Width/Height, so the selection outline (computed independently
		// through viewport.Scale) would resize with zoom while the rendered form image itself
		// visibly stayed at its pre-zoom size.
		readonly DesignFramePresenter framePresenter = new(Stretch.Fill,
			horizontalAlignment: HorizontalAlignment.Left, verticalAlignment: VerticalAlignment.Top);
		readonly Canvas adorners;
		readonly Canvas guides;
		// Drag-snap alignment guides (see SnapGuideCalculator): a vertical or horizontal line
		// shown while a component is being dragged near another component's edge/centre,
		// matching UnoDesignSurfaceControl's own guide overlay/rendering. Kept separate from
		// `guides` (which UpdateDesignGuides clears wholesale on every viewport/selection
		// change) so a live drag's guides aren't wiped by an unrelated redraw.
		readonly Canvas snapGuideOverlay = new Canvas { IsHitTestVisible = false };
		readonly List<Rectangle> snapGuides = new();
		readonly SelectionAdornerLayer adornerLayer = new(Array.Empty<string>(), Brushes.DodgerBlue, showLabel: false);
		readonly Rectangle marqueeBorder;
		readonly Thumb moveThumb;
		readonly Thumb resizeHitTarget;
		readonly Thumb resizeThumb;
		readonly Border disconnectedOverlay;
		readonly TextBlock disconnectedText;
		long version;
		DesignerSessionState state;
		long lastFrameSequence;
		// The design surface is unscaled; the shared canvas shell's zoom toolbar controls the
		// presentation scale around it via DesignViewport - the same coordinate math
		// UnoDesignSurfaceControl uses for its zoom/pan, so both backends' conversions share
		// one type (see DesignViewport's doc comment). Zoom/Fit re-derive the viewport and
		// re-present without re-decoding the frame.
		DesignViewport viewport = DesignViewport.Identity(0, 0);
		DesignerComponentInfo selectedComponent;
		double dragX;
		double dragY;
		double dragStartX;
		double dragStartY;
		double dragWidth;
		double dragHeight;
		// Kept next to the Forms overlay rather than in Designer.Presentation so the add-in
		// never requires an ABI change in a host-provided shared presentation assembly.
		Rect renderedSelection;
		int selectedLocalX;
		int selectedLocalY;
		bool showTabOrder;
		bool resizingDrag;
		bool previewResizeDrag;
		Point previewDragPoint;
		bool marqueeSelecting;
		bool marqueeExtendsSelection;
		Point marqueeStart;
		readonly HashSet<string> selectedComponentNames = new HashSet<string>(StringComparer.Ordinal);
		readonly HashSet<string> lockedComponentNames = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>Empty space kept on every side of the design inside the canvas, so the root
		/// component's own resize handles are reachable and DesignerCanvas's tiled "EdgePattern"
		/// background is visible around the form - matches WpfSurfaceDesignerControl's own
		/// CanvasPadding (the WPF designer already has this; this control did not, which is
		/// exactly why the WinForms designer's canvas visibly had no border around the form
		/// while the WPF/WinUI ones did).</summary>
		const double CanvasMargin = 24;

		static readonly double[] ZoomPresets = { 0.25, 0.5, 0.75, 1.0, 1.5, 2.0 };
		static readonly string[] ZoomLabels = { "Fit", "25%", "50%", "75%", "100%", "150%", "200%" };
		// The zoom combo starts at "100%" (VS behavior), so the initial render must be a
		// literal 100% zoom, not Fit; Fit is a user choice.
		bool fitMode = false;
		double zoomScale = 1.0;

		/// <summary>A Thumb template that draws nothing but a transparent hit-target fill, so the
		/// thumb stays invisible while still receiving mouse input - see moveThumb's own comment
		/// on why relying on the theme's default Thumb template is not safe here.</summary>
		static ControlTemplate CreateTransparentThumbTemplate()
		{
			var surface = new FrameworkElementFactory(typeof(Border));
			surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
			return new ControlTemplate(typeof(Thumb)) { VisualTree = surface };
		}

		void RebuildViewport()
		{
			if (state?.Render == null)
				return;
			// Re-derive the viewport from the current toolbar zoom and re-present. This must
			// not go through Show's frame-sequence guard - a zoom change replays the same
			// SessionState (same Sequence), so the guard would early-return and the zoom would
			// never take effect.
			ApplyViewport();
		}

		public RemoteFormsDesignerControl(FormsDesignerHostClient client, string backendName)
		{
			this.client = client;
			Focusable = true;
			BackendName = backendName;
			Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit | DesignerCanvasCapabilities.StatusBar;
			StatusText = $"Starting {BackendName} design host…";
			// The shared DesignerCanvas shell provides the dotted empty-canvas edge pattern and
			// the common zoom toolbar; the design surface is transparent so the edge pattern
			// shows around the rendered form bitmap.

			designSurface.Children.Add(framePresenter.Visual);
			guides = new Canvas { IsHitTestVisible = false };
			designSurface.Children.Add(guides);
			designSurface.Children.Add(snapGuideOverlay);
			adorners = new Canvas { IsHitTestVisible = true };
			marqueeBorder = new Rectangle {
				Stroke = Brushes.DodgerBlue, StrokeThickness = 1,
				Fill = new SolidColorBrush(Color.FromArgb(35, 30, 144, 255)),
				StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false,
				Visibility = Visibility.Collapsed
			};
			// moveThumb covers the WHOLE selected control and exists only as an invisible drag
			// target (the visible outline is drawn by adornerLayer). Its Template must be set
			// explicitly: the default WPF Thumb theme template paints its chrome from
			// SystemColors brushes, NOT from TemplateBinding Background, so setting
			// Background=Transparent alone does nothing. Under this app's dark theme (whose
			// Theme.Dark.xaml overrides ControlBrushKey/ControlLightLightColorKey to #252526 /
			// #333337) that chrome rendered as an OPAQUE DARK rectangle over the entire selected
			// control - the "selecting a Panel turns it black" bug, located with DevFlow's
			// ui/tree: the Thumb's inner template Borders reported background #252526/#333337
			// at exactly the panel's rect, on top of the rendered form bitmap.
			moveThumb = new Thumb {
				Background = Brushes.Transparent,
				Cursor = Cursors.SizeAll,
				Visibility = Visibility.Collapsed,
				Template = CreateTransparentThumbTemplate()
			};
			resizeThumb = new Thumb { Width = 8, Height = 8, Background = Brushes.White, BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(1), Cursor = Cursors.SizeNWSE, Visibility = Visibility.Collapsed };
			// Keep the conventional 8px visual handle while providing a forgiving transparent
			// input target around it.  At fractional DPI a real pointer can land one or two device
			// pixels off the visible square; without this, ScrollViewer sees the gesture instead of
			// the resize Thumb and scrolls the canvas rather than resizing the selected component.
			resizeHitTarget = new Thumb {
				Width = 20, Height = 20, Background = Brushes.Transparent,
				Cursor = Cursors.SizeNWSE, Visibility = Visibility.Collapsed,
				Template = CreateTransparentThumbTemplate()
			};
			adorners.Children.Add(marqueeBorder);
			adorners.Children.Add(adornerLayer.Visual);
			adorners.Children.Add(moveThumb);
			adorners.Children.Add(resizeHitTarget);
			adorners.Children.Add(resizeThumb);
			designSurface.Children.Add(adorners);
			disconnectedText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
			var restartButton = new Button { Content = "Restart designer", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 5, 12, 5) };
			restartButton.Click += (sender, args) => RestartRequested?.Invoke(this, EventArgs.Empty);
			disconnectedOverlay = new Border {
				Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
				BorderBrush = Brushes.IndianRed,
				BorderThickness = new Thickness(1),
				Padding = new Thickness(20),
				Visibility = Visibility.Collapsed,
				Child = new StackPanel { Children = { disconnectedText, restartButton } }
			};
			designSurface.Children.Add(disconnectedOverlay);
			// A form can be resized beyond the visible design tab.  Hosting the surface in a
			// ScrollViewer lets the canvas grow with it instead of clipping the bottom-right
			// Thumb (and, consequently, releasing a resize drag outside the canvas).
			scrollContent.Children.Add(designSurface);
			scroller.Content = scrollContent;
			ContentHost.Content = scroller;

			// Only controls backed by this designer are visible. Editing commands remain in the
			// IDE command system; grid/theme/name/device controls are not inert toolbar chrome.
			foreach (var label in ZoomLabels)
				ZoomCombo.Items.Add(label);
			ZoomCombo.SelectedIndex = 4; // 100%
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

			AllowDrop = true;
			MouseLeftButtonDown += OnMouseLeftButtonDown;
			MouseMove += OnMouseMove;
			MouseLeftButtonUp += OnMouseLeftButtonUp;
			// The ScrollViewer hosting the expandable canvas can consume bubbling mouse events.
			// Preview handlers keep the root-form resize gesture reachable even after scrollbars
			// appear, matching the WPF/WinUI designer surfaces' input-routing strategy.
			PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
			PreviewMouseMove += OnPreviewMouseMove;
			PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
			moveThumb.DragStarted += OnDragStarted;
			moveThumb.DragDelta += OnMoveDragDelta;
			moveThumb.DragCompleted += OnDragCompleted;
			resizeThumb.DragStarted += OnDragStarted;
			resizeThumb.DragDelta += OnResizeDragDelta;
			resizeThumb.DragCompleted += OnDragCompleted;
			resizeHitTarget.DragStarted += OnDragStarted;
			resizeHitTarget.DragDelta += OnResizeDragDelta;
			resizeHitTarget.DragCompleted += OnDragCompleted;
			DragOver += OnDragOver;
			Drop += OnDrop;
			KeyDown += OnKeyDown;
		}

		public string SelectedComponentName { get; private set; } = "";

		/// <summary>
		/// Surface geometry for the integration tests' resize-drag assertions: the rendered form
		/// bitmap bounds, the current selection outline bounds, the selected element's bounds and
		/// the bottom-right resize handle position - all in screen coordinates. The selection
		/// outline must coincide with the rendered form (and the handle sit at its bottom-right
		/// corner) both before and after a resize drag; this is the smoke probe for that
		/// invariant.
		/// </summary>
		public DesignerSurfaceGeometry SurfaceGeometry()
		{
			var frame = DesignerSurfaceGeometryProbe.ScreenBoundsOf(framePresenter.Visual);
			Rect selection = default;
			if (selectedComponent != null)
			{
				selection = DesignerSurfaceGeometryProbe.DesignRectToScreen(viewport,
					new Rect(selectedComponent.SurfaceX, selectedComponent.SurfaceY,
						selectedComponent.Width, selectedComponent.Height),
					designSurface);
			}
			var resizeBounds = DesignerSurfaceGeometryProbe.ScreenBoundsOf(resizeThumb);
			var handle = resizeBounds.IsEmpty
				? new Point(selection.X + selection.Width, selection.Y + selection.Height)
				: new Point(resizeBounds.X + resizeBounds.Width / 2, resizeBounds.Y + resizeBounds.Height / 2);
			return new DesignerSurfaceGeometry(frame, selection, handle, selection);
		}
		public string[] SelectedComponentNames => String.IsNullOrEmpty(SelectedComponentName)
			? selectedComponentNames.ToArray()
			: new[] { SelectedComponentName }.Concat(selectedComponentNames.Where(name => name != SelectedComponentName)).ToArray();
		public bool IsLocked(string componentName) => lockedComponentNames.Contains(componentName);
		public void RenameSelection(string oldName, string newName)
		{
			if (selectedComponentNames.Remove(oldName)) selectedComponentNames.Add(newName);
			if (lockedComponentNames.Remove(oldName)) lockedComponentNames.Add(newName);
			if (SelectedComponentName == oldName) SelectedComponentName = newName;
		}
		public DesignerSessionState State => state;
		public event EventHandler SelectionChanged;
		public event EventHandler<RemoteToolboxDropEventArgs> ToolboxDrop;
		public event EventHandler<RemoteBoundsChangedEventArgs> BoundsChanged;
		public event EventHandler<RemoteSelectionMoveEventArgs> SelectionMoveRequested;
		public event EventHandler<RemoteComponentEventArgs> DeleteRequested;
		public event EventHandler<RemoteComponentEventArgs> DefaultEventRequested;
		public event EventHandler RestartRequested;

		protected override AutomationPeer OnCreateAutomationPeer() => new RemoteDesignerAutomationPeer(this);

		public void Show(DesignerSessionState state)
		{
			disconnectedOverlay.Visibility = Visibility.Collapsed;
			this.state = state;
			version = state.Version;
			if (state.Render == null || (String.IsNullOrEmpty(state.Render.PngBase64) && String.IsNullOrEmpty(state.Render.Data))) {
				return;
			}
			if (state.Render.Sequence > 0 && state.Render.Sequence <= lastFrameSequence) {
				return;
			}
			lastFrameSequence = state.Render.Sequence;
			var dpiForStatus = Math.Max(1, state.Render.Dpi);
			StatusText = $"Rendered by {BackendName} design host ({state.Render.Width / dpiForStatus:0}×{state.Render.Height / dpiForStatus:0}).";
			ImageSource bitmap;
			if (!String.IsNullOrEmpty(state.Render.Data)) {
				var pixels = DesignerFrameCodec.DecodeBgra32(state.Render);
				bitmap = BitmapSource.Create(state.Render.Width, state.Render.Height, 96, 96,
					PixelFormats.Bgra32, null, pixels, state.Render.Width * 4);
				bitmap.Freeze();
			} else {
				var png = new BitmapImage();
				using (var stream = new MemoryStream(Convert.FromBase64String(state.Render.PngBase64))) {
					png.BeginInit();
					png.CacheOption = BitmapCacheOption.OnLoad;
					png.StreamSource = stream;
					png.EndInit();
					png.Freeze();
				}
				bitmap = png;
			}
			framePresenter.SetSource(bitmap);
			ApplyViewport();
			if (!String.IsNullOrEmpty(SelectedComponentName)) {
				selectedComponent = state.Components.FirstOrDefault(item => item.Name == SelectedComponentName);
				selectedComponentNames.RemoveWhere(name => !state.Components.Any(item => item.Name == name));
				lockedComponentNames.RemoveWhere(name => !state.Components.Any(item => item.Name == name));
				UpdateAdorners();
			}
			AutomationProperties.SetName(this, selectedComponent?.AccessibleName ?? "WinForms designer");
			AutomationProperties.SetHelpText(this, selectedComponent?.AccessibleDescription ?? "");
		}

		void ApplyViewport()
		{
			var dpi = Math.Max(1, state.Render.Dpi);
			var designWidth = state.Render.Width / dpi;
			var designHeight = state.Render.Height / dpi;
			// Fit/zoom inside an inset area, then shift everything back out by the same margin
			// through the viewport's own pan - so the frame bitmap, the guide overlay and every
			// DesignToSurface-based adorner all move together and stay aligned (matches
			// WpfSurfaceDesignerControl's identical CanvasPadding treatment).
			var availableWidth = Math.Max(0, scroller.ViewportWidth - 2 * CanvasMargin);
			var availableHeight = Math.Max(0, scroller.ViewportHeight - 2 * CanvasMargin);
			if (fitMode)
				viewport = DesignViewport.Fit(designWidth, designHeight, availableWidth, availableHeight, 1.0, CanvasMargin, CanvasMargin);
			else
				viewport = DesignViewport.Zoom(designWidth, designHeight, availableWidth, availableHeight, zoomScale, CanvasMargin, CanvasMargin);
			framePresenter.Resize(viewport);
			// The rendered form must sit at the viewport's base (centered-fit origin + pan),
			// exactly where the DesignToSurface-based guides/adorners are placed - otherwise the
			// selection outline and the bitmap drift apart whenever Scale != 1.
			framePresenter.Visual.Margin = new Thickness(
				Math.Max(0, viewport.OriginX) + viewport.PanX,
				Math.Max(0, viewport.OriginY) + viewport.PanY, 0, 0);
			UpdateCanvasExtent();
			UpdateDesignGuides();
			if (selectedComponent != null)
				UpdateAdorners();
		}

		void UpdateDesignGuides()
		{
			guides.Children.Clear();
			if (state?.Render == null) return;
			// The form outline must cover exactly the rendered bitmap, so both corners go
			// through DesignToSurface (same space the frame sits in once zoomed/centered).
			var (fx, fy) = viewport.DesignToSurface(0, 0);
			var (fx2, fy2) = viewport.DesignToSurface(state.Render.Width / Math.Max(1, state.Render.Dpi),
				state.Render.Height / Math.Max(1, state.Render.Dpi));
			var formOutline = new Rectangle {
				Width = Math.Max(1, fx2 - fx), Height = Math.Max(1, fy2 - fy),
				Stroke = Brushes.Gray, StrokeThickness = 1
			};
			Canvas.SetLeft(formOutline, fx);
			Canvas.SetTop(formOutline, fy);
			guides.Children.Add(formOutline);
			foreach (var component in state.Components.Where(item => !String.IsNullOrEmpty(item.Parent))) {
				var (surfaceX, surfaceY) = viewport.DesignToSurface(component.SurfaceX, component.SurfaceY);
				var (surfaceX2, surfaceY2) = viewport.DesignToSurface(
					component.SurfaceX + component.Width, component.SurfaceY + component.Height);
				var outline = new Rectangle {
					Width = Math.Max(1, surfaceX2 - surfaceX), Height = Math.Max(1, surfaceY2 - surfaceY),
					Stroke = lockedComponentNames.Contains(component.Name) ? Brushes.DarkOrange
						: selectedComponentNames.Contains(component.Name) ? Brushes.DodgerBlue : new SolidColorBrush(Color.FromArgb(150, 80, 80, 80)),
					StrokeThickness = selectedComponentNames.Contains(component.Name) ? 2 : 1,
					StrokeDashArray = selectedComponentNames.Contains(component.Name) ? null : new DoubleCollection { 3, 2 }
				};
				Canvas.SetLeft(outline, surfaceX);
				Canvas.SetTop(outline, surfaceY);
				guides.Children.Add(outline);
				if (component.Height >= 18 && component.Width >= 35) {
					var label = new TextBlock {
						Text = component.Name, FontSize = 10, Foreground = Brushes.DimGray,
						Background = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
						Padding = new Thickness(2, 0, 2, 0)
					};
					Canvas.SetLeft(label, surfaceX + 2);
					Canvas.SetTop(label, surfaceY + 2);
					guides.Children.Add(label);
				}
				if (showTabOrder) {
					var tabIndex = component.Properties.FirstOrDefault(item => item.Name == "TabIndex")?.Value ?? "?";
					var badge = new Border {
						Background = Brushes.RoyalBlue, CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 1, 5, 1),
						Child = new TextBlock { Text = tabIndex, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11 }
					};
					Canvas.SetLeft(badge, surfaceX - 5);
					Canvas.SetTop(badge, surfaceY - 8);
					guides.Children.Add(badge);
				}
			}
		}

		public void SetTabOrderMode(bool value)
		{
			showTabOrder = value;
			UpdateDesignGuides();
		}

		public void SelectAllComponents()
		{
			selectedComponentNames.Clear();
			foreach (var component in state.Components.Where(item => !String.IsNullOrEmpty(item.Parent)))
				selectedComponentNames.Add(component.Name);
			SelectedComponentName = selectedComponentNames.FirstOrDefault() ?? "";
			selectedComponent = state.Components.FirstOrDefault(item => item.Name == SelectedComponentName);
			UpdateDesignGuides();
			UpdateAdorners();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>
		/// Sets the whole selection to the named components (first is primary), keeping the rest
		/// of the selection machinery and the <see cref="SelectionChanged"/> event in sync -
		/// mirrors <see cref="SelectAllComponents"/> but from an explicit name list, so DevFlow
		/// actions can drive multi-select align/distribute the same way a rubber-band drag would.
		/// Unknown names are skipped; the first known name becomes the primary selection.
		/// </summary>
		public void SelectComponents(params string[] names)
		{
			var known = names == null
				? Array.Empty<string>()
				: names.Where(name => state?.Components?.Any(item => item.Name == name) == true).ToArray();
			selectedComponentNames.Clear();
			foreach (var name in known)
				selectedComponentNames.Add(name);
			SelectedComponentName = known.FirstOrDefault() ?? "";
			selectedComponent = String.IsNullOrEmpty(SelectedComponentName) ? null
				: state.Components.FirstOrDefault(item => item.Name == SelectedComponentName);
			UpdateDesignGuides();
			UpdateAdorners();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>Selects a single component by name (no-op when unknown), keeping the rest
		/// of the selection machinery and the <see cref="SelectionChanged"/> event in sync.
		/// Used by the Document Outline pad.</summary>
		public void SelectComponent(string componentName)
		{
			if (componentName != null && state?.Components?.Any(item => item.Name == componentName) == true)
				SelectSingleComponent(componentName);
		}

		public void ToggleSelectedLocked()
		{
			var shouldLock = selectedComponentNames.Any(name => !lockedComponentNames.Contains(name));
			foreach (var name in selectedComponentNames) {
				if (shouldLock) lockedComponentNames.Add(name); else lockedComponentNames.Remove(name);
			}
			UpdateDesignGuides();
			UpdateAdorners();
		}

		public void ShowDisconnected(string message)
		{
			disconnectedText.Text = message;
			disconnectedOverlay.Visibility = Visibility.Visible;
			adornerLayer.ClearSelection();
			moveThumb.Visibility = resizeHitTarget.Visibility = resizeThumb.Visibility = Visibility.Collapsed;
			StatusText = message;
		}

		public bool TryGetComponentScreenBounds(string componentName, out Rect bounds)
		{
			bounds = Rect.Empty;
			var component = state?.Components?.FirstOrDefault(item => item.Name == componentName);
			if (component == null || !framePresenter.Visual.IsVisible)
				return false;
			// Both corners through DesignToSurface so the UIA peer bounds track the (possibly
			// zoomed) design rect, then PointToScreen from the design surface grid.
			var (x, y) = viewport.DesignToSurface(component.SurfaceX, component.SurfaceY);
			var (x2, y2) = viewport.DesignToSurface(
				component.SurfaceX + component.Width, component.SurfaceY + component.Height);
			var topLeft = designSurface.PointToScreen(new Point(x, y));
			var bottomRight = designSurface.PointToScreen(new Point(x2, y2));
			bounds = new Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
			return true;
		}

		async void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			try {
				var extendSelection = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
				// GetPosition on the (possibly zoomed) frame image yields surface pixels;
				// component bounds and the child's hit-testing are design-space.
				var point = e.GetPosition(framePresenter.Visual);
				var designPoint = new Point(point.X / viewport.Scale, point.Y / viewport.Scale);
				if (!state.Components.Any(component => !String.IsNullOrEmpty(component.Parent)
					&& new Rect(component.SurfaceX, component.SurfaceY, component.Width, component.Height).Contains(designPoint))) {
					marqueeSelecting = true;
					marqueeExtendsSelection = extendSelection;
					marqueeStart = designPoint;
					marqueeBorder.Width = marqueeBorder.Height = 0;
					var (mx, my) = viewport.DesignToSurface(designPoint.X, designPoint.Y);
					Canvas.SetLeft(marqueeBorder, mx);
					Canvas.SetTop(marqueeBorder, my);
					marqueeBorder.Visibility = Visibility.Visible;
					CaptureMouse();
					e.Handled = true;
					return;
				}
				var result = await client.HitTestAsync(version, (int)designPoint.X, (int)designPoint.Y, CancellationToken.None);
				if (!extendSelection) selectedComponentNames.Clear();
				if (!String.IsNullOrEmpty(result.ComponentName)) {
					if (extendSelection && selectedComponentNames.Contains(result.ComponentName)) selectedComponentNames.Remove(result.ComponentName);
					else selectedComponentNames.Add(result.ComponentName);
				}
				SelectedComponentName = selectedComponentNames.Contains(result.ComponentName)
					? result.ComponentName : selectedComponentNames.FirstOrDefault() ?? "";
				selectedComponent = state?.Components?.FirstOrDefault(item => item.Name == SelectedComponentName);
				UpdateDesignGuides();
				UpdateAdorners();
				Focus();
				SelectionChanged?.Invoke(this, EventArgs.Empty);
				if (e.ClickCount == 2 && !extendSelection && !String.IsNullOrEmpty(SelectedComponentName)) {
					DefaultEventRequested?.Invoke(this, new RemoteComponentEventArgs(SelectedComponentName));
					e.Handled = true;
				}
			} catch { }
		}

		void OnMouseMove(object sender, MouseEventArgs e)
		{
			if (!marqueeSelecting || e.LeftButton != MouseButtonState.Pressed) return;
			// Marquee state is design-space; convert both corners before drawing so the
			// rubber band tracks the zoomed design rect exactly.
			var point = e.GetPosition(framePresenter.Visual);
			var designPoint = new Point(point.X / viewport.Scale, point.Y / viewport.Scale);
			var left = Math.Min(marqueeStart.X, designPoint.X);
			var top = Math.Min(marqueeStart.Y, designPoint.Y);
			var (sx, sy) = viewport.DesignToSurface(left, top);
			var (sx2, sy2) = viewport.DesignToSurface(
				left + Math.Abs(designPoint.X - marqueeStart.X),
				top + Math.Abs(designPoint.Y - marqueeStart.Y));
			Canvas.SetLeft(marqueeBorder, sx);
			Canvas.SetTop(marqueeBorder, sy);
			marqueeBorder.Width = Math.Max(0, sx2 - sx);
			marqueeBorder.Height = Math.Max(0, sy2 - sy);
		}

		void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (!marqueeSelecting) return;
			marqueeSelecting = false;
			ReleaseMouseCapture();
			var bounds = new Rect(Canvas.GetLeft(marqueeBorder), Canvas.GetTop(marqueeBorder),
				marqueeBorder.Width, marqueeBorder.Height);
			marqueeBorder.Visibility = Visibility.Collapsed;
			if (!marqueeExtendsSelection) selectedComponentNames.Clear();
			if (bounds.Width >= 3 || bounds.Height >= 3) {
				foreach (var component in state.Components.Where(item => !String.IsNullOrEmpty(item.Parent))) {
					// The marquee rect is drawn in surface space; convert each component rect
					// the same way before intersecting.
					var (cx, cy) = viewport.DesignToSurface(component.SurfaceX, component.SurfaceY);
					var (cx2, cy2) = viewport.DesignToSurface(
						component.SurfaceX + component.Width, component.SurfaceY + component.Height);
					var componentBounds = new Rect(cx, cy, cx2 - cx, cy2 - cy);
					if (bounds.IntersectsWith(componentBounds)) selectedComponentNames.Add(component.Name);
				}
			} else if (!marqueeExtendsSelection) {
				var root = state.Components.FirstOrDefault(item => String.IsNullOrEmpty(item.Parent));
				if (root != null) selectedComponentNames.Add(root.Name);
			}
			SelectedComponentName = selectedComponentNames.FirstOrDefault() ?? "";
			selectedComponent = state.Components.FirstOrDefault(item => item.Name == SelectedComponentName);
			UpdateDesignGuides();
			UpdateAdorners();
			Focus();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
			e.Handled = true;
		}

		void OnKeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Escape && selectedComponent != null && !String.IsNullOrEmpty(selectedComponent.Parent)) {
				SelectSingleComponent(selectedComponent.Parent);
				e.Handled = true;
				return;
			}
			if (e.Key == Key.Tab && state?.Components?.Count > 0) {
				var selectable = state.Components.Where(item => !String.IsNullOrEmpty(item.Parent))
					.OrderBy(item => ParseTabIndex(item)).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
				if (selectable.Length > 0) {
					var current = Array.FindIndex(selectable, item => item.Name == SelectedComponentName);
					var direction = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
					var next = (current + direction + selectable.Length) % selectable.Length;
					SelectSingleComponent(selectable[next].Name);
					e.Handled = true;
					return;
				}
			}
			if (e.Key == Key.Delete && selectedComponent != null && !String.IsNullOrEmpty(selectedComponent.Parent)
				&& !lockedComponentNames.Contains(selectedComponent.Name)) {
				DeleteRequested?.Invoke(this, new RemoteComponentEventArgs(selectedComponent.Name));
				e.Handled = true;
				return;
			}
			if (selectedComponent == null || String.IsNullOrEmpty(selectedComponent.Parent) || lockedComponentNames.Contains(selectedComponent.Name)) return;
			var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
			var dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
			var dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
			if (dx == 0 && dy == 0) return;
			SelectionMoveRequested?.Invoke(this, new RemoteSelectionMoveEventArgs(dx, dy));
			e.Handled = true;
		}

		static int ParseTabIndex(DesignerComponentInfo component)
		{
			var value = component.Properties.FirstOrDefault(item => item.Name == "TabIndex")?.Value;
			return Int32.TryParse(value, out var result) ? result : Int32.MaxValue;
		}

		void SelectSingleComponent(string componentName)
		{
			var component = state?.Components?.FirstOrDefault(item => item.Name == componentName);
			if (component == null) return;
			selectedComponentNames.Clear();
			selectedComponentNames.Add(component.Name);
			SelectedComponentName = component.Name;
			selectedComponent = component;
			UpdateDesignGuides();
			UpdateAdorners();
			Focus();
			SelectionChanged?.Invoke(this, EventArgs.Empty);
		}

		sealed class RemoteDesignerAutomationPeer : FrameworkElementAutomationPeer, ISelectionProvider
		{
			readonly RemoteFormsDesignerControl owner;

			public RemoteDesignerAutomationPeer(RemoteFormsDesignerControl owner) : base(owner) => this.owner = owner;

			protected override string GetClassNameCore() => nameof(RemoteFormsDesignerControl);
			protected override string GetNameCore() => "WinForms designer";
			protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
			protected override List<AutomationPeer> GetChildrenCore() => owner.state?.Components?
				.Where(item => !String.IsNullOrEmpty(item.Name) && String.IsNullOrEmpty(item.Parent))
				.Select(item => (AutomationPeer)new RemoteComponentAutomationPeer(owner, this, item)).ToList()
				?? new List<AutomationPeer>();
			public override object GetPattern(PatternInterface patternInterface)
				=> patternInterface == PatternInterface.Selection ? this : base.GetPattern(patternInterface);

			public bool CanSelectMultiple => true;
			public bool IsSelectionRequired => false;
			public IRawElementProviderSimple[] GetSelection() => owner.state.Components
				.Where(item => owner.selectedComponentNames.Contains(item.Name))
				.Select(item => new RemoteComponentAutomationPeer(owner, this, item))
				.Select(ProviderFromPeer).ToArray();
		}

		sealed class RemoteComponentAutomationPeer : AutomationPeer, ISelectionItemProvider
		{
			readonly RemoteFormsDesignerControl owner;
			readonly RemoteDesignerAutomationPeer container;
			readonly DesignerComponentInfo component;

			public RemoteComponentAutomationPeer(RemoteFormsDesignerControl owner,
				RemoteDesignerAutomationPeer container, DesignerComponentInfo component)
			{
				this.owner = owner;
				this.container = container;
				this.component = component;
			}

			protected override string GetNameCore() => String.IsNullOrEmpty(component.AccessibleName)
				? component.Name : component.AccessibleName;
			protected override string GetHelpTextCore() => component.AccessibleDescription ?? "";
			protected override string GetClassNameCore() => component.Type;
			protected override string GetAutomationIdCore() => component.Name;
			protected override string GetAcceleratorKeyCore() => "";
			protected override string GetAccessKeyCore() => "";
			protected override string GetItemStatusCore() => IsSelected ? "Selected" : "";
			protected override string GetItemTypeCore() => component.AccessibleRole ?? "";
			protected override AutomationControlType GetAutomationControlTypeCore() => ControlType(component.Type);
			protected override Rect GetBoundingRectangleCore()
				=> owner.TryGetComponentScreenBounds(component.Name, out var bounds) ? bounds : Rect.Empty;
			protected override Point GetClickablePointCore()
			{
				var bounds = GetBoundingRectangleCore();
				return bounds.IsEmpty ? new Point(Double.NaN, Double.NaN)
					: new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
			}
			protected override List<AutomationPeer> GetChildrenCore() => owner.state.Components
				.Where(item => item.Parent == component.Name)
				.Select(item => (AutomationPeer)new RemoteComponentAutomationPeer(owner, container, item)).ToList();
			protected override AutomationPeer GetLabeledByCore() => null;
			protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
			protected override bool IsControlElementCore() => true;
			protected override bool IsContentElementCore() => true;
			protected override bool IsEnabledCore() => true;
			protected override bool HasKeyboardFocusCore() => owner.IsKeyboardFocusWithin && IsSelected;
			protected override bool IsKeyboardFocusableCore() => true;
			protected override bool IsOffscreenCore() => GetBoundingRectangleCore().IsEmpty;
			protected override bool IsPasswordCore() => false;
			protected override bool IsRequiredForFormCore() => false;
			protected override void SetFocusCore() => owner.SelectSingleComponent(component.Name);
			public override object GetPattern(PatternInterface patternInterface)
				=> patternInterface == PatternInterface.SelectionItem ? this : null;

			public bool IsSelected => owner.selectedComponentNames.Contains(component.Name);
			public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(container);
			public void AddToSelection()
			{
				owner.selectedComponentNames.Add(component.Name);
				owner.SelectedComponentName = component.Name;
				owner.selectedComponent = component;
				owner.UpdateDesignGuides();
				owner.UpdateAdorners();
				owner.SelectionChanged?.Invoke(owner, EventArgs.Empty);
			}
			public void RemoveFromSelection()
			{
				owner.selectedComponentNames.Remove(component.Name);
				if (owner.SelectedComponentName == component.Name) {
					owner.SelectedComponentName = owner.selectedComponentNames.FirstOrDefault() ?? "";
					owner.selectedComponent = owner.state.Components.FirstOrDefault(item => item.Name == owner.SelectedComponentName);
				}
				owner.UpdateDesignGuides();
				owner.UpdateAdorners();
				owner.SelectionChanged?.Invoke(owner, EventArgs.Empty);
			}
			public void Select() => owner.SelectSingleComponent(component.Name);

			static AutomationControlType ControlType(string type) => type switch {
				"System.Windows.Forms.Button" => AutomationControlType.Button,
				"System.Windows.Forms.CheckBox" => AutomationControlType.CheckBox,
				"System.Windows.Forms.RadioButton" => AutomationControlType.RadioButton,
				"System.Windows.Forms.TextBox" => AutomationControlType.Edit,
				"System.Windows.Forms.ComboBox" => AutomationControlType.ComboBox,
				"System.Windows.Forms.ListBox" => AutomationControlType.List,
				"System.Windows.Forms.TreeView" => AutomationControlType.Tree,
				"System.Windows.Forms.DataGridView" => AutomationControlType.DataGrid,
				"System.Windows.Forms.Form" => AutomationControlType.Window,
				_ => AutomationControlType.Custom
			};
		}

		void OnDragStarted(object sender, DragStartedEventArgs e)
		{
			if (selectedComponent == null || lockedComponentNames.Contains(selectedComponent.Name)) return;
			resizingDrag = ReferenceEquals(sender, resizeThumb) || ReferenceEquals(sender, resizeHitTarget);
			BeginDrag();
		}

		void BeginDrag()
		{
			if (selectedComponent == null)
				return;
			dragX = selectedComponent.SurfaceX;
			dragY = selectedComponent.SurfaceY;
			dragStartX = dragX;
			dragStartY = dragY;
			selectedLocalX = selectedComponent.X;
			selectedLocalY = selectedComponent.Y;
			dragWidth = selectedComponent.Width;
			dragHeight = selectedComponent.Height;
			SetSnapGuides(Array.Empty<(bool, double)>());
		}

		bool IsOverResizeHitTarget(Point point)
		{
			if (resizeHitTarget.Visibility != Visibility.Visible || !resizeHitTarget.IsEnabled)
				return false;
			// Compare in the root canvas's coordinate space rather than relying on Canvas.Left in
			// the ScrollViewer child.  LibreWPF's composed ScrollViewer can apply its own transform
			// between the two, whereas TranslatePoint follows the actual rendered visual chain.
			var centre = resizeThumb.TranslatePoint(
				new Point(resizeThumb.ActualWidth / 2, resizeThumb.ActualHeight / 2), this);
			const double hitSlop = 16;
			return Math.Abs(point.X - centre.X) <= hitSlop && Math.Abs(point.Y - centre.Y) <= hitSlop;
		}

		void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (selectedComponent == null || lockedComponentNames.Contains(selectedComponent.Name))
				return;
			var pt = e.GetPosition(this);
			var hitTest = IsOverResizeHitTarget(pt);
			var thumbCentre = resizeThumb.TranslatePoint(new Point(resizeThumb.ActualWidth / 2, resizeThumb.ActualHeight / 2), this);
			if (!hitTest)
				return;
			resizingDrag = true;
			previewResizeDrag = true;
			previewDragPoint = e.GetPosition(this);
			BeginDrag();
			CaptureMouse();
			e.Handled = true;
		}

		void OnPreviewMouseMove(object sender, MouseEventArgs e)
		{
			if (!previewResizeDrag)
				return;
			if (e.LeftButton != MouseButtonState.Pressed)
			{
				CompletePreviewResizeDrag(canceled: true);
				return;
			}
			var point = e.GetPosition(this);
			var scale = Math.Max(0.0001, viewport.Scale);
			var deltaX = (point.X - previewDragPoint.X) / scale;
			var deltaY = (point.Y - previewDragPoint.Y) / scale;
			dragWidth = Math.Max(8, dragWidth + deltaX);
			dragHeight = Math.Max(8, dragHeight + deltaY);
			previewDragPoint = point;
			UpdateCanvasExtent();
			PositionAdorners();
			ScrollResizeHandleIntoView();
			e.Handled = true;
		}

		void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (!previewResizeDrag)
				return;
			CompletePreviewResizeDrag(canceled: false);
			e.Handled = true;
		}

		void CompletePreviewResizeDrag(bool canceled)
		{
			if (!previewResizeDrag)
				return;
			previewResizeDrag = false;
			if (IsMouseCaptured)
				ReleaseMouseCapture();
			if (selectedComponent == null || canceled) {
				return;
			}
			var selection = renderedSelection;
			var selectionWidth = (int)Math.Round(selection.Width);
			var selectionHeight = (int)Math.Round(selection.Height);
			BoundsChanged?.Invoke(this, new RemoteBoundsChangedEventArgs(selectedComponent.Name,
				selectedLocalX + (int)Math.Round(selection.X - selectedComponent.SurfaceX),
				selectedLocalY + (int)Math.Round(selection.Y - selectedComponent.SurfaceY),
				selectionWidth, selectionHeight));
		}

		void OnMoveDragDelta(object sender, DragDeltaEventArgs e)
		{
			// DragDelta reports surface pixels; convert to design units (the adorner math and
			// the child's coordinates are design-space).
			var scale = viewport.Scale;
			var proposedX = Math.Max(0, dragX + e.HorizontalChange / scale);
			var proposedY = Math.Max(0, dragY + e.VerticalChange / scale);
			// Snap the dragged component's edges/centre to nearby siblings' edges/centres and
			// show alignment guides while dragging (move only - resizes are not snapped),
			// matching UnoDesignSurfaceControl's own ApplySnap behavior.
			var (snapDx, snapDy, guideLines) = SnapGuideCalculator.ApplySnap(
				(dragStartX, dragStartY, dragWidth, dragHeight),
				proposedX - dragStartX, proposedY - dragStartY, SiblingBounds());
			dragX = dragStartX + snapDx;
			dragY = dragStartY + snapDy;
			SetSnapGuides(guideLines);
			PositionAdorners();
		}

		/// <summary>Every other component's design-space bounds, for <see cref="SnapGuideCalculator"/>
		/// to snap the dragged component against.</summary>
		IEnumerable<(double X, double Y, double Width, double Height)> SiblingBounds()
		{
			if (state?.Components == null || selectedComponent == null)
				yield break;
			foreach (var component in state.Components)
			{
				if (component.Name == selectedComponent.Name)
					continue;
				yield return (component.SurfaceX, component.SurfaceY, component.Width, component.Height);
			}
		}

		/// <summary>Shows snap alignment guides at the given design positions
		/// ((isVertical, position) pairs); empty clears them.</summary>
		void SetSnapGuides(IReadOnlyList<(bool IsVertical, double Position)> guidesToShow)
		{
			foreach (var guide in snapGuides)
				snapGuideOverlay.Children.Remove(guide);
			snapGuides.Clear();
			if (state?.Render == null || guidesToShow.Count == 0)
				return;
			var dpi = Math.Max(1, state.Render.Dpi);
			var designWidth = state.Render.Width / dpi;
			var designHeight = state.Render.Height / dpi;
			var (x0, y0) = viewport.DesignToSurface(0, 0);
			var (x1, y1) = viewport.DesignToSurface(designWidth, designHeight);
			foreach (var (isVertical, position) in guidesToShow)
			{
				var guide = new Rectangle {
					Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x5D, 0x2A)),
					IsHitTestVisible = false
				};
				if (isVertical)
				{
					var (px, _) = viewport.DesignToSurface(position, 0);
					Canvas.SetLeft(guide, px);
					Canvas.SetTop(guide, y0);
					guide.Width = 1;
					guide.Height = Math.Max(1, y1 - y0);
				}
				else
				{
					var (_, py) = viewport.DesignToSurface(0, position);
					Canvas.SetLeft(guide, x0);
					Canvas.SetTop(guide, py);
					guide.Width = Math.Max(1, x1 - x0);
					guide.Height = 1;
				}
				snapGuides.Add(guide);
				snapGuideOverlay.Children.Add(guide);
			}
		}

		void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
		{
			var scale = viewport.Scale;
			dragWidth = Math.Max(8, dragWidth + e.HorizontalChange / scale);
			dragHeight = Math.Max(8, dragHeight + e.VerticalChange / scale);
			UpdateCanvasExtent();
			PositionAdorners();
			// PositionAdorners runs while a new frame/selection can still be in WPF's measure
			// pass, when ScrollViewer.ViewportHeight is zero or stale.  Defer one dispatcher
			// turn so the actual scrollbar viewport is known before deciding whether to scroll.
			Dispatcher.BeginInvoke(new Action(() => {
				// A bottom-right thumb is reached from the bottom-right canvas corner.  When an
				// axis has a scrollbar, partial "just visible" scrolling still leaves the pointer
				// competing with that bar; place both viewport axes at their real ends first.
				scroller.ScrollToRightEnd();
				scroller.ScrollToBottom();
				ScrollResizeHandleIntoView();
			}), System.Windows.Threading.DispatcherPriority.Loaded);
		}

		/// <summary>Expands the scrollable design surface enough to retain the selected item's
		/// bottom-right resize handle and the normal empty-canvas margin.  It never shrinks while
		/// a drag is active, which avoids the scrollbar moving underneath the captured pointer.</summary>
		void UpdateCanvasExtent()
		{
			if (state?.Render == null)
				return;
			var dpi = Math.Max(1, state.Render.Dpi);
			var designWidth = state.Render.Width / dpi;
			var designHeight = state.Render.Height / dpi;
			if (selectedComponent != null)
			{
				designWidth = Math.Max(designWidth, dragX + dragWidth);
				designHeight = Math.Max(designHeight, dragY + dragHeight);
			}
			var (right, bottom) = viewport.DesignToSurface(designWidth, designHeight);
			designSurface.Width = Math.Max(scroller.ViewportWidth, right + CanvasMargin);
			designSurface.Height = Math.Max(scroller.ViewportHeight, bottom + CanvasMargin);
			scrollContent.Width = designSurface.Width;
			scrollContent.Height = designSurface.Height;
		}

		/// <summary>Keeps a live resize's handle inside the visible canvas.  This is deliberately
		/// performed during the drag, rather than after completion, so a user can continue growing
		/// a form without driving the pointer beyond the tab's edge.</summary>
		void ScrollResizeHandleIntoView()
		{
			// Do not derive an offset from design coordinates here.  That used to scroll on the
			// first drag sample (even while the handle was already visible), and the resulting
			// ScrollViewer transform canceled the captured pointer's next relative movement.
			// Instead, inspect the actual rendered handle in the viewport and scroll only after
			// it reaches the visible edge.
			if (scroller.ViewportWidth <= 0 || scroller.ViewportHeight <= 0)
				return;
			var handle = resizeThumb.TranslatePoint(
				new Point(resizeThumb.ActualWidth / 2, resizeThumb.ActualHeight / 2), scroller);
			const double edgeMargin = 24;
			if (handle.X > scroller.ViewportWidth - edgeMargin)
				scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + handle.X - (scroller.ViewportWidth - edgeMargin));
			else if (handle.X < edgeMargin)
				scroller.ScrollToHorizontalOffset(Math.Max(0, scroller.HorizontalOffset + handle.X - edgeMargin));
			if (handle.Y > scroller.ViewportHeight - edgeMargin)
				scroller.ScrollToVerticalOffset(scroller.VerticalOffset + handle.Y - (scroller.ViewportHeight - edgeMargin));
			else if (handle.Y < edgeMargin)
				scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + handle.Y - edgeMargin));
		}

		void OnDragCompleted(object sender, DragCompletedEventArgs e)
		{
			SetSnapGuides(Array.Empty<(bool, double)>());
			if (selectedComponent == null || e.Canceled) return;
			if (!resizingDrag) {
				SelectionMoveRequested?.Invoke(this, new RemoteSelectionMoveEventArgs(
					(int)Math.Round(dragX - selectedComponent.SurfaceX), (int)Math.Round(dragY - selectedComponent.SurfaceY)));
				return;
			}
			BoundsChanged?.Invoke(this, new RemoteBoundsChangedEventArgs(selectedComponent.Name,
				selectedLocalX + (int)Math.Round(dragX - selectedComponent.SurfaceX),
				selectedLocalY + (int)Math.Round(dragY - selectedComponent.SurfaceY),
				(int)Math.Round(dragWidth), (int)Math.Round(dragHeight)));
		}

		void UpdateAdorners()
		{
			AutomationProperties.SetName(this, String.IsNullOrEmpty(selectedComponent?.AccessibleName)
				? selectedComponent?.Name ?? "WinForms designer" : selectedComponent.AccessibleName);
			AutomationProperties.SetHelpText(this, selectedComponent?.AccessibleDescription ?? "");
			var visible = selectedComponent != null;
			var isRoot = visible && String.IsNullOrEmpty(selectedComponent.Parent);
			resizeHitTarget.Visibility = resizeThumb.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
			moveThumb.Visibility = visible && !isRoot ? Visibility.Visible : Visibility.Collapsed;
			if (!visible) {
				adornerLayer.ClearSelection();
				return;
			}
			var locked = lockedComponentNames.Contains(selectedComponent.Name);
			moveThumb.IsEnabled = !locked;
			resizeHitTarget.IsEnabled = resizeThumb.IsEnabled = isRoot || !locked;
			adornerLayer.SelectionStroke = locked ? Brushes.DarkOrange : Brushes.DodgerBlue;
			dragX = selectedComponent.SurfaceX;
			dragY = selectedComponent.SurfaceY;
			selectedLocalX = selectedComponent.X;
			selectedLocalY = selectedComponent.Y;
			dragWidth = selectedComponent.Width;
			dragHeight = selectedComponent.Height;
			PositionAdorners();
			// A root form can be selected with its bottom-right edge exactly behind the
			// ScrollViewer's horizontal bar.  In that state no resize can start at all: the
			// scrollbar consumes the initial press before the Thumb can capture it.  Keep the
			// handle in the same safe viewport inset used while a resize is in progress.
			// This runs only after selection/layout, never between drag samples.
			ScrollResizeHandleIntoView();
		}

		void PositionAdorners()
		{
			renderedSelection = new Rect(dragX, dragY, dragWidth, dragHeight);
			adornerLayer.ShowSelection(renderedSelection, viewport);
			// Convert both design corners to surface coordinates so the move/resize handles
			// track the (possibly zoomed) design rect exactly.
			var (left, top) = viewport.DesignToSurface(dragX, dragY);
			var (right, bottom) = viewport.DesignToSurface(dragX + dragWidth, dragY + dragHeight);
			Canvas.SetLeft(moveThumb, left);
			Canvas.SetTop(moveThumb, top);
			moveThumb.Width = Math.Max(1, right - left);
			moveThumb.Height = Math.Max(1, bottom - top);
			Canvas.SetLeft(resizeThumb, right - resizeThumb.Width / 2);
			Canvas.SetTop(resizeThumb, bottom - resizeThumb.Height / 2);
			Canvas.SetLeft(resizeHitTarget, right - resizeHitTarget.Width / 2);
			Canvas.SetTop(resizeHitTarget, bottom - resizeHitTarget.Height / 2);
			Panel.SetZIndex(resizeHitTarget, 99);
			Panel.SetZIndex(resizeThumb, 100);
		}

		void OnDragOver(object sender, System.Windows.DragEventArgs e)
		{
			if (e.Data.GetDataPresent(typeof(ToolboxItem))) {
				e.Effects = System.Windows.DragDropEffects.Copy;
				e.Handled = true;
			}
		}

		async void OnDrop(object sender, System.Windows.DragEventArgs e)
		{
			if (e.Data.GetData(typeof(ToolboxItem)) is not ToolboxItem item || String.IsNullOrEmpty(item.TypeName))
				return;
			// GetPosition on the (possibly zoomed) frame image yields surface pixels; the
			// child's hit-testing and the drop position are design-space.
			var point = e.GetPosition(framePresenter.Visual);
			var designX = point.X / viewport.Scale;
			var designY = point.Y / viewport.Scale;
			var hit = await client.HitTestAsync(version, (int)designX, (int)designY, CancellationToken.None);
			var target = state.Components.FirstOrDefault(component => component.Name == hit.ComponentName);
			if (target != null && !IsContainer(target.Type))
				target = state.Components.FirstOrDefault(component => component.Name == target.Parent);
			target ??= state.Components.FirstOrDefault(component => String.IsNullOrEmpty(component.Parent));
			if (target != null)
				ToolboxDrop?.Invoke(this, new RemoteToolboxDropEventArgs(item.TypeName, target.Name,
					(int)designX - target.SurfaceX, (int)designY - target.SurfaceY));
			e.Handled = true;
		}

		static bool IsContainer(string type) => type == "System.Windows.Forms.Form"
			|| type == "System.Windows.Forms.Panel" || type == "System.Windows.Forms.GroupBox"
			|| type == "System.Windows.Forms.TabPage" || type == "System.Windows.Forms.UserControl";
	}

	sealed class RemoteToolboxDropEventArgs : EventArgs
	{
		public RemoteToolboxDropEventArgs(string controlType, string parentName, int x, int y)
		{
			ControlType = controlType;
			ParentName = parentName;
			X = x;
			Y = y;
		}

		public string ControlType { get; }
		public string ParentName { get; }
		public int X { get; }
		public int Y { get; }
	}

	sealed class RemoteBoundsChangedEventArgs : EventArgs
	{
		public RemoteBoundsChangedEventArgs(string componentName, int x, int y, int width, int height)
		{
			ComponentName = componentName;
			X = x;
			Y = y;
			Width = width;
			Height = height;
		}
		public string ComponentName { get; }
		public int X { get; }
		public int Y { get; }
		public int Width { get; }
		public int Height { get; }
	}

	sealed class RemoteSelectionMoveEventArgs : EventArgs
	{
		public RemoteSelectionMoveEventArgs(int deltaX, int deltaY) { DeltaX = deltaX; DeltaY = deltaY; }
		public int DeltaX { get; }
		public int DeltaY { get; }
	}

	sealed class RemoteComponentEventArgs : EventArgs
	{
		public RemoteComponentEventArgs(string componentName) => ComponentName = componentName;
		public string ComponentName { get; }
	}
}
