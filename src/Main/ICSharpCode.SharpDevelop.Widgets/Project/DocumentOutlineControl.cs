// Shared Document Outline pad control: shows the design surface's element tree (the common
// DesignerElementNode model from the out-of-process designer protocol, doc/technotes/
// designer-common.md) as a TreeView, and drives selection back into the designer.
//
// All three designer backends (WinForms, WinUI/Uno, WPF once isolated) feed this control with
// the protocol's element tree; selection is one-way here - the host (design surface) owns the
// real selection state and this control only mirrors it.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Widgets
{
	/// <summary>
	/// VS-style document outline for visual designers: a tree of the designed document's
	/// elements (name + type), with selection synchronized to the design surface.
	/// </summary>
	public sealed class DocumentOutlineControl : TreeView
	{
		/// <summary>Raised when the user (or a programmatic selection) picks a node;
		/// <see cref="SelectedNode"/> holds the picked element.</summary>
		public event EventHandler SelectionCommitted;

		public DesignerElementNode SelectedNode => (SelectedItem as TreeViewItem)?.Tag as DesignerElementNode;

		/// <summary>Optional per-node context menu factory (e.g. designer-specific commands).</summary>
		public Func<DesignerElementNode, ContextMenu> ContextMenuFactory { get; set; }

		public DocumentOutlineControl()
		{
			// A selection change is the single commit path, whether the user clicked a node or
			// SelectNodeById picked it programmatically (IsSelected=true raises the same event) -
			// otherwise a real click would never reach the consumers (they only subscribe to
			// SelectionCommitted).
			SelectedItemChanged += (_, _) => SelectionCommitted?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>Shows a new element tree. Collapses nothing and keeps the current
		/// selection if the tree still contains it; otherwise clears the selection.</summary>
		public void SetRoot(DesignerElementNode root)
		{
			if (root == null) {
				Items.Clear();
				return;
			}
			var keepId = SelectedNode?.Id;
			Items.Clear();
			var item = CreateItem(root);
			Items.Add(item);
			if (keepId != null)
				SelectNodeById(keepId);
		}

		/// <summary>Programmatically selects the node with the given id (no-op when absent).
		/// Goes through the same <see cref="SelectionCommitted"/> path as a user click. NULL
		/// means "no selection" - the empty string is NOT equivalent: a document ROOT's id is
		/// itself "" (paths are built root-first), so treating "" the same as null here made the
		/// root unselectable from the Outline pad (a real bug this fixes) even after
		/// WpfSurfaceDesignerControl/WpfSurfaceHostService's own null-vs-empty root fix, since
		/// this control's own guard never let the id through in the first place.</summary>
		public void SelectNodeById(string? id)
		{
			if (id == null || Items.Count == 0)
				return;
			var match = FindNode((TreeViewItem)Items[0], id);
			if (match != null) {
				match.IsSelected = true;
				match.BringIntoView();
			}
		}

		/// <summary>Selects nothing.</summary>
		public void ClearSelection()
		{
			if (SelectedItem is TreeViewItem selected)
				selected.IsSelected = false;
		}

		TreeViewItem CreateItem(DesignerElementNode node)
		{
			var item = new TreeViewItem { Tag = node, IsExpanded = true };
			if (ContextMenuFactory != null)
				item.ContextMenu = ContextMenuFactory(node);

			// Name in regular weight, type in gray small text next to it (like the Xceed
			// events row); unnamed elements show only their type.
			var header = new StackPanel { Orientation = Orientation.Horizontal };
			if (!string.IsNullOrEmpty(node.Name)) {
				header.Children.Add(new TextBlock { Text = node.Name, VerticalAlignment = VerticalAlignment.Center });
				header.Children.Add(new TextBlock {
					Text = "  " + node.Type,
					VerticalAlignment = VerticalAlignment.Center,
					FontSize = 11,
					Foreground = Brushes.Gray
				});
			} else {
				header.Children.Add(new TextBlock { Text = node.Type, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray });
			}
			item.Header = header;

			foreach (var child in node.Children) {
				if (!child.IsDesignable)
					continue;
				item.Items.Add(CreateItem(child));
			}
			return item;
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
	}
}
