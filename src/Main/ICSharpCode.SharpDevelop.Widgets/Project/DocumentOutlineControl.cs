// Shared Document Outline pad control: shows the design surface's element tree (the common
// DesignerElementNode model from the out-of-process designer protocol, doc/technotes/
// designer-common.md) as a TreeView, and drives selection back into the designer.
//
// All three designer backends (WinForms, WinUI/Uno, WPF once isolated) feed this control with
// the protocol's element tree; selection is one-way here - the host (design surface) owns the
// real selection state and this control only mirrors it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

using ICSharpCode.Core.Presentation;

using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Widgets
{
	/// <summary>
	/// VS-style document outline for visual designers: a tree of the designed document's
	/// elements (name + type), with selection synchronized to the design surface.
	/// </summary>
	public sealed class DocumentOutlineControl : TreeView
	{
		bool rebuilding;
		bool applyingSelection;
		readonly List<string> selectedIds = new();
		/// <summary>Raised when the user (or a programmatic selection) picks a node;
		/// <see cref="SelectedNode"/> holds the picked element.</summary>
		public event EventHandler SelectionCommitted;

		/// <summary>Raised when a row's design-time hidden or locked toggle is clicked, with the flag
		/// (<see cref="HiddenFlag"/> or <see cref="LockedFlag"/>) and its requested value. The tree
		/// itself changes only when the backend publishes the next snapshot.</summary>
		public event EventHandler<(DesignerElementNode Node, string Flag, bool Value)> DesignTimeFlagToggled;

		/// <summary>Whether rows can be dragged onto other rows to move elements (the in-process WPF
		/// Outline's drag and drop). Off by default: a backend opts in when it handles
		/// <see cref="MoveRequested"/>.</summary>
		public bool AllowElementMoves {
			get => allowElementMoves;
			set {
				allowElementMoves = value;
				AllowDrop = value || allowElementCopies;
				// The handlers are attached only for an opted-in outline: a backend with its own
				// outline drag and drop (WinUI reorders through its own DragOver/Drop) keeps an event
				// chain exactly as before.
				if (value && !moveHandlersAttached) {
					moveHandlersAttached = true;
					AttachMoveHandlers();
				}
			}
		}
		bool allowElementMoves;

		/// <summary>Whether holding Ctrl while dragging a row requests a copy instead of a move. Off
		/// by default: a backend opts in when it handles <see cref="CopyRequested"/>.</summary>
		public bool AllowElementCopies {
			get => allowElementCopies;
			set {
				allowElementCopies = value;
				AllowDrop = value || allowElementMoves;
				if (value && !moveHandlersAttached) {
					moveHandlersAttached = true;
					AttachMoveHandlers();
				}
			}
		}
		bool allowElementCopies;
		bool moveHandlersAttached;

		/// <summary>Raised when a row is dropped: move <c>Node</c> into <c>Target</c>, right after
		/// <c>After</c> (null = first). The tree changes only with the backend's next snapshot.</summary>
		public event EventHandler<(DesignerElementNode Node, DesignerElementNode Target, DesignerElementNode? After)> MoveRequested;

		/// <summary>Raised when several selected rows are dropped together. The nodes preserve their
		/// displayed selection order; the backend must make the move one transaction.</summary>
		public event EventHandler<(IReadOnlyList<DesignerElementNode> Nodes, DesignerElementNode Target, DesignerElementNode? After)> MoveManyRequested;

		/// <summary>Raised when Ctrl-dragging a row drops it: copy <c>Node</c> into <c>Target</c>,
		/// right after <c>After</c> (null = first). The backend owns cloning and name uniquing.</summary>
		public event EventHandler<(DesignerElementNode Node, DesignerElementNode Target, DesignerElementNode? After)> CopyRequested;

		const string DragFormat = "OpenDevelop.DesignerOutlineNodeIds";
		Point dragStart;
		TreeViewItem? dragSource;
		IReadOnlyList<string>? dragIds;
		TreeViewItem? dropHighlight;

		public const string HiddenFlag = "Hidden";
		public const string LockedFlag = "Locked";
		/// <summary>The toggles' names, for automation.</summary>
		public const string HiddenToggleName = "DesignTimeHiddenToggle";
		public const string LockedToggleName = "DesignTimeLockedToggle";

		public DesignerElementNode SelectedNode => (SelectedItem as TreeViewItem)?.Tag as DesignerElementNode;
		/// <summary>All selected displayed nodes, with the TreeView's current row first.</summary>
		public IReadOnlyList<DesignerElementNode> SelectedNodes => selectedIds
			.Select(id => FindDisplayed(id)?.Tag as DesignerElementNode)
			.Where(node => node != null)
			.Cast<DesignerElementNode>()
			.ToArray();

		/// <summary>Optional per-node context menu factory (e.g. designer-specific commands).</summary>
		public Func<DesignerElementNode, ContextMenu> ContextMenuFactory { get; set; }

		/// <summary>Picks each row's icon. Defaults to the VS Image Library glyph for the element
		/// type (<see cref="DesignerTypeIcons"/>); a designer with its own icon set (WinForms'
		/// toolbox icons) replaces it. Set before the first <see cref="SetRoots"/>.</summary>
		public Func<DesignerElementNode, ImageSource>? IconSelector { get; set; }

		/// <summary>The framework's control mapper, consulted before the shared name table so a
		/// type only that framework's addin knows ("GtkEntry") still resolves to its concept's glyph.
		/// Null for a designer whose outline is fed by an IconSelector of its own.</summary>
		public IDesignerControlMapper IconMapper { get; set; }

		/// <summary>Resolves a row's glyph: the IconSelector when one is set (a designer with real
		/// icons of its own), otherwise the shared concept table through <see cref="IconMapper"/>.
		/// </summary>
		public ImageSource? ResolveIcon(DesignerElementNode node)
		{
			if (node == null)
				return null;
			if (IconSelector != null)
				return IconSelector(node);
			return DesignerTypeIcons.GetIcon(node, IconMapper);
		}

		public DocumentOutlineControl()
		{
			// A selection change is the single commit path, whether the user clicked a node or
			// SelectNodeById picked it programmatically (IsSelected=true raises the same event) -
			// otherwise a real click would never reach the consumers (they only subscribe to
			// SelectionCommitted).
			SelectedItemChanged += (_, _) => {
				if (rebuilding || applyingSelection)
					return;
				if (SelectedNode is { } selected)
					SetSelectedIds(new[] { selected.Id }, selectPrimary: false);
				SelectionCommitted?.Invoke(this, EventArgs.Empty);
			};
			PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
		}

		void OnPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0
				|| ItemAt(e.OriginalSource)?.Tag is not DesignerElementNode node)
				return;
			var ids = selectedIds.ToList();
			if (!ids.Remove(node.Id))
				ids.Add(node.Id);
			SetSelectedIds(ids, selectPrimary: true);
			e.Handled = true;
			if (!rebuilding)
				SelectionCommitted?.Invoke(this, EventArgs.Empty);
		}

		void AttachMoveHandlers()
		{
			// The Ctrl-click multi-selection handler claims the initial down event. Listen even when it
			// did so that Ctrl-drag remains the existing single-row copy gesture.
			AddHandler(System.Windows.Input.Mouse.PreviewMouseDownEvent, new System.Windows.Input.MouseButtonEventHandler((_, e) => {
				if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
					return;
				dragStart = e.GetPosition(this);
				dragSource = AllowElementMoves || AllowElementCopies ? ItemAt(e.OriginalSource) : null;
				dragIds = dragSource?.Tag is DesignerElementNode node ? DragNodeIds(node) : null;
			}), true);
			PreviewMouseMove += (_, e) => {
				if (dragSource?.Tag is not DesignerElementNode node || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
					return;
				var delta = e.GetPosition(this) - dragStart;
				if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
					return;
				// Copy retains its established one-row meaning even when the row is part of a move
				// multi-selection; only a plain drag carries all selected outer roots.
				var ids = IsCopyDrag() ? new[] { node.Id } : dragIds ?? DragNodeIds(node);
				dragSource = null;
				dragIds = null;
				DragDrop.DoDragDrop(this, new DataObject(DragFormat, ids.ToArray()), AllowElementMoves && AllowElementCopies
					? DragDropEffects.Move | DragDropEffects.Copy
					: AllowElementCopies ? DragDropEffects.Copy : DragDropEffects.Move);
				SetDropHighlight(null);
			};
			// Only this outline's own row drags are claimed; any other drag passes through untouched.
			DragOver += (_, e) => {
				if (!(AllowElementMoves || AllowElementCopies) || e.Data.GetData(DragFormat) is not string[] ids || ids.Length == 0)
					return;
				var drop = ResolveDrop(ids, e);
				e.Effects = drop != null ? IsCopyDrag(e) ? DragDropEffects.Copy : DragDropEffects.Move : DragDropEffects.None;
				if (e.Effects == DragDropEffects.Copy && ids.Length != 1)
					e.Effects = DragDropEffects.None;
				if (e.Effects == DragDropEffects.Copy && !AllowElementCopies || e.Effects == DragDropEffects.Move && !AllowElementMoves)
					e.Effects = DragDropEffects.None;
				SetDropHighlight(drop?.Highlight);
				e.Handled = true;
			};
			DragLeave += (_, _) => SetDropHighlight(null);
			Drop += (_, e) => {
				if (!(AllowElementMoves || AllowElementCopies) || e.Data.GetData(DragFormat) is not string[] ids || ids.Length == 0)
					return;
				SetDropHighlight(null);
				if (ResolveDrop(ids, e) is { } drop) {
					if (IsCopyDrag(e))
						RequestCopy(ids[0], drop.Target.Id, drop.After?.Id);
					else
						RequestMove(ids, drop.Target.Id, drop.After?.Id);
				}
				e.Handled = true;
			};
		}

		static bool IsCopyDrag(DragEventArgs e)
			=> (e.KeyStates & DragDropKeyStates.ControlKey) != 0;

		static bool IsCopyDrag()
			=> (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;

		IReadOnlyList<string> DragNodeIds(DesignerElementNode dragged)
			=> selectedIds.Contains(dragged.Id) && selectedIds.Count > 1 ? selectedIds.ToArray() : new[] { dragged.Id };

		/// <summary>Raises <see cref="MoveRequested"/> for the displayed nodes with these ids, as a
		/// drop does. False when one is not displayed.</summary>
		public bool RequestMove(string nodeId, string targetId, string? afterId)
			=> RequestMove(new[] { nodeId }, targetId, afterId);

		/// <summary>Raises the appropriate Outline move event for displayed rows. A move is rejected
		/// before it reaches a backend unless every source and insertion anchor is still displayed.</summary>
		public bool RequestMove(IEnumerable<string> nodeIds, string targetId, string? afterId)
		{
			var ids = (nodeIds ?? Array.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
			var nodes = ids.Select(FindDisplayed).ToArray();
			var target = FindDisplayed(targetId);
			var after = afterId == null ? null : FindDisplayed(afterId);
			if (nodes.Length == 0 || nodes.Any(item => item?.Tag is not DesignerElementNode)
				|| target?.Tag is not DesignerElementNode container
				|| afterId != null && after?.Tag is not DesignerElementNode)
				return false;
			// A parent and its displayed child can both be Ctrl-selected. The child travels with the
			// parent, so sending both would make the host remove it twice; reduce to outer roots.
			var movedItems = nodes.Cast<TreeViewItem>().ToArray();
			var outerRoots = movedItems.Where(item => !movedItems.Any(other => other != item && IsAncestor(other, item))).ToHashSet();
			// Selection order is click order; insertion must instead retain document/Outline order.
			var moved = DisplayedItems().Where(outerRoots.Contains).Select(item => (DesignerElementNode)item.Tag).ToArray();
			if (moved.Length == 1)
				MoveRequested?.Invoke(this, (moved[0], container, after?.Tag as DesignerElementNode));
			else
				MoveManyRequested?.Invoke(this, (moved, container, after?.Tag as DesignerElementNode));
			return true;
		}

		static bool IsAncestor(TreeViewItem ancestor, TreeViewItem item)
		{
			for (var current = ItemsControl.ItemsControlFromItemContainer(item) as TreeViewItem; current != null;
				current = ItemsControl.ItemsControlFromItemContainer(current) as TreeViewItem)
				if (current == ancestor)
					return true;
			return false;
		}

		IEnumerable<TreeViewItem> DisplayedItems()
		{
			foreach (TreeViewItem root in Items)
				foreach (var item in DisplayedItems(root))
					yield return item;
		}

		static IEnumerable<TreeViewItem> DisplayedItems(TreeViewItem item)
		{
			yield return item;
			foreach (TreeViewItem child in item.Items.OfType<TreeViewItem>())
				foreach (var descendant in DisplayedItems(child))
					yield return descendant;
		}

		/// <summary>Raises <see cref="CopyRequested"/> for the displayed nodes with these ids, as a
		/// Ctrl-drop does. False when one is not displayed.</summary>
		public bool RequestCopy(string nodeId, string targetId, string? afterId)
		{
			var node = FindDisplayed(nodeId);
			var target = FindDisplayed(targetId);
			var after = afterId == null ? null : FindDisplayed(afterId);
			if (node?.Tag is not DesignerElementNode copied || target?.Tag is not DesignerElementNode container
				|| afterId != null && after?.Tag is not DesignerElementNode)
				return false;
			CopyRequested?.Invoke(this, (copied, container, after?.Tag as DesignerElementNode));
			return true;
		}

		/// <summary>Where a drop at the pointer goes, as the in-process Outline's drag tree decides:
		/// the top quarter of a row inserts before it, the bottom quarter after it (both into its
		/// parent), anything else into the row itself after its last child. Never into the dragged
		/// row or its own subtree.</summary>
		(DesignerElementNode Target, DesignerElementNode? After, TreeViewItem Highlight)? ResolveDrop(IReadOnlyCollection<string> draggedIds, DragEventArgs e)
		{
			var over = ItemAt(e.OriginalSource);
			if (over?.Tag is not DesignerElementNode hovered)
				return null;
			for (var item = over; item != null; item = ItemsControl.ItemsControlFromItemContainer(item) as TreeViewItem)
				if ((item.Tag as DesignerElementNode) is { } node && draggedIds.Contains(node.Id))
					return null;
			var header = (FrameworkElement)over.Header;
			var y = e.GetPosition(header).Y / Math.Max(1, header.ActualHeight);
			var parentItem = ItemsControl.ItemsControlFromItemContainer(over) as TreeViewItem;
			if ((y < 0.25 || y > 0.75) && parentItem?.Tag is DesignerElementNode parent) {
				var index = parentItem.Items.IndexOf(over) + (y > 0.75 ? 1 : 0);
				// Siblings shown before the insertion point, other than any dragged row.
				var previous = parentItem.Items.Cast<TreeViewItem>().Take(index)
					.Select(item => (DesignerElementNode)item.Tag).LastOrDefault(node => !draggedIds.Contains(node.Id) && !IsSynthetic(node));
				return (parent, previous, parentItem);
			}
			var lastChild = over.Items.Cast<TreeViewItem>().Select(item => (DesignerElementNode)item.Tag).LastOrDefault(node => !draggedIds.Contains(node.Id) && !IsSynthetic(node));
			return (hovered, lastChild, over);
		}

		/// <summary>A row the backend adds for structure rather than content - a ContextMenu or
		/// Header group, whose path segment starts with '@' - which is no insertion anchor.</summary>
		static bool IsSynthetic(DesignerElementNode node)
		{
			var segment = node.Id.Substring(node.Id.LastIndexOf(',') + 1);
			return segment.StartsWith("@", StringComparison.Ordinal);
		}

		void SetDropHighlight(TreeViewItem? item)
		{
			if (dropHighlight == item)
				return;
			if (dropHighlight?.Header is Panel previous)
				previous.Background = null;
			dropHighlight = item;
			if (item?.Header is Panel header)
				header.Background = new SolidColorBrush(SystemColors.HighlightColor) { Opacity = 0.3 };
		}

		static TreeViewItem? ItemAt(object source)
		{
			for (var current = source as DependencyObject; current != null; current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
				if (current is TreeViewItem item)
					return item;
			return null;
		}

		TreeViewItem? FindDisplayed(string id)
		{
			foreach (TreeViewItem root in Items)
				if (FindNode(root, id) is { } match)
					return match;
			return null;
		}

		/// <summary>Shows a new element tree. Collapses nothing and keeps the current
		/// selection if the tree still contains it; otherwise clears the selection.</summary>
		public void SetRoot(DesignerElementNode root)
		{
			SetRoots(root == null ? null : new[] { root });
		}

		/// <summary>Shows a new element forest (multiple top-level nodes, e.g. a XAML file's
		/// root element plus sibling property elements). Same selection-keeping behavior as
		/// <see cref="SetRoot"/>; a NULL/empty sequence just clears the tree.</summary>
		public void SetRoots(IEnumerable<DesignerElementNode> roots)
		{
			var keepIds = selectedIds.ToArray();
			rebuilding = true;
			try {
				Items.Clear();
				if (roots != null)
				{
					foreach (var root in roots)
						Items.Add(CreateItem(root));
				}
				SetSelectedIds(keepIds, selectPrimary: true);
			} finally {
				rebuilding = false;
			}
		}

		/// <summary>Programmatically selects the node with the given id (no-op when absent).
		/// Goes through the same <see cref="SelectionCommitted"/> path as a user click. NULL
		/// means "no selection" - the empty string is NOT equivalent: a document ROOT's id is
		/// itself "" (paths are built root-first), so treating "" the same as null here made the
		/// root unselectable from the Outline pad (a real bug this fixes) even after
		/// WpfSurfaceDesignerControl/WpfSurfaceHostService's own null-vs-empty root fix, since
		/// this control's own guard never let the id through in the first place.</summary>
		public void SelectNodeById(string? id)
			=> SelectNodesById(id == null ? Array.Empty<string>() : new[] { id });

		/// <summary>Selects exactly the displayed ids. The first becomes the ordinary TreeView
		/// selection; additional rows remain visibly selected for a Ctrl multi-row drag.</summary>
		public void SelectNodesById(IEnumerable<string> ids)
		{
			SetSelectedIds(ids, selectPrimary: true);
		}

		/// <summary>Selects nothing.</summary>
		public void ClearSelection()
		{
			SetSelectedIds(Array.Empty<string>(), selectPrimary: false);
		}

		void SetSelectedIds(IEnumerable<string> ids, bool selectPrimary)
		{
			var next = (ids ?? Array.Empty<string>()).Where(id => !string.IsNullOrEmpty(id))
				.Distinct(StringComparer.Ordinal).Where(id => FindDisplayed(id) != null).ToArray();
			selectedIds.Clear();
			selectedIds.AddRange(next);
			applyingSelection = true;
			try {
				foreach (TreeViewItem root in Items)
					RefreshSelectionVisuals(root);
				if (selectPrimary && next.FirstOrDefault() is { } primary && FindDisplayed(primary) is { } item) {
					item.IsSelected = true;
					item.BringIntoView();
				} else if (next.Length == 0 && SelectedItem is TreeViewItem selected)
					selected.IsSelected = false;
			} finally {
				applyingSelection = false;
			}
		}

		void RefreshSelectionVisuals(TreeViewItem item)
		{
			var isSecondary = item.Tag is DesignerElementNode node && selectedIds.Contains(node.Id)
				&& !StringComparer.Ordinal.Equals(node.Id, SelectedNode?.Id);
			// ClearValue, never "= null": assigning null sets a local null brush that overrides the inherited
			// Foreground, and every row's TextBlock then drew no text - only the icons were left - after the
			// first selection change.
			if (isSecondary) {
				item.Background = SystemColors.HighlightBrush;
				item.Foreground = SystemColors.HighlightTextBrush;
			} else {
				item.ClearValue(Control.BackgroundProperty);
				item.ClearValue(Control.ForegroundProperty);
			}
			foreach (TreeViewItem child in item.Items.OfType<TreeViewItem>())
				RefreshSelectionVisuals(child);
		}

		/// <summary>Returns the nodes the control is actually displaying, in depth-first order.
		/// This intentionally walks the realized tree rather than retaining the last protocol tree:
		/// non-designable children are filtered while rows are created, so the latter is not a
		/// faithful representation of what the Document Outline pad shows.</summary>
		public DocumentOutlineSnapshot Snapshot()
		{
			var nodes = new List<DocumentOutlineSnapshotNode>();
			foreach (TreeViewItem root in Items)
				CollectSnapshotNodes(root, nodes, depth: 0);
			return new DocumentOutlineSnapshot {
				RootCount = Items.Count,
				Nodes = nodes,
				SelectedId = SelectedNode?.Id
			};
		}

		TreeViewItem CreateItem(DesignerElementNode node)
		{
			var item = new TreeViewItem { Tag = node, IsExpanded = true };
			if (ContextMenuFactory != null)
				item.ContextMenu = ContextMenuFactory(node);

			// Visual Studio's Document Outline row: the element type's glyph, then the element's name,
			// or "[Type]" when it has none. The type itself is the tooltip.
			var header = new StackPanel { Orientation = Orientation.Horizontal };
			var icon = ResolveIcon(node);
			if (icon != null) {
				header.Children.Add(new Image {
					Source = icon,
					Width = 16,
					Height = 16,
					Margin = new Thickness(0, 0, 4, 0),
					VerticalAlignment = VerticalAlignment.Center
				});
			}
			header.Children.Add(new TextBlock { Text = GetDisplayText(node), VerticalAlignment = VerticalAlignment.Center });
			// The in-process WPF Outline's eye and lock toggles, offered only where the backend
			// publishes the flags.
			if (node.IsDesignTimeHidden is { } hidden)
				header.Children.Add(CreateFlagToggle(node, HiddenFlag, HiddenToggleName, hidden, "Icons.16x16.HideMember", "Hide at design time"));
			if (node.IsDesignTimeLocked is { } locked)
				header.Children.Add(CreateFlagToggle(node, LockedFlag, LockedToggleName, locked, locked ? "Icons.16x16.Lock" : "Icons.16x16.Unlock", "Lock at design time"));
			if (!string.IsNullOrEmpty(node.Type))
				header.ToolTip = node.Type;
			item.Header = header;

			foreach (var child in node.Children) {
				if (!child.IsDesignable)
					continue;
				item.Items.Add(CreateItem(child));
			}
			return item;
		}

		ToggleButton CreateFlagToggle(DesignerElementNode node, string flag, string name, bool isSet, string iconKey, string toolTip)
		{
			var image = new Image { Width = 12, Height = 12 };
			try {
				image.Source = PresentationResourceService.GetImageSource(iconKey);
			} catch {
				image.Source = null;
			}
			var toggle = new ToggleButton {
				Name = name,
				Content = image,
				IsChecked = isSet,
				ToolTip = toolTip,
				Focusable = false,
				Padding = new Thickness(1),
				Margin = new Thickness(6, 0, 0, 0),
				// An unset flag stays faint, as in Visual Studio, so the row reads as normal.
				Opacity = isSet ? 1 : 0.35,
				VerticalAlignment = VerticalAlignment.Center
			};
			toggle.Click += (_, e) => {
				e.Handled = true;
				DesignTimeFlagToggled?.Invoke(this, (node, flag, toggle.IsChecked == true));
			};
			return toggle;
		}

		/// <summary>A row's text as Visual Studio shows it: the element's name, or its bare type in
		/// brackets ("[Button]") when it has none.</summary>
		public static string GetDisplayText(DesignerElementNode node)
		{
			if (!string.IsNullOrEmpty(node.Name))
				return node.Name;
			var type = DesignerTypeIcons.GetElementTypeName(node.Type);
			return "[" + (type.Length > 0 ? type : node.Type) + "]";
		}

		static TreeViewItem FindNode(TreeViewItem item, string id)
		{
			if ((item.Tag as DesignerElementNode)?.Id == id)
				return item;
			foreach (object child in item.Items) {
				if (child is TreeViewItem childItem && FindNode(childItem, id) is { } match)
					return match;
			}
			return null;
		}

		static void CollectSnapshotNodes(TreeViewItem item, List<DocumentOutlineSnapshotNode> nodes, int depth)
		{
			if (item.Tag is DesignerElementNode node) {
				nodes.Add(new DocumentOutlineSnapshotNode {
					Id = node.Id,
					Name = node.Name,
					Type = node.Type,
					Label = GetDisplayText(node),
					Depth = depth
				});
			}
			foreach (TreeViewItem child in item.Items)
				CollectSnapshotNodes(child, nodes, depth + 1);
		}
	}

	/// <summary>Read-only, UI-neutral projection of a live <see cref="DocumentOutlineControl"/>.
	/// Used by DevFlow diagnostics so every designer reports the same visible element tree.</summary>
	public sealed class DocumentOutlineSnapshot
	{
		public int RootCount { get; set; }
		public List<DocumentOutlineSnapshotNode> Nodes { get; set; } = new List<DocumentOutlineSnapshotNode>();
		public string SelectedId { get; set; }
	}

	public sealed class DocumentOutlineSnapshotNode
	{
		public string Id { get; set; }
		public string Name { get; set; }
		public string Type { get; set; }
		public string Label { get; set; }
		public int Depth { get; set; }
	}
}
