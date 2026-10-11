using ICSharpCode.SharpDevelop.Designer.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class DesignerFrameResizeCalculatorTests
{
	[Theory]
	[InlineData(200, 60, 200, 100, 1, "e")]
	[InlineData(203, 60, 200, 100, 2, "e")]
	[InlineData(80, 100, 200, 100, 1, "s")]
	[InlineData(80, 103, 200, 100, 2, "s")]
	[InlineData(200, 100, 200, 100, 1, "se")]
	[InlineData(203, 103, 200, 100, 2, "se")]
	[InlineData(204, 104, 200, 100, 1, "se")]
	[InlineData(700, 100, 200, 100, 0, "se")]
	public void EdgeAt_ReturnsMatchingFrameEdge(double x, double y, double width, double height, double scale, string expected)
	{
		Assert.Equal(expected, DesignerFrameResizeCalculator.EdgeAt(x, y, width, height, scale));
	}

	[Theory]
	[InlineData(190, 90, 200, 100, 1)]
	[InlineData(207, 100, 200, 100, 1)]
	[InlineData(200, -7, 200, 100, 1)]
	[InlineData(100, 50, 0, 100, 1)]
	public void EdgeAt_RejectsPointsOutsideTheFrameHitTarget(double x, double y, double width, double height, double scale)
	{
		Assert.Null(DesignerFrameResizeCalculator.EdgeAt(x, y, width, height, scale));
	}

	[Theory]
	[InlineData("e", 4, 0, true)]
	[InlineData("e", 3, 10, false)]
	[InlineData("s", 10, 4, true)]
	[InlineData("s", 0, 3, false)]
	[InlineData("se", 4, 0, true)]
	[InlineData("se", 0, 4, true)]
	[InlineData("se", 3, 3, false)]
	public void HasExceededDragThreshold_UsesEveryActiveEdge(string edge, double horizontalDelta, double verticalDelta, bool expected)
	{
		Assert.Equal(expected, DesignerFrameResizeCalculator.HasExceededDragThreshold(edge, horizontalDelta, verticalDelta, 4));
	}
}
