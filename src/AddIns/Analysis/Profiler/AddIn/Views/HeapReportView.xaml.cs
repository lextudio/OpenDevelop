using System.Windows;
using System.Windows.Controls;
using ICSharpCode.Core;
using Microsoft.Win32;
using OpenDevelop.Profiler.Heap;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>
/// One line of the Types view; the diff fields are only meaningful while comparing. <paramref name="Retained"/>
/// is negative until the retained sizes have been computed.
/// </summary>
internal sealed record TypeRow(
	string Name, long Count, long Size, double SizeFraction, long CountDiff, long SizeDiff, long Retained = -1)
{
	public string SizeText => ReportFormat.Bytes(Size);
	public string RetainedText => Retained < 0 ? "…" : ReportFormat.Bytes(Retained);
	public string CountDiffText => Signed(CountDiff);
	public string SizeDiffText => (SizeDiff > 0 ? "+" : "") + ReportFormat.Bytes(SizeDiff);

	static string Signed(long value) => (value > 0 ? "+" : "") + value.ToString("N0");
}

/// <summary>One line of the Largest retainers list.</summary>
internal sealed record RetainerRow(HeapObject Object)
{
	public string TypeName => Object.TypeName;
	public string AddressText => $"0x{Object.Instance.Address:X}";
	public string RetainedText => ReportFormat.Bytes(Object.Instance.RetainedSize);
}

/// <summary>One line of the Event handler targets list.</summary>
internal sealed record HandlerTargetRow(DelegateTarget Target)
{
	public string TypeName => Target.TypeName;
	public long Delegates => Target.Delegates;
	public string RetainedText => ReportFormat.Bytes(Target.RetainedSize);
}

/// <summary>One line of the Instances tab.</summary>
internal sealed record InstanceRow(HeapInstance Instance)
{
	public string AddressText => $"0x{Instance.Address:X}";
	public string SizeText => ReportFormat.Bytes(Instance.Size);
	public string RetainedText => ReportFormat.Bytes(Instance.RetainedSize);
}

/// <summary>
/// The managed heap by type: how many live objects of each type, how much memory they take themselves and how
/// much they keep alive. Selecting a type shows what holds it, what it holds, its largest instances and the way
/// a GC root reaches it. A second snapshot can be loaded to see which types grew or shrank between the two.
/// </summary>
public partial class HeapReportView : UserControl
{
	const double HiddenColumnWidth = 0;
	const double CountDiffWidth = 80;
	const double SizeDiffWidth = 90;
	const int InstanceLimit = 100;

	readonly HeapSnapshot snapshot;
	readonly ColumnSorter<TypeRow> sorter;
	readonly TaskCompletionSource retainedReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
	List<TypeRow> allRows = [];
	HeapSnapshot? baseline;
	Dictionary<string, long>? retainedByType;
	int detailsVersion;

	public HeapReportView(HeapSnapshot snapshot)
	{
		this.snapshot = snapshot;
		InitializeComponent();
		sorter = new ColumnSorter<TypeRow>(typeList, new Dictionary<string, Func<TypeRow, IComparable>>
		{
			["Name"] = r => r.Name,
			["Count"] = r => r.Count,
			["Size"] = r => r.Size,
			["Retained"] = r => r.Retained,
			["SizeFraction"] = r => r.SizeFraction,
			["CountDiff"] = r => r.CountDiff,
			["SizeDiff"] = r => r.SizeDiff,
		});
		summaryText.Text = StringParser.Format(
			"${res:AddIns.Profiler.Report.HeapSummary}", snapshot.ObjectCount, ReportFormat.Bytes(snapshot.HeapSize));
		ShowTypes();
		if (snapshot.Graph is { } graph)
			_ = LoadRetainedSizesAsync(graph);
		else
			retainedReady.TrySetResult();
	}

	/// <summary>Completes once retained sizes are shown (or known to be unavailable).</summary>
	internal Task RetainedReady => retainedReady.Task;

	// The dominator tree behind retained sizes is the expensive part of loading a snapshot, so the table is
	// shown at once with the sizes pending and filled in when the computation finishes.
	async Task LoadRetainedSizesAsync(HeapGraph graph)
	{
		try
		{
			var (retained, retainers, handlers) = await Task.Run(() => (
				graph.RetainedByType().ToDictionary(t => t.TypeName, t => t.RetainedSize),
				graph.LargestRetainers(15),
				graph.DelegateTargets(15)));
			retainedByType = retained;
			retainersList.ItemsSource = retainers.Select(o => new RetainerRow(o)).ToList();
			handlerTargetList.ItemsSource = handlers.Select(t => new HandlerTargetRow(t)).ToList();
			var selected = (typeList.SelectedItem as TypeRow)?.Name;
			ShowTypes();
			if (selected is not null)
				await SelectTypeAsync(selected);
		}
		finally
		{
			retainedReady.TrySetResult();
		}
	}

	void ShowTypes()
	{
		var wasComparing = baseline is not null;
		long RetainedOf(string name) => retainedByType is null ? -1 : retainedByType.GetValueOrDefault(name, 0);
		if (baseline is null)
		{
			allRows = snapshot.Types
				.Select(t => new TypeRow(
					t.Name, t.Count, t.Size, ReportFormat.Fraction(t.Size, snapshot.HeapSize), 0, 0, RetainedOf(t.Name)))
				.ToList();
		}
		else
		{
			allRows = snapshot.Compare(baseline)
				.Select(d => new TypeRow(
					d.Name, d.Count, d.Size, ReportFormat.Fraction(d.Size, snapshot.HeapSize), d.CountDiff, d.SizeDiff, RetainedOf(d.Name)))
				.ToList();
		}

		countDiffColumn.Width = wasComparing ? CountDiffWidth : HiddenColumnWidth;
		sizeDiffColumn.Width = wasComparing ? SizeDiffWidth : HiddenColumnWidth;
		ApplyFilter(wasComparing ? "SizeDiff" : "Size");
	}

	void ApplyFilter(string sortColumn)
	{
		var text = filterBox.Text.Trim();
		var rows = text.Length == 0
			? allRows
			: allRows.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
		// Sizes and growth are most interesting largest first; a changed type can also have shrunk.
		sorter.SetItems(rows, sortColumn, defaultDescending: true);
	}

	void FilterBox_TextChanged(object sender, TextChangedEventArgs e) =>
		ApplyFilter(baseline is null ? "Size" : "SizeDiff");

	async void Compare_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new OpenFileDialog
		{
			Filter = StringParser.Parse("${res:AddIns.Profiler.FileFilter.Capture}") + "|*.nettrace",
			CheckFileExists = true,
		};
		if (dialog.ShowDialog() != true)
			return;

		compareButton.IsEnabled = false;
		try
		{
			await CompareWithAsync(dialog.FileName);
		}
		catch (ProfilingException exception)
		{
			MessageService.ShowError(exception.Message);
		}
		finally
		{
			compareButton.IsEnabled = true;
		}
	}

	/// <summary>The summary and first rows in their current order, for the integration-test probes.</summary>
	internal object Snapshot(int max) => new
	{
		summary = summaryText.Text,
		comparing = baseline is not null,
		compareText = compareText.Text,
		rows = (typeList.ItemsSource as IEnumerable<TypeRow> ?? [])
			.Take(max)
			.Select(r => new { name = r.Name, count = r.Count, size = r.Size, retained = r.Retained, countDiff = r.CountDiff, sizeDiff = r.SizeDiff })
			.ToList(),
	};

	internal void SetFilter(string text) => filterBox.Text = text;

	/// <summary>Selects a type by name, as a click on its row would, and shows what holds it.</summary>
	internal async Task<bool> SelectTypeAsync(string name)
	{
		var row = (typeList.ItemsSource as IEnumerable<TypeRow> ?? []).FirstOrDefault(r => r.Name == name);
		if (row is null)
			return false;
		typeList.SelectedItem = row;
		await ShowDetailsAsync(row);
		return true;
	}

	/// <summary>Selects an instance of the selected type, as a click on its row would, and shows its path to a root.</summary>
	internal bool SelectInstance(int index)
	{
		var rows = instanceList.ItemsSource as IReadOnlyList<InstanceRow>;
		if (rows is null || index < 0 || index >= rows.Count)
			return false;
		instanceList.SelectedItem = rows[index];
		return true;
	}

	internal Task WaitForRetainedSizesAsync() => RetainedReady;

	internal object Details() => new
	{
		type = (typeList.SelectedItem as TypeRow)?.Name,
		retained = (typeList.SelectedItem as TypeRow)?.Retained,
		path = pathList.Items.Cast<object>().Select(item => item.ToString()).ToList(),
		referencedBy = (referencedByList.ItemsSource as IEnumerable<TypeLink> ?? []).Take(10).Select(l => new { name = l.TypeName, references = l.References }).ToList(),
		references = (referencesList.ItemsSource as IEnumerable<TypeLink> ?? []).Take(10).Select(l => new { name = l.TypeName, references = l.References }).ToList(),
		instances = (instanceList.ItemsSource as IEnumerable<InstanceRow> ?? []).Take(5)
			.Select(i => new { address = i.AddressText, size = i.Instance.Size, retained = i.Instance.RetainedSize }).ToList(),
		insights = new
		{
			retainers = (retainersList.ItemsSource as IEnumerable<RetainerRow> ?? []).Take(5).Select(r => new { type = r.TypeName, retained = r.Object.Instance.RetainedSize }).ToList(),
			handlerTargets = (handlerTargetList.ItemsSource as IEnumerable<HandlerTargetRow> ?? []).Take(10).Select(h => new { type = h.TypeName, delegates = h.Delegates, retained = h.Target.RetainedSize }).ToList(),
		},
	};

	async void TypeList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
		await ShowDetailsAsync(typeList.SelectedItem as TypeRow);

	/// <summary>Fills the Path to Root, Referenced By, References and Instances tabs for the selected type.</summary>
	async Task ShowDetailsAsync(TypeRow? row)
	{
		var version = ++detailsVersion;
		pathList.Items.Clear();
		referencedByList.ItemsSource = null;
		referencesList.ItemsSource = null;
		instanceList.ItemsSource = null;
		if (row is null)
			return;
		if (snapshot.Graph is not { } graph)
		{
			pathList.Items.Add(StringParser.Parse("${res:AddIns.Profiler.Report.NoGraph}"));
			return;
		}

		referencedByList.ItemsSource = graph.ReferencedBy(row.Name);
		referencesList.ItemsSource = graph.References(row.Name);
		ShowPath(graph.PathToRoot(row.Name));

		// Instances need the dominator tree, which may still be computing: wait for it off the UI thread, and drop
		// the result if the selection has moved on in the meantime.
		var instances = await Task.Run(() => graph.Instances(row.Name, InstanceLimit));
		if (version == detailsVersion)
			instanceList.ItemsSource = instances.Select(i => new InstanceRow(i)).ToList();
	}

	void ShowPath(HeapPath? path)
	{
		pathList.Items.Clear();
		if (path is null)
		{
			pathList.Items.Add(StringParser.Parse("${res:AddIns.Profiler.Report.PathNone}"));
			return;
		}

		pathList.Items.Add(StringParser.Format("${res:AddIns.Profiler.Report.PathRoot}", path.RootKind));
		for (var i = 0; i < path.Types.Count; i++)
			pathList.Items.Add(new string(' ', 2 * (i + 1)) + "→ " + path.Types[i]);
	}

	// Choosing one instance replaces the type's shortest path with the way a root reaches that very object.
	void InstanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (instanceList.SelectedItem is InstanceRow row && snapshot.Graph is { } graph)
			ShowPath(graph.PathToRoot(row.Instance.Node));
	}

	internal async Task CompareWithAsync(string path)
	{
		baseline = await Task.Run(() => LoadHeap(path));
		compareText.Text = StringParser.Format(
			"${res:AddIns.Profiler.Report.ComparingWith}", System.IO.Path.GetFileName(path));
		compareText.Visibility = Visibility.Visible;
		stopCompareButton.Visibility = Visibility.Visible;
		ShowTypes();
	}

	internal void StopComparing() => StopCompare_Click(this, new RoutedEventArgs());

	static HeapSnapshot LoadHeap(string path)
	{
		var report = ReportLoader.Load(path);
		return report.Heap
			?? throw new ProfilingException(StringParser.Format("${res:AddIns.Profiler.Report.NotAHeapCapture}", System.IO.Path.GetFileName(path)));
	}

	void StopCompare_Click(object sender, RoutedEventArgs e)
	{
		baseline = null;
		compareText.Visibility = Visibility.Collapsed;
		stopCompareButton.Visibility = Visibility.Collapsed;
		ShowTypes();
	}
}
