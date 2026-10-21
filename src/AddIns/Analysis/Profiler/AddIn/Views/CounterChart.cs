using System.Globalization;
using System.Windows;
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

	public CounterChart(CounterSeries series, double originMs, double durationMs)
	{
		this.series = series;
		this.originMs = originMs;
		this.durationMs = Math.Max(durationMs, 1);
		Height = 64;
		MinWidth = 120;
	}

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

		var label = new FormattedText(
			ReportFormat.CounterValue(series.Max, series.Units),
			CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, SystemColors.GrayTextBrush,
			VisualTreeHelper.GetDpi(this).PixelsPerDip);
		context.DrawText(label, new Point(4, 1));
	}
}
