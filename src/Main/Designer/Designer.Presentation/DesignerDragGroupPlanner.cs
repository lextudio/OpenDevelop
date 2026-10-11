using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>Plans the source-backed portion of a multi-selection drag without depending on a
/// live design surface. One batch may only contain direct siblings, while snap targets must omit
/// every keyed descendant that moves implicitly with one of those roots.</summary>
public static class DesignerDragGroupPlanner
{
	public static IReadOnlyList<string> SameParentGroup(
		IEnumerable<string> selectedKeys, string primaryKey,
		IReadOnlyDictionary<string, DesignerElementNode?> parents)
	{
		if (selectedKeys == null)
			throw new ArgumentNullException(nameof(selectedKeys));
		if (primaryKey == null)
			throw new ArgumentNullException(nameof(primaryKey));
		if (parents == null)
			throw new ArgumentNullException(nameof(parents));
		parents.TryGetValue(primaryKey, out var primaryParent);
		var result = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var key in selectedKeys)
		{
			if (key == null || !seen.Add(key))
				continue;
			if (key == primaryKey || (parents.TryGetValue(key, out var parent)
				&& ReferenceEquals(parent, primaryParent)))
				result.Add(key);
		}
		if (!seen.Contains(primaryKey))
			result.Insert(0, primaryKey);
		return result;
	}

	public static HashSet<string> CollectMovingSubtreeKeys(
		IEnumerable<string> rootKeys, IReadOnlyDictionary<string, DesignerElementNode> nodes,
		Func<DesignerElementNode, string?> keyOf)
	{
		if (rootKeys == null)
			throw new ArgumentNullException(nameof(rootKeys));
		if (nodes == null)
			throw new ArgumentNullException(nameof(nodes));
		if (keyOf == null)
			throw new ArgumentNullException(nameof(keyOf));
		var result = new HashSet<string>(StringComparer.Ordinal);
		var visited = new HashSet<DesignerElementNode>(ReferenceEqualityComparer.Instance);
		foreach (var rootKey in rootKeys)
		{
			if (nodes.TryGetValue(rootKey, out var root))
				Collect(root);
		}
		return result;

		void Collect(DesignerElementNode node)
		{
			if (!visited.Add(node))
				return;
			var key = keyOf(node);
			if (key != null)
				result.Add(key);
			foreach (var child in node.Children)
				Collect(child);
		}
	}
}
