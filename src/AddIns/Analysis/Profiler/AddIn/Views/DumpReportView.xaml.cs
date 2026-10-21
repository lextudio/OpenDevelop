using System.Windows.Controls;
using ICSharpCode.Core;
using OpenDevelop.Profiler.Dump;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>
/// A memory dump opened as a report: what it is, then the managed heap (the same views as a captured heap snapshot, plus the
/// duplicate strings and sparse arrays found in it) and the managed threads with their stacks. Read offline; nothing is running.
/// </summary>
public partial class DumpReportView : UserControl
{
	readonly HeapReportView heapView;
	readonly ThreadsView threadsView = new();

	public DumpReportView(DumpReport dump)
	{
		InitializeComponent();
		infoText.Text = StringParser.Format(
			"${res:AddIns.Profiler.Report.DumpInfo}", dump.Info.RuntimeVersion, dump.Info.Platform, dump.Info.Architecture,
			ReportFormat.Bytes(dump.Info.FileBytes));
		heapView = new HeapReportView(dump.Heap, dump.Contents);
		heapTab.Content = heapView;
		threadsView.Show(dump.Threads);
		threadsTab.Content = threadsView;
	}

	/// <summary>The summary and what each tab shows, for the integration-test probes.</summary>
	internal object Describe() => new
	{
		info = infoText.Text,
		tab = (tabs.SelectedItem as TabItem)?.Name,
		heap = heapView.Snapshot(40),
		threads = threadsView.Snapshot(),
	};

	internal bool SelectThread(int managedId) => threadsView.Select(managedId);

	internal void ShowTab(string name) => tabs.SelectedItem = name.Equals("threads", StringComparison.OrdinalIgnoreCase) ? threadsTab : heapTab;
}
