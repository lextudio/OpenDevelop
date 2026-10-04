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

		// A window with a primary (source) and a secondary (designer) view hosts both in a Grid
		// divided by a GridSplitter — Visual Studio's designer-on-top / XAML-below arrangement.
		// There is no tab layout any more: hiding a view means collapsing its pane, not switching
		// tabs. Once both panes are visibly parented, both views are initialized without changing
		// the file's active/save view.
		Grid splitHost;
		ContentControl splitTopHost;
		ContentControl splitBottomHost;
		FrameworkElement splitBar;
		bool splitActive;
		int splitActiveIndex;
		// Split-bar state, changed by the little VS-style buttons on the bar.
		bool splitHorizontal = true;   // stacked (designer above source) vs side by side
		bool splitSwapped;             // exchange the two panes' positions
		// Collapse, expand and pop-out always act on the second pane - the bottom one when
		// stacked, the right one when side by side - whichever view a swap has put there.
		bool splitSecondCollapsed;     // the second pane is folded against the edge; the bar stays
		GridLength splitFirstLength = new GridLength(1, GridUnitType.Star);
		GridLength splitSecondLength = new GridLength(1, GridUnitType.Star);
		GridSplitter splitSplitter;
		Button splitCollapseButton;
		Window splitFloatWindow;       // non-null while the second pane is popped out into its own window
		int splitFloatIndex = -1;      // the view index living in splitFloatWindow

		int SplitFirstIndex => splitSwapped ? 0 : ViewContents.Count - 1;
		int SplitSecondIndex => splitSwapped ? ViewContents.Count - 1 : 0;

		// Probes for the DevFlow action od.editor.split-status / integration tests.
		internal bool SplitViewActive => splitActive && splitHost != null;
		internal bool SplitViewFloating => splitFloatWindow != null;
		internal bool SplitViewHorizontal => splitHorizontal;
		internal bool SplitViewSecondCollapsed => splitSecondCollapsed;
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
				// Also covers a popped-out pane: the document then shows the other view alone,
				// and splitActiveIndex points at it.
				if (splitActiveIndex >= 0 && splitActiveIndex < ViewContents.Count)
					return ViewContents[splitActiveIndex];
				return null;
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
			if (!splitActive || splitHost == null)
				return;
			if (viewNumber < 0 || viewNumber >= ViewContents.Count)
				return;
			// Asking for the folded view is asking to see it.
			if (splitSecondCollapsed && viewNumber == SplitSecondIndex)
				ToggleSecondPaneCollapsed();
			splitActiveIndex = viewNumber;
			RefreshSplitLabels();
			UpdateActiveViewContent();

			IViewContent splitView = this.ActiveViewContent;
			if (splitView != null && this.IsActive)
				SetFocus(() => splitView.InitiallyFocusedControl as IInputElement);
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

		/// <summary>
		/// Ctrl+PgUp / Ctrl+PgDown switches between the two panes (SD-1735), as the old view tabs
		/// did; SwitchView expands a folded pane on the way.
		/// </summary>
		void OnSplitHostPreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (e.Handled || (e.Key != Key.PageUp && e.Key != Key.PageDown) || e.KeyboardDevice.Modifiers != ModifierKeys.Control)
				return;
			SwitchView(splitActiveIndex == SplitFirstIndex ? SplitSecondIndex : SplitFirstIndex);
			e.Handled = true;
		}

		/// <summary>
		/// (Re)builds the window body from the current view contents: a bare control for a single
		/// view, or the split host. Rebuilds from scratch so a view control is never parented in
		/// two places when the layout changes.
		/// </summary>
		void RebuildContent()
		{
			int previousActive = splitActiveIndex;

			DetachContentHosts();

			if (ViewContents.Count == 0) {
				splitActive = false;
				splitActiveIndex = -1;
				this.Content = null;
				return;
			}
			if (ViewContents.Count == 1) {
				splitActive = false;
				splitActiveIndex = 0;
				this.Content = ViewContents[0].Control;
				return;
			}

			// The second pane is floating in its own window; the document shows the other view alone.
			if (splitFloatWindow != null) {
				splitActive = false;
				int dockedIndex = splitFloatIndex == 0 ? ViewContents.Count - 1 : 0;
				splitActiveIndex = dockedIndex;
				this.Content = ViewContents[dockedIndex].Control;
				return;
			}

			// Every visual designer gets the same split chrome. Most current designers already
			// expose a WPF surface, while legacy WinForms designers are wrapped by
			// IWinFormsService in CreateSplitPane. The split shows the primary view and the last
			// secondary view: display bindings attach at most one designer per file (XAML is
			// routed to a single dialect), so there is no third view to lose.
			if (ViewContents.Count > 2)
				LoggingService.Warn("AvalonWorkbenchWindow: " + ViewContents.Count + " views for " + Title
					+ "; the split shows only the primary and the last secondary view.");
			splitActive = true;
			splitActiveIndex = Math.Min(Math.Max(previousActive, 0), ViewContents.Count - 1);
			BuildSplitContent();
		}

		void DetachContentHosts()
		{
			this.Content = null;

			if (splitHost != null) {
				splitHost.PreviewKeyDown -= OnSplitHostPreviewKeyDown;
				// Keep the dragged proportions across a swap/orientation rebuild.
				if (!splitSecondCollapsed)
					(splitFirstLength, splitSecondLength) = SplitLengths();
				if (splitTopHost != null) splitTopHost.Content = null;
				if (splitBottomHost != null) splitBottomHost.Content = null;
				splitHost.Children.Clear();
				splitHost = null;
				splitTopHost = null;
				splitBottomHost = null;
				splitBar = null;
				splitSplitter = null;
				splitCollapseButton = null;
			}
		}

		void BuildSplitContent()
		{
			// Visual Studio's convention: the designer (secondary view, added last) starts above
			// the XAML source; the split-bar buttons can swap them or switch to side by side.
			int firstIndex = SplitFirstIndex;
			int secondIndex = SplitSecondIndex;
			if (splitSecondCollapsed && splitActiveIndex == secondIndex)
				splitActiveIndex = firstIndex;

			splitHost = new Grid();
			splitHost.PreviewKeyDown += OnSplitHostPreviewKeyDown;
			splitTopHost = CreateSplitPane(ViewContents[firstIndex], firstIndex);
			splitBottomHost = CreateSplitPane(ViewContents[secondIndex], secondIndex);
			PrepareVisibleSplitView(ViewContents[firstIndex]);
			PrepareVisibleSplitView(ViewContents[secondIndex]);
			splitBar = BuildSplitBar();

			if (splitHorizontal) {
				splitHost.RowDefinitions.Add(new RowDefinition());
				splitHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				splitHost.RowDefinitions.Add(new RowDefinition());
				Grid.SetRow(splitTopHost, 0);
				Grid.SetRow(splitBar, 1);
				Grid.SetRow(splitBottomHost, 2);
			} else {
				splitHost.ColumnDefinitions.Add(new ColumnDefinition());
				splitHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				splitHost.ColumnDefinitions.Add(new ColumnDefinition());
				Grid.SetColumn(splitTopHost, 0);
				Grid.SetColumn(splitBar, 1);
				Grid.SetColumn(splitBottomHost, 2);
			}

			splitHost.Children.Add(splitTopHost);
			splitHost.Children.Add(splitBar);
			splitHost.Children.Add(splitBottomHost);
			ApplySplitCollapse();

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
						var file = view.PrimaryFile;
						if (file == null)
							continue;
						// Deferred: by the time this runs the file may have been deleted from disk (another
						// tool, a git checkout, a cleaned-up temp folder). Loading it would throw on the UI
						// thread and surface as a crash dialog; leave the view as it is instead.
						if (file.CurrentView != view && !file.IsUntitled && !file.IsDirty && !System.IO.File.Exists(file.FileName)) {
							LoggingService.Warn("Not initializing split view for " + file.FileName + ": the file no longer exists.");
							continue;
						}
						file.ForceInitializeView(view);
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

			int firstIndex = SplitFirstIndex;
			int secondIndex = SplitSecondIndex;
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
			// Pop-out acts on the second pane, so it sits beside that pane's tab: to its right when
			// stacked, below it when side by side.
			tabs.Children.Add(CreateSplitButton("Move this view to a separate window", "NavigateExternalInlineNoHalo", PopOutSecondPane));
			Place(bar, tabs, 0);

			// The splitter owns only the intentionally empty centre lane. The surrounding chrome is
			// still clickable, while resizing cannot accidentally invoke one of the commands.
			var splitter = splitSplitter = new GridSplitter {
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Stretch,
				ResizeDirection = splitHorizontal ? GridResizeDirection.Rows : GridResizeDirection.Columns,
				ResizeBehavior = GridResizeBehavior.PreviousAndNext,
				Background = Brushes.Transparent,
				Cursor = splitHorizontal ? Cursors.SizeNS : Cursors.SizeWE,
				ToolTip = ResourceService.GetString("Workbench.DragToResizeToolTip")
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
			// Collapsing keeps the split: the second pane folds against the edge and this button
			// turns into the one that brings it back (ApplySplitCollapse sets its glyph).
			splitCollapseButton = CreateSplitButton("", "ExpandDown", ToggleSecondPaneCollapsed);
			commands.Children.Add(splitCollapseButton);
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
			string text = SplitTabText(view, index);
			string extension = view.PrimaryFile != null ? Path.GetExtension(view.PrimaryFile.FileName.ToString()) : null;
			bool isXaml = string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase);
			var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
			var icon = new Image {
				Source = PresentationResourceService.GetImageSource("Icons.16x16." + SplitTabIconName(index, extension, isXaml)),
				Width = 14,
				Height = 14,
				Stretch = Stretch.Uniform,
				VerticalAlignment = VerticalAlignment.Center
			};
			var content = new StackPanel { Orientation = Orientation.Horizontal };
			content.Children.Add(icon);
			// A side-by-side split has a 22px vertical rail: show only the icon there, which is
			// distinct enough to tell the views apart; the name stays in the tooltip.
			if (splitHorizontal)
				content.Children.Add(label);
			var tab = new Border {
				Child = content,
				ToolTip = text,
				Padding = splitHorizontal ? new Thickness(7, 0, 9, 0) : new Thickness(0, 6, 0, 6),
				Cursor = Cursors.Hand,
				BorderBrush = ThemeBrush("Border", Brushes.Gray),
				BorderThickness = splitHorizontal ? new Thickness(0, 0, 1, 0) : new Thickness(0, 0, 0, 1)
			};
			tab.MouseLeftButtonDown += delegate { ActivateSplitPane(index, fromTab: true); };
			splitLabelHosts.Add((tab, label, index, facesFirstPane));
			return tab;
		}

		static string SplitTabText(IViewContent view, int index)
		{
			string extension = view.PrimaryFile != null ? Path.GetExtension(view.PrimaryFile.FileName.ToString()) : null;
			if (index == 0 && string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase))
				return "XAML";
			return StringParser.Parse(view.TabPageText);
		}

		static string SplitTabIconName(int index, string extension, bool isXaml)
		{
			if (index != 0)
				return "DesignMode";   // the designer (secondary view)
			if (isXaml)
				return "MarkupXML";
			switch (extension?.ToLowerInvariant()) {
				case ".cs": return "CSFile";
				case ".vb": return "VB";
				default: return "TextFile";
			}
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

		/// <summary>
		/// Folds the second pane (bottom when stacked, right when side by side) against the edge,
		/// or brings it back. The split itself stays: the bar moves to the edge and its close
		/// button becomes the expand button.
		/// </summary>
		internal void ToggleSecondPaneCollapsed()
		{
			if (!splitActive || splitHost == null)
				return;
			if (!splitSecondCollapsed) {
				var (first, second) = SplitLengths();
				splitFirstLength = first;
				splitSecondLength = second;
				if (splitActiveIndex == SplitSecondIndex)
					ActivateSplitPane(SplitFirstIndex);
			}
			splitSecondCollapsed = !splitSecondCollapsed;
			ApplySplitCollapse();
		}

		(GridLength First, GridLength Second) SplitLengths()
		{
			// Read what the grid was built with: an orientation toggle flips splitHorizontal
			// before the old grid is torn down.
			if (splitHost.RowDefinitions.Count == 3)
				return (splitHost.RowDefinitions[0].Height, splitHost.RowDefinitions[2].Height);
			return (splitHost.ColumnDefinitions[0].Width, splitHost.ColumnDefinitions[2].Width);
		}

		void ApplySplitCollapse()
		{
			if (splitHost == null)
				return;
			GridLength first = splitSecondCollapsed ? new GridLength(1, GridUnitType.Star) : splitFirstLength;
			GridLength second = splitSecondCollapsed ? new GridLength(0) : splitSecondLength;
			if (splitHorizontal) {
				splitHost.RowDefinitions[0].Height = first;
				splitHost.RowDefinitions[2].Height = second;
			} else {
				splitHost.ColumnDefinitions[0].Width = first;
				splitHost.ColumnDefinitions[2].Width = second;
			}
			splitBottomHost.Visibility = splitSecondCollapsed ? Visibility.Collapsed : Visibility.Visible;
			if (splitSplitter != null)
				splitSplitter.IsEnabled = !splitSecondCollapsed;
			if (splitCollapseButton != null) {
				string where = splitHorizontal ? "bottom" : "right";
				splitCollapseButton.ToolTip = splitSecondCollapsed
					? "Expand the " + where + " view"
					: "Collapse the " + where + " view";
				// A chevron pair: collapse points toward the edge the pane folds into (down / right),
				// expand points back the way it comes out (up / left).
				string glyph = splitHorizontal
					? (splitSecondCollapsed ? "CollapseUp" : "ExpandDown")
					: (splitSecondCollapsed ? "CollapseLeft" : "ExpandRight");
				((Image)splitCollapseButton.Content).Source = PresentationResourceService.GetImageSource("Icons.16x16." + glyph);
			}
		}

		/// <summary>Moves the second pane's view into its own window; closing it docks it back.</summary>
		internal void PopOutSecondPane()
		{
			if (splitFloatWindow != null) {
				splitFloatWindow.Activate();
				return;
			}
			if (!splitActive || splitHost == null || splitBottomHost == null)
				return;
			int index = SplitSecondIndex;
			// Move whatever the pane hosts - the view's own WPF control, or the WinForms host
			// wrapping it - so the view is never parented twice.
			object content = splitBottomHost.Content;
			if (content == null)
				return;
			splitBottomHost.Content = null;

			var window = new Window {
				Title = ViewContents[index].TitleName,
				Width = 720,
				Height = 520,
				Owner = Application.Current != null ? Application.Current.MainWindow : null,
				Content = content,
				ShowInTaskbar = true
			};
			splitFloatWindow = window;
			splitFloatIndex = index;
			window.Closed += delegate {
				window.Content = null;
				splitFloatWindow = null;
				splitFloatIndex = -1;
				RebuildContent();
				UpdateActiveViewContent();
			};
			window.Show();
			RebuildContent();   // the document now shows the other view alone
			UpdateActiveViewContent();
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

		void ActivateSplitPane(int index, bool fromTab = false)
		{
			if (!splitActive)
				return;
			if (splitSecondCollapsed && index == SplitSecondIndex) {
				// Clicking the tab of the folded pane brings it back. Focus or mouse events from
				// the hidden pane itself must not: a late focus change would undo the collapse.
				if (!fromTab)
					return;
				ToggleSecondPaneCollapsed();
			}
			if (splitActiveIndex == index)
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
			foreach (var (tab, label, index, _) in splitLabelHosts) {
				if (index < 0 || index >= ViewContents.Count)
					continue;
				string text = SplitTabText(ViewContents[index], index);
				label.Text = text;
				tab.ToolTip = text;
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
