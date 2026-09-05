using System;
using System.Collections.Generic;
using System.Windows;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>One candidate for a design-surface click, projected from whatever component model
	/// the calling designer uses (WinForms' DesignerComponentInfo, WinUI's element tree, ...) so
	/// this arbitration stays pure and shared rather than reimplemented per backend.</summary>
	/// <param name="Name">The component's own id. Compared by ordinal equality against the current
	/// selection and against <paramref name="ParentName"/>, so it must be the SAME identifier the
	/// caller uses for both.</param>
	/// <param name="ParentName">The containing component's id, or null/empty for the design root.
	/// The root is never a click candidate: clicking empty canvas must start a marquee, not
	/// "select the form", which the caller's own hit-test decides afterwards.</param>
	/// <param name="Bounds">Bounds in the same design-space the click point is given in.</param>
	/// <param name="IsVisible">Whether the component is actually on screen right now. A control on
	/// a non-selected TabPage reports bounds overlapping the page that IS showing, so counting it
	/// makes a click resolve to something the user cannot even see.</param>
	public readonly record struct DesignSurfaceClickCandidate(
		string Name, string? ParentName, Rect Bounds, bool IsVisible);

	/// <summary>What the design surface should do with a left-button press.</summary>
	public enum DesignSurfaceClickAction
	{
		/// <summary>The press belongs to an adorner glyph drawn on top (a move/resize thumb) which
		/// runs its own drag gesture - leave it alone.</summary>
		LetAdornerHandle,
		/// <summary>The press landed on empty canvas - begin a rubber-band selection.</summary>
		StartMarquee,
		/// <summary>The press landed on a component - resolve and select it.</summary>
		SelectComponent
	}

	/// <param name="Action">What to do.</param>
	/// <param name="ReleaseAdornerCapture">True when an adorner glyph may already have captured the
	/// mouse for a drag as this same press bubbled through it, but the press actually belongs to a
	/// component underneath. The caller must release that capture, or the drag continues and
	/// completes on top of the selection change.</param>
	public readonly record struct DesignSurfaceClickDecision(
		DesignSurfaceClickAction Action, bool ReleaseAdornerCapture);

	/// <summary>
	/// Decides who owns a left-button press on the design surface: an adorner glyph drawn on top of
	/// the selection, a component underneath it, or empty canvas.
	///
	/// This exists as a pure function because getting it wrong is invisible until someone clicks the
	/// exact wrong pixel, and every version of this logic that lived inline in a designer's mouse
	/// handler shipped a regression:
	/// <list type="number">
	/// <item>Bailing out on "the press came from an adorner" BEFORE checking for a tab-header hit
	/// meant clicking a TabControl's header did nothing once the TabControl itself was selected,
	/// because its move thumb covers the header strip too (the header is inside the control's own
	/// bounding rect).</item>
	/// <item>Fixing that by drilling through whenever the press landed on ANY component's bounds
	/// broke move-dragging outright: the selected component's own bounds contain the press, so the
	/// arbiter tore the move thumb's mouse capture away before it saw a single drag delta.</item>
	/// <item>Fixing THAT by ignoring only the selection itself still broke move-dragging for every
	/// NESTED control, because each of the selection's ANCESTORS contains the press as well - a
	/// TabPage contains its button, the TabControl contains that page. Only top-level controls,
	/// whose sole containing ancestor is the design root (never a candidate), kept working.</item>
	/// </list>
	/// The distinction that actually matters is "is something MORE SPECIFIC than the current
	/// selection under the pointer?" - i.e. a candidate that is neither the selection nor one of its
	/// ancestors. That is a drill-through; anything else within the selection's own bounds is the
	/// user starting to drag what they already selected.
	/// </summary>
	public static class DesignSurfaceClickArbiter
	{
		/// <summary>The gates a caller must apply BEFORE consulting this arbiter, in order:
		/// a press outside the design surface is ignored; a press while a resize/marquee drag is
		/// already running belongs to that drag; and a backend-specific hit that no component model
		/// can express - a TabControl's tab header, which is painted by the control itself and is
		/// not a component - must win even against an adorner drawn over it. Only then is the
		/// press a candidate for ordinary arbitration.</summary>
		public static DesignSurfaceClickDecision Decide(
			IReadOnlyList<DesignSurfaceClickCandidate> candidates,
			Point designPoint,
			string? selectedName,
			bool pressOriginatedOnAdorner)
		{
			var drillThrough = HasCandidateMoreSpecificThanSelection(candidates, designPoint, selectedName);
			if (pressOriginatedOnAdorner && !drillThrough)
				return new DesignSurfaceClickDecision(DesignSurfaceClickAction.LetAdornerHandle, false);
			var action = HitsAnyCandidate(candidates, designPoint)
				? DesignSurfaceClickAction.SelectComponent
				: DesignSurfaceClickAction.StartMarquee;
			return new DesignSurfaceClickDecision(action, drillThrough);
		}

		/// <summary>Whether any visible, non-root candidate contains the point - i.e. whether this
		/// press is on a component at all, as opposed to empty canvas.</summary>
		public static bool HitsAnyCandidate(
			IReadOnlyList<DesignSurfaceClickCandidate> candidates, Point designPoint)
		{
			for (var index = 0; index < candidates.Count; index++) {
				if (IsClickable(candidates[index]) && candidates[index].Bounds.Contains(designPoint))
					return true;
			}
			return false;
		}

		/// <summary>Whether something under the point is more specific than the current selection:
		/// a visible, non-root candidate that is neither the selection nor one of its ancestors.
		/// See the type-level remarks - excluding the ancestor chain is the whole point.</summary>
		public static bool HasCandidateMoreSpecificThanSelection(
			IReadOnlyList<DesignSurfaceClickCandidate> candidates, Point designPoint, string? selectedName)
		{
			var ancestors = AncestorsOf(candidates, selectedName);
			for (var index = 0; index < candidates.Count; index++) {
				var candidate = candidates[index];
				if (!IsClickable(candidate)) continue;
				if (String.Equals(candidate.Name, selectedName, StringComparison.Ordinal)) continue;
				if (ancestors.Contains(candidate.Name)) continue;
				if (candidate.Bounds.Contains(designPoint)) return true;
			}
			return false;
		}

		/// <summary>The selection's containing chain, by name. Tolerates a parent that is missing
		/// from <paramref name="candidates"/> (the design root is deliberately absent) and a cyclic
		/// Parent chain, which would otherwise hang the caller's mouse handler.</summary>
		public static ISet<string> AncestorsOf(
			IReadOnlyList<DesignSurfaceClickCandidate> candidates, string? selectedName)
		{
			var ancestors = new HashSet<string>(StringComparer.Ordinal);
			if (String.IsNullOrEmpty(selectedName)) return ancestors;
			var current = Find(candidates, selectedName);
			while (current is { } node && !String.IsNullOrEmpty(node.ParentName)) {
				if (!ancestors.Add(node.ParentName!)) break;
				current = Find(candidates, node.ParentName);
			}
			return ancestors;
		}

		/// <summary>The design root is not a click candidate (clicking empty canvas starts a
		/// marquee), and neither is anything not currently on screen.</summary>
		static bool IsClickable(DesignSurfaceClickCandidate candidate)
			=> candidate.IsVisible && !String.IsNullOrEmpty(candidate.ParentName);

		static DesignSurfaceClickCandidate? Find(
			IReadOnlyList<DesignSurfaceClickCandidate> candidates, string? name)
		{
			if (String.IsNullOrEmpty(name)) return null;
			for (var index = 0; index < candidates.Count; index++) {
				if (String.Equals(candidates[index].Name, name, StringComparison.Ordinal))
					return candidates[index];
			}
			return null;
		}
	}
}
