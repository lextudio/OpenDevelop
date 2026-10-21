using System.Windows.Controls;
using OpenDevelop.Profiler.Dump;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>The managed threads of a dump: the list on top, and the selected thread's stack, top frame first, below it.</summary>
public partial class ThreadsView : UserControl
{
	sealed record ThreadRow(DumpThread Thread)
	{
		public int ManagedId => Thread.ManagedId;
		public string OsIdText => "0x" + Thread.OsId.ToString("X");
		public string State => Thread.State;
		public string Exception => Thread.Exception ?? string.Empty;
	}

	public ThreadsView() => InitializeComponent();

	internal void Show(IReadOnlyList<DumpThread> threads)
	{
		threadList.ItemsSource = threads.Select(t => new ThreadRow(t)).ToList();
		// A thread that is stopped on an exception is the likeliest reason anyone opens a dump; start there. Failing that, the
		// live thread with the deepest stack, which is the program's own work rather than the finalizer or a runtime helper.
		var rows = (threadList.ItemsSource as IEnumerable<ThreadRow>)?.ToList() ?? [];
		threadList.SelectedItem = rows.FirstOrDefault(r => r.Thread.Exception is not null)
			?? rows.Where(r => r.Thread.State == "Alive").OrderByDescending(r => r.Thread.Frames.Count).FirstOrDefault()
			?? rows.FirstOrDefault();
	}

	internal bool Select(int managedId)
	{
		var row = (threadList.ItemsSource as IEnumerable<ThreadRow>)?.FirstOrDefault(r => r.ManagedId == managedId);
		threadList.SelectedItem = row;
		return row is not null;
	}

	void ThreadList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
		frameList.ItemsSource = (threadList.SelectedItem as ThreadRow)?.Thread.Frames;

	/// <summary>The threads and the selected thread's stack, for the integration-test probes.</summary>
	internal object Snapshot() => new
	{
		threads = (threadList.ItemsSource as IEnumerable<ThreadRow> ?? [])
			.Select(r => new { r.ManagedId, r.State, r.Exception, frames = r.Thread.Frames.Count })
			.ToList(),
		selected = (threadList.SelectedItem as ThreadRow)?.ManagedId,
		stack = (frameList.ItemsSource as IEnumerable<string> ?? []).Take(20).ToList(),
	};
}
