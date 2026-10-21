using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenDevelop.Profiler.Cpu;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>A caller or callee line: the neighbouring function and how much of the selection flows through it.</summary>
internal sealed record NeighbourRow(int FrameId, string Name, double TotalMs, double TotalFraction);

/// <summary>
/// One function in the middle, the functions that called it on the left and the ones it called on the
/// right. Picking a neighbour makes it the current function; Back returns to the previous one.
/// </summary>
public partial class CallerCalleePanel : UserControl
{
	/// <summary>Below this width the three blocks are stacked instead of placed side by side.</summary>
	const double SideBySideMinWidth = 760;

	bool? stacked;

	public CallerCalleePanel()
	{
		InitializeComponent();
		ApplyLayout(stack: false);
	}

	protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
	{
		base.OnRenderSizeChanged(sizeInfo);
		ApplyLayout(stack: sizeInfo.NewSize.Width < SideBySideMinWidth);
		// Side by side, the blocks fill the panel and the lists scroll themselves. Stacked, the panel is
		// taller than the space it has, so it scrolls as a whole instead.
		layoutGrid.Height = stacked == true ? double.NaN : Math.Max(sizeInfo.NewSize.Height, 0);
	}

	void ApplyLayout(bool stack)
	{
		if (stacked == stack)
			return;
		stacked = stack;
		ApplyListHeights(stack);

		layoutGrid.RowDefinitions.Clear();
		layoutGrid.ColumnDefinitions.Clear();
		if (stack)
		{
			layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			Place(callersPanel, row: 0, column: 0, new Thickness(0, 0, 0, 4));
			Place(currentPanel, row: 1, column: 0, new Thickness(0, 0, 0, 4));
			Place(calleesPanel, row: 2, column: 0, new Thickness(0));
		}
		else
		{
			layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 220 });
			layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			Place(callersPanel, row: 0, column: 0, new Thickness(0, 0, 4, 0));
			Place(currentPanel, row: 0, column: 1, new Thickness(4, 0, 4, 0));
			Place(calleesPanel, row: 0, column: 2, new Thickness(4, 0, 0, 0));
		}
	}

	static void Place(FrameworkElement element, int row, int column, Thickness margin)
	{
		Grid.SetRow(element, row);
		Grid.SetColumn(element, column);
		element.Margin = margin;
	}

	// Stacked lists need a floor, or a short pane squeezes each of them down to its header.
	void ApplyListHeights(bool stack)
	{
		var minHeight = stack ? 110 : 0;
		callersPanel.MinHeight = minHeight;
		calleesPanel.MinHeight = minHeight;
	}

	/// <summary>Raised with a frame id when the user picks a neighbouring function.</summary>
	public event EventHandler<int>? NavigateRequested;

	/// <summary>Raised when the user asks to go back to the previously shown function.</summary>
	public event EventHandler? BackRequested;

	internal void Show(CallerCalleeView? view, long totalSamples, double sampleIntervalMs, bool canGoBack)
	{
		backButton.IsEnabled = canGoBack;
		if (view is null)
		{
			functionName.Text = string.Empty;
			functionModule.Text = string.Empty;
			totalText.Text = bodyText.Text = calleesText.Text = string.Empty;
			callerList.ItemsSource = calleeList.ItemsSource = null;
			return;
		}

		var function = view.Function;
		functionName.Text = function.Name;
		functionModule.Text = function.Module;
		var inCallees = function.Total - function.Self;
		totalText.Text = Describe(function.Total, totalSamples, sampleIntervalMs);
		bodyText.Text = Describe(function.Self, totalSamples, sampleIntervalMs);
		calleesText.Text = Describe(inCallees, totalSamples, sampleIntervalMs);
		callerList.ItemsSource = Rows(view.Callers, totalSamples, sampleIntervalMs);
		calleeList.ItemsSource = Rows(view.Callees, totalSamples, sampleIntervalMs);
	}


	/// <summary>The shown function and its neighbours' names, for the integration-test probes.</summary>
	internal object Snapshot() => new
	{
		function = functionName.Text,
		module = functionModule.Text,
		total = totalText.Text,
		body = bodyText.Text,
		callers = (callerList.ItemsSource as IEnumerable<NeighbourRow> ?? []).Select(r => r.Name).ToList(),
		callees = (calleeList.ItemsSource as IEnumerable<NeighbourRow> ?? []).Select(r => r.Name).ToList(),
		canGoBack = backButton.IsEnabled,
	};

	static string Describe(long samples, long totalSamples, double sampleIntervalMs) =>
		$"{samples * sampleIntervalMs:N0} ms  ({ReportFormat.Fraction(samples, totalSamples):P1})";

	static List<NeighbourRow> Rows(IReadOnlyList<FunctionStat> stats, long totalSamples, double sampleIntervalMs) =>
		stats
			.Select(s => new NeighbourRow(
				s.FrameId, s.Name, s.Total * sampleIntervalMs, ReportFormat.Fraction(s.Total, totalSamples)))
			.ToList();

	void NeighbourList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (sender is ListView { SelectedItem: NeighbourRow row })
			NavigateRequested?.Invoke(this, row.FrameId);
	}

	void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}
