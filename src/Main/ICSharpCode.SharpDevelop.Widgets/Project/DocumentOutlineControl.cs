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
		bool rebuilding;
		/// <summary>Raised when the user (or a programmatic selection) picks a node;
		/// <see cref="SelectedNode"/> holds the picked element.</summary>
		public event EventHandler SelectionCommitted;

		public DesignerElementNode SelectedNode => (SelectedItem as TreeViewItem)?.Tag as DesignerElementNode;

		/// <summary>Optional per-node context menu factory (e.g. designer-specific commands).</summary>
		public Func<DesignerElementNode, ContextMenu> ContextMenuFactory { get; set; }

		/// <summary>Picks each row's icon. Defaults to the VS Image Library glyph for the element
		/// type (<see cref="DocumentOutlineIcons"/>); a designer with its own icon set (WinForms'
		/// toolbox icons) replaces it. Set before the first <see cref="SetRoots"/>.</summary>
		public Func<DesignerElementNode, ImageSource> IconSelector { get; set; } = DocumentOutlineIcons.GetIcon;

		public DocumentOutlineControl()
		{
			// A selection change is the single commit path, whether the user clicked a node or
			// SelectNodeById picked it programmatically (IsSelected=true raises the same event) -
			// otherwise a real click would never reach the consumers (they only subscribe to
			// SelectionCommitted).
			SelectedItemChanged += (_, _) => { if (!rebuilding) SelectionCommitted?.Invoke(this, EventArgs.Empty); };
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
			var keepId = SelectedNode?.Id;
			rebuilding = true;
			try {
				Items.Clear();
				if (roots != null)
				{
					foreach (var root in roots)
						Items.Add(CreateItem(root));
				}
				if (keepId != null)
					SelectNodeById(keepId);
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
		{
			if (id == null || Items.Count == 0)
				return;
			foreach (TreeViewItem rootItem in Items)
			{
				var match = FindNode(rootItem, id);
				if (match != null)
				{
					match.IsSelected = true;
					match.BringIntoView();
					return;
				}
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

			// Visual Studio's Document Outline row: the element type's glyph, then the element's name,
			// or "[Type]" when it has none. The type itself is the tooltip.
			var header = new StackPanel { Orientation = Orientation.Horizontal };
			var icon = IconSelector?.Invoke(node);
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

		/// <summary>A row's text as Visual Studio shows it: the element's name, or its bare type in
		/// brackets ("[Button]") when it has none.</summary>
		public static string GetDisplayText(DesignerElementNode node)
		{
			if (!string.IsNullOrEmpty(node.Name))
				return node.Name;
			var type = DocumentOutlineIcons.GetElementTypeName(node.Type);
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
	}
}
