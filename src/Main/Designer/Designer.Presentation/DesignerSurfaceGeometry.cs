using System.Windows;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>
	/// The smoke-probe geometry all three designers report through their
	/// <c>od.&lt;x&gt;-designer.surface-geometry</c> DevFlow action: the rendered design
	/// bitmap bounds, the current selection outline bounds, the bottom-right resize handle
	/// position, and the selected element's own bounds - all in screen coordinates
	/// (<c>PointToScreen</c> is reliable under LibreWPF, so integration tests can drive the
	/// resize handle directly and compare in one coordinate space).
	/// The resize-drag invariant is that selection/handle always hug the rendered element;
	/// the integration tests assert it before and after every drag.
	/// </summary>
	public readonly record struct DesignerSurfaceGeometry(Rect Frame, Rect Selection, Point Handle, Rect Element)
	{
		/// <summary>The screen rectangle of the canvas area actually visible to the user, or
		/// <see cref="Rect.Empty"/> when a designer does not report it. A pointer gesture aimed at
		/// a handle outside it lands on whatever covers that point (a split pane's editor, a
		/// splitter), not on the design surface.</summary>
		public Rect Viewport { get; init; } = Rect.Empty;

		/// <summary>The rendered frame's size in design units, or <see cref="Size.Empty"/> when not
		/// reported. Unlike <see cref="Frame"/> it does not depend on zoom: in Fit mode a resized
		/// design is re-scaled to fit, so its screen size barely changes while this grows.</summary>
		public Size DesignSize { get; init; } = Size.Empty;

		/// <summary>True when <see cref="Handle"/> lies inside a reported <see cref="Viewport"/>.</summary>
		public bool? HandleVisible => Viewport.IsEmpty ? null : Viewport.Contains(Handle);
	}

	public static class DesignerSurfaceGeometryProbe
	{
		/// <summary>An element's screen-space bounding rectangle (two-corner PointToScreen).</summary>
		public static Rect ScreenBoundsOf(FrameworkElement element)
			=> ScreenBoundsOf(element, new Size(element.ActualWidth, element.ActualHeight));

		/// <summary>Screen-space bounds using an explicit rendered size when it differs from layout size.</summary>
		public static Rect ScreenBoundsOf(FrameworkElement element, Size size)
		{
			var topLeft = element.PointToScreen(new Point(0, 0));
			var bottomRight = element.PointToScreen(new Point(size.Width, size.Height));
			return new Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
		}

		/// <summary>
		/// Maps a design-space rect through a viewport to the screen rectangle of a UI element
		/// in the same visual space as the design surface. <paramref name="scrollOffset"/> is
		/// the scroll-viewer offset to subtract for scrolling surfaces (WinUI/Uno); pass
		/// default for unscrolled surfaces (WinForms, WPF).
		/// </summary>
		public static Rect DesignRectToScreen(DesignViewport viewport, Rect designRect, UIElement origin,
			Vector scrollOffset = default)
		{
			var (x, y) = viewport.DesignToSurface(designRect.X, designRect.Y);
			var (x2, y2) = viewport.DesignToSurface(
				designRect.X + designRect.Width, designRect.Y + designRect.Height);
			var topLeft = origin.PointToScreen(new Point(x - scrollOffset.X, y - scrollOffset.Y));
			var bottomRight = origin.PointToScreen(new Point(x2 - scrollOffset.X, y2 - scrollOffset.Y));
			return new Rect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
		}

		/// <summary>
		/// The JSON payload of the <c>surface-geometry</c> DevFlow actions (identical shape on
		/// all three designers: <c>frame</c> is the whole design bitmap, <c>element</c> the
		/// selected element's bounds, <c>selection</c> its outline, <c>handle</c> the bottom-right
		/// corner).
		/// </summary>
		public static object ToJson(DesignerSurfaceGeometry geometry)
		{
			return new {
				available = true,
				frame = new { x = geometry.Frame.X, y = geometry.Frame.Y, width = geometry.Frame.Width, height = geometry.Frame.Height },
				selection = new { x = geometry.Selection.X, y = geometry.Selection.Y, width = geometry.Selection.Width, height = geometry.Selection.Height },
				handle = new { x = geometry.Handle.X, y = geometry.Handle.Y },
				element = new { x = geometry.Element.X, y = geometry.Element.Y, width = geometry.Element.Width, height = geometry.Element.Height },
				viewport = geometry.Viewport.IsEmpty ? null : new { x = geometry.Viewport.X, y = geometry.Viewport.Y, width = geometry.Viewport.Width, height = geometry.Viewport.Height },
				handleVisible = geometry.HandleVisible,
				designSize = geometry.DesignSize.IsEmpty ? null : new { width = geometry.DesignSize.Width, height = geometry.DesignSize.Height }
			};
		}
	}
}
