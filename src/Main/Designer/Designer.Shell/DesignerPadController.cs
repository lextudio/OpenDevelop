using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Shell;

/// <summary>Connects the runtime-neutral selection model to Outline and Properties pad adapters.</summary>
public sealed class DesignerPadController : IDisposable
{
	readonly DesignerSelectionController selection;
	readonly Action<IReadOnlyList<DesignerElementNode>> setOutlineRoots;
	readonly Action<object?> setPropertyObject;
	readonly Action<string> selectOutlineNode;
	readonly Action<DesignerElementNode?>? selectionApplied;
	bool updatingOutline;

	public DesignerPadController(DesignerSelectionController selection, Action<IReadOnlyList<DesignerElementNode>> setOutlineRoots,
		Action<object?> setPropertyObject, Action<string> selectOutlineNode, Action<DesignerElementNode?>? selectionApplied = null)
	{
		this.selection = selection; this.setOutlineRoots = setOutlineRoots; this.setPropertyObject = setPropertyObject;
		this.selectOutlineNode = selectOutlineNode; this.selectionApplied = selectionApplied;
		selection.TreeChanged += OnTreeChanged; selection.SelectionChanged += OnSelectionChanged;
	}

	public void UpdateTree(DesignerElementNode? root) => selection.UpdateTree(root);
	public void UpdateRoots(IEnumerable<DesignerElementNode>? roots) => selection.UpdateRoots(roots);
	public bool CommitOutlineSelection(string? id) => !updatingOutline && selection.Select(id);
	public bool CommitSelection(IEnumerable<string> ids, DesignerSelectionOperation operation = DesignerSelectionOperation.Replace)
		=> selection.Select(ids, operation);

	void OnTreeChanged(object? sender, EventArgs e) { updatingOutline = true; try { setOutlineRoots(selection.Roots); } finally { updatingOutline = false; } }
	void OnSelectionChanged(object? sender, EventArgs e)
	{
		setPropertyObject(selection.SelectedPropertyObject); selectionApplied?.Invoke(selection.SelectedNode);
		if (selection.SelectedId is { } id) { updatingOutline = true; try { selectOutlineNode(id); } finally { updatingOutline = false; } }
	}
	public void Dispose() { selection.TreeChanged -= OnTreeChanged; selection.SelectionChanged -= OnSelectionChanged; }
}
