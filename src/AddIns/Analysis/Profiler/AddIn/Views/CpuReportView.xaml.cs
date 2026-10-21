using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    }

    sealed record ThreadChoice(int? Id, string Label);

    sealed record ViewChoice(string Label, ImageSource Icon);

    readonly CpuProfile profile;
    readonly Func<FrameInfo, bool> isUserCode;
    readonly Stack<int> history = new();
    CallTreeNode? tree;
    int? currentFrame;
    double? rangeStartMs;
    double? rangeEndMs;
    bool initialised;

    public CpuReportView(CpuProfile profile, IReadOnlyList<string> solutionAssemblies)
    {
        this.profile = profile;
        isUserCode = UserCode.ForProfile(profile, solutionAssemblies);
        InitializeComponent();

        // One entry per Mode, in Mode order: the selector's index is the mode.
        viewSelector.ItemsSource = new[] { "CallTree", "Functions", "CallerCallee", "FlameGraph", "RingDiagram" }
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

    void Refresh()
    {
        if (!initialised)
            return;

        var filter = Filter();
        var total = profile.CountSamples(filter);
        UpdateStatus(total);
        var interval = profile.SampleIntervalMs;

        callTree.Visibility = Visible(CurrentMode == Mode.CallTree);
        functions.Visibility = Visible(CurrentMode == Mode.Functions);
        callerCallee.Visibility = Visible(CurrentMode == Mode.CallerCallee);
        flameScroll.Visibility = Visible(CurrentMode == Mode.FlameGraph);
        ringDiagram.Visibility = Visible(CurrentMode == Mode.RingDiagram);
        expandHotPathButton.IsEnabled = CurrentMode == Mode.CallTree;

        switch (CurrentMode)
        {
            case Mode.CallTree:
                callTree.Show(Tree(), interval);
                break;
            case Mode.Functions:
                functions.Show(profile.BuildFunctions(filter), total, interval);
                break;
            case Mode.CallerCallee:
                ShowCallerCallee(filter, total, interval);
                break;
            case Mode.FlameGraph:
                flameGraph.Show(Tree(), interval);
                break;
            case Mode.RingDiagram:
                ringDiagram.Show(Tree(), interval);
                break;
        }
    }

    void ShowCallerCallee(ProfileFilter filter, long total, double interval)
    {
        // With nothing chosen yet, start from the function doing the most work itself.
        currentFrame ??= profile.BuildFunctions(filter).FirstOrDefault()?.FrameId;
        var view = currentFrame is { } frame ? profile.BuildCallerCallee(frame, filter) : null;
        callerCallee.Show(view, total, interval, canGoBack: history.Count > 0);
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
        statusText.Text = StringParser.Format(
                "${res:AddIns.Profiler.Report.Status}", totalSamples, totalSamples * profile.SampleIntervalMs, range)
            + "  " + StringParser.Parse("${res:AddIns.Profiler.Report.SamplingNote}");
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
        hasRange = rangeStartMs is not null,
        rangeStartMs,
        rangeEndMs,
        samples = profile.Samples.Count,
        durationMs = profile.DurationMs,
        tree = callTree.Snapshot(40),
        functions = functions.Snapshot(15),
        callerCallee = callerCallee.Snapshot(),
        ring = ringDiagram.Snapshot(),
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
