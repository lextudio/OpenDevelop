using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenDevelop.Profiler.Cpu;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>One line of the Functions view.</summary>
internal sealed record FunctionRow(
	int FrameId, string Name, string Module, double TotalMs, double TotalFraction, double SelfMs, double SelfFraction,
	double SelfChange = 0, double TotalChange = 0, bool Compared = false)
{
	// Percentage points of the whole profile; shown only while comparing.
	public string SelfChangeText => Compared ? ReportFormat.Signed(SelfChange) : "";
	public string TotalChangeText => Compared ? ReportFormat.Signed(TotalChange) : "";
}

/// <summary>
/// Every function in the filtered profile with its inclusive and self time, sortable by any column.
/// Starts sorted by self time, which is where the work is actually being done.
/// </summary>
public partial class FunctionsView : UserControl
{
	const double ChangeColumnWidth = 90;
	readonly ColumnSorter<FunctionRow> sorter;

	public FunctionsView()
	{
		InitializeComponent();
		sorter = new ColumnSorter<FunctionRow>(functionList, new Dictionary<string, Func<FunctionRow, IComparable>>
		{
			["Name"] = r => r.Name,
			["TotalMs"] = r => r.TotalMs,
			["TotalFraction"] = r => r.TotalFraction,
			["SelfMs"] = r => r.SelfMs,
			["SelfFraction"] = r => r.SelfFraction,
			["SelfChange"] = r => r.SelfChange,
			["TotalChange"] = r => r.TotalChange,
			["Module"] = r => r.Module,
		});
	}

	/// <summary>Raised with a frame id when the user asks to see a function's callers and callees.</summary>
	public event EventHandler<int>? ShowCallerCalleeRequested;

	/// <summary>Raised with a frame name when the user asks to see where a function is declared.</summary>
	public event EventHandler<string>? GoToSourceRequested;

	internal void Show(IReadOnlyList<FunctionStat> functions, long totalSamples, double sampleIntervalMs, ProfileUnit unit = ProfileUnit.Milliseconds)
	{
		SetUnit(unit);
		var rows = functions
			.Select(f => new FunctionRow(
				f.FrameId, f.Name, f.Module,
				f.Total * sampleIntervalMs, ReportFormat.Fraction(f.Total, totalSamples),
				f.Self * sampleIntervalMs, ReportFormat.Fraction(f.Self, totalSamples)))
			.ToList();
		selfChangeColumn.Width = 0;
		totalChangeColumn.Width = 0;
		sorter.SetItems(rows, "SelfMs", defaultDescending: true);
	}

	/// <summary>
	/// The same list measured against a baseline capture, with how much each function's share of the CPU time moved.
	/// Largest change first; a function only the baseline had is listed too, with no time of its own now.
	/// </summary>
	internal void ShowComparison(IReadOnlyList<FunctionDiff> diffs, double sampleIntervalMs, ProfileUnit unit = ProfileUnit.Milliseconds)
	{
		SetUnit(unit);
		var rows = diffs
			.Select(d => new FunctionRow(
				d.FrameId, d.Name, d.Module,
				d.Total * sampleIntervalMs, d.TotalFraction, d.Self * sampleIntervalMs, d.SelfFraction,
				d.SelfFractionChange, d.TotalFractionChange, Compared: true))
			.ToList();
		selfChangeColumn.Width = ChangeColumnWidth;
		totalChangeColumn.Width = ChangeColumnWidth;
		sorter.SetItems(rows, "SelfChange", defaultDescending: true);
	}


	void SetUnit(ProfileUnit unit)
	{
		totalHeader.Content = ReportFormat.TotalHeader(unit);
		selfHeader.Content = ReportFormat.SelfHeader(unit);
	}

	/// <summary>The first rows in their current order, for the integration-test probes.</summary>
	internal IReadOnlyList<object> Snapshot(int max) =>
		(functionList.ItemsSource as IEnumerable<FunctionRow> ?? [])
		.Take(max)
		.Select(r => (object)new { name = r.Name, module = r.Module, totalMs = r.TotalMs, selfMs = r.SelfMs, selfChange = r.SelfChangeText })
		.ToList();

	void Activate()
	{
		if (functionList.SelectedItem is FunctionRow row)
			ShowCallerCalleeRequested?.Invoke(this, row.FrameId);
	}

	void FunctionList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Activate();

	void ShowCallerCallee_Click(object sender, RoutedEventArgs e) => Activate();

	void GoToSource_Click(object sender, RoutedEventArgs e)
	{
		if (functionList.SelectedItem is FunctionRow { FrameId: >= 0 } row)
			GoToSourceRequested?.Invoke(this, row.Name);
	}
}
