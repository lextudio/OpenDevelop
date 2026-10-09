using System;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>
/// Pure design-coordinate raster placement. The canvas uses this only when a backend opts into
/// grid snapping; drawing gridlines alone must never silently change a user's drag result.
/// </summary>
public static class RasterGridCalculator
{
	/// <summary>Returns the correction which places a proposed move on the nearest grid crossing.
	/// A non-positive <paramref name="cellSize"/> disables snapping.</summary>
	public static (double DX, double DY) SnapMove(double startX, double startY, double deltaX, double deltaY, double cellSize)
	{
		if (cellSize <= 0 || double.IsNaN(cellSize) || double.IsInfinity(cellSize))
			return (deltaX, deltaY);
		return (Snap(startX + deltaX, cellSize) - startX, Snap(startY + deltaY, cellSize) - startY);
	}

	static double Snap(double value, double cellSize) => Math.Round(value / cellSize, MidpointRounding.AwayFromZero) * cellSize;
}
