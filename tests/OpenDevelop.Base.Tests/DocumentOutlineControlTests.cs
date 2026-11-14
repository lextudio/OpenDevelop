using System.Windows.Controls;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Widgets;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The shared Outline pad's selection contract. A host asks for a row by ELEMENT NAME (the
/// Properties pad, designer actions, and the host's own outline sync all do), while WinUI/Uno rows
/// are keyed by tree PATH; selecting by name used to find no row, and the empty result then cleared
/// the row the user had just clicked. The document root's own id is the empty string, which the
/// control used to filter out as "nothing selected", so "[Page]" could never stay selected either.
/// </summary>
public class DocumentOutlineControlTests
{
	static DocumentOutlineControl CreateOutline(string rootId, string childId)
	{
		var outline = new DocumentOutlineControl {
			// No glyphs: resolving one needs the running IDE's resource service.
			IconSelector = _ => null
		};
		var root = new DesignerElementNode { Id = rootId, Name = null, Type = "Page", Path = rootId };
		root.Children.Add(new DesignerElementNode { Id = childId, Name = "TitleText", Type = "TextBlock", Path = childId });
		outline.SetRoot(root);
		return outline;
	}

	[Xunit.UIFactAttribute]
	public void A_row_can_be_selected_by_element_name_when_ids_are_tree_paths()
	{
		var outline = CreateOutline(rootId: "0", childId: "1");

		outline.SelectNodeById("TitleText");

		Assert.Equal("TitleText", outline.SelectedNode?.Name);
		Assert.Equal("1", outline.Snapshot().SelectedId);
	}

	[Xunit.UIFactAttribute]
	public void Selecting_a_row_by_name_keeps_it_selected_instead_of_clearing_the_tree()
	{
		var outline = CreateOutline(rootId: "0", childId: "1");
		var row = (TreeViewItem)((TreeViewItem)outline.Items[0]).Items[0];
		row.IsSelected = true;

		// The host's sync asks for the element name, not the tree path.
		outline.SelectNodeById("TitleText");

		Assert.True(row.IsSelected);
		Assert.Equal("TitleText", outline.SelectedNode?.Name);
	}

	[Xunit.UIFactAttribute]
	public void The_document_root_row_can_be_selected_by_its_empty_id()
	{
		var outline = CreateOutline(rootId: "", childId: "1");

		outline.SelectNodeById("");

		var root = Assert.IsType<DesignerElementNode>(outline.SelectedNode);
		Assert.Equal("", root.Id);
		Assert.Equal("[Page]", DocumentOutlineControl.GetDisplayText(root));
		Assert.Equal("", outline.Snapshot().SelectedId);
	}
}
