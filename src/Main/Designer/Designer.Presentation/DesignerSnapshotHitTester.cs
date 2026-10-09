using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>Pure, snapshot-only hit testing for bitmap-backed designers.  It deliberately knows
/// nothing about a child host or dispatcher: a pointer press must remain answerable while that
/// child is rendering, restarting, or completely unresponsive.</summary>
public static class DesignerSnapshotHitTester
{
	/// <summary>Returns the innermost source-backed visible node containing the design point.
	/// Later siblings win because they paint over earlier siblings.</summary>
	public static DesignerElementNode? FindNodeAt(DesignerElementNode? node, double x, double y)
	{
		if (node == null || !node.IsVisible)
			return null;
		for (var index = node.Children.Count - 1; index >= 0; index--)
		{
			if (FindNodeAt(node.Children[index], x, y) is { } child)
				return child;
		}
		// Hosts normally make a template part non-designable too, but keep the explicit flag in
		// the policy: a partially populated/older snapshot must never turn template chrome into a
		// source selection merely because its generic designable bit was optimistic.
		var containsPoint = x >= node.X && y >= node.Y && x <= node.X + node.Width && y <= node.Y + node.Height;
		return containsPoint && node.IsDesignable && !node.IsTemplatePart && !node.IsTrayComponent ? node : null;
	}
}
