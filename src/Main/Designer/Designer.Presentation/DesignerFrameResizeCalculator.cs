namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>Hit-tests the preview frame's resizable right/bottom edges in design coordinates.</summary>
public static class DesignerFrameResizeCalculator
{
	/// <summary>Returns whether movement on any active frame edge exceeded the drag threshold.</summary>
	public static bool HasExceededDragThreshold(string edge, double horizontalDelta, double verticalDelta, double threshold)
	{
		return (edge.Contains('e') && System.Math.Abs(horizontalDelta) >= threshold)
			|| (edge.Contains('s') && System.Math.Abs(verticalDelta) >= threshold);
	}

	/// <summary>Returns <c>e</c>, <c>s</c>, or <c>se</c> for the matching edge; otherwise null.
	/// <paramref name="screenTolerance"/> remains visually constant across canvas zoom levels.</summary>
	public static string? EdgeAt(double x, double y, double width, double height, double scale,
		double screenTolerance = 6)
	{
		if (width <= 0 || height <= 0)
			return null;
		var tolerance = screenTolerance / System.Math.Max(scale, 0.01);
		var onRight = System.Math.Abs(x - width) <= tolerance && y >= -tolerance && y <= height + tolerance;
		var onBottom = System.Math.Abs(y - height) <= tolerance && x >= -tolerance && x <= width + tolerance;
		return onRight && onBottom ? "se" : onRight ? "e" : onBottom ? "s" : null;
	}
}
