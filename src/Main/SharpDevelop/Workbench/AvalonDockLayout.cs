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
using ICSharpCode.ILSpy.ViewModels;

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
			IdeThemeService.Attach(dockingManager);
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
				// A layout restore rebuilds the whole RootPanel from the snapshot (which only
				// knows MEF panes - LayoutSnapshotConverter.Apply), leaving a legacy pad that was
				// docked before the restore detached from the live tree: its Root is either null
				// (parent pane replaced) or the stale pre-restore LayoutRoot (XmlLayoutSerializer
				// replaces Manager.Layout wholesale). Re-create the anchorable on demand, mirroring
				// how a re-added MEF model gets a fresh anchorable.
				if (pad.Root != dockWorkspace.Layout)
					pad = ReplacePad(padDescriptor, pad);
				if (pad.Parent == null)
					pad.ShowInDefaultPosition();
				else if (!pad.IsVisible)
					pad.Show();
				EnsureDefaultPositionSize(pad, padDescriptor);
			} else {
				pad = new AvalonPadContent(this, padDescriptor);
				pads.Add(padDescriptor, pad);
				padsByClass.Add(padDescriptor.Class, pad);
				pad.ShowInDefaultPosition();
				EnsureDefaultPositionSize(pad, padDescriptor);
			}
		}

		// A legacy pad whose anchorable belongs to a stale pre-restore LayoutRoot cannot be
		// re-docked in place: the DockingManager never un-registered that anchorable's layout
		// item (the stale root's element-removed events aren't watched after the layout object is
		// swapped out), so re-attaching the same anchorable re-registers a duplicate logical
		// child and the DockingManager throws (Debug build: InvalidOperationException).
		// Re-attaching can also trip AddToLayout's own guards (the anchorable may still be
		// parented or in a root's Hidden collection, where removing from the plain
		// ObservableCollection does not clear Parent). Replace the anchorable wholesale - the pad
		// control itself reloads on first show (LoadPadContentIfRequired), exactly like a fresh
		// show.
		AvalonPadContent ReplacePad(PadDescriptor padDescriptor, AvalonPadContent pad)
		{
			var replacement = new AvalonPadContent(this, padDescriptor);
			pads[padDescriptor] = replacement;
			padsByClass[padDescriptor.Class] = replacement;
			return replacement;
		}

		// A pad that was docked, then dropped by a layout restore (a saved layout only restores the
		// panes it lists; everything else is re-docked on demand), gets re-docked into whatever
		// strip AvalonDock re-creates for it - which, measured, collapses to a tab-row-only strip
		// (25px, content viewport 0) so a virtualized tree/list never realizes its rows. Re-applying
		// the legacy default-position sizing on every show is idempotent (the same values a fresh
		// ShowInDefaultPosition docks would set) and gives a re-shown pad a usable pane again.
		static void EnsureDefaultPositionSize(AvalonPadContent pad, PadDescriptor padDescriptor)
		{
			if (pad.Parent is not LayoutAnchorablePane pane)
				return;
			if ((padDescriptor.DefaultPosition & DefaultPadPositions.Left) != 0)
				pane.DockWidth = new GridLength(250);
			else if ((padDescriptor.DefaultPosition & DefaultPadPositions.Right) != 0)
				pane.DockWidth = new GridLength(280);
			else if ((padDescriptor.DefaultPosition & DefaultPadPositions.Bottom) != 0)
				pane.DockHeight = new GridLength(188);
		}
		
		public void ActivatePad(PadDescriptor padDescriptor)
		{
			if (TryShowMefToolPane(padDescriptor))
				return;

			AvalonPadContent p;
			if (pads.TryGetValue(padDescriptor, out p)) {
				// See ShowPad: a layout restore can leave a previously-docked legacy pad
				// detached from the live tree, so replace the anchorable before activating it.
				if (p.Root != dockWorkspace.Layout)
					p = ReplacePad(padDescriptor, p);
				if (p.Parent == null)
					p.ShowInDefaultPosition();
				else if (!p.IsVisible)
					p.Show();
				EnsureDefaultPositionSize(p, padDescriptor);
				p.IsSelected = true;
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
			if (File.Exists(LayoutConfiguration.CurrentLayoutFileName)) {
				try {
					LoadLayout(LayoutConfiguration.CurrentLayoutFileName);
					return;
				} catch (FileFormatException) {
					// error when version of AvalonDock has changed: ignore and load template instead
				}
			}
			if (File.Exists(LayoutConfiguration.CurrentLayoutTemplateFileName)) {
				LoadLayout(LayoutConfiguration.CurrentLayoutTemplateFileName);
			}
		}
		
		void LoadLayout(string fileName)
		{
			LoggingService.Info("Loading layout file: " + fileName);
			// Re-enabled (doc/technotes/ilspy.md "Phased implementation plan" Phase 2, 2026-08-02).
			// DockWorkspace.RestoreLayout's LayoutSerializationCallback already skips (Cancel=true,
			// no exception) any serialized LayoutAnchorable whose ContentId isn't a MEF-exported
			// ToolPaneModel - legacy (AddInTree Pad-based) anchorables are silently dropped rather
			// than restored, not migrated. The real reason this was disabled is that the shipped
			// data/layouts/*.xml template files were stale AvalonDock 1.x-schema XML, incompatible
			// with XmlLayoutSerializer's modern schema - regenerated as part of this change (see
			// the templates themselves for provenance).

			dockWorkspace.RestoreLayout(fileName);

			// A layout switch is an INCREMENTAL operation (doc/technotes/ilspy.md "Legacy pad
			// migration", 2026-08-09): it must open and surface the panes the layout names, but
			// must NOT close panes that were already open (e.g. switching to the "Debug" layout
			// for a debug session shouldn't evict pads the user had open). Panes not named in the
			// restored layout are re-docked by the AnchorablesSource import that follows
			// RestoreLayout - DockWorkspace.BeforeInsertAnchorable sends them to their
			// ToolPaneModel.PreferredDockSide (and into the Hidden area when IsVisible is false),
			// the same "initial dock" treatment a freshly-enabled pad gets, instead of the
			// default "land in front of whatever is active" placement the eviction used to be
			// there to undo.
			// NOTE: the previous behavior ("a named layout shows exactly the panes it contains")
			// evicted non-layout panes from ToolPanes entirely, which is what made a debug
			// session's layout switch *close* pads the user had open (the reported bug this
			// change fixes).
		}

		// ReadAnchorableContentIds removed with the eviction it served (2026-08-09): a layout
		// switch is incremental - panes not named in the restored layout stay docked at their
		// PreferredDockSide instead of being removed from ToolPanes.

		
		public void StoreConfiguration()
		{
			// Symmetric with LoadConfiguration's own "doesn't do anything until the docking
			// manager is loaded" guard (see dockingManager_Loaded) - without it, switching layouts
			// (LayoutConfiguration.CurrentLayoutName's setter, or ChooseLayoutComboBox's reactive
			// re-selection in response to LayoutConfiguration.LayoutChanged) before the docking
			// manager's first Loaded event has fired persists whatever ad-hoc arrangement
			// AvalonDock's default insertion strategy produced for the newly-registered panes
			// (e.g. an addin's onActivating adding its panes reactively via the AnchorablesSource
			// binding) - not the layout's real template. Once that gets written to disk, it's
			// self-reinforcing: the *next* LoadConfiguration (the one dockingManager_Loaded
			// actually runs once IsLoaded becomes true) faithfully restores exactly that broken
			// file instead of ever reaching the addin's clean template, and every later save just
			// re-persists it (doc/technotes/ilspy.md, "the layout gets lost" - measured directly:
			// opening an assembly right after startup, before the docking manager had rendered
			// once, permanently corrupted the saved "ILSpy" layout to tab all three ILSpy pads
			// into the pre-existing Properties/Projects pane instead of their own LeftPane/
			// TopPane/BottomPane groups).
			if (!dockingManager.IsLoaded)
				return;
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

		// Generalized (doc/technotes/ilspy.md "Docking and layout replacement" item 4/item 1
		// consolidation, 2026-08-03) from a single hardcoded `padDescriptor.Class ==
		// typeof(ProjectBrowserPad).FullName -> "ProjectBrowser"` comparison into a lookup driven
		// by ToolPaneModel.LegacyPadClass, so migrating another legacy Pad to the modern model
		// needs no change here at all - just setting LegacyPadClass in the new model's
		// constructor, the same way ProjectBrowserViewModel and (now) OutlineViewModel do.
		//
		// AddIns can't be MEF parts of the App assembly (OpenDevelopMefHost.BindExports only
		// scans the App assembly), so an AddIn's migrated pad registers through
		// PadToolPaneProvider instead (doc/technotes/ilspy.md "Legacy pad migration"): resolve the
		// model lazily on first miss and register it with the workspace. The first ShowPad runs
		// inside Attach, before InitializeLayout/BindSources, so the pane is already in
		// ToolPanes when the AnchorablesSource binding attaches - exactly like a built-in pane.
		string GetMefToolPaneContentId(PadDescriptor padDescriptor)
		{
			var pane = dockWorkspace.ToolPanes
				.FirstOrDefault(pane => pane.LegacyPadClass == padDescriptor.Class);
			if (pane == null) {
				pane = PadToolPaneProvider.Resolve(padDescriptor.Class);
				if (pane != null)
					dockWorkspace.AddToolPane(pane);
			}
			return pane?.ContentId;
		}
	}
}
