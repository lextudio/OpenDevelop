using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>
/// Plans primary-selection navigation for a snapshot-backed design surface. The order is the
/// document tree's pre-order traversal and wraps at either end, matching the legacy designer's
/// Tab/Shift+Tab behaviour without requiring a live WPF visual tree.
/// </summary>
public static class DesignerFocusNavigator
{
	/// <summary>
	/// Gets the next selectable, visible, designable node key. If the current key disappeared from
	/// the latest snapshot, navigation starts at the appropriate end instead of retaining a stale
	/// selection. Returns null when the snapshot contains no selectable nodes.
	/// </summary>
	public static string? Next(DesignerElementNode? root, string? currentKey, bool backwards,
		Func<DesignerElementNode, string?> keyOf, Func<string, bool> isSelectable)
	{
		ArgumentNullException.ThrowIfNull(keyOf);
		ArgumentNullException.ThrowIfNull(isSelectable);
		if (root == null)
			return null;

		var keys = new List<string>();
		var seenKeys = new HashSet<string>(StringComparer.Ordinal);
		var visited = new HashSet<DesignerElementNode>(ReferenceEqualityComparer.Instance);
		Walk(root);
		if (keys.Count == 0)
			return null;

		var index = currentKey == null ? -1 : keys.IndexOf(currentKey);
		if (index < 0)
			return backwards ? keys[^1] : keys[0];
		return keys[(index + (backwards ? keys.Count - 1 : 1)) % keys.Count];

		void Walk(DesignerElementNode node)
		{
			if (!visited.Add(node))
				return;
			// IsVisible describes the rendered frame. A child can retain stale local metadata after
			// its container becomes collapsed, so inherited invisibility must stop the whole branch.
			if (!node.IsVisible)
				return;
			var key = keyOf(node);
			if (node.IsDesignable && key != null && isSelectable(key) && seenKeys.Add(key))
				keys.Add(key);
			foreach (var child in node.Children)
				Walk(child);
		}
	}
}
