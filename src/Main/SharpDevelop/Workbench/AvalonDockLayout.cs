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
		// Panes excluded from the currently restored layout (see LoadLayout): kept so the next
		// layout switch can put them back into the source collection before restoring.
		readonly List<ToolPaneModel> layoutExcludedPanes = new List<ToolPaneModel>();

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
				// replaces Manager.Layout wholesale). Re-dock it on demand, mirroring how a
				// re-added MEF model gets a fresh anchorable.
				if (pad.Root != dockWorkspace.Layout)
					ReDock(pad);
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

		// Detaches a legacy pad's anchorable from a stale (pre-restore) pane and docks it again:
		// ShowInDefaultPosition -> AddToLayout throws InvalidOperationException while the
		// anchorable is still parented (IsVisible), and its parent pane belongs to a layout tree
		// that no longer renders (see ShowPad's comment above).
		void ReDock(AvalonPadContent pad)
		{
			if (pad.Parent is ILayoutContainer staleParent)
				staleParent.RemoveChild(pad);
			pad.ShowInDefaultPosition();
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
				// detached from the live tree, so re-dock before showing/activating it.
				if (p.Root != dockWorkspace.Layout)
					ReDock(p);
				if (!p.IsVisible)
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
			// Re-enabled (doc/technotes/ilspy.md "Phased implementation plan" Phase 2, 2026-08-02).
			// DockWorkspace.RestoreLayout's LayoutSerializationCallback already skips (Cancel=true,
			// no exception) any serialized LayoutAnchorable whose ContentId isn't a MEF-exported
			// ToolPaneModel - legacy (AddInTree Pad-based) anchorables are silently dropped rather
			// than restored, not migrated. The real reason this was disabled is that the shipped
			// data/layouts/*.xml template files were stale AvalonDock 1.x-schema XML, incompatible
			// with XmlLayoutSerializer's modern schema - regenerated as part of this change (see
			// the templates themselves for provenance).

			// Re-add panes a previous layout switch excluded (see below), so this layout can
			// restore them again if it contains them.
			foreach (ToolPaneModel pane in layoutExcludedPanes) {
				dockWorkspace.AddToolPane(pane);
			}
			layoutExcludedPanes.Clear();

			dockWorkspace.RestoreLayout(fileName);

			// A named layout shows exactly the panes it contains. The AnchorablesSource
			// reconciliation re-docks any visible ToolPaneModel that isn't in the restored layout
			// (e.g. the Project Browser when entering the ILSpy layout, landing in front), so
			// remove those from the source collection here - their docked anchorable is removed
			// with them. They stay registered and are re-added on the next LoadLayout call.
			// NOTE: the "in layout" set must come from the layout FILE, not from the live
			// dockingManager.Layout - by the time RestoreLayout returns, the reconciliation has
			// already re-docked the extra panes, so a live check would see them as "in layout".
			var contentIdsInLayout = ReadAnchorableContentIds(fileName);
			foreach (ToolPaneModel pane in dockWorkspace.ToolPanes.ToList()) {
				if (!contentIdsInLayout.Contains(pane.ContentId)) {
					dockWorkspace.RemoveToolPane(pane);
					layoutExcludedPanes.Add(pane);
				}
			}
		}

		static HashSet<string> ReadAnchorableContentIds(string fileName)
		{
			var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			try {
				string content = File.ReadAllText(fileName).TrimStart();
				if (content.StartsWith("{", StringComparison.Ordinal)) {
					// The layout DTO format (doc/technotes/ilspy.md, "Real versioned layout DTO,
					// step 2") - DockWorkspace.SaveLayout now always writes this, even though the
					// file still carries a ".xml" name (LayoutConfiguration.CurrentLayoutFileName
					// is unchanged). Walk the JSON tree for any "ContentId" property rather than
					// deserializing the full LayoutSnapshot shape here - this method only ever
					// needs the flat set of IDs, and staying structure-agnostic means it can't get
					// out of sync with LayoutSnapshot's own shape as that evolves.
					using var doc = System.Text.Json.JsonDocument.Parse(content);
					CollectContentIds(doc.RootElement, ids);
				} else {
					var xmlDoc = new System.Xml.XmlDocument();
					xmlDoc.Load(fileName);
					var nodes = xmlDoc.SelectNodes("//@ContentId");
					if (nodes != null) {
						foreach (System.Xml.XmlAttribute attribute in nodes) {
							if (!string.IsNullOrWhiteSpace(attribute.Value))
								ids.Add(attribute.Value);
						}
					}
				}
			} catch (Exception ex) {
				LoggingService.Warn("Could not read anchorable ContentIds from layout file '" + fileName + "'.", ex);
			}
			return ids;
		}

		static void CollectContentIds(System.Text.Json.JsonElement element, HashSet<string> ids)
		{
			switch (element.ValueKind) {
				case System.Text.Json.JsonValueKind.Object:
					foreach (var property in element.EnumerateObject()) {
						if (property.NameEquals("ContentId") && property.Value.ValueKind == System.Text.Json.JsonValueKind.String) {
							var value = property.Value.GetString();
							if (!string.IsNullOrWhiteSpace(value))
								ids.Add(value);
						} else {
							CollectContentIds(property.Value, ids);
						}
					}
					break;
				case System.Text.Json.JsonValueKind.Array:
					foreach (var item in element.EnumerateArray())
						CollectContentIds(item, ids);
					break;
			}
		}
		
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
		string GetMefToolPaneContentId(PadDescriptor padDescriptor)
		{
			return dockWorkspace.ToolPanes
				.FirstOrDefault(pane => pane.LegacyPadClass == padDescriptor.Class)
				?.ContentId;
		}
	}
}
