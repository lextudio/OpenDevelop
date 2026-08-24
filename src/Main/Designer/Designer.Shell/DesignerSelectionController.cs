using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Shell;

/// <summary>Runtime-neutral authority for a designer document's outline and single selection.</summary>
public sealed class DesignerSelectionController
{
	readonly Func<DesignerElementNode, object?> propertyAdapterFactory;
	IReadOnlyList<DesignerElementNode> roots = Array.Empty<DesignerElementNode>();
	string? preferredSelectionId;

	public DesignerSelectionController(Func<DesignerElementNode, object?>? propertyAdapterFactory = null)
	{
		this.propertyAdapterFactory = propertyAdapterFactory ?? (_ => null);
	}

	public event EventHandler? TreeChanged;
	public event EventHandler? SelectionChanged;
	public IReadOnlyList<DesignerElementNode> Roots => roots;
	public DesignerElementNode? SelectedNode { get; private set; }
	public string? SelectedId => SelectedNode?.Id;
	public object? SelectedPropertyObject { get; private set; }

	public void UpdateTree(DesignerElementNode? root) => UpdateRoots(root == null ? null : new[] { root });

	public void UpdateRoots(IEnumerable<DesignerElementNode>? newRoots)
	{
		var keepId = preferredSelectionId;
		roots = newRoots?.ToArray() ?? Array.Empty<DesignerElementNode>();
		TreeChanged?.Invoke(this, EventArgs.Empty);
		ApplySelection(keepId == null ? null : Find(keepId));
	}

	public bool Select(string? id)
	{
		if (id == null) {
			preferredSelectionId = null;
			ApplySelection(null);
			return true;
		}
		var node = Find(id);
		if (node == null) return false;
		preferredSelectionId = id;
		ApplySelection(node);
		return true;
	}

	public bool Select(DesignerElementNode? node)
	{
		if (node == null) {
			preferredSelectionId = null;
			ApplySelection(null);
			return true;
		}
		var current = Find(node.Id);
		if (current == null) return false;
		preferredSelectionId = current.Id;
		ApplySelection(current);
		return true;
	}

	public DesignerElementNode? Find(string id) => Flatten().FirstOrDefault(node => StringComparer.Ordinal.Equals(node.Id, id));
	public IEnumerable<DesignerElementNode> Flatten() => roots.SelectMany(Flatten);

	void ApplySelection(DesignerElementNode? node)
	{
		var changed = !ReferenceEquals(SelectedNode, node) || SelectedPropertyObject == null && node != null;
		SelectedNode = node;
		SelectedPropertyObject = node == null ? null : propertyAdapterFactory(node);
		if (changed) SelectionChanged?.Invoke(this, EventArgs.Empty);
	}

	static IEnumerable<DesignerElementNode> Flatten(DesignerElementNode node)
	{
		yield return node;
		foreach (var child in node.Children)
			foreach (var descendant in Flatten(child))
				yield return descendant;
	}
}
