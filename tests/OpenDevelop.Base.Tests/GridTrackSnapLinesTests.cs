using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The Grid half of alignment snapping: a Grid parent contributes its outer edges and its measured
/// row/column dividers as guide lines, each limited to the Grid's own extent, so a drag can land on
/// a cell boundary that has no sibling element sitting on it.
/// </summary>
public sealed class GridTrackSnapLinesTests
{
	static DesignerElementNode Grid(bool accepted = true, double width = 300, double height = 200)
		=> new() {
			Id = "0", Name = "Layout", Type = "Grid", X = 100, Y = 200, Width = width, Height = height,
			GridTracks = new DesignerGridGuides {
				Accepted = accepted,
				ColumnTracks = {
					new DesignerGridTrackInfo { Offset = 0, Size = 120 },
					new DesignerGridTrackInfo { Offset = 120, Size = 180 }
				},
				RowTracks = {
					new DesignerGridTrackInfo { Offset = 0, Size = 80 },
					new DesignerGridTrackInfo { Offset = 80, Size = 120 }
				}
			}
		};

	[Fact]
	public void AGridsOuterEdgesAndMeasuredDividersBecomeLinesWithinItsOwnExtent()
	{
		var (vertical, horizontal) = GridTrackSnapLines.For(Grid());

		Assert.Equal(new[] { 100d, 400d, 220d, 400d }, vertical.Select(line => Math.Round(line.Position)));
		Assert.All(vertical, line => Assert.Equal((200d, 400d), (line.OrthogonalStart, line.OrthogonalEnd)));
		Assert.Equal(new[] { 200d, 400d, 280d, 400d }, horizontal.Select(line => Math.Round(line.Position)));
		Assert.All(horizontal, line => Assert.Equal((100d, 400d), (line.OrthogonalStart, line.OrthogonalEnd)));
	}

	[Fact]
	public void AGridViewThatReportedNoMeasuredLayoutContributesNoLines()
	{
		Assert.Empty(GridTrackSnapLines.For(Grid(accepted: false)).Vertical);
		Assert.Empty(GridTrackSnapLines.For(null).Vertical);
		Assert.Empty(GridTrackSnapLines.For(new DesignerElementNode { Type = "Grid", Width = 10, Height = 10 }).Horizontal);
	}

	[Fact]
	public void AMoveSnapsItsEdgeOntoAGridDivider()
	{
		var (vertical, horizontal) = GridTrackSnapLines.For(Grid());

		// The element's left edge is proposed 4 units right of the Grid's outer edge at 100; its
		// centre and right edge are farther away, so the edge is the line that catches it.
		var result = SnapGuideCalculator.ApplySnap(
			(104, 210, 4, 10), 0, 0, Array.Empty<SnapGuideCalculator.Target>(), ownBaselineOffset: null,
			requireOverlap: true, verticalLines: vertical, horizontalLines: horizontal);

		Assert.Equal(-4, result.DX);
		Assert.Contains(result.Guides, guide => guide is (true, 100));
	}

	[Fact]
	public void ADividerDoesNotSnapAGestureThatIsOutsideTheGrid()
	{
		var (vertical, horizontal) = GridTrackSnapLines.For(Grid());

		var result = SnapGuideCalculator.ApplySnap(
			(104, 600, 4, 10), 0, 0, Array.Empty<SnapGuideCalculator.Target>(), ownBaselineOffset: null,
			requireOverlap: true, verticalLines: vertical, horizontalLines: horizontal);

		Assert.Equal(0, result.DX);
		Assert.DoesNotContain(result.Guides, guide => guide.IsVertical);
	}

	[Fact]
	public void AResizeSnapsTheActivelyDraggedEdgeOntoAGridDivider()
	{
		var (vertical, _) = GridTrackSnapLines.For(Grid());

		// The right edge is dragged to 216, 4 units short of the column divider at 220.
		var result = SnapGuideCalculator.SnapEdge(
			vertical: true, position: 216, orthogonalStart: 210, orthogonalEnd: 220,
			siblingBounds: Array.Empty<(double, double, double, double)>(), lines: vertical);

		Assert.Equal(4, result.Correction);
		Assert.Equal(220, result.Guide);
	}
}
