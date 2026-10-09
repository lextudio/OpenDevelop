using System;
using System.Collections.Generic;
using System.Windows;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>
	/// Pure snapshot-to-overlay placement math shared by designer selection adorners.  It keeps
	/// design-space bounds, the viewport transform, label placement and resize anchors together
	/// so a rendering backend does not need to reproduce its own scale/origin arithmetic.
	/// </summary>
	public static class SelectionAdornerPlacementCalculator
	{
		public const double DefaultHandleSize = 7;

		public static Rect MapRect(Rect designRect, DesignViewport viewport)
		{
			var (left, top) = viewport.DesignToSurface(designRect.X, designRect.Y);
			return new Rect(left, top, designRect.Width * viewport.Scale, designRect.Height * viewport.Scale);
		}

		public static SelectionAdornerPlacement Calculate(Rect designRect, DesignViewport viewport,
			IReadOnlyList<string> handleNames)
		{
			var selection = MapRect(designRect, viewport);
			var handles = new List<(string Name, Point Center)>();
			foreach (var name in handleNames)
			{
				if (!TryGetDesignAnchor(designRect, name, out var anchor))
					continue;
				var (x, y) = viewport.DesignToSurface(anchor.X, anchor.Y);
				handles.Add((name, new Point(x, y)));
			}
			return new SelectionAdornerPlacement(selection,
				new Point(selection.X, Math.Max(0, selection.Y - 17)), handles);
		}

		/// <summary>Returns the enabled resize handle under a design-space pointer, preserving the
		/// center-third move zone used by the existing designer gestures.</summary>
		public static string? HandleAt(Rect designRect, Point designPoint, DesignViewport viewport,
			IReadOnlyList<string> handleNames, double handleSize = DefaultHandleSize)
		{
			if (designRect.IsEmpty)
				return null;
			var center = new Point(designRect.X + designRect.Width / 2, designRect.Y + designRect.Height / 2);
			if (Math.Abs(designPoint.X - center.X) < designRect.Width / 3
				&& Math.Abs(designPoint.Y - center.Y) < designRect.Height / 3)
				return null;
			var tolerance = (handleSize / 2 + 2) / viewport.Scale;
			foreach (var name in handleNames)
			{
				if (!TryGetDesignAnchor(designRect, name, out var anchor))
					continue;
				if (Math.Abs(designPoint.X - anchor.X) <= tolerance
					&& Math.Abs(designPoint.Y - anchor.Y) <= tolerance)
					return name;
			}
			return null;
		}

		static bool TryGetDesignAnchor(Rect bounds, string name, out Point anchor)
		{
			var x = bounds.X;
			var y = bounds.Y;
			var right = x + bounds.Width;
			var bottom = y + bounds.Height;
			anchor = name switch {
				"nw" => new Point(x, y),
				"n" => new Point(x + bounds.Width / 2, y),
				"ne" => new Point(right, y),
				"e" => new Point(right, y + bounds.Height / 2),
				"se" => new Point(right, bottom),
				"s" => new Point(x + bounds.Width / 2, bottom),
				"sw" => new Point(x, bottom),
				"w" => new Point(x, y + bounds.Height / 2),
				_ => default
			};
			return name is "nw" or "n" or "ne" or "e" or "se" or "s" or "sw" or "w";
		}
	}

	public readonly record struct SelectionAdornerPlacement(
		Rect Selection, Point LabelOrigin, IReadOnlyList<(string Name, Point Center)> Handles);
}
