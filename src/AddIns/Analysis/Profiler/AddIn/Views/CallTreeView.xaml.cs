using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenDevelop.Profiler.Cpu;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>
/// The call tree as an expandable grid: Total and Self time and share per function. Only the expanded
/// part of the tree is materialised as rows, so a deep profile stays responsive.
/// </summary>
public partial class CallTreeView : UserControl
{
	readonly ObservableCollection<CallTreeRow> rows = [];
	CallTreeNode? root;
	long rootTotal;
	double sampleIntervalMs = 1;

	public CallTreeView()
	{
		InitializeComponent();
		rowList.ItemsSource = rows;
	}

	/// <summary>Raised with a frame id when the user asks to see a function's callers and callees.</summary>
	public event EventHandler<int>? ShowCallerCalleeRequested;

	/// <summary>Raised with a frame name when the user asks to see where a function is declared.</summary>
	public event EventHandler<string>? GoToSourceRequested;

	CallTreeRow? SelectedRow => rowList.SelectedItem as CallTreeRow;

	/// <summary>Replaces the tree. The root is shown expanded along its hot path.</summary>
	internal void Show(CallTreeNode treeRoot, double intervalMs, ProfileUnit unit = ProfileUnit.Milliseconds)
	{
		totalColumn.Header = ReportFormat.TotalHeader(unit);
		selfColumn.Header = ReportFormat.SelfHeader(unit);
		root = treeRoot;
		rootTotal = treeRoot.Total;
		sampleIntervalMs = intervalMs;
		rows.Clear();
		rows.Add(NewRow(treeRoot, 0));
		ExpandHotPath(treeRoot);
		rowList.SelectedIndex = -1;
	}

	/// <summary>Puts keyboard focus back on the tree, e.g. after the find box closes.</summary>
	public void FocusList() => rowList.Focus();

	/// <summary>Expands the heaviest path below the selected row, or below the root when nothing is selected.</summary>
	public void ExpandHotPath()
	{
		if (root is not null)
			ExpandHotPath(SelectedRow?.Node ?? root);
	}

	/// <summary>
	/// Selects the next (or previous) node, in tree order, whose name contains the text, wrapping around.
	/// An incremental search keeps the current node if it still matches, so typing more of a name does not
	/// jump away from where it already landed. Returns the 1-based position of the match among all
	/// matches and the number of matches; both are 0 when nothing matches.
	/// </summary>
	public (int Current, int Total) Find(string text, bool forward, bool includeCurrent)
	{
		if (root is null || text.Length == 0)
			return (0, 0);

		var matches = Preorder(root)
			.Where(node => node.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
			.ToList();
		if (matches.Count == 0)
			return (0, 0);

		var order = Preorder(root).ToList();
		var matchSet = new HashSet<CallTreeNode>(matches);
		var current = SelectedRow is { } selected ? order.IndexOf(selected.Node) : -1;
		// With nothing selected, start just before the first node (forward) or just after the last (backward).
		var origin = current >= 0 ? current : forward ? -1 : order.Count;
		var firstStep = includeCurrent && current >= 0 ? 0 : 1;

		var target = matches[0];
		for (var step = firstStep; step <= order.Count; step++)
		{
			var index = forward ? origin + step : origin - step;
			var candidate = order[(index % order.Count + order.Count) % order.Count];
			if (matchSet.Contains(candidate))
			{
				target = candidate;
				break;
			}
		}

		Select(target);
		return (matches.IndexOf(target) + 1, matches.Count);
	}


	/// <summary>The first visible rows, for the integration-test probes.</summary>
	internal IReadOnlyList<object> Snapshot(int max) => rows
		.Take(max)
		.Select(r => (object)new
		{
			depth = r.Depth, name = r.Name, module = r.Module, totalMs = r.TotalMs, selfMs = r.SelfMs, expanded = r.IsExpanded,
		})
		.ToList();

	CallTreeRow NewRow(CallTreeNode node, int depth) => new(node, depth, rootTotal, sampleIntervalMs);

	CallTreeRow? RowFor(CallTreeNode node) => rows.FirstOrDefault(r => ReferenceEquals(r.Node, node));

	void ExpandHotPath(CallTreeNode start)
	{
		Reveal(start);
		var path = new List<CallTreeNode> { start };
		path.AddRange(start.HotPath());
		foreach (var node in path)
		{
			if (RowFor(node) is { } row)
				Expand(row);
		}
	}

	void Select(CallTreeNode node)
	{
		Reveal(node);
		if (RowFor(node) is { } row)
		{
			rowList.SelectedItem = row;
			rowList.ScrollIntoView(row);
		}
	}

	/// <summary>Expands every ancestor of the node so that its row exists.</summary>
	void Reveal(CallTreeNode node)
	{
		var ancestors = new List<CallTreeNode>();
		for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
			ancestors.Add(ancestor);
		ancestors.Reverse();
		foreach (var ancestor in ancestors)
		{
			if (RowFor(ancestor) is { } row)
				Expand(row);
		}
	}

	void Expand(CallTreeRow row)
	{
		if (!row.HasChildren || row.IsExpanded)
			return;
		var index = rows.IndexOf(row);
		row.IsExpanded = true;
		foreach (var child in row.Node.Children)
			rows.Insert(++index, NewRow(child, row.Depth + 1));
	}

	void Collapse(CallTreeRow row)
	{
		if (!row.IsExpanded)
			return;
		var index = rows.IndexOf(row) + 1;
		while (index < rows.Count && rows[index].Depth > row.Depth)
			rows.RemoveAt(index);
		row.IsExpanded = false;
	}

	void Toggle(CallTreeRow row)
	{
		if (row.IsExpanded)
			Collapse(row);
		else
			Expand(row);
	}

	static IEnumerable<CallTreeNode> Preorder(CallTreeNode start)
	{
		var stack = new Stack<CallTreeNode>();
		stack.Push(start);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			yield return node;
			for (var i = node.Children.Count - 1; i >= 0; i--)
				stack.Push(node.Children[i]);
		}
	}

	void Glyph_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: CallTreeRow row })
		{
			Toggle(row);
			e.Handled = true;
		}
	}

	void RowList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (SelectedRow is { } row)
			Toggle(row);
	}

	void RowList_KeyDown(object sender, KeyEventArgs e)
	{
		if (SelectedRow is not { } row)
			return;
		switch (e.Key)
		{
			case Key.Right when !row.IsExpanded:
				Expand(row);
				break;
			case Key.Right when row.HasChildren:
				rowList.SelectedIndex = rows.IndexOf(row) + 1;
				break;
			case Key.Left when row.IsExpanded:
				Collapse(row);
				break;
			case Key.Left when row.Node.Parent is { } parent && RowFor(parent) is { } parentRow:
				rowList.SelectedItem = parentRow;
				break;
			case Key.Enter:
				Toggle(row);
				break;
			default:
				return;
		}
		e.Handled = true;
		if (rowList.SelectedItem is not null)
			rowList.ScrollIntoView(rowList.SelectedItem);
	}

	void ShowCallerCallee_Click(object sender, RoutedEventArgs e)
	{
		if (SelectedRow is { Node.FrameId: not CallTreeNode.RootFrameId } row)
			ShowCallerCalleeRequested?.Invoke(this, row.Node.FrameId);
	}

	void GoToSource_Click(object sender, RoutedEventArgs e)
	{
		if (SelectedRow is { Node.FrameId: >= 0 } row)
			GoToSourceRequested?.Invoke(this, row.Name);
	}

	void ExpandHotPathFromSelection_Click(object sender, RoutedEventArgs e) => ExpandHotPath();

	void CopyStack_Click(object sender, RoutedEventArgs e)
	{
		if (SelectedRow is not { } row)
			return;
		var frames = new List<string>();
		for (var node = row.Node; node is not null && node.FrameId != CallTreeNode.RootFrameId; node = node.Parent)
			frames.Add(node.Name);
		// Innermost frame first, like a debugger's call stack.
		CopyToClipboard(string.Join(Environment.NewLine, frames));
	}

	void CopyRow_Click(object sender, RoutedEventArgs e)
	{
		if (SelectedRow is { } row)
		{
			CopyToClipboard(string.Join(
				"\t", row.Name, row.TotalMs.ToString("N0"), row.TotalFraction.ToString("P1"),
				row.SelfMs.ToString("N0"), row.SelfFraction.ToString("P1"), row.Module));
		}
	}

	static void CopyToClipboard(string text)
	{
		try
		{
			Clipboard.SetText(text);
		}
		catch (ExternalException)
		{
			// Another application holds the clipboard; copying is a convenience, not worth an error box.
		}
	}
}
