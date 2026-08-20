using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>
	/// Pure geometry for drag-move alignment snapping: corrects a proposed move delta so the
	/// dragged element's left/center/right (and top/middle/bottom) lines snap onto the nearest
	/// matching line among a set of sibling bounds, within <paramref name="tolerance"/> design
	/// units. Relocated from UnoDesignRuntimeHost's own ApplySnap (the only Uno-specific parts
	/// were the surrounding drag-lifecycle/RPC plumbing, not this calculation), so the WPF and
	/// WinForms designers can share the exact same snapping behavior instead of a
	/// grid-based/no snapping at all.
	/// </summary>
	public static class SnapGuideCalculator
	{
		public static (double DX, double DY, IReadOnlyList<(bool IsVertical, double Position)> Guides) ApplySnap(
			(double X, double Y, double Width, double Height) startRect,
			double deltaX, double deltaY,
			IEnumerable<(double X, double Y, double Width, double Height)> siblingBounds,
			double tolerance = 8.0)
		{
			var guides = new List<(bool, double)>();
			var verticalCandidates = new List<double>();
			var horizontalCandidates = new List<double>();
			foreach (var (x, y, width, height) in siblingBounds)
			{
				verticalCandidates.Add(x);
				verticalCandidates.Add(x + width / 2);
				verticalCandidates.Add(x + width);
				horizontalCandidates.Add(y);
				horizontalCandidates.Add(y + height / 2);
				horizontalCandidates.Add(y + height);
			}

			var (ex, ey, ew, eh) = (startRect.X + deltaX, startRect.Y + deltaY, startRect.Width, startRect.Height);
			var ownV = new[] { ex, ex + ew / 2, ex + ew };
			var ownH = new[] { ey, ey + eh / 2, ey + eh };

			foreach (var own in ownV)
			{
				if (verticalCandidates.Count == 0)
					break;
				var best = verticalCandidates.OrderBy(c => Math.Abs(c - own)).First();
				if (Math.Abs(best - own) <= tolerance)
				{
					deltaX += best - own;
					guides.Add((true, best));
					break;
				}
			}
			foreach (var own in ownH)
			{
				if (horizontalCandidates.Count == 0)
					break;
				var best = horizontalCandidates.OrderBy(c => Math.Abs(c - own)).First();
				if (Math.Abs(best - own) <= tolerance)
				{
					deltaY += best - own;
					guides.Add((false, best));
					break;
				}
			}

			return (deltaX, deltaY, guides);
		}
	}
}
