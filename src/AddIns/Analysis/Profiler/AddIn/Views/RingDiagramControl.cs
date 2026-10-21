// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenDevelop.Profiler.Cpu;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>
/// The call tree as concentric rings: the centre is the focused node, each ring outward is one call depth,
/// and a slice's angle is its share of the focus's samples. Click a slice to focus it, click the centre
/// to go up one level, double-click a slice for its callers and callees.
/// </summary>
/// <remarks>
/// Adapted from the SharpDevelop profiler's <c>RingDiagramControl</c>, <c>PiePiece</c> and <c>HSVColor</c>:
/// the wedge geometry, the angle-based colouring and the interaction are carried over, retargeted from the
/// old <c>CallTreeNodeViewModel</c> to <see cref="CallTreeNode"/>. The slices are drawn by this one element
/// instead of one shape per slice, so a wide tree does not turn into thousands of controls.
/// </remarks>
public sealed class RingDiagramControl : FrameworkElement
{
	const int MaxLevels = 10;
	const double MinSweepDegrees = 0.5;
	const double SliceMargin = 6;

	sealed record Slice(CallTreeNode Node, int Level, double StartAngle, double SweepAngle);

	readonly List<Slice> slices = [];
	CallTreeNode? focus;
	Slice? hovered;
	double sampleIntervalMs = 1;
	ProfileUnit unit = ProfileUnit.Milliseconds;

	public RingDiagramControl()
	{
		ClipToBounds = true;
		Focusable = true;
	}

	/// <summary>Raised with a frame id when a slice is double-clicked.</summary>
	public event EventHandler<int>? FrameActivated;

	internal void Show(CallTreeNode treeRoot, double intervalMs, ProfileUnit profileUnit = ProfileUnit.Milliseconds)
	{
		unit = profileUnit;
		sampleIntervalMs = intervalMs;
		SetFocus(treeRoot);
	}

	/// <summary>What the diagram currently shows, for the integration-test probes.</summary>
	internal object Snapshot() => new
	{
		focus = focus?.Name,
		slices = slices.Count,
		levels = slices.Count == 0 ? 0 : slices.Max(s => s.Level),
	};

	void SetFocus(CallTreeNode node)
	{
		focus = node;
		hovered = null;
		slices.Clear();
		if (node.Total > 0)
			AddSlices(node, startAngle: 0, level: 1);
		ToolTip = null;
		InvalidateVisual();
	}

	void AddSlices(CallTreeNode parent, double startAngle, int level)
	{
		if (level > MaxLevels || focus is null)
			return;
		foreach (var child in parent.Children)
		{
			var sweep = Math.Min(child.Total * 360.0 / focus.Total, 359.9999);
			// Slices thinner than half a degree are invisible and only cost time.
			if (sweep <= MinSweepDegrees)
				continue;
			slices.Add(new Slice(child, level, startAngle, sweep));
			AddSlices(child, startAngle, level + 1);
			startAngle += sweep;
		}
	}

	(Point Center, double CentreRadius, double RingThickness) Layout()
	{
		var half = Math.Max(Math.Min(ActualWidth, ActualHeight) / 2 - SliceMargin, 20);
		var centreRadius = Math.Min(half * 0.18, 44);
		var levels = slices.Count == 0 ? 1 : Math.Min(slices.Max(s => s.Level), MaxLevels);
		var thickness = Math.Min((half - centreRadius) / levels, 70);
		return (new Point(ActualWidth / 2, ActualHeight / 2), centreRadius, thickness);
	}

	protected override Size MeasureOverride(Size availableSize) =>
		new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
			double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height);

	protected override void OnRender(DrawingContext context)
	{
		context.DrawRectangle(SystemColors.WindowBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
		if (focus is null)
			return;

		var (center, centreRadius, thickness) = Layout();
		var outline = new Pen(SystemColors.WindowBrush, 0.75);
		foreach (var slice in slices)
		{
			var color = ColourOf(slice).ToColor();
			context.DrawGeometry(
				new SolidColorBrush(color), outline,
				Wedge(center, centreRadius + (slice.Level - 1) * thickness, centreRadius + slice.Level * thickness,
					slice.StartAngle, slice.SweepAngle));
		}

		// The centre disc is the focused node; clicking it goes back up.
		context.DrawEllipse(SystemColors.ControlBrush, new Pen(SystemColors.ActiveBorderBrush, 1), center, centreRadius, centreRadius);
		var label = new FormattedText(
			focus.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11,
			SystemColors.ControlTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
		{
			MaxTextWidth = Math.Max(centreRadius * 1.6, 1),
			MaxTextHeight = centreRadius * 1.4,
			TextAlignment = TextAlignment.Center,
			Trimming = TextTrimming.CharacterEllipsis,
		};
		context.DrawText(label, new Point(center.X - label.Width / 2, center.Y - label.Height / 2));
	}

	// Colour follows the slice's angle and darkens with depth; the hovered slice is brightened.
	HsvColor ColourOf(Slice slice)
	{
		var colour = new HsvColor(slice.StartAngle, 0.5, 0.82 - slice.Level / 40.0);
		return ReferenceEquals(slice, hovered) ? colour.WithValue(0.95) : colour;
	}

	/// <summary>A ring segment between two radii, from <paramref name="startAngle"/> clockwise from twelve o'clock.</summary>
	static StreamGeometry Wedge(Point center, double innerRadius, double outerRadius, double startAngle, double sweepAngle)
	{
		Point At(double angle, double radius)
		{
			var radians = Math.PI / 180 * (angle - 90);
			return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
		}

		var largeArc = sweepAngle > 180;
		var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
		using (var context = geometry.Open())
		{
			context.BeginFigure(At(startAngle, innerRadius), isFilled: true, isClosed: true);
			context.LineTo(At(startAngle, outerRadius), isStroked: true, isSmoothJoin: false);
			context.ArcTo(At(startAngle + sweepAngle, outerRadius), new Size(outerRadius, outerRadius), 0, largeArc,
				SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
			context.LineTo(At(startAngle + sweepAngle, innerRadius), isStroked: true, isSmoothJoin: false);
			context.ArcTo(At(startAngle, innerRadius), new Size(innerRadius, innerRadius), 0, largeArc,
				SweepDirection.Counterclockwise, isStroked: true, isSmoothJoin: false);
		}
		geometry.Freeze();
		return geometry;
	}

	/// <summary>The slice under a point, <c>null</c> for the centre or empty space; the centre is reported separately.</summary>
	Slice? SliceAt(Point point, out bool inCentre)
	{
		inCentre = false;
		if (focus is null)
			return null;
		var (center, centreRadius, thickness) = Layout();
		var dx = point.X - center.X;
		var dy = point.Y - center.Y;
		var distance = Math.Sqrt(dx * dx + dy * dy);
		if (distance <= centreRadius)
		{
			inCentre = true;
			return null;
		}

		var level = (int)Math.Floor((distance - centreRadius) / thickness) + 1;
		var angle = Math.Atan2(dy, dx) * 180 / Math.PI + 90; // clockwise from twelve o'clock
		angle = (angle % 360 + 360) % 360;
		return slices.FirstOrDefault(s => s.Level == level && angle >= s.StartAngle && angle < s.StartAngle + s.SweepAngle);
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		var slice = SliceAt(e.GetPosition(this), out var inCentre);
		if (!ReferenceEquals(slice, hovered))
		{
			hovered = slice;
			InvalidateVisual();
		}

		var node = slice?.Node ?? (inCentre ? focus : null);
		ToolTip = node is null || focus is null
			? null
			: $"{node.Name}\n{node.Module}\n"
			  + $"Total {ReportFormat.Quantity(node.Total * sampleIntervalMs, unit)} ({ReportFormat.Fraction(node.Total, focus.Total):P1} of the centre)   "
			  + $"Self {ReportFormat.Quantity(node.Self * sampleIntervalMs, unit)}";
	}

	protected override void OnMouseLeave(MouseEventArgs e)
	{
		if (hovered is not null)
		{
			hovered = null;
			InvalidateVisual();
		}
	}

	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		Focus();
		var slice = SliceAt(e.GetPosition(this), out var inCentre);
		if (inCentre && focus?.Parent is { } parent)
		{
			SetFocus(parent);
		}
		else if (slice is not null && e.ClickCount == 2 && slice.Node.FrameId != CallTreeNode.RootFrameId)
		{
			FrameActivated?.Invoke(this, slice.Node.FrameId);
		}
		else if (slice is not null && slice.Node.Children.Count > 0)
		{
			SetFocus(slice.Node);
		}
		e.Handled = true;
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		if (e.Key is Key.Escape or Key.Back && focus?.Parent is { } parent)
		{
			SetFocus(parent);
			e.Handled = true;
		}
	}
}
