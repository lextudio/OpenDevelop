using ICSharpCode.SharpDevelop.Designer.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class SnapGuideCalculatorTests
{
	[Fact]
	public void RasterGrid_SnapsMoveToNearestDesignCoordinate()
	{
		var snapped = RasterGridCalculator.SnapMove(11, 29, 7, 2, 20);

		Assert.Equal(9, snapped.DX);
		Assert.Equal(11, snapped.DY);
	}

	[Fact]
	public void RasterGrid_InvalidCellSizeLeavesMoveUntouched()
	{
		var snapped = RasterGridCalculator.SnapMove(11, 29, 7.5, -2.5, 0);

		Assert.Equal(7.5, snapped.DX);
		Assert.Equal(-2.5, snapped.DY);
	}

	[Fact]
	public void RequireOverlap_DoesNotSnapToAnUnrelatedRow()
	{
		var result = SnapGuideCalculator.ApplySnap(
			(0, 0, 10, 10), 91, 0,
			new[] { (100d, 100d, 10d, 10d) }, requireOverlap: true);

		Assert.Equal(91, result.DX);
		Assert.DoesNotContain(result.Guides, guide => guide.IsVertical);
	}

	[Fact]
	public void RequireOverlap_SnapsToTheNearestOverlappingSiblingEdge()
	{
		var result = SnapGuideCalculator.ApplySnap(
			(0, 0, 10, 10), 91, 0,
			new[] { (100d, 5d, 10d, 10d), (99d, 100d, 10d, 10d) }, requireOverlap: true);

		Assert.Equal(90, result.DX);
		Assert.Contains(result.Guides, guide => guide is (true, 100));
	}

	[Fact]
	public void SnapEdge_UsesOnlyTheActiveResizeEdgeAndOverlappingSiblings()
	{
		var result = SnapGuideCalculator.SnapEdge(
			vertical: true, position: 101, orthogonalStart: 0, orthogonalEnd: 10,
			siblingBounds: new[] { (100d, 5d, 10d, 10d), (99d, 100d, 10d, 10d) });

		Assert.Equal(-1, result.Correction);
		Assert.Equal(100, result.Guide);
	}
}
