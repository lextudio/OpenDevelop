using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>
	/// Pure geometry for drag-to-reorder among siblings: given each sibling's extent along the
	/// drag axis (Start/Length - X/Width for a horizontal strip, Y/Height for a vertical list) and
	/// how far the dragged item has moved, computes which slot it should land in. Framework- and
	/// axis-agnostic like <see cref="SnapGuideCalculator"/>, so a WPF-hosted tray (vertical) and a
	/// WinForms-hosted strip (horizontal) can share one implementation instead of each re-deriving
	/// "which index did the drag land on" from scratch - see wpf-designer.md's ContextMenu-tray
	/// drag-reorder notes for why this one exists (the WPF tray previously used a "deliberately
	/// simpler" pair of up/down move buttons instead of a real drag, unlike the WinForms designer's
	/// own strip/popup reorder gesture).
	/// </summary>
	public static class ReorderGestureCalculator
	{
		/// <summary>Returns the 0-based index <paramref name="draggedIndex"/> should move to, in
		/// the same "remove then insert" semantics a typical reorder RPC uses (remove the dragged
		/// item from the list, then insert it at the returned index) - i.e. suitable directly as
		/// the target for <c>delta = ComputeTargetIndex(...) - draggedIndex</c>.</summary>
		/// <param name="itemExtents">Every sibling's (Start, Length) along the drag axis, in their
		/// current (pre-drag) order - including the dragged item itself, at <paramref name="draggedIndex"/>.</param>
		/// <param name="draggedIndex">The dragged item's index into <paramref name="itemExtents"/>.</param>
		/// <param name="dragDelta">Cumulative pointer movement along the drag axis since the drag
		/// started, in the same units as Start/Length.</param>
		public static int ComputeTargetIndex(
			IReadOnlyList<(double Start, double Length)> itemExtents, int draggedIndex, double dragDelta)
		{
			if (itemExtents.Count <= 1)
				return draggedIndex;
			var dragged = itemExtents[draggedIndex];
			var draggedCenter = dragged.Start + dragged.Length / 2 + dragDelta;
			// The dragged item's new slot is simply how many OTHER items it has moved past -
			// counting siblings whose own (undragged) center now sits before the dragged item's
			// current center reproduces exactly the "remove then insert" index a caller needs,
			// with no separate before/after-removal index-shifting logic required.
			var newIndex = 0;
			for (var i = 0; i < itemExtents.Count; i++)
			{
				if (i == draggedIndex)
					continue;
				var other = itemExtents[i];
				if (other.Start + other.Length / 2 <= draggedCenter)
					newIndex++;
			}
			return Math.Clamp(newIndex, 0, itemExtents.Count - 1);
		}
	}
}
