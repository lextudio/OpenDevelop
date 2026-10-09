using ICSharpCode.SharpDevelop.Designer.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class GridGuideHitTesterTests
{
	static readonly double[] Rows = [0, 25, 70, 100];
	static readonly double[] Columns = [0, 40, 120, 200];

	[Fact]
	public void Find_IdentifiesOnlyInternalDividersInsideTheGrid()
	{
		Assert.Equal((false, 0), GridGuideHitTester.Find(50, 80, 10, 20, 200, 100, Rows, Columns, 1));
		Assert.Equal((true, 1), GridGuideHitTester.Find(100, 90, 10, 20, 200, 100, Rows, Columns, 1));
	}

	[Fact]
	public void Find_RejectsAnAlignedPointOutsideTheGrid()
	{
		// x=50 is the first column divider, but y is below this Grid.
		Assert.Null(GridGuideHitTester.Find(50, 121, 10, 20, 200, 100, Rows, Columns, 2));
		// y=45 is the first row divider, but x is left of this Grid.
		Assert.Null(GridGuideHitTester.Find(9, 45, 10, 20, 200, 100, Rows, Columns, 2));
	}

	[Fact]
	public void Find_UsesCallerSuppliedDesignSpaceTolerance()
	{
		Assert.Equal((false, 0), GridGuideHitTester.Find(52.5, 80, 10, 20, 200, 100, Rows, Columns, 3));
		Assert.Null(GridGuideHitTester.Find(53.1, 80, 10, 20, 200, 100, Rows, Columns, 3));
	}
}
