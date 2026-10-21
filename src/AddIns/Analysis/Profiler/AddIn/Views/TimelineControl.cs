using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>The range a user dragged on the timeline, or <c>null</c> when the selection was cleared.</summary>
public sealed class TimelineRangeEventArgs(double? startMs, double? endMs) : EventArgs
{
	public double? StartMs { get; } = startMs;
	public double? EndMs { get; } = endMs;
}

/// <summary>
/// A bar graph of sample counts over time. Dragging across it selects a time range; a plain click clears
/// the selection. Every report view recomputes for the selected range.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
	const double DragThreshold = 3;
	const double DefaultHeight = 64;

	double[] bins = [];
	double durationMs;
	double? selectionStartMs;
	double? selectionEndMs;
	double? dragOriginX;
	double? dragCurrentX;

	public TimelineControl()
	{
		Cursor = Cursors.Cross;
		ClipToBounds = true;
	}

	/// <summary>Raised when the selection changes; both values are <c>null</c> when it is cleared.</summary>
	public event EventHandler<TimelineRangeEventArgs>? RangeSelected;

	public void SetData(double[] sampleCounts, double durationInMs)
	{
		bins = sampleCounts;
		durationMs = durationInMs;
		selectionStartMs = selectionEndMs = null;
		InvalidateVisual();
	}

	/// <summary>Selects a range as if it had been dragged, raising <see cref="RangeSelected"/>.</summary>
	public void SelectRange(double startMs, double endMs)
	{
		selectionStartMs = startMs;
		selectionEndMs = endMs;
		InvalidateVisual();
		RangeSelected?.Invoke(this, new TimelineRangeEventArgs(startMs, endMs));
	}

	public void ClearSelection()
	{
		if (selectionStartMs is null)
			return;
		selectionStartMs = selectionEndMs = null;
		InvalidateVisual();
	}

	protected override Size MeasureOverride(Size availableSize) =>
		new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, DefaultHeight);

	protected override void OnRender(DrawingContext context)
	{
		var width = ActualWidth;
		var height = ActualHeight;
		context.DrawRectangle(SystemColors.WindowBrush, null, new Rect(0, 0, width, height));
		if (bins.Length == 0 || width <= 0)
			return;

		var peak = bins.Max();
		var barWidth = width / bins.Length;
		var plotHeight = height - 14; // leave room for the time labels
		if (peak > 0)
		{
			var barBrush = SystemColors.HighlightBrush;
			for (var i = 0; i < bins.Length; i++)
			{
				var barHeight = bins[i] / peak * plotHeight;
				if (barHeight <= 0)
					continue;
				context.DrawRectangle(
					barBrush, null, new Rect(i * barWidth, plotHeight - barHeight, Math.Max(barWidth - 0.5, 0.5), barHeight));
			}
		}

		DrawSelection(context, width, height);
		DrawLabel(context, "0 s", 2, height - 13, TextAlignment.Left, width);
		DrawLabel(context, FormatSeconds(durationMs), width - 2, height - 13, TextAlignment.Right, width);
	}

	void DrawSelection(DrawingContext context, double width, double height)
	{
		double? left = null, right = null;
		if (dragOriginX is { } origin && dragCurrentX is { } current)
		{
			left = Math.Min(origin, current);
			right = Math.Max(origin, current);
		}
		else if (selectionStartMs is { } start && selectionEndMs is { } end && durationMs > 0)
		{
			left = start / durationMs * width;
			right = end / durationMs * width;
		}
		if (left is null || right is null)
			return;

		var fill = SystemColors.HighlightBrush.Clone();
		fill.Opacity = 0.25;
		var pen = new Pen(SystemColors.HighlightBrush, 1);
		context.DrawRectangle(fill, pen, new Rect(left.Value, 0, Math.Max(right.Value - left.Value, 1), height - 14));
	}

	void DrawLabel(DrawingContext context, string text, double x, double y, TextAlignment alignment, double width)
	{
		var formatted = new FormattedText(
			text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10,
			SystemColors.GrayTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
		var left = alignment == TextAlignment.Right ? Math.Max(x - formatted.Width, 0) : x;
		context.DrawText(formatted, new Point(Math.Min(left, Math.Max(width - formatted.Width, 0)), y));
	}

	static string FormatSeconds(double ms) => (ms / 1000).ToString("0.##", CultureInfo.CurrentCulture) + " s";

	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		dragOriginX = dragCurrentX = Clamp(e.GetPosition(this).X);
		CaptureMouse();
		e.Handled = true;
	}

	protected override void OnMouseMove(MouseEventArgs e)
	{
		if (dragOriginX is null)
			return;
		dragCurrentX = Clamp(e.GetPosition(this).X);
		InvalidateVisual();
	}

	protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
	{
		if (dragOriginX is not { } origin)
			return;
		var end = Clamp(e.GetPosition(this).X);
		dragOriginX = dragCurrentX = null;
		ReleaseMouseCapture();

		if (Math.Abs(end - origin) < DragThreshold || ActualWidth <= 0 || durationMs <= 0)
		{
			selectionStartMs = selectionEndMs = null;
			InvalidateVisual();
			RangeSelected?.Invoke(this, new TimelineRangeEventArgs(null, null));
			return;
		}

		selectionStartMs = Math.Min(origin, end) / ActualWidth * durationMs;
		// The filter's end is exclusive, so nudge it past the last sample the drag covers.
		selectionEndMs = Math.Max(origin, end) / ActualWidth * durationMs + 0.001;
		InvalidateVisual();
		RangeSelected?.Invoke(this, new TimelineRangeEventArgs(selectionStartMs, selectionEndMs));
		e.Handled = true;
	}

	double Clamp(double x) => Math.Clamp(x, 0, Math.Max(ActualWidth, 0));
}
