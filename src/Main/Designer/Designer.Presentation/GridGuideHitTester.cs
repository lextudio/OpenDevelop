using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>Pure hit testing for measured Grid divider guides. Keeps the axis-specific divider
/// lookup and the Grid's two-dimensional bounds together so pointer code cannot accidentally
/// begin a resize outside the container.</summary>
public static class GridGuideHitTester
{
	/// <returns><c>IsRow=true</c> for a horizontal row divider; otherwise a vertical column divider.</returns>
	public static (bool IsRow, int Index)? Find(double x, double y,
		double gridX, double gridY, double gridWidth, double gridHeight,
		IReadOnlyList<double> rowOffsets, IReadOnlyList<double> columnOffsets, double tolerance)
	{
		if (x < gridX || x > gridX + gridWidth || y < gridY || y > gridY + gridHeight)
			return null;
		for (var index = 1; index < columnOffsets.Count - 1; index++)
		{
			if (Math.Abs(x - (gridX + columnOffsets[index])) <= tolerance)
				return (false, index - 1);
		}
		for (var index = 1; index < rowOffsets.Count - 1; index++)
		{
			if (Math.Abs(y - (gridY + rowOffsets[index])) <= tolerance)
				return (true, index - 1);
		}
		return null;
	}
}
