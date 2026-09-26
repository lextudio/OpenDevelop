// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.ILSpy.ViewModels;

namespace ICSharpCode.SharpDevelop.Workbench
{
	sealed class AvalonWorkbenchWindow : PaneModel, IWorkbenchWindow, IOwnerState
	{
		AvalonDockLayout dockLayout;
		ImageSource icon;
		object content;
		object toolTip;

		public AvalonWorkbenchWindow(AvalonDockLayout dockLayout)
		{
			if (dockLayout == null)
				throw new ArgumentNullException("dockLayout");

			this.dockLayout = dockLayout;
			viewContents = new ViewContentCollection(this);

			SD.ResourceService.LanguageChanged += OnTabPageTextChanged;

			PropertyChanged += AvalonWorkbenchWindow_PropertyChanged;
		}

		void AvalonWorkbenchWindow_PropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(IsActive))
				OnIsActiveChanged();
		}

		void OnIsActiveChanged()
		{
			if (!IsActive)
				return;
			IViewContent vc = ActiveViewContent;
			if (vc != null)
				SetFocus(() => vc.InitiallyFocusedControl as IInputElement);
		}

		internal static void SetFocus(Func<IInputElement> activeChildFunc)
		{
			_ = Application.Current?.Dispatcher.BeginInvoke(
				DispatcherPriority.Loaded,
				new Action(
					delegate {
						IInputElement activeChild = activeChildFunc();
						if (activeChild != null) {
							Keyboard.Focus(activeChild);
						}
					}));
		}

		public bool IsDisposed { get { return false; } }

		public ImageSource Icon {
			get { return icon; }
			set { SetProperty(ref icon, value); }
		}

		public object Content {
			get { return content; }
			private set { SetProperty(ref content, value); }
		}

		public object ToolTip {
			get { return toolTip; }
			private set { SetProperty(ref toolTip, value); }
		}

		#region IOwnerState
		[Flags]
		public enum OpenFileTabStates {
			Nothing             = 0,
			FileDirty           = 1,
			FileReadOnly        = 2,
			FileUntitled        = 4,
			ViewContentWithoutFile = 8
		}

		public System.Enum InternalState {
			get {
				IViewContent content = this.ActiveViewContent;
				OpenFileTabStates state = OpenFileTabStates.Nothing;
				if (content != null) {
					if (content.IsDirty)
						state |= OpenFileTabStates.FileDirty;
					if (content.IsReadOnly)
						state |= OpenFileTabStates.FileReadOnly;
					if (content.PrimaryFile != null && content.PrimaryFile.IsUntitled)
						state |= OpenFileTabStates.FileUntitled;
					if (content.PrimaryFile == null)
						state |= OpenFileTabStates.ViewContentWithoutFile;
				}
				return state;
			}
		}
		#endregion

		TabControl viewTabControl;

		// Side-by-side layout (spike): instead of a TabControl switching between the primary
		// (source) and secondary (designer) views, host both in a Grid divided by a GridSplitter
		// — Visual Studio's designer-on-top / XAML-below arrangement. Once both panes are visibly
		// parented, both views are initialized without changing the file's active/save view.
		Grid splitHost;
		ContentControl splitTopHost;
		ContentControl splitBottomHost;
		FrameworkElement splitBar;
		bool splitActive;
		int splitActiveIndex;
		// Split-bar state, changed by the little VS-style buttons on the bar.
		bool splitHorizontal = true;   // stacked (designer above source) vs side by side
		bool splitSwapped;             // exchange the two panes' positions
		Window splitFloatWindow;       // non-null while the source is popped out into its own window

		/// <summary>
		/// Whether the secondary view is shown side by side with the primary instead of as a tab.
		/// Spike switch: set <c>OD_XAML_SPLIT=1</c> in the environment, or toggle live with the
		/// DevFlow action <c>od.editor.toggle-split</c>. Off by default: the save authority and
		/// live edit sync are not yet split-aware, so this is for evaluating the layout only.
		/// </summary>
		public static bool SplitViewEnabled { get; set; } =
			Environment.GetEnvironmentVariable("OD_XAML_SPLIT") == "1";

		/// <summary>Turns the side-by-side layout on/off for this window (needs two views).</summary>
		public void SetSplitView(bool enabled)
		{
			SplitViewEnabled = enabled;
			if (!enabled && splitFloatWindow != null) {
				splitFloatWindow.Close();   // re-docks through its Closed handler
				return;
			}
			RebuildContent();
			UpdateActiveViewContent();
		}

		// Probes for the DevFlow action od.editor.split-status / integration tests.
		internal bool SplitViewActive => splitActive && splitHost != null;
		internal bool SplitViewFloating => splitFloatWindow != null;
		internal bool SplitViewHorizontal => splitHorizontal;
		internal int SplitViewIndex => splitActiveIndex;
		internal FrameworkElement SplitViewBar => splitBar;
		internal ContentControl SplitViewTopHost => splitTopHost;
		internal ContentControl SplitViewBottomHost => splitBottomHost;

		/// <summary>
		/// The current view content which is shown inside this window.
		/// </summary>
		public IViewContent ActiveViewContent {
			get {
				SD.MainThread.VerifyAccess();
				if (splitActive && splitHost != null
				    && splitActiveIndex >= 0 && splitActiveIndex < ViewContents.Count) {
					return ViewContents[splitActiveIndex];
				}
				if (viewTabControl != null && viewTabControl.SelectedIndex >= 0 && viewTabControl.SelectedIndex < ViewContents.Count) {
					return ViewContents[viewTabControl.SelectedIndex];
				} else if (ViewContents.Count == 1) {
					return ViewContents[0];
				} else {
					return null;
				}
			}
			set {
				int pos = ViewContents.IndexOf(value);
				if (pos < 0)
					throw new ArgumentException();
				SwitchView(pos);
			}
		}

		public event EventHandler ActiveViewContentChanged;

		IViewContent oldActiveViewContent;

		void UpdateActiveViewContent()
		{
			UpdateTitleAndInfoTip();

			IViewContent newActiveViewContent = this.ActiveViewContent;

			if (oldActiveViewContent != newActiveViewContent && ActiveViewContentChanged != null) {
				ActiveViewContentChanged(this, EventArgs.Empty);
			}
			oldActiveViewContent = newActiveViewContent;
			CommandManager.InvalidateRequerySuggested();
		}

		sealed class ViewContentCollection : Collection<IViewContent>
		{
			readonly AvalonWorkbenchWindow window;

			internal ViewContentCollection(AvalonWorkbenchWindow window)
			{
				this.window = window;
			}

			protected override void ClearItems()
			{
				foreach (IViewContent vc in this) {
					window.UnregisterContent(vc);
				}

				base.ClearItems();
				window.RebuildContent();
				window.UpdateActiveViewContent();
			}

			protected override void InsertItem(int index, IViewContent item)
			{
				base.InsertItem(index, item);

				window.RegisterNewContent(item);
				window.RebuildContent();
				window.UpdateActiveViewContent();
			}

			protected override void RemoveItem(int index)
			{
				window.UnregisterContent(this[index]);

				base.RemoveItem(index);

				window.RebuildContent();
				window.UpdateActiveViewContent();
			}

			protected override void SetItem(int index, IViewContent item)
			{
				window.UnregisterContent(this[index]);

				base.SetItem(index, item);

				window.RegisterNewContent(item);
				window.RebuildContent();
				window.UpdateActiveViewContent();
			}
		}

		readonly ViewContentCollection viewContents;

		public IList<IViewContent> ViewContents {
			get { return viewContents; }
		}

		/// <summary>
		/// Gets whether any contained view content has changed
		/// since the last save/load operation.
		/// </summary>
		public bool IsDirty {
			get { return this.ViewContents.Any(vc => vc.IsDirty); }
		}

		public void SwitchView(int viewNumber)
		{
			if (splitActive && splitHost != null) {
				if (viewNumber < 0 || viewNumber >= ViewContents.Count)
					return;
				splitActiveIndex = viewNumber;
				UpdateActiveViewContent();

				IViewContent splitView = this.ActiveViewContent;
				if (splitView != null && this.IsActive)
					SetFocus(() => splitView.InitiallyFocusedControl as IInputElement);
				return;
			}
			if (viewTabControl != null) {
				this.viewTabControl.SelectedIndex = viewNumber;
				this.viewTabControl.UpdateLayout();

				IViewContent vc = this.ActiveViewContent;
				if (vc != null && this.IsActive)
					SetFocus(() => vc.InitiallyFocusedControl as IInputElement);
			}
		}

		public void SelectWindow()
		{
			this.IsSelected = true;
			this.IsActive = true;
		}

		void Dispose()
		{
			SD.ResourceService.LanguageChanged -= OnTabPageTextChanged;
			// DetachContent must be called before the controls are disposed
			List<IViewContent> viewContents = this.ViewContents.ToList();
			this.ViewContents.Clear();
			viewContents.ForEach(vc => vc.Dispose());
		}

		sealed class TabControlWithModifiedShortcuts : TabControl
		{
			readonly AvalonWorkbenchWindow parentWindow;

			public TabControlWithModifiedShortcuts(AvalonWorkbenchWindow parentWindow)
			{
				this.parentWindow = parentWindow;
				// LibreWPF's implicit-style lookup does not walk BaseType, so this TabControl
				// subclass never picks up the semantic theme's implicit TabControl style and
				// falls back to the Aero2 chrome (white background, light border). Assign the
				// theme brushes directly (SetResourceReference is unreliable here) and re-apply
				// them on IDE theme switches.
				ApplyThemeBrushes();
				IdeThemeService.ThemeChanged += OnIdeThemeChanged;
			}

			void OnIdeThemeChanged(object sender, string theme) => ApplyThemeBrushes();

			void ApplyThemeBrushes()
			{
				if (Application.Current == null)
					return;
				if (Application.Current.TryFindResource("ToolWindowBackground") is Brush background)
					Background = background;
				if (Application.Current.TryFindResource("Border") is Brush border)
					BorderBrush = border;
			}

			protected override void OnKeyDown(KeyEventArgs e)
			{
				// We don't call base.KeyDown to prevent the TabControl from handling Ctrl+Tab.
				// Instead, we let the key press bubble up to the DocumentPane.
			}

			protected override void OnPreviewKeyDown(KeyEventArgs e)
			{
				base.OnPreviewKeyDown(e);
				if (e.Handled)
					return;

				// However, we do want to handle Ctrl+PgUp / Ctrl+PgDown (SD-1735)
				if ((e.Key == Key.PageUp || e.Key == Key.PageDown) && e.KeyboardDevice.Modifiers == ModifierKeys.Control) {
					int index = this.SelectedIndex;
					if (e.Key == Key.PageUp) {
						if (++index >= this.Items.Count)
							index = 0;
					} else {
						if (--index < 0)
							index = this.Items.Count - 1;
					}
					parentWindow.SwitchView(index);

					e.Handled = true;
				}
			}
		}

		/// <summary>
		/// (Re)builds the window body from the current view contents: a bare control for a single
		/// view, the tab control, or the side-by-side split host. Rebuilds from scratch so a view
		/// control is never parented in two places when the layout mode changes.
		/// </summary>
		void RebuildContent()
		{
			int previousActive = splitActive ? splitActiveIndex
				: (viewTabControl != null ? viewTabControl.SelectedIndex : 0);

			DetachContentHosts();

			if (ViewContents.Count == 0) {
				this.Content = null;
				return;
			}
			if (ViewContents.Count == 1) {
				splitActive = false;
				splitActiveIndex = 0;
				this.Content = ViewContents[0].Control;
				return;
			}

			// The source pane is floating in its own window; the document shows the designer alone.
			if (splitFloatWindow != null) {
				splitActive = false;
				this.Content = ViewContents[ViewContents.Count - 1].Control;
				return;
			}

			// Every visual designer gets the same split chrome. Most current designers already
			// expose a WPF surface, while legacy WinForms designers are wrapped by
			// IWinFormsService in CreateSplitPane; do not silently send either class back to tabs.
			splitActive = SplitViewEnabled;
			if (splitActive) {
				splitActiveIndex = Math.Min(Math.Max(previousActive, 0), ViewContents.Count - 1);
				BuildSplitContent();
			} else {
				BuildTabContent(previousActive);
			}
		}

		void DetachContentHosts()
		{
			this.Content = null;

			if (viewTabControl != null) {
				foreach (TabItem page in viewTabControl.Items) {
					page.Content = null;
				}
				viewTabControl.Items.Clear();
				viewTabControl = null;
			}
			if (splitHost != null) {
				if (splitTopHost != null) splitTopHost.Content = null;
				if (splitBottomHost != null) splitBottomHost.Content = null;
				splitHost.Children.Clear();
				splitHost = null;
				splitTopHost = null;
				splitBottomHost = null;
				splitBar = null;
			}
		}

		void BuildTabContent(int selectedIndex)
		{
			viewTabControl = new TabControlWithModifiedShortcuts(this);
			viewTabControl.TabStripPlacement = System.Windows.Controls.Dock.Bottom;
			foreach (IViewContent vc in ViewContents) {
				viewTabControl.Items.Add(new TabItem {
					Header = StringParser.Parse(vc.TabPageText),
					Content = vc.Control
				});
			}
			if (selectedIndex >= 0 && selectedIndex < viewTabControl.Items.Count)
				viewTabControl.SelectedIndex = selectedIndex;

			viewTabControl.SelectionChanged += delegate {
				UpdateActiveViewContent();
			};
			this.Content = viewTabControl;
		}

		void BuildSplitContent()
		{
			// Visual Studio's convention: the designer (secondary view, added last) starts above
			// the XAML source; the split-bar buttons can swap them or switch to side by side.
			int designerIndex = ViewContents.Count - 1;
			const int sourceIndex = 0;
			int firstIndex = splitSwapped ? sourceIndex : designerIndex;
			int secondIndex = splitSwapped ? designerIndex : sourceIndex;

			splitHost = new Grid();
			splitTopHost = CreateSplitPane(ViewContents[firstIndex], firstIndex);
			splitBottomHost = CreateSplitPane(ViewContents[secondIndex], secondIndex);
			PrepareVisibleSplitView(ViewContents[firstIndex]);
			PrepareVisibleSplitView(ViewContents[secondIndex]);
			splitBar = BuildSplitBar();

			if (splitHorizontal) {
				splitHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
				splitHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				splitHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
				Grid.SetRow(splitTopHost, 0);
				Grid.SetRow(splitBar, 1);
				Grid.SetRow(splitBottomHost, 2);
			} else {
				splitHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				splitHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				splitHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				Grid.SetColumn(splitTopHost, 0);
				Grid.SetColumn(splitBar, 1);
				Grid.SetColumn(splitBottomHost, 2);
			}

			splitHost.Children.Add(splitTopHost);
			splitHost.Children.Add(splitBar);
			splitHost.Children.Add(splitBottomHost);

			this.Content = splitHost;
			InitializeVisibleSplitViews(firstIndex, secondIndex);
		}

		void PrepareVisibleSplitView(IViewContent view)
		{
			// This is deliberately synchronous and runs before the ContentPresenter reaches the
			// visual tree. Scheduling it at Loaded leaves one compositor frame where a new designer
			// pane is entirely blank, which looks indistinguishable from a failed load.
			if (view is AbstractViewContentHandlingLoadErrors delayedView)
				delayedView.ShowLoadingPlaceholder("Loading design view…");
		}

		/// <summary>
		/// A tab view is initialized by becoming <see cref="ActiveViewContent"/>. That is not
		/// sufficient in split mode: the inactive pane is already on screen and must render before
		/// its first click. OpenedFile.ForceInitializeView deliberately loads that view without
		/// switching the file's current/save owner, preserving the normal editor authority.
		/// </summary>
		void InitializeVisibleSplitViews(int firstIndex, int secondIndex)
		{
			Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
				if (!splitActive || splitHost == null)
					return;
				foreach (int index in new[] { firstIndex, secondIndex }.Distinct()) {
					if (index >= 0 && index < ViewContents.Count) {
						IViewContent view = ViewContents[index];
						view.PrimaryFile?.ForceInitializeView(view);
					}
				}
			}));
		}

		/// <summary>
		/// Visual Studio-style design/source split chrome. Each tab opens toward the pane it names;
		/// the swap glyph belongs between those tabs. The resize lane and the remaining commands are
		/// deliberately separate, so the relationship stays intact in either orientation.
		/// </summary>
		FrameworkElement BuildSplitBar()
		{
			var bar = new Grid { Background = ThemeBrush("ToolWindowBackground", Brushes.Transparent) };
			if (splitHorizontal) {
				bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			} else {
				bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				bar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
				bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			}

			int designerIndex = ViewContents.Count - 1;
			const int sourceIndex = 0;
			int firstIndex = splitSwapped ? sourceIndex : designerIndex;
			int secondIndex = splitSwapped ? designerIndex : sourceIndex;
			splitLabelHosts.Clear();

			var tabs = new StackPanel {
				Orientation = splitHorizontal ? Orientation.Horizontal : Orientation.Vertical,
				VerticalAlignment = VerticalAlignment.Stretch,
				HorizontalAlignment = HorizontalAlignment.Stretch
			};
			tabs.Children.Add(CreateSplitTab(ViewContents[firstIndex], firstIndex, facesFirstPane: true));
			tabs.Children.Add(CreateSplitButton("Swap the designer and source panes",
				"SwitchSourceOrTarget", SwapSplitPanes));
			tabs.Children.Add(CreateSplitTab(ViewContents[secondIndex], secondIndex, facesFirstPane: false));
			Place(bar, tabs, 0);

			// The splitter owns only the intentionally empty centre lane. The surrounding chrome is
			// still clickable, while resizing cannot accidentally invoke one of the commands.
			var splitter = new GridSplitter {
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Stretch,
				ResizeDirection = splitHorizontal ? GridResizeDirection.Rows : GridResizeDirection.Columns,
				ResizeBehavior = GridResizeBehavior.PreviousAndNext,
				Background = Brushes.Transparent,
				Cursor = splitHorizontal ? Cursors.SizeNS : Cursors.SizeWE,
				ToolTip = "Drag to resize the Design and XAML views"
			};
			Place(bar, splitter, 1);

			var commands = new StackPanel {
				Orientation = splitHorizontal ? Orientation.Horizontal : Orientation.Vertical,
				HorizontalAlignment = splitHorizontal ? HorizontalAlignment.Right : HorizontalAlignment.Center,
				VerticalAlignment = splitHorizontal ? VerticalAlignment.Center : VerticalAlignment.Bottom,
				Margin = splitHorizontal ? new Thickness(2, 1, 3, 1) : new Thickness(1, 2, 1, 3)
			};
			commands.Children.Add(new Border {
				Background = ThemeBrush("Border", Brushes.Gray),
				Width = splitHorizontal ? 1 : double.NaN,
				Height = splitHorizontal ? double.NaN : 1,
				Margin = splitHorizontal ? new Thickness(2, 2, 3, 2) : new Thickness(2, 2, 2, 3)
			});
			commands.Children.Add(CreateSplitButton(
				splitHorizontal ? "Switch to a side-by-side (vertical) split" : "Switch to a stacked (horizontal) split",
				splitHorizontal ? "SplitScreenVertically" : "SplitScreenHorizontally", ToggleSplitOrientation));
			commands.Children.Add(CreateSplitButton("Move the source to a separate window", "PopOut", PopOutSourcePane));
			commands.Children.Add(CreateSplitButton("Close the split (back to tabs)", "Close", () => SetSplitView(false)));
			Place(bar, commands, 2);
			RefreshSplitLabels();

			if (splitHorizontal)
				bar.Height = 22;
			else
				bar.Width = 22;
			return bar;
		}

		static void Place(Grid grid, UIElement child, int index)
		{
			if (grid.ColumnDefinitions.Count > 0)
				Grid.SetColumn(child, index);
			else
				Grid.SetRow(child, index);
			grid.Children.Add(child);
		}

		readonly List<(Border Tab, TextBlock Label, int Index, bool FacesFirstPane)> splitLabelHosts
			= new List<(Border, TextBlock, int, bool)>();

		Border CreateSplitTab(IViewContent view, int index, bool facesFirstPane)
		{
			string text = StringParser.Parse(view.TabPageText);
			string extension = view.PrimaryFile != null ? Path.GetExtension(view.PrimaryFile.FileName.ToString()) : null;
			if (index == 0 && string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase))
				text = "XAML";
			var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
			// A side-by-side split has a 22px vertical rail. Rotate the tab text so it measures
			// along that rail instead of widening the divider or clipping the view name.
			if (!splitHorizontal)
				label.LayoutTransform = new RotateTransform(-90);
			var tab = new Border {
				Child = label,
				Padding = splitHorizontal ? new Thickness(9, 0, 9, 0) : new Thickness(0, 9, 0, 9),
				Cursor = Cursors.Hand,
				BorderBrush = ThemeBrush("Border", Brushes.Gray),
				BorderThickness = splitHorizontal ? new Thickness(0, 0, 1, 0) : new Thickness(0, 0, 0, 1)
			};
			tab.MouseLeftButtonDown += delegate { ActivateSplitPane(index); };
			splitLabelHosts.Add((tab, label, index, facesFirstPane));
			return tab;
		}

		void RefreshSplitLabels()
		{
			Brush activeBackground = ThemeBrush("WindowBackground", Brushes.White);
			Brush activeForeground = ThemeBrush("Foreground", Brushes.Black);
			Brush inactiveForeground = ThemeBrush("MutedForeground", Brushes.Gray);
			foreach (var (tab, label, index, facesFirstPane) in splitLabelHosts) {
				bool active = index == splitActiveIndex;
				tab.Background = active ? activeBackground : Brushes.Transparent;
				label.Foreground = active ? activeForeground : inactiveForeground;
				// The open edge always faces the corresponding pane: top/bottom when stacked,
				// left/right when side-by-side. This must follow the views when they are swapped.
				tab.BorderThickness = SplitTabBorder(facesFirstPane);
			}
		}

		Thickness SplitTabBorder(bool facesFirstPane)
		{
			if (splitHorizontal)
				return facesFirstPane ? new Thickness(1, 0, 1, 1) : new Thickness(1, 1, 1, 0);
			return facesFirstPane ? new Thickness(0, 1, 1, 1) : new Thickness(1, 1, 0, 1);
		}

		Button CreateSplitButton(string tooltip, string iconName, Action action)
		{
			var button = new Button {
				Width = 18,
				Height = 18,
				Padding = new Thickness(0),
				Margin = new Thickness(1),
				BorderThickness = new Thickness(0),
				Background = Brushes.Transparent,
				Focusable = false,
				ToolTip = tooltip,
				Content = new Image {
					Source = PresentationResourceService.GetImageSource("Icons.16x16." + iconName),
					Width = 14,
					Height = 14,
					Stretch = Stretch.Uniform
				}
			};
			var style = new Style(typeof(Button));
			style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
			style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
			var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
			hover.Setters.Add(new Setter(Control.BackgroundProperty, ThemeBrush("Border", Brushes.LightGray)));
			style.Triggers.Add(hover);
			button.Style = style;
			button.Click += delegate { action(); };
			return button;
		}

		static Brush ThemeBrush(string key, Brush fallback)
		{
			if (Application.Current != null && Application.Current.TryFindResource(key) is Brush brush)
				return brush;
			return fallback;
		}

		internal void ToggleSplitOrientation()
		{
			splitHorizontal = !splitHorizontal;
			RebuildContent();
		}

		internal void SwapSplitPanes()
		{
			splitSwapped = !splitSwapped;
			RebuildContent();
		}

		internal void PopOutSourcePane()
		{
			if (splitFloatWindow != null) {
				splitFloatWindow.Activate();
				return;
			}
			// The source is ViewContents[0]; whichever host currently holds it is detached and
			// re-parented into a floating window.
			object sourceControl = ViewContents[0].Control;
			ContentControl host = ReferenceEquals(splitTopHost?.Content, sourceControl) ? splitTopHost
				: ReferenceEquals(splitBottomHost?.Content, sourceControl) ? splitBottomHost
				: null;
			if (host == null)
				return;
			host.Content = null;

			var window = new Window {
				Title = ViewContents[0].TitleName,
				Width = 720,
				Height = 520,
				Owner = Application.Current != null ? Application.Current.MainWindow : null,
				Content = sourceControl,
				ShowInTaskbar = true
			};
			splitFloatWindow = window;
			window.Closed += delegate {
				window.Content = null;
				splitFloatWindow = null;
				RebuildContent();
			};
			window.Show();
			RebuildContent();   // the document now shows the designer pane alone
		}

		ContentControl CreateSplitPane(IViewContent view, int index)
		{
			var pane = new ContentControl();
			if (view.Control is UIElement)
				pane.Content = view.Control;
			else
				SD.WinForms.SetContent(pane, view.Control, view);
			// The window's active view drives the designer's lazy Load (OpenedFile.SwitchedToView)
			// and the save owner, so clicking or focusing a pane activates its view — the same
			// contract the tabs use, just without a tab switch.
			pane.PreviewMouseDown += delegate { ActivateSplitPane(index); };
			pane.GotKeyboardFocus += delegate { ActivateSplitPane(index); };
			return pane;
		}

		void ActivateSplitPane(int index)
		{
			if (!splitActive || splitActiveIndex == index)
				return;
			splitActiveIndex = index;
			RefreshSplitLabels();
			UpdateActiveViewContent();
		}

		void OnTitleNameChanged(object sender, EventArgs e)
		{
			if (sender == ActiveViewContent) {
				UpdateTitle();
			}
		}

		void OnInfoTipChanged(object sender, EventArgs e)
		{
			if (sender == ActiveViewContent) {
				UpdateInfoTip();
			}
		}

		void OnIsDirtyChanged(object sender, EventArgs e)
		{
			UpdateTitle();
			CommandManager.InvalidateRequerySuggested();
		}

		void UpdateTitleAndInfoTip()
		{
			UpdateInfoTip();
			UpdateTitle();
		}

		void UpdateInfoTip()
		{
			IViewContent content = ActiveViewContent;
			if (content != null) {
				string newInfoTip = content.InfoTip;
				if (!Equals(newInfoTip, this.ToolTip)) {
					this.ToolTip = newInfoTip;
					OnInfoTipChanged();
				}
			}
		}

		void UpdateTitle()
		{
			IViewContent content = ActiveViewContent;
			if (content != null) {
				string newTitle = content.TitleName;
				if (content.IsDirty)
					newTitle += "*";
				if (newTitle != Title) {
					Title = newTitle;
					OnTitleChanged();
				}
			}
		}

		void RegisterNewContent(IViewContent content)
		{
			Debug.Assert(content.WorkbenchWindow == null);
			content.WorkbenchWindow = this;

			content.TabPageTextChanged += OnTabPageTextChanged;
			content.TitleNameChanged += OnTitleNameChanged;
			content.InfoTipChanged += OnInfoTipChanged;
			content.IsDirtyChanged += OnIsDirtyChanged;

			this.dockLayout.Workbench.OnViewOpened(new ViewContentEventArgs(content));
		}

		void UnregisterContent(IViewContent content)
		{
			content.WorkbenchWindow = null;

			content.TabPageTextChanged -= OnTabPageTextChanged;
			content.TitleNameChanged -= OnTitleNameChanged;
			content.InfoTipChanged -= OnInfoTipChanged;
			content.IsDirtyChanged -= OnIsDirtyChanged;

			this.dockLayout.Workbench.OnViewClosed(new ViewContentEventArgs(content));
		}

		void OnTabPageTextChanged(object sender, EventArgs e)
		{
			RefreshTabPageTexts();
		}

		bool forceClose;

		public bool CloseWindow(bool force)
		{
			SD.MainThread.VerifyAccess();

			forceClose = force;
			var args = new CancelEventArgs();
			OnClosingEvent(this, args);
			if (!args.Cancel)
				OnClosedEvent(this, EventArgs.Empty);
			return this.ViewContents.Count == 0;
		}

		void OnClosingEvent(object sender, CancelEventArgs e)
		{
			if (!e.Cancel && !forceClose && this.IsDirty) {
				// This prompt bypasses IMessageService (which suppresses dialogs in test mode
				// centrally), so it needs its own check or an integration-test run hangs here with
				// nobody to click it. "No" is the safe default: the close still proceeds (Cancel
				// would block teardown, and reopening a solution later), and nothing is written to
				// disk, so a test never silently saves over a repository fixture. It also avoids
				// the Yes branch below, whose "while (vc.IsDirty)" retry loop would spin forever
				// once its DiscardChanges AskQuestion starts auto-answering No in test mode.
				MessageBoxResult dr;
				if (TestMode.IsActive) {
					dr = MessageBoxResult.No;
					LoggingService.Info("OD_TEST_MODE: suppressed \"save changes?\" prompt for " + Title + ", auto-answered No (close without saving)");
				} else {
					dr = MessageBox.Show(
						ResourceService.GetString("MainWindow.SaveChangesMessage"),
						ResourceService.GetString("MainWindow.SaveChangesMessageHeader") + " " + Title + " ?",
						MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
						MessageBoxResult.Yes);
				}
				switch (dr) {
					case MessageBoxResult.Yes:
						foreach (IViewContent vc in this.ViewContents) {
							while (vc.IsDirty) {
								ICSharpCode.SharpDevelop.Commands.SaveFileHelper.Save(vc);
								if (vc.IsDirty) {
									if (MessageService.AskQuestion("${res:MainWindow.DiscardChangesMessage}")) {
										break;
									}
								}
							}
						}
						break;
					case MessageBoxResult.No:
						break;
					case MessageBoxResult.Cancel:
						e.Cancel = true;
						break;
				}
			}
			if (!e.Cancel) {
				foreach (IViewContent vc in this.viewContents) {
					dockLayout.Workbench.StoreMemento(vc);
				}
			}
		}

		void OnClosedEvent(object sender, EventArgs e)
		{
			Dispose();
			dockLayout.RemoveDocument(this);
			Closed?.Invoke(this, EventArgs.Empty);
			CommandManager.InvalidateRequerySuggested();
		}

		void RefreshTabPageTexts()
		{
			if (viewTabControl != null) {
				for (int i = 0; i < viewTabControl.Items.Count; ++i) {
					TabItem tabPage = (TabItem)viewTabControl.Items[i];
					tabPage.Header = StringParser.Parse(ViewContents[i].TabPageText);
				}
			}
		}

		void OnTitleChanged()
		{
			if (TitleChanged != null) {
				TitleChanged(this, EventArgs.Empty);
			}
		}

		public event EventHandler TitleChanged;

		void OnInfoTipChanged()
		{
			if (InfoTipChanged != null) {
				InfoTipChanged(this, EventArgs.Empty);
			}
		}

		public event EventHandler InfoTipChanged;

		public event EventHandler Closed;

		public override string ToString()
		{
			return "[AvalonWorkbenchWindow: " + this.Title + "]";
		}

		/// <summary>
		/// Gets the target for re-routing commands to this window.
		/// </summary>
		internal IInputElement GetCommandTarget()
		{
			IViewContent vc = ActiveViewContent;
			return vc != null ? vc.Control as IInputElement : null;
		}
	}
}
