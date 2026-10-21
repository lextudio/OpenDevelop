using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenDevelop.Profiler.Cpu;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>One line of the Functions view.</summary>
internal sealed record FunctionRow(
    int FrameId, string Name, string Module, double TotalMs, double TotalFraction, double SelfMs, double SelfFraction);

/// <summary>
/// Every function in the filtered profile with its inclusive and self time, sortable by any column.
/// Starts sorted by self time, which is where the work is actually being done.
/// </summary>
public partial class FunctionsView : UserControl
{
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
            ["Module"] = r => r.Module,
        });
    }

    /// <summary>Raised with a frame id when the user asks to see a function's callers and callees.</summary>
    public event EventHandler<int>? ShowCallerCalleeRequested;

    /// <summary>Raised with a frame name when the user asks to see where a function is declared.</summary>
    public event EventHandler<string>? GoToSourceRequested;

    internal void Show(IReadOnlyList<FunctionStat> functions, long totalSamples, double sampleIntervalMs)
    {
        var rows = functions
            .Select(f => new FunctionRow(
                f.FrameId, f.Name, f.Module,
                f.Total * sampleIntervalMs, ReportFormat.Fraction(f.Total, totalSamples),
                f.Self * sampleIntervalMs, ReportFormat.Fraction(f.Self, totalSamples)))
            .ToList();
        sorter.SetItems(rows, "SelfMs", defaultDescending: true);
    }


    /// <summary>The first rows in their current order, for the integration-test probes.</summary>
    internal IReadOnlyList<object> Snapshot(int max) =>
        (functionList.ItemsSource as IEnumerable<FunctionRow> ?? [])
        .Take(max)
        .Select(r => (object)new { name = r.Name, module = r.Module, totalMs = r.TotalMs, selfMs = r.SelfMs })
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
