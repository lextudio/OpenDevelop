using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenDevelop.Profiler.Cpu;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>One runtime counter drawn as a line over the capture timeline, scaled from zero to its peak.</summary>
internal sealed class CounterChart : FrameworkElement
{
	const double Padding = 3;
	readonly CounterSeries series;
	readonly double originMs;
	readonly double durationMs;
	int hoverIndex = -1;

	public CounterChart(CounterSeries series, double originMs, double durationMs)
	{
		this.series = series;
		this.originMs = originMs;
		this.durationMs = Math.Max(durationMs, 1);
		Height = 64;
		MinWidth = 120;
	}

	internal string Name => series.Name;

	/// <summary>The index of the reading nearest to a point's time, or -1 for a counter with no readings.</summary>
	int NearestTo(Point point)
	{
		if (series.TimesMs.Count == 0)
			return -1;
		var span = Math.Max(ActualWidth - 2 * Padding, 1);
		var timeMs = originMs + (point.X - Padding) / span * durationMs;
		var best = 0;
		for (var i = 1; i < series.TimesMs.Count; i++)
			if (Math.Abs(series.TimesMs[i] - timeMs) < Math.Abs(series.TimesMs[best] - timeMs))
				best = i;
		return best;
	}

	/// <summary>
	/// Marks the reading nearest to a point and shows its time and value as the tooltip, as moving the mouse there does;
	/// returns the text. The integration tests drive the same path with a point taken from <see cref="Snapshot"/>.
	/// </summary>
	internal string? HoverAt(Point point)
	{
		hoverIndex = NearestTo(point);
		InvalidateVisual();
		if (hoverIndex < 0)
		{
			ToolTip = null;
			return null;
		}
		var text = $"{series.DisplayName}\n{ReportFormat.CounterValue(series.Values[hoverIndex], series.Units)} "
			+ $"at {(series.TimesMs[hoverIndex] - originMs) / 1000:0.0#} s";
		ToolTip = text;
		return text;
	}

	protected override void OnMouseMove(MouseEventArgs e) => HoverAt(e.GetPosition(this));

	protected override void OnMouseLeave(MouseEventArgs e)
	{
		hoverIndex = -1;
		ToolTip = null;
		InvalidateVisual();
	}

	Point PointOf(int index)
	{
		var (x, y) = Plot()[index];
		return new Point(
			Padding + Math.Clamp(x, 0, 1) * (ActualWidth - 2 * Padding),
			ActualHeight - Padding - y * (ActualHeight - 2 * Padding));
	}

	/// <summary>The chart's size and each reading's position in this element's coordinates, for the probes.</summary>
	internal object Snapshot() => new
	{
		name = series.Name,
		width = ActualWidth,
		height = ActualHeight,
		peak = series.Max,
		hover = hoverIndex < 0 ? null : new { index = hoverIndex, value = series.Values[hoverIndex] },
		points = series.TimesMs.Select((time, i) => new
		{
			timeMs = time - originMs, value = series.Values[i], x = PointOf(i).X, y = PointOf(i).Y,
		}).ToList(),
	};

	/// <summary>The points as (x, y) fractions of the plot, left to right and bottom to top; for tests and the probes.</summary>
	internal IReadOnlyList<(double X, double Y)> Plot()
	{
		var top = series.Max > 0 ? series.Max * 1.1 : 1;
		return series.TimesMs
			.Select((time, i) => (X: (time - originMs) / durationMs, Y: series.Values[i] / top))
			.ToList();
	}

	protected override void OnRender(DrawingContext context)
	{
		var width = ActualWidth;
		var height = ActualHeight;
		context.DrawRectangle(SystemColors.WindowBrush, new Pen(SystemColors.ControlLightBrush, 1), new Rect(0, 0, width, height));
		var middle = new Pen(SystemColors.ControlLightBrush, 1) { DashStyle = DashStyles.Dot };
		context.DrawLine(middle, new Point(0, height / 2), new Point(width, height / 2));

		var plot = Plot();
		var pen = new Pen(SystemColors.HighlightBrush, 1.5);
		Point At((double X, double Y) point) => new(
			Padding + Math.Clamp(point.X, 0, 1) * (width - 2 * Padding),
			height - Padding - point.Y * (height - 2 * Padding));
		for (var i = 1; i < plot.Count; i++)
			context.DrawLine(pen, At(plot[i - 1]), At(plot[i]));
		// A single reading has no line to draw; mark it so the chart is not blank.
		if (plot.Count == 1)
			context.DrawEllipse(SystemColors.HighlightBrush, null, At(plot[0]), 2.5, 2.5);

		if (hoverIndex >= 0 && hoverIndex < plot.Count)
		{
			var marked = At(plot[hoverIndex]);
			context.DrawLine(new Pen(SystemColors.GrayTextBrush, 1) { DashStyle = DashStyles.Dash },
				new Point(marked.X, 0), new Point(marked.X, height));
			context.DrawEllipse(SystemColors.WindowBrush, new Pen(SystemColors.HighlightBrush, 2), marked, 3.5, 3.5);
		}

		var label = new FormattedText(
			ReportFormat.CounterValue(series.Max, series.Units),
			CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, SystemColors.GrayTextBrush,
			VisualTreeHelper.GetDpi(this).PixelsPerDip);
		context.DrawText(label, new Point(4, 1));
	}
}
