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
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.WpfDesign.Designer.Services;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// Manages the WpfToolbox: a grouped list of the WPF popular-controls set plus one
	/// group per assembly referenced by the project being designed.
	/// </summary>
	public class WpfToolbox : ISharedToolboxHost
	{
		const string PopularControlsCategory = "Windows Presentation Foundation";
		const string WinFormsControlsCategory = "Windows Forms";

		static WpfToolbox instance;

		public static WpfToolbox Instance {
			get {
				SD.MainThread.VerifyAccess();
				return instance ?? (instance = new WpfToolbox());
			}
		}

		readonly ListBox toolbox = new ListBox();
		readonly CollectionViewSource itemsView = new CollectionViewSource();
		readonly List<WpfSideTabItem> items = new List<WpfSideTabItem>();
		Point dragStartPoint;
		WpfSideTabItem dragStartItem;
		// Guards OnPreviewMouseMove against the re-entrant moves a portable drag delivers while
		// DoDragDrop is blocked - see OnPreviewMouseMove's own comment.
		bool isDragging;

		IToolService toolService;

		public WpfToolbox()
		{
			// Guarantees Metadata.GetPopularControls() is populated before this constructor reads
			// it below, regardless of whether a WpfViewContent (which also calls this) has been
			// constructed yet - WpfToolbox.Instance is a lazily-constructed, process-lifetime
			// singleton, and whichever caller touches it first otherwise permanently freezes
			// "items" as empty if that caller ran before any WpfViewContent existed (e.g. the
			// Tools pad querying the active view's IToolsHost.ToolsContent before the WPF
			// designer's own secondary view for the current file has loaded).
			ICSharpCode.WpfDesign.Designer.BasicMetadata.Register();

			itemsView.Source = items;
			itemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(WpfSideTabItem.CategoryName)));

			// Disabled rather than left to the default: ItemContainerGenerator.ContainerFromItem
			// was confirmed (via direct hit-testing) to sometimes report a container for a
			// virtualized/recycled row whose actual on-screen position doesn't match where that
			// item renders, once the list is scrolled deep enough - a real click at the reported
			// bounds then lands on a different row. DevFlow's toolbox-bounds queries work around
			// this by walking the live visual tree instead of trusting the generator (see
			// WpfDesignDevFlowActions.FindRealizedContainer), which only finds a correct answer if
			// every item is actually realized - guaranteed by disabling virtualization here. This
			// list is small enough (a few dozen items) that virtualization has no real benefit.
			VirtualizingPanel.SetIsVirtualizing(toolbox, false);
			toolbox.ItemsSource = itemsView.View;
			toolbox.SelectionChanged += OnSelectionChanged;
			toolbox.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
			toolbox.PreviewMouseMove += OnPreviewMouseMove;

			toolbox.ItemTemplate = CreateItemTemplate();
			toolbox.GroupStyle.Add(CreateGroupStyle());

			items.Add(new WpfSideTabItem(PopularControlsCategory));
			foreach (Type t in Metadata.GetPopularControls())
				items.Add(new WpfSideTabItem(PopularControlsCategory, t));

			// "items" is a plain List<T> (not observable), so the CollectionViewSource.View bound
			// as the ListBox's ItemsSource above won't pick up these .Add() calls on its own -
			// AddProjectDlls already knows this and calls Refresh() itself, but the constructor's
			// own initial population needs the same nudge or the toolbox renders empty until
			// something else happens to add project DLLs later.
			itemsView.View.Refresh();

			toolbox.SelectedIndex = 0;

			// Registered here (not by FormsDesigner) so neither AddIn needs a compile-time
			// reference to the other - see ISharedToolboxHost's own doc comment.
			SD.Services.AddService(typeof(ISharedToolboxHost), this);
		}

		// A small representative set, same spirit as Metadata.GetPopularControls() for WPF -
		// dragged items are routed through the real System.Drawing.Design.IToolboxService
		// (registered by FormsDesigner into SD.Services - see DesignerViewContent.cs's own doc
		// comment) rather than WpfDesign's CreateComponentTool, since it's WinForms'
		// ParentControlDesigner.OnDragEnter/OnDragDrop that actually creates the component on a
		// WinForms DesignSurface. Each type gets its own System.Drawing.Design.ToolboxItem,
		// created once and registered with the toolbox service up front (AddToolboxItem) - the
		// drop side's DeserializeToolboxItem only accepts items it already knows about.
		static readonly Type[] WinFormsPopularControls = {
			typeof(System.Windows.Forms.Button),
			typeof(System.Windows.Forms.Label),
			typeof(System.Windows.Forms.TextBox),
			typeof(System.Windows.Forms.CheckBox),
			typeof(System.Windows.Forms.RadioButton),
			typeof(System.Windows.Forms.ComboBox),
			typeof(System.Windows.Forms.ListBox),
			typeof(System.Windows.Forms.Panel),
			typeof(System.Windows.Forms.GroupBox),
			typeof(System.Windows.Forms.NumericUpDown),
		};

		bool winFormsControlsAdded;

		void AddWinFormsControls()
		{
			if (winFormsControlsAdded)
				return;
			var toolboxService = SD.Services.GetService(typeof(System.Drawing.Design.IToolboxService)) as System.Drawing.Design.IToolboxService;
			if (toolboxService == null)
				return;

			winFormsControlsAdded = true;
			items.Add(new WpfSideTabItem(WinFormsControlsCategory));
			foreach (Type t in WinFormsPopularControls) {
				var toolboxItem = new System.Drawing.Design.ToolboxItem(t);
				toolboxService.AddToolboxItem(toolboxItem);
				items.Add(new WpfSideTabItem(WinFormsControlsCategory, t, toolboxItem));
			}
			itemsView.View.Refresh();
		}

		static DataTemplate CreateItemTemplate()
		{
			var iconImage = new FrameworkElementFactory(typeof(Image));
			iconImage.SetValue(FrameworkElement.WidthProperty, 16d);
			iconImage.SetValue(FrameworkElement.HeightProperty, 16d);
			iconImage.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
			iconImage.SetBinding(Image.SourceProperty, new Binding(nameof(WpfSideTabItem.Icon)));

			var text = new FrameworkElementFactory(typeof(TextBlock));
			text.SetBinding(TextBlock.TextProperty, new Binding(nameof(WpfSideTabItem.DisplayName)));
			text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

			var panel = new FrameworkElementFactory(typeof(StackPanel));
			panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
			panel.AppendChild(iconImage);
			panel.AppendChild(text);

			return new DataTemplate(typeof(WpfSideTabItem)) { VisualTree = panel };
		}

		static GroupStyle CreateGroupStyle()
		{
			var header = new FrameworkElementFactory(typeof(TextBlock));
			header.SetBinding(TextBlock.TextProperty, new Binding("Name"));
			header.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
			header.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 4, 0, 2));

			return new GroupStyle {
				HeaderTemplate = new DataTemplate { VisualTree = header }
			};
		}

		static bool IsControl(Type t)
		{
			return !t.IsAbstract && !t.IsGenericTypeDefinition && t.IsSubclassOf(typeof(FrameworkElement));
		}

		static readonly HashSet<string> addedAssemblies = new HashSet<string>();
		public void AddProjectDlls(OpenedFile file)
		{
			var project = SD.ProjectService.FindProjectContainingFile(file.FileName);
			if (project == null)
				return;

			var typeResolutionService = new TypeResolutionService(file.FileName);

			// Enumerate the project's referenced assemblies from MSBuild's ResolveAssemblyReferences
			// target (the Roslyn-aligned reference source) instead of the old NRefactory
			// ICompilation.ReferencedAssemblies, which is null now that C# projects use Roslyn/LSP.
			foreach (var reference in project.ResolveAssemblyReferences(System.Threading.CancellationToken.None)) {
				string assemblyFileName = reference.FileName;

				if (string.IsNullOrEmpty(assemblyFileName) || !System.IO.File.Exists(assemblyFileName) || addedAssemblies.Contains(assemblyFileName))
					continue;

				try {
					// DO NOT USE Assembly.LoadFrom!!!
					// see http://community.sharpdevelop.net/forums/t/19968.aspx
					Assembly assembly = typeResolutionService.LoadAssembly(assemblyFileName);
					if (assembly == null) continue;

					string categoryName = StringParser.Parse(assembly.FullName.Split(new[] { ',' })[0]);
					var controlTypes = new List<Type>();
					foreach (var t in assembly.GetExportedTypes()) {
						if (IsControl(t))
							controlTypes.Add(t);
					}

					if (controlTypes.Count > 0) {
						items.Add(new WpfSideTabItem(categoryName));
						foreach (var t in controlTypes)
							items.Add(new WpfSideTabItem(categoryName, t));
						itemsView.View.Refresh();
					}

					addedAssemblies.Add(assemblyFileName);
				} catch (Exception ex) {
					WpfViewContent.DllLoadErrors.Add(new SDTask(new BuildError(assemblyFileName, ex.Message)));
				}
			}
		}

		void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			var item = toolbox.SelectedItem as WpfSideTabItem;
			if (toolService == null)
				return;

			// ListBox's own built-in Selector keeps tracking MouseMove and updating SelectedItem to
			// whatever row is under the cursor while the button is held - completely independent of
			// (and not suppressed by) WpfToolbox's own isDragging guard on OnPreviewMouseMove, since
			// that guard only protects THIS class's handler, not the ListBox's internal one. Once a
			// portable drag is actually under way, PortableDragDropOperation keeps routing every
			// subsequent MouseMove through WPF's normal event system (see OnPreviewMouseMove's own
			// comment) - which means the Selector goes on reassigning SelectedItem, and this handler
			// keeps firing, for the ENTIRE remaining duration of the drag as the pointer sweeps from
			// the toolbox towards the drop target. Each firing would overwrite CurrentTool away from
			// dragStartItem (the row actually pressed and already sealed into the DataObject),
			// breaking designPanel_DragOver's identity check (e.Data.GetData(this.GetType()) != this)
			// on every subsequent DragOver - even though OnPreviewMouseMove already reasserted
			// CurrentTool correctly right before DoDragDrop. dragStartItem is authoritative for the
			// life of the drag; ignore the Selector's own opinion until it ends.
			if (isDragging)
				return;

			toolService.CurrentTool = item?.Tool ?? toolService.PointerTool;
		}

		void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			ClearSelectedWinFormsTool();
			dragStartPoint = e.GetPosition(toolbox);

			// Latch WHICH row the press landed on, rather than reading toolbox.SelectedItem later
			// when the drag threshold is finally exceeded: the pointer has usually already moved
			// across other rows by then, and ListBox keeps moving its selection to whatever row is
			// under the cursor while the button is held - so SelectedItem at threshold time is
			// frequently a different control than the one the user actually grabbed (measured: a
			// press on "NumericUpDown" reported "Panel", then "RadioButton", as the pointer swept
			// upward toward the drop target). On Windows this is masked because DoDragDrop enters a
			// modal OLE loop that stops delivering mouse moves to the ListBox at all; the portable
			// drag loop (PortableDragDropOperation) keeps routing them, so the item has to be
			// captured up front here to get the same "you drag what you pressed on" behavior.
			// Fall back to SelectedItem when the press did not land on a draggable row at all (the
			// per-category "Pointer" row, a group header, or the empty area below the last item):
			// those have no tool attached, and treating them as "the user grabbed nothing" would
			// silently swallow a drag that a caller had already set up by selecting the item
			// explicitly. A press that DOES land on a draggable row always wins over the selection.
			var pressedItem = ResolveItemFromEventSource(e.OriginalSource);
			dragStartItem = IsDraggable(pressedItem) ? pressedItem : toolbox.SelectedItem as WpfSideTabItem;
		}

		static bool IsDraggable(WpfSideTabItem item)
		{
			return item != null && (item.Tool != null || item.WinFormsToolboxItem != null);
		}

		static WpfSideTabItem ResolveItemFromEventSource(object originalSource)
		{
			for (DependencyObject node = originalSource as DependencyObject; node != null; ) {
				if (node is ListBoxItem listBoxItem)
					return listBoxItem.DataContext as WpfSideTabItem;

				node = node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D
					? System.Windows.Media.VisualTreeHelper.GetParent(node)
					: LogicalTreeHelper.GetParent(node);
			}
			return null;
		}

		void OnPreviewMouseMove(object sender, MouseEventArgs e)
		{
			if (e.LeftButton != MouseButtonState.Pressed)
				return;

			// A portable (non-Windows) drag keeps pumping input through WPF's normal event system
			// while DoDragDrop blocks on its own nested DispatcherFrame, so this very handler is
			// re-entered on every mouse move for the whole duration of the drag it just started -
			// real OLE's native modal loop on Windows never delivers those moves here, which is why
			// this guard was never needed before. PortableDragDropOperation already fails the
			// nested DoDragDrop call closed (its own s_isRunning check), but that inner call still
			// RETURNS, so its finally would run ResetToolSelection() while the outer drag is still
			// in flight - switching CurrentTool back to the pointer tool, which Deactivates
			// CreateComponentTool and unsubscribes the DesignPanel.DragOver handler the in-flight
			// drag depends on. The drop then silently creates nothing. Ignore re-entrant moves.
			if (isDragging)
				return;

			Point position = e.GetPosition(toolbox);
			if (Math.Abs(position.X - dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
			    Math.Abs(position.Y - dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
				return;

			// dragStartItem (latched on mouse-down), NOT toolbox.SelectedItem - see
			// OnPreviewMouseLeftButtonDown's own comment on why the live selection is unreliable here.
			var item = dragStartItem;
			if (item == null || (item.Tool == null && item.WinFormsToolboxItem == null))
				return;

			if (item.WinFormsToolboxItem != null)
			{
				// Route through the real System.Drawing.Design.IToolboxService rather than
				// WpfDesign's CreateComponentTool - WinForms' ParentControlDesigner is what
				// actually creates the component on drop (see WpfSideTabItem.WinFormsToolboxItem's
				// doc comment). SetSelectedToolboxItem matches the real .NET toolbox drag
				// contract; ParentControlDesigner.OnDragEnter reads the item back from the data
				// object itself (via DeserializeToolboxItem), not from SetSelectedToolboxItem, but
				// setting it too keeps IToolboxService.GetSelectedToolboxItem consistent for any
				// other caller that asks it mid-drag (e.g. SetCursor's "is something selected?").
				var toolboxService = SD.Services.GetService(typeof(System.Drawing.Design.IToolboxService)) as System.Drawing.Design.IToolboxService;
				toolboxService?.SetSelectedToolboxItem(item.WinFormsToolboxItem);

				// WPF's own DataObject.SetData(Type, object) and Windows Forms' IDataObject.SetData
				// use the same format-name convention (Type.FullName), and LibreWinForms' portable
				// WindowsFormsHost forwards a WPF drop's data across that boundary format-by-format
				// (CreateFormsDragData) - so a plain WPF DataObject carrying this same format key
				// is all the WinForms side needs; no OLE marshaling is happening on either side.
				var data = new DataObject();
				data.SetData(typeof(System.Drawing.Design.ToolboxItem), item.WinFormsToolboxItem);

				isDragging = true;
				try {
					DragDrop.DoDragDrop(toolbox, data, DragDropEffects.Copy);
				} finally {
					isDragging = false;
					ResetToolSelection();
				}
				return;
			}

			// Between mouse-down and this threshold check, the ListBox's own selection-follows-
			// cursor behavior may have already fired OnSelectionChanged for whatever row the
			// pointer drifted across on its way here (the exact drift OnPreviewMouseLeftButtonDown's
			// own comment measured: "a press on NumericUpDown reported Panel, then RadioButton").
			// That leaves toolService.CurrentTool pointing at the DRIFTED row instead of
			// dragStartItem, the one actually pressed and about to be put in the DataObject below.
			// CreateComponentTool.designPanel_DragOver's own identity check
			// (e.Data.GetData(this.GetType()) != this) compares the DataObject's payload against
			// "this" - the instance Activate()'d via CurrentTool - so a mismatch here makes every
			// drop silently create nothing, regardless of which row the pointer actually lands on.
			// Re-assert CurrentTool from dragStartItem right before the data leaves this method, so
			// the tool that gets Activated always matches the tool being dragged.
			if (toolService != null)
				toolService.CurrentTool = item.Tool;

			var wpfData = new DataObject(item.Tool);

			if (item.Tool is CreateComponentTool componentTool)
			{
				wpfData.SetData(typeof(Type), componentTool.ComponentType);
				wpfData.SetData("ComponentTypeName", componentTool.ComponentType.FullName);
			}

			isDragging = true;
			try {
				DragDrop.DoDragDrop(toolbox, wpfData, DragDropEffects.Copy);
			}
			finally
			{
				isDragging = false;
				ResetToolSelection();
			}
		}

		void ResetToolSelection()
		{
			ClearSelectedWinFormsTool();
			if (toolService != null)
				toolService.CurrentTool = toolService.PointerTool;
			toolbox.SelectedIndex = 0;
			dragStartItem = null;
		}

		static void ClearSelectedWinFormsTool()
		{
			var toolboxService = SD.Services.GetService(typeof(System.Drawing.Design.IToolboxService)) as System.Drawing.Design.IToolboxService;
			toolboxService?.SetSelectedToolboxItem(null);
		}

		public object ToolboxControl {
			get {
				// AddWinFormsControls() no-ops if IToolboxService isn't registered in SD.Services
				// yet - construction order between WpfToolbox and FormsDesigner's static ctor
				// (which registers it) isn't guaranteed, since both are lazily-constructed
				// singletons touched on first use. Retry here (idempotent) so the Windows Forms
				// category still shows up if a .xaml file (which constructs WpfToolbox) happened
				// to be opened before the first WinForms designer file in this session.
				AddWinFormsControls();
				return toolbox;
			}
		}

		public IToolService ToolService {
			get { return toolService; }
			set {
				if (toolService != null)
					toolService.CurrentToolChanged -= OnCurrentToolChanged;

				toolService = value;

				if (toolService != null) {
					toolService.CurrentToolChanged += OnCurrentToolChanged;
					OnCurrentToolChanged(null, null);
				}
			}
		}

		void OnCurrentToolChanged(object sender, EventArgs e)
		{
			if (toolService == null)
				return;

			var toolToFind = toolService.CurrentTool == toolService.PointerTool ? null : toolService.CurrentTool;
			foreach (WpfSideTabItem item in items) {
				if (ReferenceEquals(item.Tool, toolToFind)) {
					toolbox.SelectedItem = item;
					return;
				}
			}

			toolbox.SelectedIndex = 0;
		}
	}
}
