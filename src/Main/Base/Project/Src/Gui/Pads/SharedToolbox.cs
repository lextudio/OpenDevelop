// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// The merged Toolbox engine: WpfToolbox (WpfDesign.AddIn, WPF + WinForms) and WinUIXamlToolbox
// (WinUIXamlDesigner.AddIn, WinUI/Uno) each used to own an independent copy of the exact same
// ListBox/grouping/drag-start/selection-follows-cursor state machine, down to identical bug
// fixes (virtualization-recycled-row hit-test, portable-drag re-entrant-move guard, ListBox's
// own Selector fighting the latched drag item) - clear evidence this was genuinely shared logic,
// not incidental duplication. This class owns that ONE state machine; each framework's own
// Toolbox facade (WpfToolbox/WinUIXamlToolbox) builds SharedToolboxItems from its own tool model
// (ITool, System.Drawing.Design.ToolboxItem, a WinUI catalog entry) and delegates to it, keeping
// its own external public surface (ToolboxControl, DragDataFormat, FindItem, ...) unchanged so
// no other file in the codebase needs to know this merge happened.
//
// A single ListBox instance is reused across every scope (WPF, WinForms, WinUI) rather than one
// per designer: SetActiveScopes swaps the CollectionViewSource's Filter right before a facade
// hands the control to its own ToolsContent, so the exact same instance shows only the
// categories relevant to whichever document is active - preserving today's behavior (a WPF
// document's Tools pad shows WPF + WinForms together via ISharedToolboxHost; a WinUI document's
// Tools pad shows only WinUI categories) without needing two live ListBoxes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace ICSharpCode.SharpDevelop.Gui
{
	/// <summary>A single Toolbox row, host-agnostic: the owning facade supplies
	/// <see cref="Payload"/> (its own tool/component identity) and the two delegates that know
	/// what to do with it - this class has no idea what an <c>ITool</c> or a WinForms
	/// <c>ToolboxItem</c> is.</summary>
	public sealed class SharedToolboxItem
	{
		public string CategoryName { get; }
		public string DisplayName { get; }
		public ImageSource Icon { get; }

		/// <summary>Which facade this item belongs to ("wpf", "winforms", "winui", ...) - used
		/// only by <see cref="SharedToolbox.SetActiveScopes"/> to filter the shared list, never
		/// interpreted otherwise.</summary>
		public string Scope { get; }

		/// <summary>The owning facade's own tool/component identity (an <c>ITool</c>, a
		/// <c>System.Drawing.Design.ToolboxItem</c>, a catalog entry, ...) - opaque here.</summary>
		public object Payload { get; }

		/// <summary>True for a real draggable row; false for the per-category "Pointer" row
		/// (no <see cref="Payload"/>, exists only to let the user switch back to selection mode).</summary>
		public bool IsDraggable => Payload != null;

		/// <summary>Fills in whatever <see cref="DataObject"/> formats this item's drop targets
		/// expect - replaces the WPF/WinForms/WinUI branches every existing OnPreviewMouseMove
		/// used to hardcode inline. Null for the non-draggable "Pointer" row.</summary>
		public Action<DataObject> PackDragData { get; }

		/// <summary>Raised when this item becomes the toolbox's current selection (e.g. WPF's
		/// <c>toolService.CurrentTool = ...</c>). Null when the owning facade does not need one.</summary>
		public Action OnActivated { get; }

		public SharedToolboxItem(string categoryName, string displayName, string scope,
			ImageSource icon = null, object payload = null,
			Action<DataObject> packDragData = null, Action onActivated = null)
		{
			CategoryName = categoryName ?? throw new ArgumentNullException(nameof(categoryName));
			DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
			Scope = scope ?? throw new ArgumentNullException(nameof(scope));
			Icon = icon;
			Payload = payload;
			PackDragData = packDragData;
			OnActivated = onActivated;
		}

		public override string ToString() => DisplayName;
	}

	/// <summary>The merged Toolbox pad engine - see this file's own header comment for why this
	/// exists and how scoping keeps today's per-designer filtering behavior unchanged.</summary>
	public sealed class SharedToolbox : IFilterableToolbox
	{
		static SharedToolbox instance;

		public static SharedToolbox Instance {
			get {
				SD.MainThread.VerifyAccess();
				return instance ??= new SharedToolbox();
			}
		}

		readonly ListBox toolbox = new();
		readonly CollectionViewSource itemsView = new();
		readonly List<SharedToolboxItem> items = new();
		HashSet<string> activeScopes;
		string filterText = "";

		Point dragStartPoint;
		SharedToolboxItem dragStartItem;
		// Guards OnPreviewMouseMove against the re-entrant moves a portable drag delivers while
		// DoDragDrop is blocked - a portable (non-Windows) drag keeps pumping input through WPF's
		// normal event system while DoDragDrop blocks on its own nested DispatcherFrame, so this
		// very handler would otherwise be re-entered on every mouse move for the whole duration
		// of the drag it just started (real OLE's native modal loop on Windows never delivers
		// those moves here, which is why this guard was never needed there).
		bool isDragging;

		public event EventHandler<SharedToolboxItem> SelectionChanged;

		SharedToolbox()
		{
			itemsView.Source = items;
			itemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SharedToolboxItem.CategoryName)));
			itemsView.Filter += (_, e) => {
				var item = (SharedToolboxItem)e.Item;
				e.Accepted = (activeScopes == null || activeScopes.Contains(item.Scope))
					&& (String.IsNullOrEmpty(filterText)
						|| item.DisplayName.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0
						|| item.CategoryName.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0);
			};

			// Disabled rather than left to the default: ItemContainerGenerator.ContainerFromItem
			// was confirmed (via direct hit-testing, in both of this class's predecessors) to
			// sometimes report a container for a virtualized/recycled row whose actual on-screen
			// position doesn't match where that item renders once the list is scrolled deep
			// enough - a real click at the reported bounds then lands on a different row.
			// DevFlow's toolbox-bounds queries work around this by walking the live visual tree
			// instead of trusting the generator, which only finds a correct answer if every item
			// is actually realized - guaranteed by disabling virtualization here. The combined
			// list is small enough (a few hundred items across every scope) that virtualization
			// has no real benefit.
			VirtualizingPanel.SetIsVirtualizing(toolbox, false);
			toolbox.ItemsSource = itemsView.View;
			toolbox.Tag = this;
			toolbox.ItemTemplate = CreateItemTemplate();
			toolbox.GroupStyle.Add(CreateGroupStyle());
			toolbox.SelectionChanged += OnSelectionChanged;
			toolbox.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
			toolbox.PreviewMouseMove += OnPreviewMouseMove;
		}

		/// <summary>The shared WPF control - the same instance every caller gets back, filtered
		/// to whichever scopes were last activated via <see cref="SetActiveScopes"/>.</summary>
		public object ToolboxControl => toolbox;

		public SharedToolboxItem SelectedItem => toolbox.SelectedItem as SharedToolboxItem;
		public string FilterText => filterText;
		public int VisibleItemCount => itemsView.View.Cast<object>().Count();

		public int ItemCount(string scope) => items.Count(item => item.Scope == scope);

		public int GroupCount {
			get {
				itemsView.View?.Refresh();
				return itemsView.View?.Groups?.Count ?? 0;
			}
		}

		/// <summary>Adds items to the shared list (idempotency, e.g. "have I already added this
		/// project DLL's controls", is the calling facade's responsibility - mirrors both
		/// predecessors, which already tracked that themselves).</summary>
		public void AddItems(IEnumerable<SharedToolboxItem> newItems)
		{
			foreach (var item in newItems)
				if (!items.Any(existing => existing.Scope == item.Scope
					&& existing.CategoryName == item.CategoryName
					&& existing.DisplayName == item.DisplayName))
					items.Add(item);
			// List<T> raises no collection-change notification, so the CollectionViewSource.View
			// bound as the ListBox's ItemsSource won't pick up these .Add()s on its own.
			itemsView.View.Refresh();
		}

		/// <summary>Applies the same case-insensitive display-name/category filter to whichever
		/// designer scopes are active. The owning pad may expose this through its search chrome.</summary>
		public void Filter(string text)
		{
			filterText = text?.Trim() ?? String.Empty;
			itemsView.View.Refresh();
			if (toolbox.SelectedItem is SharedToolboxItem selected && !itemsView.View.Contains(selected))
				toolbox.SelectedItem = null;
		}

		/// <summary>Looks a tool up by display name within one scope, so an insertion driven
		/// through DevFlow cannot succeed for a control that scope's Toolbox does not offer.</summary>
		public SharedToolboxItem FindItem(string scope, string displayName) =>
			items.FirstOrDefault(item => item.Scope == scope
				&& string.Equals(item.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));

		/// <summary>Looks a tool up by reference identity within one scope - for a facade (e.g.
		/// WpfToolbox's own <c>ToolService.CurrentToolChanged</c> handler) that already holds the
		/// exact payload instance (an <c>ITool</c>, a WinForms <c>ToolboxItem</c>, ...) and needs
		/// the row that wraps it, e.g. to keep the visible selection in sync with an externally
		/// driven tool change.</summary>
		public SharedToolboxItem FindByPayload(string scope, object payload) =>
			items.FirstOrDefault(item => item.Scope == scope && ReferenceEquals(item.Payload, payload));

		/// <summary>Selects a specific row directly (bypassing drag/click), e.g. to mirror an
		/// externally driven tool change back onto the visible selection.</summary>
		public void Select(SharedToolboxItem item) => toolbox.SelectedItem = item;

		/// <summary>Filters the shared list down to the given scopes and selects that scope's
		/// first ("Pointer") row - call this right before handing <see cref="ToolboxControl"/> to
		/// an <see cref="IToolsHost.ToolsContent"/> caller, so the one shared ListBox shows only
		/// the categories relevant to whichever document is actually active.</summary>
		public void SetActiveScopes(params string[] scopes)
		{
			activeScopes = new HashSet<string>(scopes, StringComparer.Ordinal);
			itemsView.View.Refresh();
			SelectFirstInActiveScope();
		}

		void SelectFirstInActiveScope()
		{
			var first = items.FirstOrDefault(item => activeScopes.Contains(item.Scope));
			toolbox.SelectedItem = first;
		}

		/// <summary>Resets the selection back to whichever scope's first ("Pointer") row is
		/// active - called by a facade after a drop completes, mirroring both predecessors'
		/// ResetToolSelection.</summary>
		public void ResetSelection() => SelectFirstInActiveScope();

		static DataTemplate CreateItemTemplate()
		{
			var iconImage = new FrameworkElementFactory(typeof(Image));
			iconImage.SetValue(FrameworkElement.WidthProperty, 16d);
			iconImage.SetValue(FrameworkElement.HeightProperty, 16d);
			iconImage.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
			iconImage.SetBinding(Image.SourceProperty, new Binding(nameof(SharedToolboxItem.Icon)));

			var text = new FrameworkElementFactory(typeof(TextBlock));
			text.SetBinding(TextBlock.TextProperty, new Binding(nameof(SharedToolboxItem.DisplayName)));
			text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

			var panel = new FrameworkElementFactory(typeof(StackPanel));
			panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
			panel.AppendChild(iconImage);
			panel.AppendChild(text);

			return new DataTemplate(typeof(SharedToolboxItem)) { VisualTree = panel };
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

		void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			// ListBox's own built-in Selector keeps tracking MouseMove and updating SelectedItem
			// to whatever row is under the cursor while the button is held - completely
			// independent of (and not suppressed by) isDragging's guard on
			// OnPreviewMouseMove, since that guard only protects this class's own handler, not
			// the ListBox's internal one. Once a portable drag is actually under way, it keeps
			// routing every subsequent MouseMove through WPF's normal event system, so the
			// Selector goes on reassigning SelectedItem for the ENTIRE remaining duration of the
			// drag - ignore its opinion until the drag ends; dragStartItem is authoritative.
			if (isDragging)
				return;
			(toolbox.SelectedItem as SharedToolboxItem)?.OnActivated?.Invoke();
			SelectionChanged?.Invoke(this, toolbox.SelectedItem as SharedToolboxItem);
		}

		void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			dragStartPoint = e.GetPosition(toolbox);

			// Latch WHICH row the press landed on, rather than reading toolbox.SelectedItem later
			// when the drag threshold is finally exceeded: the pointer has usually already moved
			// across other rows by then, and ListBox keeps moving its selection to whatever row
			// is under the cursor while the button is held - so SelectedItem at threshold time is
			// frequently a different control than the one the user actually grabbed. Fall back to
			// SelectedItem when the press did not land on a draggable row at all (the "Pointer"
			// row, a group header, or the empty area below the last item).
			var pressedItem = ResolveItemFromEventSource(e.OriginalSource);
			dragStartItem = pressedItem?.IsDraggable == true ? pressedItem : toolbox.SelectedItem as SharedToolboxItem;
		}

		static SharedToolboxItem ResolveItemFromEventSource(object originalSource)
		{
			for (var node = originalSource as DependencyObject; node != null;) {
				if (node is ListBoxItem listBoxItem)
					return listBoxItem.DataContext as SharedToolboxItem;
				node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
					? VisualTreeHelper.GetParent(node)
					: LogicalTreeHelper.GetParent(node);
			}
			return null;
		}

		void OnPreviewMouseMove(object sender, MouseEventArgs e)
		{
			if (isDragging || e.LeftButton != MouseButtonState.Pressed)
				return;

			var position = e.GetPosition(toolbox);
			if (Math.Abs(position.X - dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
				&& Math.Abs(position.Y - dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
				return;

			// dragStartItem (latched on mouse-down), NOT toolbox.SelectedItem - see
			// OnPreviewMouseLeftButtonDown's own comment on why the live selection is unreliable.
			var item = dragStartItem;
			if (item?.IsDraggable != true)
				return;

			toolbox.SelectedItem = item;
			item.OnActivated?.Invoke();

			var data = new DataObject();
			item.PackDragData?.Invoke(data);

			isDragging = true;
			try {
				DragDrop.DoDragDrop(toolbox, data, DragDropEffects.Copy);
			} finally {
				isDragging = false;
				ResetSelection();
				dragStartItem = null;
			}
		}
	}
}
