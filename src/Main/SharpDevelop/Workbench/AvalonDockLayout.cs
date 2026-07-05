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
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;

using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.ViewModels;

namespace ICSharpCode.SharpDevelop.Workbench
{
	/// <summary>
	/// Workbench layout using the AvalonDock library.
	/// </summary>
	sealed class AvalonDockLayout : IWorkbenchLayout
	{
		WpfWorkbench workbench;
		DockingManager dockingManager = new DockingManager();
		DockWorkspace dockWorkspace;
		List<IWorkbenchWindow> workbenchWindows = new List<IWorkbenchWindow>();
		internal bool Busy;
		
		public WpfWorkbench Workbench {
			get { return workbench; }
		}
		
		public DockingManager DockingManager {
			get { return dockingManager; }
		}
		
		public AvalonDockLayout()
		{
			dockingManager.Theme = new AvalonDock.Themes.Vs2013BlueTheme();
			dockWorkspace = new DockWorkspace(dockingManager);
			ConfigureDockingManagerForWorkspace();
			dockingManager.ActiveContentChanged += dockingManager_ActiveContentChanged;
			dockingManager.Loaded += dockingManager_Loaded;
		}
		
		#if DEBUG
		internal void WriteState(TextWriter output)
		{
			output.WriteLine("AvalonDock: ActiveContent = " + WpfWorkbench.GetElementName(dockingManager.ActiveContent));
		}
		#endif
		
		void dockingManager_Loaded(object sender, RoutedEventArgs e)
		{
			// LoadConfiguration doesn't do anything until the docking manager is loaded,
			// so we have to load the configuration now
			LoggingService.Info("dockingManager_Loaded");
			LoadConfiguration();
			EnsureFloatingWindowsLocations();
		}
		
		void EnsureFloatingWindowsLocations()
		{
			foreach (var window in dockingManager.FloatingWindows) {
				var newLocation = FormLocationHelper.Validate(new Rect(window.Left, window.Top, window.Width, window.Height));
				window.Left = newLocation.Left;
				window.Top = newLocation.Top;
			}
		}
		
		void dockingManager_ActiveContentChanged(object sender, EventArgs e)
		{
			WpfWorkbench.FocusDebug("AvalonDock: ActiveContent changed to {0}", WpfWorkbench.GetElementName(dockingManager.ActiveContent));
			if (ActiveContentChanged != null)
				ActiveContentChanged(this, e);
			if (ActiveWorkbenchWindowChanged != null)
				ActiveWorkbenchWindowChanged(this, e);
			CommandManager.InvalidateRequerySuggested();
		}
		
		public event EventHandler ActiveWorkbenchWindowChanged;
		
		public IWorkbenchWindow ActiveWorkbenchWindow {
			get {
				return dockWorkspace.ActiveDocument;
			}
		}
		
		public event EventHandler ActiveContentChanged;
		
		public IServiceProvider ActiveContent {
			get {
				object activeContent = dockingManager.ActiveContent;
				AvalonPadContent padContent = activeContent as AvalonPadContent;
				if (padContent != null)
					return padContent.PadContent;
				AvalonWorkbenchWindow window = activeContent as AvalonWorkbenchWindow;
				if (window != null)
					return window.ActiveViewContent;
				return null;
			}
		}
		
		public IList<IWorkbenchWindow> WorkbenchWindows {
			get {
				return workbenchWindows.AsReadOnly();
			}
		}
		
		public void Attach(IWorkbench workbench)
		{
			if (this.workbench != null)
				throw new InvalidOperationException("Can attach only once!");
			this.workbench = (WpfWorkbench)workbench;
			this.workbench.mainContent.Content = dockingManager;
			CommandManager.AddCanExecuteHandler(this.workbench, OnCanExecuteRoutedCommand);
			CommandManager.AddExecutedHandler(this.workbench, OnExecuteRoutedCommand);
			Busy = true;
			try {
				foreach (PadDescriptor pd in workbench.PadContentCollection) {
					if (!IsMefToolPane(pd))
						ShowPad(pd);
				}
			} finally {
				Busy = false;
			}
			dockWorkspace.InitializeLayout();
			LoadConfiguration();
			dockWorkspace.BindSources();
			EnsureFloatingWindowsLocations();
		}
		
		public void Detach()
		{
			StoreConfiguration();
			this.workbench.mainContent.Content = null;
			CommandManager.RemoveCanExecuteHandler(this.workbench, OnCanExecuteRoutedCommand);
			CommandManager.RemoveExecutedHandler(this.workbench, OnExecuteRoutedCommand);
		}

		bool isInNestedCanExecute;

		// Custom command routing:
		// if the command isn't handled on the current focus, try to execute it on the focus inside the active workbench window
		void OnCanExecuteRoutedCommand(object sender, CanExecuteRoutedEventArgs e)
		{
			workbench.VerifyAccess();
			RoutedCommand routedCommand = e.Command as RoutedCommand;
			AvalonWorkbenchWindow workbenchWindow = ActiveWorkbenchWindow as AvalonWorkbenchWindow;
			if (!e.Handled && routedCommand != null && workbenchWindow != null && !isInNestedCanExecute) {
				IInputElement target = workbenchWindow.GetCommandTarget();
				if (target != null && target != e.OriginalSource) {
					isInNestedCanExecute = true;
					try {
						e.CanExecute = routedCommand.CanExecute(e.Parameter, target);
					} finally {
						isInNestedCanExecute = false;
					}
					e.Handled = true;
				}
			}
		}

		bool isInNestedExecute;
		
		void OnExecuteRoutedCommand(object sender, ExecutedRoutedEventArgs e)
		{
			workbench.VerifyAccess();
			RoutedCommand routedCommand = e.Command as RoutedCommand;
			AvalonWorkbenchWindow workbenchWindow = ActiveWorkbenchWindow as AvalonWorkbenchWindow;
			if (!e.Handled && routedCommand != null && workbenchWindow != null && !isInNestedExecute) {
				IInputElement target = workbenchWindow.GetCommandTarget();
				if (target != null && target != e.OriginalSource) {
					isInNestedExecute = true;
					try {
						routedCommand.Execute(e.Parameter, target);
					} finally {
						isInNestedExecute = false;
					}
					e.Handled = true;
				}
			}
		}
		
		Dictionary<PadDescriptor, AvalonPadContent> pads = new Dictionary<PadDescriptor, AvalonPadContent>();
		Dictionary<string, AvalonPadContent> padsByClass = new Dictionary<string, AvalonPadContent>();
		
		public void ShowPad(PadDescriptor padDescriptor)
		{
			if (TryShowMefToolPane(padDescriptor))
				return;

			AvalonPadContent pad;
			if (pads.TryGetValue(padDescriptor, out pad)) {
				pad.Show();
			} else {
				pad = new AvalonPadContent(this, padDescriptor);
				pads.Add(padDescriptor, pad);
				padsByClass.Add(padDescriptor.Class, pad);
				pad.ShowInDefaultPosition();
				if (pad.Parent is LayoutAnchorablePane pane) {
					if ((padDescriptor.DefaultPosition & DefaultPadPositions.Left) != 0)
						pane.DockWidth = new GridLength(250);
					else if ((padDescriptor.DefaultPosition & DefaultPadPositions.Right) != 0)
						pane.DockWidth = new GridLength(280);
					else if ((padDescriptor.DefaultPosition & DefaultPadPositions.Bottom) != 0)
						pane.DockHeight = new GridLength(188);
				}
			}
		}
		
		public void ActivatePad(PadDescriptor padDescriptor)
		{
			if (TryShowMefToolPane(padDescriptor))
				return;

			AvalonPadContent p;
			if (pads.TryGetValue(padDescriptor, out p)) {
				if (!p.IsVisible)
					p.Show();
				p.IsActive = true;
			} else {
				ShowPad(padDescriptor);
			}
		}
		
		public void HidePad(PadDescriptor padDescriptor)
		{
			if (IsMefToolPane(padDescriptor)) {
				DockWorkspace.Current?.Remove(dockWorkspace.ToolPanes.First(p => p.ContentId == GetMefToolPaneContentId(padDescriptor)));
				return;
			}

			AvalonPadContent p;
			if (pads.TryGetValue(padDescriptor, out p))
				p.Hide();
		}
		
		public void UnloadPad(PadDescriptor padDescriptor)
		{
			AvalonPadContent p = pads[padDescriptor];
			p.Hide();
			if (p.Parent is ILayoutContainer parent)
				parent.RemoveChild(p);
			p.Dispose();
		}
		
		public bool IsVisible(PadDescriptor padDescriptor)
		{
			if (IsMefToolPane(padDescriptor)) {
				var pane = dockWorkspace.ToolPanes.FirstOrDefault(p => p.ContentId == GetMefToolPaneContentId(padDescriptor));
				return pane != null && pane.IsVisible;
			}

			AvalonPadContent p;
			if (pads.TryGetValue(padDescriptor, out p))
				return p.IsVisible;
			else
				return false;
		}
		
		public IWorkbenchWindow ShowView(IViewContent content, bool switchToOpenedView)
		{
			AvalonWorkbenchWindow window = new AvalonWorkbenchWindow(this);
			workbenchWindows.Add(window);
			window.ViewContents.Add(content);
			window.ViewContents.AddRange(content.SecondaryViewContents);
			dockWorkspace.AddDocument(window, switchToOpenedView);
			window.Closed += window_Closed;
			return window;
		}
		
		void window_Closed(object sender, EventArgs e)
		{
			workbenchWindows.Remove((IWorkbenchWindow)sender);
		}

		internal void RemoveDocument(AvalonWorkbenchWindow window)
		{
			dockWorkspace.RemoveDocument(window);
		}
		
		public void LoadConfiguration()
		{
			if (!dockingManager.IsLoaded)
				return;
			Busy = true;
			try {
				TryLoadConfiguration();
			} catch (Exception ex) {
				MessageService.ShowException(ex);
				// ignore errors loading configuration
			} finally {
				Busy = false;
			}
			foreach (AvalonPadContent p in pads.Values) {
				p.LoadPadContentIfRequired();
			}
		}
		
		void TryLoadConfiguration()
		{
			bool isPlainLayout = LayoutConfiguration.CurrentLayoutName == "Plain";
			if (File.Exists(LayoutConfiguration.CurrentLayoutFileName)) {
				try {
					LoadLayout(LayoutConfiguration.CurrentLayoutFileName, isPlainLayout);
					return;
				} catch (FileFormatException) {
					// error when version of AvalonDock has changed: ignore and load template instead
				}
			}
			if (File.Exists(LayoutConfiguration.CurrentLayoutTemplateFileName)) {
				LoadLayout(LayoutConfiguration.CurrentLayoutTemplateFileName, isPlainLayout);
			}
		}
		
		void LoadLayout(string fileName, bool hideAllLostPads)
		{
			LoggingService.Info("Loading layout file: " + fileName + ", hideAllLostPads=" + hideAllLostPads);
			// TODO: re-enable after migrating legacy pads to MEF ToolPaneModel (layout serializer only handles MEF panes)
			// dockWorkspace.RestoreLayout(fileName);
		}
		
		public void StoreConfiguration()
		{
			try {
				LayoutConfiguration current = LayoutConfiguration.CurrentLayout;
				if (current != null && !current.ReadOnly) {
					string configPath = LayoutConfiguration.ConfigLayoutPath;
					Directory.CreateDirectory(configPath);
					string fileName = Path.Combine(configPath, current.FileName);
					LoggingService.Info("Saving layout file: " + fileName);
					dockWorkspace.SaveLayout(fileName);
				}
			} catch (Exception e) {
				MessageService.ShowException(e);
			}
		}
		
		public void SwitchLayout(string layoutName)
		{
			StoreConfiguration();
			LayoutConfiguration.CurrentLayoutName = layoutName;
		}

		void ConfigureDockingManagerForWorkspace()
		{
			dockingManager.LayoutItemContainerStyleSelector = new PaneStyleSelector {
				ToolPaneStyle = CreateToolPaneStyle(),
				TabPageStyle = CreateDocumentPaneStyle()
			};

			var toolPaneTemplate = new DataTemplate(typeof(ToolPaneModel));
			var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
			presenter.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ToolPaneModel.Content)));
			toolPaneTemplate.VisualTree = presenter;
			dockingManager.Resources.Add(new DataTemplateKey(typeof(ToolPaneModel)), toolPaneTemplate);

			var documentTemplate = new DataTemplate(typeof(AvalonWorkbenchWindow));
			var documentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
			documentPresenter.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(AvalonWorkbenchWindow.Content)));
			documentTemplate.VisualTree = documentPresenter;
			dockingManager.Resources.Add(new DataTemplateKey(typeof(AvalonWorkbenchWindow)), documentTemplate);
		}

		static Style CreateToolPaneStyle()
		{
			var style = new Style(typeof(LayoutAnchorableItem));
			style.Setters.Add(new Setter(LayoutItem.TitleProperty, new Binding("Model.Title")));
			style.Setters.Add(new Setter(LayoutItem.ContentIdProperty, new Binding("Model.ContentId")));
			style.Setters.Add(new Setter(LayoutItem.IsSelectedProperty, new Binding("Model.IsSelected") { Mode = BindingMode.TwoWay }));
			style.Setters.Add(new Setter(LayoutItem.IsActiveProperty, new Binding("Model.IsActive") { Mode = BindingMode.TwoWay }));
			style.Setters.Add(new Setter(LayoutAnchorableItem.CanHideProperty, new Binding("Model.IsCloseable")));
			style.Setters.Add(new Setter(LayoutAnchorableItem.HideCommandProperty, new Binding("Model.CloseCommand")));
			style.Setters.Add(new Setter(LayoutItem.CanCloseProperty, new Binding("Model.IsCloseable")));
			style.Setters.Add(new Setter(LayoutItem.CloseCommandProperty, new Binding("Model.CloseCommand")));
			return style;
		}

		static Style CreateDocumentPaneStyle()
		{
			var style = new Style(typeof(LayoutItem));
			style.Setters.Add(new Setter(LayoutItem.TitleProperty, new Binding("Model.Title")));
			style.Setters.Add(new Setter(LayoutItem.ContentIdProperty, new Binding("Model.ContentId")));
			style.Setters.Add(new Setter(LayoutItem.IsSelectedProperty, new Binding("Model.IsSelected") { Mode = BindingMode.TwoWay }));
			style.Setters.Add(new Setter(LayoutItem.IsActiveProperty, new Binding("Model.IsActive") { Mode = BindingMode.TwoWay }));
			style.Setters.Add(new Setter(LayoutItem.CloseCommandProperty, new Binding("Model.CloseCommand")));
			style.Setters.Add(new Setter(LayoutItem.CanCloseProperty, new Binding("Model.IsCloseable") { Mode = BindingMode.TwoWay }));
			return style;
		}

		bool IsMefToolPane(PadDescriptor padDescriptor)
		{
			var contentId = GetMefToolPaneContentId(padDescriptor);
			return contentId != null && dockWorkspace.ContainsToolPane(contentId);
		}

		bool TryShowMefToolPane(PadDescriptor padDescriptor)
		{
			var contentId = GetMefToolPaneContentId(padDescriptor);
			return contentId != null && dockWorkspace.ShowToolPane(contentId);
		}

		static string GetMefToolPaneContentId(PadDescriptor padDescriptor)
		{
			if (padDescriptor.Class == typeof(ICSharpCode.SharpDevelop.Services.SolutionExplorerPad).FullName)
				return "SolutionExplorer";
			return null;
		}
	}
}
