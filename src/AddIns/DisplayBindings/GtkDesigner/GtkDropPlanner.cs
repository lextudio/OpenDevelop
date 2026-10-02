using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.GtkDesigner;

/// <summary>A laid-out object as the drop planner sees it: GTK's measured bounds (design
/// coordinates), the object's own orientation (boxes), and its &lt;layout&gt; cell (grid children).</summary>
public sealed record GtkDropNode(string Id, string ClassName, double X, double Y, double Width, double Height,
	IReadOnlyList<GtkDropNode> Children, string? Orientation = null, int Column = 0, int Row = 0, int ColumnSpan = 1, int RowSpan = 1)
{
	public bool Contains(double x, double y) => Width > 0 && Height > 0 && x >= X && y >= Y && x <= X + Width && y <= Y + Height;
}

/// <summary>Where a toolbox drop goes: <see cref="ContainerId"/>, and either an insertion
/// <see cref="Index"/> (a box) or a <see cref="Cell"/> (a grid); neither means append.
/// <see cref="Indicator"/> is what the canvas draws while dragging, in design coordinates: a thin
/// insertion line for a box, the target cell for a grid, the container itself otherwise.</summary>
public sealed record GtkDropPlan(string ContainerId, int? Index, (int Column, int Row)? Cell, (double X, double Y, double Width, double Height) Indicator);

/// <summary>
/// GTK 4 has no free positioning: a drop is resolved by the receiving container's child policy
/// (doc/technotes/gtk-designer.md, "Drag/drop") - an ordered position in a GtkBox, a row/column
/// in a GtkGrid, append elsewhere. Pure, so the IDE (drag-over feedback) and the design host (the
/// actual insertion, from its own measured bounds) compute the same answer.
/// </summary>
public static class GtkDropPlanner
{
	/// <summary>Builder classes that take dropped children.</summary>
	public static readonly HashSet<string> Containers = new(StringComparer.Ordinal) {
		"GtkBox", "GtkGrid", "GtkCenterBox", "GtkPaned", "GtkScrolledWindow", "GtkWindow", "GtkApplicationWindow",
		"GtkNotebook", "GtkStack", "GtkOverlay", "GtkFrame", "AdwClamp", "AdwPreferencesPage", "AdwPreferencesGroup"
	};

	const double LineThickness = 2;

	/// <summary>The plan for a drop at (<paramref name="x"/>, <paramref name="y"/>): the deepest
	/// container under the point (or its nearest containing ancestor), or null when there is none.</summary>
	public static GtkDropPlan? Plan(GtkDropNode root, double x, double y)
	{
		var container = DeepestContainer(root, x, y);
		if (container == null) return null;
		var indicator = (container.X, container.Y, container.Width, container.Height);
		switch (container.ClassName) {
			case "GtkBox": return PlanBox(container, x, y);
			case "GtkGrid": return PlanGrid(container, x, y);
			default: return new GtkDropPlan(container.Id, null, null, indicator);
		}
	}

	static GtkDropNode? DeepestContainer(GtkDropNode node, double x, double y)
	{
		if (!node.Contains(x, y)) return null;
		foreach (var child in node.Children) {
			var deeper = DeepestContainer(child, x, y);
			if (deeper != null) return deeper;
		}
		return Containers.Contains(node.ClassName) ? node : null;
	}

	/// <summary>Index = how many children lie before the point along the box's axis (GtkBox's
	/// default orientation is horizontal); the line sits midway in the gap at that index.</summary>
	static GtkDropPlan PlanBox(GtkDropNode box, double x, double y)
	{
		var vertical = string.Equals(box.Orientation, "vertical", StringComparison.OrdinalIgnoreCase);
		var children = box.Children.Where(c => c.Width > 0 && c.Height > 0).ToList();
		var index = children.Count(c => vertical ? c.Y + c.Height / 2 < y : c.X + c.Width / 2 < x);
		double position;
		if (children.Count == 0) position = vertical ? box.Y : box.X;
		else if (index == 0) position = vertical ? children[0].Y : children[0].X;
		else if (index == children.Count) { var last = children[^1]; position = vertical ? last.Y + last.Height : last.X + last.Width; }
		else {
			var before = children[index - 1]; var after = children[index];
			position = vertical ? (before.Y + before.Height + after.Y) / 2 : (before.X + before.Width + after.X) / 2;
		}
		var line = vertical
			? (box.X, position - LineThickness / 2, box.Width, LineThickness)
			: (position - LineThickness / 2, box.Y, LineThickness, box.Height);
		// The index counts laid-out children; the document position is the same order.
		return new GtkDropPlan(box.Id, index, null, line);
	}

	/// <summary>The cell under the point, from the columns' and rows' measured extents; past the
	/// last column/row it is the next one. An occupied cell moves down to the first free row
	/// of that column, so a drop never stacks two children on one cell.</summary>
	static GtkDropPlan PlanGrid(GtkDropNode grid, double x, double y)
	{
		var children = grid.Children.Where(c => c.Width > 0 && c.Height > 0).ToList();
		if (children.Count == 0)
			return new GtkDropPlan(grid.Id, null, (0, 0), (grid.X, grid.Y, grid.Width, grid.Height));
		var columns = Tracks(children, c => (c.Column, c.ColumnSpan, c.X, c.Width));
		var rows = Tracks(children, c => (c.Row, c.RowSpan, c.Y, c.Height));
		var column = TrackAt(columns, x);
		var row = TrackAt(rows, y);
		while (children.Any(c => column >= c.Column && column < c.Column + c.ColumnSpan && row >= c.Row && row < c.Row + c.RowSpan)) row++;
		var (cellX, cellWidth) = Extent(columns, column);
		var (cellY, cellHeight) = Extent(rows, row);
		return new GtkDropPlan(grid.Id, null, (column, row), (cellX, cellY, cellWidth, cellHeight));
	}

	/// <summary>Start and size of each track index, from the children that start in it with span 1.</summary>
	static SortedDictionary<int, (double Start, double Size)> Tracks(List<GtkDropNode> children, Func<GtkDropNode, (int Index, int Span, double Start, double Size)> select)
	{
		var tracks = new SortedDictionary<int, (double Start, double Size)>();
		foreach (var (index, span, start, size) in children.Select(select)) {
			var single = span <= 1 ? size : size / span;
			if (!tracks.TryGetValue(index, out var t)) tracks[index] = (start, single);
			else tracks[index] = (Math.Min(t.Start, start), Math.Max(t.Size, single));
		}
		return tracks;
	}

	static int TrackAt(SortedDictionary<int, (double Start, double Size)> tracks, double position)
	{
		var result = tracks.Keys.First();
		foreach (var (index, (start, size)) in tracks) {
			if (position >= start) result = index;
			if (position > start + size && index == tracks.Keys.Last()) result = index + 1;
		}
		return result;
	}

	static (double Start, double Size) Extent(SortedDictionary<int, (double Start, double Size)> tracks, int index)
	{
		if (tracks.TryGetValue(index, out var t)) return t;
		// A new track past the last one: same size as the last, right after it.
		var last = tracks.Last();
		var gap = index - last.Key;
		return (last.Value.Start + last.Value.Size * gap, last.Value.Size);
	}
}
