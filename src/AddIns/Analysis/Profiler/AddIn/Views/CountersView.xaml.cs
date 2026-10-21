using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OpenDevelop.Profiler.Cpu;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>
/// The runtime's own counters recorded with the capture, one chart each, on the same time axis as the samples: CPU use,
/// GC heap size, collections per generation, allocation rate and the rest. A rise in heap size or a burst of collections
/// shows next to the stretch of time the call tree can then be narrowed to.
/// </summary>
public partial class CountersView : UserControl
{
	// The ones that explain most problems first; whatever else the runtime reported follows in its own order.
	static readonly string[] Preferred =
	[
		"cpu-usage", "working-set", "gc-heap-size", "gen-0-gc-count", "gen-1-gc-count", "gen-2-gc-count", "time-in-gc",
		"alloc-rate", "exception-count", "threadpool-thread-count", "monitor-lock-contention-count",
	];

	public CountersView() => InitializeComponent();

	internal void Show(IReadOnlyList<CounterSeries> counters, double originMs, double durationMs)
	{
		panel.Children.Clear();
		emptyText.Visibility = counters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		scroller.Visibility = counters.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

		var ordered = counters
			.OrderBy(c => Preferred.Contains(c.Name) ? Array.IndexOf(Preferred, c.Name) : Preferred.Length)
			.ToList();
		foreach (var series in ordered)
		{
			panel.Children.Add(new TextBlock
			{
				Margin = new Thickness(0, 6, 0, 2),
				FontWeight = FontWeights.SemiBold,
				Text = $"{series.DisplayName}   ({ReportFormat.CounterValue(series.Last, series.Units)}, "
					+ $"{ReportFormat.CounterValue(series.Max, series.Units)} max)",
			});
			panel.Children.Add(new CounterChart(series, originMs, durationMs));
		}
	}

	/// <summary>The charts shown, for the integration-test probes.</summary>
	internal object Snapshot() => new
	{
		empty = emptyText.Visibility == Visibility.Visible,
		charts = panel.Children.OfType<TextBlock>().Select(t => t.Text).ToList(),
		points = panel.Children.OfType<CounterChart>().Select(c => c.Plot().Count).ToList(),
	};
}
