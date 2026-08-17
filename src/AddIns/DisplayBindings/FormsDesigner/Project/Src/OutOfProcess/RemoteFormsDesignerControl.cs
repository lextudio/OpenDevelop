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

namespace ICSharpCode.FormsDesigner.OutOfProcess
{
	sealed class RemoteFormsDesignerControl : Grid
	{
		readonly FormsDesignerHostClient client;
		readonly DesignFramePresenter framePresenter = new(Stretch.None,
			horizontalAlignment: HorizontalAlignment.Left, verticalAlignment: VerticalAlignment.Top);
		readonly Canvas adorners;
		readonly Canvas guides;
		readonly SelectionAdornerLayer adornerLayer = new(Array.Empty<string>(), Brushes.DodgerBlue, showLabel: false);
		readonly Rectangle marqueeBorder;
		readonly Thumb moveThumb;
		readonly Thumb resizeThumb;
		readonly Border disconnectedOverlay;
		readonly TextBlock disconnectedText;
		long version;
		DesignerSessionState state;
		long lastFrameSequence;
		// WinForms never scales or pans - this is always the identity case of the same
		// DesignViewport shape UnoDesignSurfaceControl uses for its zoom/pan math, so the two
		// backends' coordinate conversions share one type (see DesignViewport's doc comment).
		DesignViewport viewport = DesignViewport.Identity(0, 0);
		DesignerComponentInfo selectedComponent;
		double dragX;
		double dragY;
		double dragWidth;
		double dragHeight;
		int selectedLocalX;
		int selectedLocalY;
		bool showTabOrder;
		bool resizingDrag;
		bool marqueeSelecting;
		bool marqueeExtendsSelection;
		Point marqueeStart;
		readonly HashSet<string> selectedComponentNames = new HashSet<string>(StringComparer.Ordinal);
		readonly HashSet<string> lockedComponentNames = new HashSet<string>(StringComparer.Ordinal);

		public RemoteFormsDesignerControl(FormsDesignerHostClient client)
		{
			this.client = client;
			Background = Brushes.White;
			Focusable = true;
			Children.Add(framePresenter.Visual);
			guides = new Canvas { IsHitTestVisible = false };
			Children.Add(guides);
			adorners = new Canvas { IsHitTestVisible = true };
			marqueeBorder = new Rectangle {
				Stroke = Brushes.DodgerBlue, StrokeThickness = 1,
				Fill = new SolidColorBrush(Color.FromArgb(35, 30, 144, 255)),
				StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false,
				Visibility = Visibility.Collapsed
			};
			moveThumb = new Thumb { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Visibility = Visibility.Collapsed };
			resizeThumb = new Thumb { Width = 8, Height = 8, Background = Brushes.White, BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(1), Cursor = Cursors.SizeNWSE, Visibility = Visibility.Collapsed };
			adorners.Children.Add(marqueeBorder);
			adorners.Children.Add(adornerLayer.Visual);
			adorners.Children.Add(moveThumb);
			adorners.Children.Add(resizeThumb);
			Children.Add(adorners);
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
			Children.Add(disconnectedOverlay);
			AllowDrop = true;
			MouseLeftButtonDown += OnMouseLeftButtonDown;
			MouseMove += OnMouseMove;
			MouseLeftButtonUp += OnMouseLeftButtonUp;
			moveThumb.DragStarted += OnDragStarted;
			moveThumb.DragDelta += OnMoveDragDelta;
			moveThumb.DragCompleted += OnDragCompleted;
			resizeThumb.DragStarted += OnDragStarted;
			resizeThumb.DragDelta += OnResizeDragDelta;
			resizeThumb.DragCompleted += OnDragCompleted;
			DragOver += OnDragOver;
			Drop += OnDrop;
			KeyDown += OnKeyDown;
		}

		public string SelectedComponentName { get; private set; } = "";
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
			if (state.Render == null || String.IsNullOrEmpty(state.Render.PngBase64)) return;
			if (state.Render.Sequence > 0 && state.Render.Sequence <= lastFrameSequence) return;
			lastFrameSequence = state.Render.Sequence;
			var dpi = Math.Max(1, state.Render.Dpi);
			viewport = DesignViewport.Identity(state.Render.Width / dpi, state.Render.Height / dpi);
			var bitmap = new BitmapImage();
			using (var stream = new MemoryStream(Convert.FromBase64String(state.Render.PngBase64))) {
				bitmap.BeginInit();
				bitmap.CacheOption = BitmapCacheOption.OnLoad;
				bitmap.StreamSource = stream;
				bitmap.EndInit();
				bitmap.Freeze();
			}
			framePresenter.SetSource(bitmap);
			framePresenter.Resize(viewport);
			UpdateDesignGuides();
			if (!String.IsNullOrEmpty(SelectedComponentName)) {
				selectedComponent = state.Components.FirstOrDefault(item => item.Name == SelectedComponentName);
				selectedComponentNames.RemoveWhere(name => !state.Components.Any(item => item.Name == name));
				lockedComponentNames.RemoveWhere(name => !state.Components.Any(item => item.Name == name));
				UpdateAdorners();
			}
			AutomationProperties.SetName(this, selectedComponent?.AccessibleName ?? "WinForms designer");
			AutomationProperties.SetHelpText(this, selectedComponent?.AccessibleDescription ?? "");
		}

		void UpdateDesignGuides()
		{
			guides.Children.Clear();
			if (state?.Render == null) return;
			guides.Children.Add(new Rectangle {
				Width = state.Render.Width, Height = state.Render.Height,
				Stroke = Brushes.Gray, StrokeThickness = 1
			});
			foreach (var component in state.Components.Where(item => !String.IsNullOrEmpty(item.Parent))) {
				var (surfaceX, surfaceY) = viewport.DesignToSurface(component.SurfaceX, component.SurfaceY);
				var outline = new Rectangle {
					Width = Math.Max(1, component.Width), Height = Math.Max(1, component.Height),
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
			moveThumb.Visibility = resizeThumb.Visibility = Visibility.Collapsed;
		}

		public bool TryGetComponentScreenBounds(string componentName, out Rect bounds)
		{
			bounds = Rect.Empty;
			var component = state?.Components?.FirstOrDefault(item => item.Name == componentName);
			if (component == null || !framePresenter.Visual.IsVisible)
				return false;
			var topLeft = framePresenter.Visual.PointToScreen(new Point(component.SurfaceX, component.SurfaceY));
			bounds = new Rect(topLeft.X, topLeft.Y, component.Width, component.Height);
			return true;
		}

		async void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			try {
				var extendSelection = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
				var point = e.GetPosition(framePresenter.Visual);
				if (!state.Components.Any(component => !String.IsNullOrEmpty(component.Parent)
					&& new Rect(component.SurfaceX, component.SurfaceY, component.Width, component.Height).Contains(point))) {
					marqueeSelecting = true;
					marqueeExtendsSelection = extendSelection;
					marqueeStart = point;
					marqueeBorder.Width = marqueeBorder.Height = 0;
					Canvas.SetLeft(marqueeBorder, point.X);
					Canvas.SetTop(marqueeBorder, point.Y);
					marqueeBorder.Visibility = Visibility.Visible;
					CaptureMouse();
					e.Handled = true;
					return;
				}
				var result = await client.HitTestAsync(version, (int)point.X, (int)point.Y, CancellationToken.None);
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
			var point = e.GetPosition(framePresenter.Visual);
			var left = Math.Min(marqueeStart.X, point.X);
			var top = Math.Min(marqueeStart.Y, point.Y);
			Canvas.SetLeft(marqueeBorder, left);
			Canvas.SetTop(marqueeBorder, top);
			marqueeBorder.Width = Math.Abs(point.X - marqueeStart.X);
			marqueeBorder.Height = Math.Abs(point.Y - marqueeStart.Y);
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
					var componentBounds = new Rect(component.SurfaceX, component.SurfaceY, component.Width, component.Height);
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
			resizingDrag = ReferenceEquals(sender, resizeThumb);
			dragX = selectedComponent.SurfaceX;
			dragY = selectedComponent.SurfaceY;
			selectedLocalX = selectedComponent.X;
			selectedLocalY = selectedComponent.Y;
			dragWidth = selectedComponent.Width;
			dragHeight = selectedComponent.Height;
		}

		void OnMoveDragDelta(object sender, DragDeltaEventArgs e)
		{
			dragX = Math.Max(0, dragX + e.HorizontalChange);
			dragY = Math.Max(0, dragY + e.VerticalChange);
			PositionAdorners();
		}

		void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
		{
			dragWidth = Math.Max(8, dragWidth + e.HorizontalChange);
			dragHeight = Math.Max(8, dragHeight + e.VerticalChange);
			PositionAdorners();
		}

		void OnDragCompleted(object sender, DragCompletedEventArgs e)
		{
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
			resizeThumb.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
			moveThumb.Visibility = visible && !isRoot ? Visibility.Visible : Visibility.Collapsed;
			if (!visible) {
				adornerLayer.ClearSelection();
				return;
			}
			var locked = lockedComponentNames.Contains(selectedComponent.Name);
			moveThumb.IsEnabled = !locked;
			resizeThumb.IsEnabled = isRoot || !locked;
			adornerLayer.SelectionStroke = locked ? Brushes.DarkOrange : Brushes.DodgerBlue;
			dragX = selectedComponent.SurfaceX;
			dragY = selectedComponent.SurfaceY;
			selectedLocalX = selectedComponent.X;
			selectedLocalY = selectedComponent.Y;
			dragWidth = selectedComponent.Width;
			dragHeight = selectedComponent.Height;
			PositionAdorners();
		}

		void PositionAdorners()
		{
			adornerLayer.ShowSelection(new Rect(dragX, dragY, dragWidth, dragHeight), viewport);
			var (left, top) = viewport.DesignToSurface(dragX, dragY);
			Canvas.SetLeft(moveThumb, left);
			Canvas.SetTop(moveThumb, top);
			moveThumb.Width = dragWidth;
			moveThumb.Height = dragHeight;
			Canvas.SetLeft(resizeThumb, left + dragWidth - resizeThumb.Width / 2);
			Canvas.SetTop(resizeThumb, top + dragHeight - resizeThumb.Height / 2);
			Panel.SetZIndex(resizeThumb, 2);
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
			var point = e.GetPosition(framePresenter.Visual);
			var hit = await client.HitTestAsync(version, (int)point.X, (int)point.Y, CancellationToken.None);
			var target = state.Components.FirstOrDefault(component => component.Name == hit.ComponentName);
			if (target != null && !IsContainer(target.Type))
				target = state.Components.FirstOrDefault(component => component.Name == target.Parent);
			target ??= state.Components.FirstOrDefault(component => String.IsNullOrEmpty(component.Parent));
			if (target != null)
				ToolboxDrop?.Invoke(this, new RemoteToolboxDropEventArgs(item.TypeName, target.Name,
					(int)point.X - target.SurfaceX, (int)point.Y - target.SurfaceY));
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
