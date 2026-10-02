using ICSharpCode.GtkDesigner;
using Xunit;

namespace GtkDesigner.Tests;

public sealed class GtkDropPlannerTests
{
	// A 300x200 window holding a vertical box with three 300x40 rows at y = 0, 50, 100.
	static GtkDropNode VerticalBox() => new("window", "GtkWindow", 0, 0, 300, 200, new[] {
		new GtkDropNode("box", "GtkBox", 0, 0, 300, 150, new[] {
			new GtkDropNode("a", "GtkLabel", 0, 0, 300, 40, Array.Empty<GtkDropNode>()),
			new GtkDropNode("b", "GtkLabel", 0, 50, 300, 40, Array.Empty<GtkDropNode>()),
			new GtkDropNode("c", "GtkButton", 0, 100, 300, 40, Array.Empty<GtkDropNode>()),
		}, Orientation: "vertical")
	});

	[Theory]
	[InlineData(10, 0)]    // above a's middle
	[InlineData(30, 1)]    // below a's middle, above b's
	[InlineData(75, 2)]
	[InlineData(130, 3)]   // past the last child: append
	public void Box_IndexFollowsTheAxis(double y, int expected)
	{
		var plan = GtkDropPlanner.Plan(VerticalBox(), 150, y)!;
		Assert.Equal("box", plan.ContainerId);
		Assert.Equal(expected, plan.Index);
		Assert.Null(plan.Cell);
	}

	[Fact]
	public void Box_IndicatorIsALineInTheGap()
	{
		var plan = GtkDropPlanner.Plan(VerticalBox(), 150, 30)!;
		var (x, y, width, height) = plan.Indicator;
		Assert.Equal(0, x); Assert.Equal(300, width);
		Assert.Equal(44, y);            // midway between a's bottom (40) and b's top (50), minus half the line
		Assert.Equal(2, height);
	}

	[Fact]
	public void Box_DefaultOrientationIsHorizontal()
	{
		var row = new GtkDropNode("box", "GtkBox", 0, 0, 200, 40, new[] {
			new GtkDropNode("a", "GtkButton", 0, 0, 100, 40, Array.Empty<GtkDropNode>()),
			new GtkDropNode("b", "GtkButton", 100, 0, 100, 40, Array.Empty<GtkDropNode>()),
		});
		Assert.Equal(1, GtkDropPlanner.Plan(row, 90, 20)!.Index);
		Assert.Equal(2, GtkDropPlanner.Plan(row, 190, 20)!.Index);
	}

	// A grid: "a" at (0,0) and "b" at (0,1), each 100x30 with 10px spacing.
	static GtkDropNode Grid() => new("grid", "GtkGrid", 0, 0, 400, 200, new[] {
		new GtkDropNode("a", "GtkLabel", 0, 0, 100, 30, Array.Empty<GtkDropNode>(), Column: 0, Row: 0),
		new GtkDropNode("b", "GtkLabel", 0, 40, 100, 30, Array.Empty<GtkDropNode>(), Column: 0, Row: 1),
	});

	[Fact]
	public void Grid_PastTheLastColumn_IsTheNextColumn()
	{
		var plan = GtkDropPlanner.Plan(Grid(), 150, 10)!;
		Assert.Equal((1, 0), plan.Cell);
		Assert.Equal((100.0, 0.0, 100.0, 30.0), plan.Indicator);   // a new column the size of the last one
	}

	[Fact]
	public void Grid_OccupiedCell_MovesToTheFirstFreeRow()
	{
		Assert.Equal((0, 2), GtkDropPlanner.Plan(Grid(), 50, 10)!.Cell);   // on "a": (0,0) and (0,1) are taken
	}

	[Fact]
	public void PointOnALeaf_UsesItsContainer_AndNoContainerMeansNoPlan()
	{
		Assert.Equal("box", GtkDropPlanner.Plan(VerticalBox(), 150, 60)!.ContainerId);
		Assert.Null(GtkDropPlanner.Plan(new GtkDropNode("label", "GtkLabel", 0, 0, 50, 20, Array.Empty<GtkDropNode>()), 10, 10));
	}

	[Fact]
	public void EditorAdd_HonoursIndexAndCell()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""
			<interface>
			  <requires lib="gtk" version="4.0"/>
			  <object class="GtkWindow" id="window">
			    <child><object class="GtkBox" id="box">
			      <child><object class="GtkLabel" id="a"/></child>
			      <child><object class="GtkLabel" id="b"/></child>
			    </object></child>
			  </object>
			</interface>
			"""), editor.Error);
		Assert.True(editor.Add("box", "GtkButton", index: 1));
		Assert.Equal(new[] { "a", "button1", "b" }, editor.Roots[0].Children[0].Children.Select(c => c.Id));

		Assert.True(editor.Reset("""
			<interface>
			  <requires lib="gtk" version="4.0"/>
			  <object class="GtkWindow" id="window">
			    <child><object class="GtkGrid" id="grid"/></child>
			  </object>
			</interface>
			"""), editor.Error);
		Assert.True(editor.Add("grid", "GtkButton", cell: (2, 3)));
		var added = editor.Roots[0].Children[0].Children[0];
		Assert.Equal("2", added.Layout!["column"]);
		Assert.Equal("3", added.Layout!["row"]);
	}
}
