using ICSharpCode.WinUIXamlDesigner;
using Xunit;

namespace OpenDevelop.WinUIXamlDocument.Tests;

/// <summary>
/// A Toolbox drop at a point is authored as a layout, not as a guess: the Grid cell from the
/// measured tracks, a Canvas offset, or a StackPanel child index (<see cref="WinUIXamlDropPlacement"/>),
/// written into the source by <see cref="WinUIXamlDocumentEditor.Insert(string, System.Xml.Linq.XElement, System.Xml.Linq.XElement, System.Collections.Generic.IReadOnlyList{System.ValueTuple{string, string}})"/>
/// as one undo step. These cases pin the rule the live surface and the DevFlow <c>drop</c> hook share.
/// </summary>
public class DropPlacementTests
{
	// A named Grid at (100, 200) measuring 480x320, with rows 40/280 and columns 100/380 - the
	// same shape SurfaceFrameTreeTests measures from a real child host.
	static readonly DropTrack[] Rows = { new(0, 40), new(40, 280) };
	static readonly DropTrack[] Columns = { new(0, 100), new(100, 380) };

	[Theory]
	[InlineData(110, 210, 0, 0)]   // inside the first cell
	[InlineData(190, 210, 0, 0)]   // just before the column divider (Grid-local x = 90)
	[InlineData(210, 210, 0, 1)]   // just past the column divider (Grid-local x = 110)
	[InlineData(110, 250, 1, 0)]   // just past the row divider (Grid-local y = 50)
	[InlineData(579, 519, 1, 1)]   // the far corner, still inside
	public void A_point_resolves_to_the_measured_Grid_cell(double x, double y, int row, int column)
	{
		var cell = WinUIXamlDropPlacement.GridCell(x, y, 100, 200, 480, 320, Rows, Columns);

		Assert.Equal((row, column), cell);
	}

	[Fact]
	public void A_point_outside_the_Grid_or_a_Grid_without_measured_tracks_claims_no_cell()
	{
		Assert.Null(WinUIXamlDropPlacement.GridCell(99, 200, 100, 200, 480, 320, Rows, Columns));
		Assert.Null(WinUIXamlDropPlacement.GridCell(100, 520, 100, 200, 480, 320, Rows, Columns)); // the far edge is exclusive
		Assert.Null(WinUIXamlDropPlacement.GridCell(150, 250, 100, 200, 480, 320, Array.Empty<DropTrack>(), Columns));
	}

	[Fact]
	public void The_default_cell_is_written_with_no_markup_and_any_other_cell_names_both_axes()
	{
		Assert.Empty(WinUIXamlDropPlacement.GridCellAttributes(0, 0));
		Assert.Equal(new[] { ("Grid.Column", "2") }, WinUIXamlDropPlacement.GridCellAttributes(0, 2));
		Assert.Equal(new[] { ("Grid.Row", "1"), ("Grid.Column", "2") }, WinUIXamlDropPlacement.GridCellAttributes(1, 2));
	}

	[Fact]
	public void A_Canvas_position_is_local_to_the_Canvas_rounded_and_never_negative()
	{
		Assert.Equal((12d, 20d), WinUIXamlDropPlacement.CanvasPosition(112, 220, 100, 200));
		Assert.Equal((13d, 7d), WinUIXamlDropPlacement.CanvasPosition(112.6, 206.6, 100, 200));
		Assert.Equal((0d, 0d), WinUIXamlDropPlacement.CanvasPosition(90, 190, 100, 200));

		var attributes = WinUIXamlDropPlacement.CanvasPositionAttributes(13, 7);
		Assert.Equal(new[] { ("Canvas.Left", "13"), ("Canvas.Top", "7") }, attributes);
	}

	[Fact]
	public void A_StackPanel_index_is_the_first_child_whose_midpoint_is_past_the_point()
	{
		// Three 30-unit-tall children at y = 0, 30, 60.
		var vertical = new[] { new DropChildBounds(0, 0, 100, 30), new DropChildBounds(0, 30, 100, 30), new DropChildBounds(0, 60, 100, 30) };

		Assert.Equal(0, WinUIXamlDropPlacement.StackIndex(10, 14, horizontal: false, vertical));
		Assert.Equal(1, WinUIXamlDropPlacement.StackIndex(10, 16, horizontal: false, vertical));
		Assert.Equal(2, WinUIXamlDropPlacement.StackIndex(10, 70, horizontal: false, vertical));
		Assert.Equal(3, WinUIXamlDropPlacement.StackIndex(10, 100, horizontal: false, vertical)); // past the last midpoint: append

		// The same children laid out left to right, where the x coordinate decides.
		var horizontal = vertical.Select((_, index) => new DropChildBounds(index * 30, 0, 30, 100)).ToArray();
		Assert.Equal(1, WinUIXamlDropPlacement.StackIndex(31, 10, horizontal: true, horizontal));
		Assert.Equal(0, WinUIXamlDropPlacement.StackIndex(10, 999, horizontal: true, horizontal));
		Assert.Equal(0, WinUIXamlDropPlacement.StackIndex(10, 10, horizontal: false, Array.Empty<DropChildBounds>()));
	}

	[Fact]
	public void Only_the_panels_with_an_unbounded_child_collection_are_drop_containers()
	{
		Assert.True(WinUIXamlDropPlacement.IsDropContainer("Grid"));
		Assert.True(WinUIXamlDropPlacement.IsDropContainer("StackPanel"));
		Assert.True(WinUIXamlDropPlacement.IsDropContainer("Canvas"));
		Assert.False(WinUIXamlDropPlacement.IsDropContainer("Button")); // one Content slot
		Assert.False(WinUIXamlDropPlacement.IsDropContainer("Page"));
	}

	const string GridSource =
		"<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
		"  <Grid x:Name=\"Layout\">\n" +
		"    <Button x:Name=\"First\" Content=\"First\" />\n" +
		"  </Grid>\n" +
		"</Page>\n";

	[Fact]
	public void An_insert_with_a_cell_and_a_position_lands_where_it_was_dropped_in_one_undo_step()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(GridSource, out var error), error);
		var grid = editor.FindElement("Layout");

		var inserted = editor.Insert("Button", grid, editor.FindElement("First"),
			WinUIXamlDropPlacement.GridCellAttributes(1, 1));
		var name = (string)inserted.Attribute(WinUIXamlDocumentEditor.NameDirective);

		Assert.Equal("1", (string)inserted.Attribute("Grid.Row"));
		Assert.Equal("1", (string)inserted.Attribute("Grid.Column"));
		// The authored order is the source order: the new element precedes the child it was
		// dropped above, which is what a StackPanel drop index means.
		Assert.Equal(0, grid.Elements().ToList().FindIndex(element => (string)element.Attribute(WinUIXamlDocumentEditor.NameDirective) == name));
		Assert.True(editor.CanUndo);
		Assert.True(editor.Undo());
		Assert.Equal(GridSource, editor.Text);
		Assert.False(editor.CanUndo);
	}

	[Fact]
	public void An_inserted_element_lands_on_its_own_line_at_the_sibling_indentation()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(GridSource, out _));
		var grid = editor.FindElement("Layout");

		// Appending after the last child must not abut it, and must not swallow the closing tag's line.
		var appended = editor.Insert("Border", grid, null, null);
		var appendedName = (string)appended.Attribute(WinUIXamlDocumentEditor.NameDirective);
		Assert.Contains("\n    <Border x:Name=\"" + appendedName + "\" />\n  </Grid>", editor.Text);

		// Landing before a child gets the same indentation, and leaves that child on its own line.
		var inserted = editor.Insert("Button", grid, editor.FindElement("First"), WinUIXamlDropPlacement.GridCellAttributes(1, 1));
		var name = (string)inserted.Attribute(WinUIXamlDocumentEditor.NameDirective);
		Assert.Contains("\n    <Button x:Name=\"" + name + "\" Grid.Row=\"1\" Grid.Column=\"1\" Content=\"Button\" />\n    <Button x:Name=\"First\"", editor.Text);
	}

	[Fact]
	public void An_insert_without_a_cell_writes_no_attached_properties_and_appends()
	{
		var editor = new WinUIXamlDocumentEditor();
		Assert.True(editor.Reset(GridSource, out _));
		var grid = editor.FindElement("Layout");

		var inserted = editor.Insert("Button", grid, null, WinUIXamlDropPlacement.GridCellAttributes(0, 0));

		Assert.Null(inserted.Attribute("Grid.Row"));
		Assert.Null(inserted.Attribute("Grid.Column"));
		Assert.Same(inserted, grid.Elements().Last());
	}
}
