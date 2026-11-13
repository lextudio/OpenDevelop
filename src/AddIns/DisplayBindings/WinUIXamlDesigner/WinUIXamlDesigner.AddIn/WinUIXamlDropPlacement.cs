using System;
using System.Collections.Generic;
using System.Globalization;

namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>One measured Grid track, in design units, relative to the Grid's own origin.</summary>
public readonly record struct DropTrack(double Offset, double Size);

/// <summary>One measured child rectangle, in design units, in the same space as the drop point.</summary>
public readonly record struct DropChildBounds(double X, double Y, double Width, double Height);

/// <summary>
/// Turns a design-surface drop point into the layout the new element has to be authored with:
/// the Grid cell it landed in, its Canvas position, or its index in a StackPanel.
///
/// Pure math over the measured frame facts (no WPF, no XDocument), so a real Toolbox drag and
/// the <c>drop</c> test hook share one rule and the rule itself is covered by executable tests.
/// Every method returns null (or an empty attribute list) when the measurement it needs is
/// missing, which leaves the caller with a plain append - the behavior a drop had before
/// placement existed - instead of a guessed position.
/// </summary>
public static class WinUIXamlDropPlacement
{
	/// <summary>The containers a drop can land in: each takes an unbounded child collection and
	/// positions its children by a different mechanic the placement below knows how to express.</summary>
	public static bool IsDropContainer(string typeName)
		=> typeName is "Grid" or "StackPanel" or "Canvas";

	/// <summary>
	/// The Grid cell containing a point, from the measured row/column tracks. Track offsets are
	/// local to the Grid, so the point is translated by the Grid's own origin first. Null when the
	/// point is outside the Grid's bounds or the host measured no tracks for it: a Grid nobody
	/// measured must not claim a cell.
	/// </summary>
	public static (int Row, int Column)? GridCell(double x, double y,
		double gridX, double gridY, double gridWidth, double gridHeight,
		IReadOnlyList<DropTrack> rows, IReadOnlyList<DropTrack> columns)
	{
		if (rows == null || columns == null || rows.Count == 0 || columns.Count == 0)
			return null;
		if (!(gridWidth > 0) || !(gridHeight > 0))
			return null;
		if (x < gridX || y < gridY || x >= gridX + gridWidth || y >= gridY + gridHeight)
			return null;
		return (TrackIndex(rows, y - gridY), TrackIndex(columns, x - gridX));
	}

	static int TrackIndex(IReadOnlyList<DropTrack> tracks, double local)
	{
		for (var index = 0; index < tracks.Count; index++)
		{
			var track = tracks[index];
			if (local < track.Offset + track.Size)
				return index;
		}
		// Inside the Grid but past the last measured track: rounding at the far edge. Clamping
		// keeps the element inside the Grid rather than deleting the cell assignment.
		return tracks.Count - 1;
	}

	/// <summary>
	/// A Canvas child's <c>Canvas.Left</c>/<c>Canvas.Top</c> for a drop point, in whole design
	/// units so the authored value is one an author can read. The point is translated by the
	/// Canvas's own origin; a negative result (a drop above/left of the Canvas) clamps to zero so
	/// the element stays where it is dropped instead of leaving the visible area.
	/// </summary>
	public static (double Left, double Top) CanvasPosition(double x, double y, double canvasX, double canvasY)
		=> (Math.Max(0, Math.Round(x - canvasX)), Math.Max(0, Math.Round(y - canvasY)));

	/// <summary>
	/// The child index a drop point falls into in a StackPanel: the first child whose midpoint is
	/// past the point, or the child count when the point is past every midpoint. This is the
	/// StackPanel's own layout rule (children are placed in order), so the index is the whole
	/// placement - there is no attached property to write.
	/// </summary>
	public static int StackIndex(double x, double y, bool horizontal, IReadOnlyList<DropChildBounds> children)
	{
		if (children == null || children.Count == 0)
			return 0;
		var value = horizontal ? x : y;
		for (var index = 0; index < children.Count; index++)
		{
			var child = children[index];
			var middle = horizontal ? child.X + child.Width / 2 : child.Y + child.Height / 2;
			if (value < middle)
				return index;
		}
		return children.Count;
	}

	/// <summary>
	/// The Grid attached properties for a cell. The default cell writes nothing: an element in
	/// row 0/column 0 needs no markup, and a redundant <c>Grid.Row="0"</c> is noise an author
	/// deletes on sight.
	/// </summary>
	public static IReadOnlyList<(string Attribute, string Value)> GridCellAttributes(int row, int column)
	{
		var attributes = new List<(string, string)>(2);
		if (row != 0)
			attributes.Add(("Grid.Row", row.ToString(CultureInfo.InvariantCulture)));
		if (column != 0)
			attributes.Add(("Grid.Column", column.ToString(CultureInfo.InvariantCulture)));
		return attributes;
	}

	/// <summary>
	/// <c>Canvas.Left</c>/<c>Canvas.Top</c> for a position, always written: a Canvas child
	/// without an offset sits at the Canvas origin, which is not where it was dropped.
	/// </summary>
	public static IReadOnlyList<(string Attribute, string Value)> CanvasPositionAttributes(double left, double top)
		=> new[] {
			("Canvas.Left", Number(left)),
			("Canvas.Top", Number(top)),
		};

	static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
