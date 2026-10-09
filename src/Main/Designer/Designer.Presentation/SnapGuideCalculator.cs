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
			double tolerance = 8.0,
			bool requireOverlap = false)
		{
			var guides = new List<(bool, double)>();
			var verticalCandidates = new List<(double Position, double OrthogonalStart, double OrthogonalEnd)>();
			var horizontalCandidates = new List<(double Position, double OrthogonalStart, double OrthogonalEnd)>();
			foreach (var (x, y, width, height) in siblingBounds)
			{
				verticalCandidates.Add((x, y, y + height));
				verticalCandidates.Add((x + width / 2, y, y + height));
				verticalCandidates.Add((x + width, y, y + height));
				horizontalCandidates.Add((y, x, x + width));
				horizontalCandidates.Add((y + height / 2, x, x + width));
				horizontalCandidates.Add((y + height, x, x + width));
			}

			var (ex, ey, ew, eh) = (startRect.X + deltaX, startRect.Y + deltaY, startRect.Width, startRect.Height);
			var ownV = new[] { ex, ex + ew / 2, ex + ew };
			var ownH = new[] { ey, ey + eh / 2, ey + eh };

			var verticalMatches = (requireOverlap
				? verticalCandidates.Where(c => Overlaps(ey, ey + eh, c.OrthogonalStart, c.OrthogonalEnd))
				: verticalCandidates)
				.SelectMany(candidate => ownV.Select(own => (Candidate: candidate, Own: own)))
				.OrderBy(match => Math.Abs(match.Candidate.Position - match.Own))
				.ToArray();
			if (verticalMatches.Length > 0 && verticalMatches[0] is var vertical
				&& Math.Abs(vertical.Candidate.Position - vertical.Own) <= tolerance)
			{
				deltaX += vertical.Candidate.Position - vertical.Own;
				guides.Add((true, vertical.Candidate.Position));
			}

			var horizontalMatches = (requireOverlap
				? horizontalCandidates.Where(c => Overlaps(ex, ex + ew, c.OrthogonalStart, c.OrthogonalEnd))
				: horizontalCandidates)
				.SelectMany(candidate => ownH.Select(own => (Candidate: candidate, Own: own)))
				.OrderBy(match => Math.Abs(match.Candidate.Position - match.Own))
				.ToArray();
			if (horizontalMatches.Length > 0 && horizontalMatches[0] is var horizontal
				&& Math.Abs(horizontal.Candidate.Position - horizontal.Own) <= tolerance)
			{
				deltaY += horizontal.Candidate.Position - horizontal.Own;
				guides.Add((false, horizontal.Candidate.Position));
			}

			return (deltaX, deltaY, guides);
		}

		static bool Overlaps(double start, double end, double otherStart, double otherEnd)
			=> start < otherEnd && otherStart < end;

		/// <summary>Finds the nearest sibling edge/centre for one actively dragged edge. Used by
		/// resize gestures, whose moving edge is not representable as a translation delta.</summary>
		public static (double Correction, double? Guide) SnapEdge(
			bool vertical, double position, double orthogonalStart, double orthogonalEnd,
			IEnumerable<(double X, double Y, double Width, double Height)> siblingBounds,
			double tolerance = 8.0, bool requireOverlap = true)
		{
			var candidates = siblingBounds.SelectMany(bounds => vertical
				? new[] { (bounds.X, bounds.Y, bounds.Y + bounds.Height), (bounds.X + bounds.Width / 2, bounds.Y, bounds.Y + bounds.Height), (bounds.X + bounds.Width, bounds.Y, bounds.Y + bounds.Height) }
				: new[] { (bounds.Y, bounds.X, bounds.X + bounds.Width), (bounds.Y + bounds.Height / 2, bounds.X, bounds.X + bounds.Width), (bounds.Y + bounds.Height, bounds.X, bounds.X + bounds.Width) });
			if (requireOverlap)
				candidates = candidates.Where(candidate => Overlaps(orthogonalStart, orthogonalEnd, candidate.Item2, candidate.Item3));
		var matches = candidates.OrderBy(candidate => Math.Abs(candidate.Item1 - position)).ToArray();
		if (matches.Length == 0)
			return (0, null);
		var nearest = matches[0];
		return Math.Abs(nearest.Item1 - position) > tolerance
				? (0, null)
				: (nearest.Item1 - position, nearest.Item1);
		}
	}
}
