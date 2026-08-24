using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Shell;

public enum DesignerSelectionOperation { Replace, Add, Toggle, Remove }

/// <summary>Runtime-neutral authority for a designer document's tree and ordered selection set.</summary>
public sealed class DesignerSelectionController
{
	readonly Func<DesignerElementNode, object?> propertyAdapterFactory;
	readonly Func<IReadOnlyList<DesignerElementNode>, object?>? multiPropertyAdapterFactory;
	IReadOnlyList<DesignerElementNode> roots = Array.Empty<DesignerElementNode>();
	readonly List<string> preferredSelectionIds = new();
	IReadOnlyList<DesignerElementNode> selectedNodes = Array.Empty<DesignerElementNode>();

	public DesignerSelectionController(Func<DesignerElementNode, object?>? propertyAdapterFactory = null,
		Func<IReadOnlyList<DesignerElementNode>, object?>? multiPropertyAdapterFactory = null)
	{
		this.propertyAdapterFactory = propertyAdapterFactory ?? (_ => null);
		this.multiPropertyAdapterFactory = multiPropertyAdapterFactory;
	}

	public event EventHandler? TreeChanged;
	public event EventHandler? SelectionChanged;
	public IReadOnlyList<DesignerElementNode> Roots => roots;
	public IReadOnlyList<DesignerElementNode> SelectedNodes => selectedNodes;
	public IReadOnlyList<string> SelectedIds => selectedNodes.Select(node => node.Id).ToArray();
	public DesignerElementNode? PrimarySelectedNode => selectedNodes.FirstOrDefault();
	public string? PrimarySelectedId => PrimarySelectedNode?.Id;
	public DesignerElementNode? SelectedNode => PrimarySelectedNode;
	public string? SelectedId => PrimarySelectedId;
	public object? SelectedPropertyObject { get; private set; }

	public void UpdateTree(DesignerElementNode? root) => UpdateRoots(root == null ? null : new[] { root });
	public void UpdateRoots(IEnumerable<DesignerElementNode>? newRoots)
	{
		roots = newRoots?.ToArray() ?? Array.Empty<DesignerElementNode>();
		TreeChanged?.Invoke(this, EventArgs.Empty);
		ApplySelection(preferredSelectionIds.Select(Find).Where(node => node != null).Cast<DesignerElementNode>().ToArray(), force: true);
	}

	public bool Select(string? id) => Select(id == null ? Array.Empty<string>() : new[] { id });
	public bool Select(DesignerElementNode? node) => Select(node?.Id);
	public bool Select(IEnumerable<DesignerElementNode>? nodes, DesignerSelectionOperation operation = DesignerSelectionOperation.Replace)
		=> Select(nodes?.Select(node => node.Id), operation);

	public bool Select(IEnumerable<string>? ids, DesignerSelectionOperation operation = DesignerSelectionOperation.Replace)
	{
		var requested = (ids ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
		if (operation == DesignerSelectionOperation.Replace && requested.Any(id => Find(id) == null)) return false;
		var next = operation == DesignerSelectionOperation.Replace ? new List<string>() : new List<string>(preferredSelectionIds);
		foreach (var id in requested) {
			if (Find(id) == null) continue;
			var index = next.FindIndex(value => StringComparer.Ordinal.Equals(value, id));
			switch (operation) {
				case DesignerSelectionOperation.Replace:
				case DesignerSelectionOperation.Add:
					if (index < 0) next.Add(id);
					break;
				case DesignerSelectionOperation.Toggle:
					if (index < 0) next.Add(id); else next.RemoveAt(index);
					break;
				case DesignerSelectionOperation.Remove:
					if (index >= 0) next.RemoveAt(index);
					break;
			}
		}
		preferredSelectionIds.Clear();
		preferredSelectionIds.AddRange(next);
		ApplySelection(next.Select(Find).Where(node => node != null).Cast<DesignerElementNode>().ToArray());
		return operation != DesignerSelectionOperation.Replace || requested.Length == next.Count;
	}

	public DesignerElementNode? Find(string id) => Flatten().FirstOrDefault(node => StringComparer.Ordinal.Equals(node.Id, id));
	public IEnumerable<DesignerElementNode> Flatten() => roots.SelectMany(Flatten);

	void ApplySelection(IReadOnlyList<DesignerElementNode> nodes, bool force = false)
	{
		var changed = selectedNodes.Count != nodes.Count ||
			!selectedNodes.Select(node => node.Id).SequenceEqual(nodes.Select(node => node.Id), StringComparer.Ordinal);
		selectedNodes = nodes;
		SelectedPropertyObject = nodes.Count switch {
			0 => null,
			1 => propertyAdapterFactory(nodes[0]),
			_ when multiPropertyAdapterFactory != null => multiPropertyAdapterFactory(nodes),
			_ => propertyAdapterFactory(nodes[0])
		};
		if (changed || force) SelectionChanged?.Invoke(this, EventArgs.Empty);
	}

	static IEnumerable<DesignerElementNode> Flatten(DesignerElementNode node)
	{
		yield return node;
		foreach (var child in node.Children)
			foreach (var descendant in Flatten(child))
				yield return descendant;
	}
}
