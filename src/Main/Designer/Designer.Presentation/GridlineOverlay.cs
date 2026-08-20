using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>
	/// Draws design-space gridlines as plain <see cref="Line"/> children of a <see cref="Canvas"/>,
	/// instead of a tiled <see cref="DrawingBrush"/> assigned as a Background. LibreWPF-on-macOS
	/// does not paint TileMode.Tile brushes even though the property assignment itself succeeds
	/// (same class of native-rendering gap as RenderTargetBitmap/wpfgfx_cor3 not existing on
	/// macOS) - see doc/technotes/designer-gridlines-bug.md. Line is an ordinary WPF shape with
	/// no tiling involved, so it renders correctly under the same host.
	/// </summary>
	public sealed class GridlineOverlay
	{
		const double GridCellSize = 20;
		static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

		// HorizontalAlignment/VerticalAlignment=Left/Top matter when this sits inside a Grid (the
		// WPF designer's designSurface): a Grid centers an explicitly-sized Stretch-aligned child
		// instead of pinning it to the Margin offset, which would drift the grid off the frame at
		// any zoom/fit where the frame isn't the same size as the available Grid cell. Harmless
		// inside a Canvas (the Uno designer's viewportCanvas), which ignores alignment for its
		// children and positions them via Canvas.Left/Top instead.
		public Canvas Visual { get; } = new Canvas {
			IsHitTestVisible = false,
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top
		};

		/// <summary>Re-draws the grid to cover <paramref name="width"/> x <paramref name="height"/>
		/// design-surface pixels, with cells sized in DESIGN units (a 20-unit cell stays 20 design
		/// units, drawn larger when zoomed in) - same behavior as the DrawingBrush it replaces.</summary>
		public void Update(double width, double height, double scale, bool show)
		{
			Visual.Children.Clear();
			if (!show || width <= 0 || height <= 0 || scale <= 0)
				return;
			var step = GridCellSize * scale;
			if (step < 2)
				return; // guard against an unbounded line count at extreme zoom-out
			for (var x = 0.0; x <= width; x += step)
				Visual.Children.Add(CreateLine(x, 0, x, height));
			for (var y = 0.0; y <= height; y += step)
				Visual.Children.Add(CreateLine(0, y, width, y));
		}

		static Line CreateLine(double x1, double y1, double x2, double y2) =>
			new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = LineBrush, StrokeThickness = 1 };
	}
}
