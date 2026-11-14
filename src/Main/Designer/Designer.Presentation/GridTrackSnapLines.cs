using System;
using System.Collections.Generic;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>
/// The snap lines a Grid parent contributes to a drag: its own outer edges plus every measured
/// row/column divider. The controller's sibling targets already cover element edges; these cover
/// the layout the elements are placed in, so a drag can land on a cell boundary without another
/// element having to sit there. Every line is restricted to the Grid's own orthogonal extent, so a
/// divider can only catch a gesture that overlaps the Grid on that axis.
///
/// The metadata comes from the described frame (<see cref="DesignerGridGuides"/> on the Grid's
/// runtime node); a Grid that reports no measured layout contributes nothing, and the drag then
/// snaps only to siblings.
/// </summary>
public static class GridTrackSnapLines
{
	public static (IReadOnlyList<SnapGuideCalculator.Line> Vertical, IReadOnlyList<SnapGuideCalculator.Line> Horizontal) For(DesignerElementNode? parent)
	{
		if (parent is not { Width: > 0, Height: > 0, GridTracks: { Accepted: true } tracks })
			return (Array.Empty<SnapGuideCalculator.Line>(), Array.Empty<SnapGuideCalculator.Line>());

		double top = parent.Y;
		double bottom = parent.Y + parent.Height;
		double left = parent.X;
		double right = parent.X + parent.Width;

		var vertical = new List<SnapGuideCalculator.Line>(tracks.ColumnTracks.Count + 2) {
			new(left, top, bottom),
			new(right, top, bottom)
		};
		var horizontal = new List<SnapGuideCalculator.Line>(tracks.RowTracks.Count + 2) {
			new(top, left, right),
			new(bottom, left, right)
		};
		// Offset is already cumulative from the Grid's own origin, so Offset + Size is the trailing
		// divider of each track (and the Grid's far edge for the last one).
		foreach (var column in tracks.ColumnTracks)
			vertical.Add(new(left + column.Offset + column.Size, top, bottom));
		foreach (var row in tracks.RowTracks)
			horizontal.Add(new(top + row.Offset + row.Size, left, right));
		return (vertical, horizontal);
	}
}
