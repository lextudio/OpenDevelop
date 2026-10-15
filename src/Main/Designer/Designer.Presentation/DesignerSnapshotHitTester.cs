using System.Collections.Generic;
using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>Pure, snapshot-only hit testing for bitmap-backed designers.  It deliberately knows
/// nothing about a child host or dispatcher: a pointer press must remain answerable while that
/// child is rendering, restarting, or completely unresponsive.</summary>
public static class DesignerSnapshotHitTester
{
	/// <summary>Returns the innermost source-backed visible node containing the design point.
	/// Among siblings the topmost wins: a higher <see cref="DesignerElementNode.ZIndex"/> paints over
	/// a lower one (a missing value counts as 0) and, at equal z-index, a later sibling paints over
	/// an earlier one. A host whose paint order is not its child order (WinForms puts the front
	/// control first) states it through ZIndex.</summary>
	static IEnumerable<int> TopmostFirst(IReadOnlyList<DesignerElementNode> children)
	{
		var plain = true;
		for (var index = 1; index < children.Count && plain; index++)
			plain = (children[index].ZIndex ?? 0) == (children[0].ZIndex ?? 0);
		if (plain)
		{
			for (var index = children.Count - 1; index >= 0; index--)
				yield return index;
			yield break;
		}
		// Stable: ties keep the later sibling in front.
		foreach (var index in Enumerable.Range(0, children.Count)
			.OrderByDescending(i => children[i].ZIndex ?? 0).ThenByDescending(i => i))
			yield return index;
	}

	public static DesignerElementNode? FindNodeAt(DesignerElementNode? node, double x, double y)
	{
		// A design-time-locked element is skipped with its whole subtree, as the in-process
		// designer's hit-test filter does, so a click falls through to what is behind or around it.
		if (node == null || !node.IsVisible || node.IsDesignTimeLocked == true)
			return null;
		foreach (var index in TopmostFirst(node.Children))
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
