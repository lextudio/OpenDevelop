using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenDevelop.Profiler.Cpu;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>
/// An icicle-style flame graph of the call tree: one row per call depth, each box as wide as the share of
/// samples that passed through it, coloured by module. Click a box to zoom into it, right-click (or
/// Escape) to zoom back out, double-click to see its callers and callees.
/// </summary>
public sealed class FlameGraphControl : FrameworkElement
{
	const double RowHeight = 20;
	const double MinDrawWidth = 0.75;
	const double MinTextWidth = 42;

	readonly Dictionary<string, Brush> brushes = [];
	CallTreeNode? root;
	CallTreeNode? zoom;
	double sampleIntervalMs = 1;
	ProfileUnit unit = ProfileUnit.Milliseconds;

	public FlameGraphControl()
	{
		Focusable = true;
		ClipToBounds = true;
	}

	/// <summary>Raised with a frame id when a box is double-clicked.</summary>
	public event EventHandler<int>? FrameActivated;

	internal void Show(CallTreeNode treeRoot, double intervalMs, ProfileUnit profileUnit = ProfileUnit.Milliseconds)
	{
		unit = profileUnit;
		root = zoom = treeRoot;
		sampleIntervalMs = intervalMs;
		InvalidateMeasure();
		InvalidateVisual();
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		var width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
		return new Size(width, zoom is null ? 0 : (Depth(zoom) + 1) * RowHeight);
	}

	protected override void OnRender(DrawingContext context)
	{
		context.DrawRectangle(SystemColors.WindowBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
		if (zoom is not null && zoom.Total > 0)
			Draw(context, zoom, 0, ActualWidth, 0);
	}

	void Draw(DrawingContext context, CallTreeNode node, double x, double width, int row)
	{
		if (width < MinDrawWidth)
			return;

		var rect = new Rect(x, row * RowHeight, width, RowHeight - 1);
		context.DrawRectangle(BrushFor(node), null, rect);
		if (width >= MinTextWidth)
			DrawText(context, node.Name, rect);

		var childX = x;
		foreach (var child in node.Children)
		{
			var childWidth = width * child.Total / node.Total;
			Draw(context, child, childX, childWidth, row + 1);
			childX += childWidth;
		}
	}

	void DrawText(DrawingContext context, string text, Rect rect)
	{
		var formatted = new FormattedText(
			text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11,
			Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip)
		{
			MaxTextWidth = Math.Max(rect.Width - 6, 1),
			MaxTextHeight = RowHeight - 2,
			Trimming = TextTrimming.CharacterEllipsis,
		};
		context.DrawText(formatted, new Point(rect.X + 3, rect.Y + 2));
	}

	Brush BrushFor(CallTreeNode node)
	{
		var key = node.FrameId == CallTreeNode.RootFrameId ? "\0root" : node.Module;
		if (!brushes.TryGetValue(key, out var brush))
		{
			var hue = key == "\0root" ? 210 : StableHash(key) % 360;
			var saturation = key == "\0root" ? 0.1 : 0.55;
			brush = new SolidColorBrush(FromHsl(hue, saturation, 0.78));
			brush.Freeze();
			brushes[key] = brush;
		}
		return brush;
	}

	// string.GetHashCode is randomised per process; colours should be the same on every run.
	static int StableHash(string text)
	{
		var hash = 17;
		foreach (var c in text)
			hash = unchecked(hash * 31 + c);
		return Math.Abs(hash);
	}

	static Color FromHsl(double hue, double saturation, double lightness)
	{
		var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
		var h = hue / 60;
		var x = c * (1 - Math.Abs(h % 2 - 1));
		var (r, g, b) = (int)h switch
		{
			0 => (c, x, 0.0),
			1 => (x, c, 0.0),
			2 => (0.0, c, x),
			3 => (0.0, x, c),
			4 => (x, 0.0, c),
			_ => (c, 0.0, x),
		};
		var m = lightness - c / 2;
		return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
	}

	static int Depth(CallTreeNode node)
	{
		var deepest = 0;
		var stack = new Stack<(CallTreeNode Node, int Depth)>();
		stack.Push((node, 0));
		while (stack.Count > 0)
		{
			var (current, depth) = stack.Pop();
			deepest = Math.Max(deepest, depth);
			foreach (var child in current.Children)
				stack.Push((child, depth + 1));
		}
		return deepest;
	}

	CallTreeNode? NodeAt(Point point)
	{
		if (zoom is null || zoom.Total == 0 || ActualWidth <= 0)
			return null;
		var node = zoom;
		var x = 0.0;
		var width = ActualWidth;
		var row = (int)(point.Y / RowHeight);
		for (var depth = 0; depth < row; depth++)
		{
			CallTreeNode? next = null;
			var childX = x;
			foreach (var child in node.Children)
			{
				var childWidth = width * child.Total / node.Total;
				if (point.X >= childX && point.X < childX + childWidth)
				{
					next = child;
					x = childX;
					width = childWidth;
					break;
				}
				childX += childWidth;
			}
			if (next is null)
				return null;
			node = next;
		}
		return point.X >= x && point.X < x + width ? node : null;
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		if (NodeAt(e.GetPosition(this)) is { } node && root is not null)
		{
			ToolTip = $"{node.Name}\n{node.Module}\n"
				+ $"Total {ReportFormat.Quantity(node.Total * sampleIntervalMs, unit)} ({ReportFormat.Fraction(node.Total, root.Total):P1})   "
				+ $"Self {ReportFormat.Quantity(node.Self * sampleIntervalMs, unit)} ({ReportFormat.Fraction(node.Self, root.Total):P1})";
		}
		else
		{
			ToolTip = null;
		}
	}

	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		Focus();
		if (NodeAt(e.GetPosition(this)) is not { } node)
			return;
		if (e.ClickCount == 2 && node.FrameId != CallTreeNode.RootFrameId)
		{
			FrameActivated?.Invoke(this, node.FrameId);
		}
		else if (!ReferenceEquals(node, zoom))
		{
			zoom = node;
			InvalidateMeasure();
			InvalidateVisual();
		}
		e.Handled = true;
	}

	protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
	{
		ZoomOut();
		e.Handled = true;
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			ZoomOut();
			e.Handled = true;
		}
	}

	void ZoomOut()
	{
		if (zoom?.Parent is not { } parent)
			return;
		zoom = parent;
		InvalidateMeasure();
		InvalidateVisual();
	}
}
