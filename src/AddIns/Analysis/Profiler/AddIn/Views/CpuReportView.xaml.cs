using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using System.Text;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using OpenDevelop.Profiler.AddIn.Services;
using OpenDevelop.Profiler.Cpu;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>
/// The CPU report: a timeline to select a range from, and four views of the same samples (call tree,
/// functions, caller/callee, flame graph). Every view is recomputed from the profile for the current
/// filter, so nothing is cached except the call tree shared by the tree and the flame graph.
/// </summary>
public partial class CpuReportView : UserControl
{
	const int TimelineBins = 200;

	enum Mode
	{
		CallTree,
		Functions,
		CallerCallee,
		FlameGraph,
		RingDiagram,
		Counters,
	}

	sealed record ThreadChoice(int? Id, string Label);

	sealed record ViewChoice(string Label, ImageSource Icon);

	readonly CpuProfile profile;
	readonly IReadOnlyList<CounterSeries> runtimeCounters;
	readonly Func<FrameInfo, bool> isUserCode;
	readonly Stack<int> history = new();
	CallTreeNode? tree;
	CpuProfile? baseline;
	string? baselineName;
	int? currentFrame;
	double? rangeStartMs;
	double? rangeEndMs;
	bool initialised;

	/// <summary>The filter a report opens with: Just My Code on, blocked samples off, every thread, the whole capture.</summary>
	internal static ProfileFilter DefaultFilter(Func<FrameInfo, bool> isUserCode) => new() { JustMyCode = true, IsUserCode = isUserCode };

	/// <param name="initialTree">The call tree for <see cref="DefaultFilter"/>, if the caller has already built it off the UI thread.</param>
	public CpuReportView(
		CpuProfile profile, IReadOnlyList<string> solutionAssemblies, IReadOnlyList<CounterSeries>? runtimeCounters = null,
		Func<FrameInfo, bool>? userCode = null, CallTreeNode? initialTree = null)
	{
		this.profile = profile;
		this.runtimeCounters = runtimeCounters ?? [];
		isUserCode = userCode ?? UserCode.ForProfile(profile, solutionAssemblies);
		tree = initialTree;
		InitializeComponent();

		// One entry per Mode, in Mode order: the selector's index is the mode.
		viewSelector.ItemsSource = new[] { "CallTree", "Functions", "CallerCallee", "FlameGraph", "RingDiagram", "Counters" }
			.Select(key => new ViewChoice(
				StringParser.Parse("${res:AddIns.Profiler.Report." + key + "}"),
				PresentationResourceService.GetImageSource("Profiler." + key)))
			.ToList();
		viewSelector.SelectedIndex = 0;

		threadSelector.Items.Add(new ThreadChoice(null, StringParser.Parse("${res:AddIns.Profiler.Report.AllThreads}")));
		foreach (var thread in profile.ThreadIds)
			threadSelector.Items.Add(new ThreadChoice(thread, StringParser.Format("${res:AddIns.Profiler.Report.ThreadN}", thread)));
		threadSelector.SelectedIndex = 0;

		flameGraph.Focusable = true;
		initialised = true;
		RebuildTimeline();
		Refresh();
	}

	Mode CurrentMode => (Mode)Math.Max(viewSelector.SelectedIndex, 0);

	ProfileFilter Filter() => new()
	{
		StartMs = rangeStartMs ?? double.NegativeInfinity,
		EndMs = rangeEndMs ?? double.PositiveInfinity,
		ThreadId = (threadSelector.SelectedItem as ThreadChoice)?.Id,
		IncludeExternalSamples = includeBlocked.IsChecked == true,
		JustMyCode = justMyCode.IsChecked == true,
		IsUserCode = isUserCode,
	};

	CallTreeNode Tree() => tree ??= profile.BuildCallTree(Filter());

	void RebuildTimeline()
	{
		timeline.SetData(profile.BuildTimeline(TimelineBins, Filter()), profile.DurationMs);
		rangeStartMs = rangeEndMs = null;
		clearRangeButton.Visibility = Visibility.Collapsed;
	}

	/// <summary>Captures with at least this many samples recompute their views off the UI thread; smaller ones do it inline.</summary>
	internal static int BackgroundSampleThreshold = 100_000;

	Task settled = Task.CompletedTask;
	int refreshVersion;

	/// <summary>Completes once the view reflects the latest filter and mode (immediately for a small capture).</summary>
	internal Task Settled => settled;

	void Refresh()
	{
		if (initialised)
			settled = RefreshAsync();
	}

	// For a small capture everything below runs inline: the awaited helpers complete synchronously, so the view is already
	// up to date when Refresh returns. For a large one the heavy work (the call tree, the function table) runs on a pool
	// thread, and a result that a newer filter change has overtaken is dropped rather than shown.
	async Task RefreshAsync()
	{
		var version = ++refreshVersion;
		var background = profile.Samples.Count >= BackgroundSampleThreshold;
		var mode = CurrentMode;
		var filter = Filter();
		var total = profile.CountSamples(filter);
		UpdateStatus(total);
		var interval = profile.SampleIntervalMs;

		callTree.Visibility = Visible(mode == Mode.CallTree);
		functions.Visibility = Visible(mode == Mode.Functions);
		callerCallee.Visibility = Visible(mode == Mode.CallerCallee);
		flameScroll.Visibility = Visible(mode == Mode.FlameGraph);
		ringDiagram.Visibility = Visible(mode == Mode.RingDiagram);
		counters.Visibility = Visible(mode == Mode.Counters);
		expandHotPathButton.IsEnabled = mode == Mode.CallTree;

		async Task<T> Compute<T>(Func<T> work) => background ? await Task.Run(work) : work();

		if (background)
			Cursor = Cursors.Wait;
		try
		{
			switch (mode)
			{
				case Mode.CallTree:
				case Mode.FlameGraph:
				case Mode.RingDiagram:
				{
					var built = tree ?? await Compute(() => profile.BuildCallTree(filter));
					if (version != refreshVersion)
						return;
					tree = built;
					if (mode == Mode.CallTree)
						callTree.Show(built, interval, profile.Unit);
					else if (mode == Mode.FlameGraph)
						flameGraph.Show(built, interval, profile.Unit);
					else
						ringDiagram.Show(built, interval, profile.Unit);
					break;
				}
				case Mode.Functions:
				{
					var compared = baseline;
					var stats = compared is null ? await Compute(() => profile.BuildFunctions(filter)) : null;
					var diffs = compared is not null ? await Compute(() => profile.CompareFunctions(compared, filter)) : null;
					if (version != refreshVersion)
						return;
					if (diffs is null)
						functions.Show(stats!, total, interval, profile.Unit);
					else
						functions.ShowComparison(diffs, interval, profile.Unit);
					break;
				}
				case Mode.CallerCallee:
				{
					// With nothing chosen yet, start from the function doing the most work itself.
					var chosen = currentFrame;
					var (frame, view) = await Compute(() =>
					{
						var f = chosen ?? profile.BuildFunctions(filter).FirstOrDefault()?.FrameId;
						return (f, f is { } id ? profile.BuildCallerCallee(id, filter) : null);
					});
					if (version != refreshVersion)
						return;
					currentFrame = frame;
					callerCallee.Show(view, total, interval, canGoBack: history.Count > 0, profile.Unit);
					break;
				}
				case Mode.Counters:
					counters.Show(runtimeCounters, profile.OriginMs, profile.DurationMs);
					break;
			}
		}
		finally
		{
			if (background && version == refreshVersion)
				Cursor = null;
		}
	}

	void UpdateStatus(long totalSamples)
	{
		if (totalSamples == 0)
		{
			statusText.Text = StringParser.Parse("${res:AddIns.Profiler.Report.NoSamples}");
			return;
		}
		var range = rangeStartMs is { } start && rangeEndMs is { } end
			? StringParser.Format("${res:AddIns.Profiler.Report.RangeSelected}", start / 1000, end / 1000)
			: StringParser.Parse("${res:AddIns.Profiler.Report.RangeWhole}");
		var allocation = profile.Unit == ProfileUnit.Kilobytes;
		statusText.Text = StringParser.Format(
				allocation ? "${res:AddIns.Profiler.Report.StatusAllocation}" : "${res:AddIns.Profiler.Report.Status}",
				totalSamples, totalSamples * profile.SampleIntervalMs, range)
			+ "  " + StringParser.Parse(allocation ? "${res:AddIns.Profiler.Report.AllocationNote}" : "${res:AddIns.Profiler.Report.SamplingNote}");
		if (baseline is not null)
			statusText.Text += "  " + StringParser.Format("${res:AddIns.Profiler.Report.ComparingFunctionsWith}", baselineName);
	}

	static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

	void SelectMode(Mode mode) => viewSelector.SelectedIndex = (int)mode;

	void InvalidateTree() => tree = null;

	void ViewSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => Refresh();

	void Filter_Changed(object sender, RoutedEventArgs e)
	{
		InvalidateTree();
		// "Include blocked samples" changes what the timeline counts; Just My Code does not.
		if (ReferenceEquals(sender, includeBlocked))
			RebuildTimeline();
		Refresh();
	}

	void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!initialised)
			return;
		InvalidateTree();
		RebuildTimeline();
		Refresh();
	}

	void Timeline_RangeSelected(object sender, TimelineRangeEventArgs e)
	{
		rangeStartMs = e.StartMs;
		rangeEndMs = e.EndMs;
		clearRangeButton.Visibility = Visible(e.StartMs is not null);
		InvalidateTree();
		Refresh();
	}

	void ClearRange_Click(object sender, RoutedEventArgs e)
	{
		timeline.ClearSelection();
		rangeStartMs = rangeEndMs = null;
		clearRangeButton.Visibility = Visibility.Collapsed;
		InvalidateTree();
		Refresh();
	}

	void ShowCallerCalleeRequested(object? sender, int frameId)
	{
		history.Clear();
		currentFrame = frameId;
		// Switching the selector refreshes the view with the new frame.
		if (CurrentMode == Mode.CallerCallee)
			Refresh();
		else
			SelectMode(Mode.CallerCallee);
	}

	void CallerCallee_NavigateRequested(object? sender, int frameId)
	{
		if (currentFrame is { } previous)
			history.Push(previous);
		currentFrame = frameId;
		Refresh();
	}

	void CallerCallee_BackRequested(object? sender, EventArgs e)
	{
		if (history.Count == 0)
			return;
		currentFrame = history.Pop();
		Refresh();
	}

	void ExpandHotPath_Click(object sender, RoutedEventArgs e) => callTree.ExpandHotPath();

	// The members below let the integration-test probes (ProfilerDevFlowActions) drive and inspect the
	// view through the same code paths the user's clicks take.

	internal bool SetMode(string name)
	{
		if (!Enum.TryParse<Mode>(name, ignoreCase: true, out var mode))
			return false;
		SelectMode(mode);
		return true;
	}

	/// <summary>Writes what the current view shows, as CSV: the call tree in tree view, otherwise the function list.</summary>
	internal void ExportTo(string path)
	{
		var csv = CurrentMode == Mode.CallTree
			? ReportExport.CallTreeCsv(profile, Filter())
			: ReportExport.FunctionsCsv(profile, Filter());
		// A byte order mark, so a spreadsheet reads non-ASCII names correctly.
		File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
	}

	void Export_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new SaveFileDialog
		{
			Filter = StringParser.Parse("${res:AddIns.Profiler.FileFilter.Csv}") + "|*.csv",
			DefaultExt = ".csv",
			FileName = CurrentMode == Mode.CallTree ? "call-tree.csv" : "functions.csv",
		};
		if (dialog.ShowDialog() != true)
			return;
		try
		{
			ExportTo(dialog.FileName);
			SD.StatusBar.SetMessage(StringParser.Format("${res:AddIns.Profiler.Report.Exported}", dialog.FileName));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			MessageService.ShowError(exception.Message);
		}
	}

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

	/// <summary>Compares the Functions view with another CPU capture; the other views stay absolute.</summary>
	internal async Task CompareWithAsync(string path)
	{
		baseline = await Task.Run(() => LoadCpu(path));
		baselineName = System.IO.Path.GetFileName(path);
		stopCompareButton.Visibility = Visibility.Visible;
		SelectMode(Mode.Functions);
		Refresh();
	}

	internal void StopComparing() => StopCompare_Click(this, new RoutedEventArgs());

	static CpuProfile LoadCpu(string path)
	{
		var report = ReportLoader.Load(path);
		return report.Cpu
			?? throw new ProfilingException(StringParser.Format("${res:AddIns.Profiler.Report.NotACpuCapture}", System.IO.Path.GetFileName(path)));
	}

	void StopCompare_Click(object sender, RoutedEventArgs e)
	{
		baseline = null;
		baselineName = null;
		stopCompareButton.Visibility = Visibility.Collapsed;
		Refresh();
	}

	internal void SetJustMyCode(bool enabled)
	{
		justMyCode.IsChecked = enabled;
		Filter_Changed(justMyCode, new RoutedEventArgs());
	}

	internal void SelectRange(double startMs, double endMs) => timeline.SelectRange(startMs, endMs);

	internal void ClearRange() => ClearRange_Click(this, new RoutedEventArgs());

	/// <summary>Shows the caller/callee view of the first function whose name contains the text.</summary>
	internal bool ShowCallerCalleeOf(string nameFragment)
	{
		var index = profile.Frames
			.Select((frame, i) => (frame, i))
			.FirstOrDefault(pair => pair.frame.Method.Contains(nameFragment, StringComparison.OrdinalIgnoreCase));
		if (index.frame is null)
			return false;
		ShowCallerCalleeRequested(this, index.i);
		return true;
	}

	internal object Describe() => new
	{
		mode = CurrentMode.ToString(),
		status = statusText.Text,
		justMyCode = justMyCode.IsChecked == true,
		includeBlocked = includeBlocked.IsChecked == true,
		threadChoices = threadSelector.Items.Count,
		comparing = baseline is not null,
		unit = profile.Unit.ToString(),
		hasRange = rangeStartMs is not null,
		rangeStartMs,
		rangeEndMs,
		samples = profile.Samples.Count,
		durationMs = profile.DurationMs,
		tree = callTree.Snapshot(40),
		functions = functions.Snapshot(15),
		callerCallee = callerCallee.Snapshot(),
		ring = ringDiagram.Snapshot(),
		counters = counters.Snapshot(),
	};

	async void GoToSourceRequested(object? sender, string frameName)
	{
		if (!await GoToSource.GoToAsync(frameName))
			SD.StatusBar.SetMessage(StringParser.Format("${res:AddIns.Profiler.Report.SourceNotFound}", frameName));
	}

	/// <summary>Looks up the declaration of the first function whose name contains the text, for the integration-test probes.</summary>
	internal async Task<SourceLocation?> FindSourceOfAsync(string nameFragment)
	{
		var frame = profile.Frames.FirstOrDefault(f => f.Method.Contains(nameFragment, StringComparison.OrdinalIgnoreCase));
		return frame is null ? null : await GoToSource.FindAsync(frame.Method);
	}

	// Find: Ctrl+F opens the floating find box over the call tree, F3 / Shift+F3 repeat the last search.
	// Only the call tree is searchable, so searching switches to it.

	void OpenFind(string? text = null)
	{
		if (CurrentMode != Mode.CallTree)
			SelectMode(Mode.CallTree);
		findOverlay.Open(text);
	}

	void RunFind(FindRequest request)
	{
		if (CurrentMode != Mode.CallTree)
			SelectMode(Mode.CallTree);
		var (current, total) = callTree.Find(request.Text, request.Forward, request.Incremental);
		lastFind = (current, total);
		findOverlay.ShowResult(current, total);
	}

	(int Current, int Total) lastFind;

	void FindButton_Click(object sender, RoutedEventArgs e) => OpenFind();

	void FindOverlay_FindRequested(object? sender, FindRequest request) => RunFind(request);

	void FindOverlay_Closed(object? sender, EventArgs e) => callTree.FocusList();

	void Report_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
		if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
		{
			OpenFind();
		}
		else if (e.Key == Key.F3 && findOverlay.Text.Length > 0)
		{
			if (!findOverlay.IsOpen)
				findOverlay.Open();
			findOverlay.Find(forward: !shift);
		}
		else if (e.Key == Key.Escape && findOverlay.IsOpen)
		{
			findOverlay.Close();
		}
		else
		{
			return;
		}
		e.Handled = true;
	}

	/// <summary>Opens the find box with the text, as typing it would, and reports how many nodes match.</summary>
	internal (int Current, int Total) FindNode(string text)
	{
		OpenFind(text);
		RunFind(new FindRequest(text, Forward: true, Incremental: true));
		return lastFind;
	}
}
