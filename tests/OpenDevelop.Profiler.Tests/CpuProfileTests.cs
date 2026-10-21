using OpenDevelop.Profiler.Cpu;
using Xunit;

namespace OpenDevelop.Profiler.Tests;

public class CpuProfileTests
{
    // Frames: 0 Main, 1 Run, 2 Helper (all "App"); 3 Wait, 4 ThreadStart (framework).
    const int Main = 0, Run = 1, Helper = 2, Wait = 3, ThreadStart = 4;

    static readonly Func<FrameInfo, bool> IsApp = f => f.Module == "App";

    static CpuProfile Build() => new(
        [
            new("App.Main()", "App"),
            new("App.Run()", "App"),
            new("App.Helper()", "App"),
            new("System.Threading.Wait()", "System.Private.CoreLib"),
            new("System.Threading.ThreadStart()", "System.Private.CoreLib"),
        ],
        [
            new CpuSample(0, 1, SampleState.Managed, [ThreadStart, Main, Run, Helper]),
            new CpuSample(1, 1, SampleState.Managed, [ThreadStart, Main, Run, Helper]),
            new CpuSample(2, 1, SampleState.Managed, [ThreadStart, Main, Run]),
            new CpuSample(3, 1, SampleState.Managed, [ThreadStart, Main, Run, Wait]),
            new CpuSample(4, 2, SampleState.External, [ThreadStart, Main, Run, Wait]),
            new CpuSample(5, 2, SampleState.Managed, [ThreadStart, Main, Helper]),
        ],
        durationMs: 5);

    static CallTreeNode Child(CallTreeNode node, int frameId) => node.Children.Single(c => c.FrameId == frameId);

    [Fact]
    public void CallTree_CountsTotalAndSelfAndSkipsBlockedSamples()
    {
        var root = Build().BuildCallTree(ProfileFilter.Default);

        Assert.Equal(5, root.Total); // the External (blocked) sample is not CPU work
        var run = Child(Child(Child(root, ThreadStart), Main), Run);
        Assert.Equal(4, run.Total);
        Assert.Equal(1, run.Self);
        Assert.Equal(2, Child(run, Helper).Self);
        Assert.Equal(1, Child(run, Wait).Self);
    }

    [Fact]
    public void CallTree_ChildrenAreSortedHeaviestFirst()
    {
        var main = Child(Child(Build().BuildCallTree(ProfileFilter.Default), ThreadStart), Main);

        Assert.Equal([Run, Helper], main.Children.Select(c => c.FrameId));
    }

    [Fact]
    public void CallTree_IncludesBlockedSamplesWhenAsked()
    {
        var root = Build().BuildCallTree(new ProfileFilter { IncludeExternalSamples = true });

        Assert.Equal(6, root.Total);
    }

    [Fact]
    public void CallTree_JustMyCodeDropsLeadingFrameworkFramesAndFoldsTheRest()
    {
        var profile = Build();
        var root = profile.BuildCallTree(new ProfileFilter { JustMyCode = true, IsUserCode = IsApp });

        Assert.Equal(5, root.Total); // folding never loses samples
        var main = Child(root, Main);
        var run = Child(main, Run);
        Assert.Equal(CallTreeNode.ExternalCodeName, Child(run, CallTreeNode.ExternalCodeFrameId).Name);
        Assert.Equal(1, Child(run, CallTreeNode.ExternalCodeFrameId).Self);
        Assert.DoesNotContain(root.Children, c => c.FrameId == ThreadStart);
    }

    [Fact]
    public void CallTree_JustMyCodeIsIgnoredWithoutAPredicate()
    {
        var root = Build().BuildCallTree(new ProfileFilter { JustMyCode = true });

        Assert.Contains(root.Children, c => c.FrameId == ThreadStart);
    }

    [Fact]
    public void CallTree_JustMyCodeKeepsAllFrameworkSamplesAsOneExternalNode()
    {
        var profile = new CpuProfile(
            [new("App.Main()", "App"), new("System.A()", "System"), new("System.B()", "System")],
            [new CpuSample(0, 1, SampleState.Managed, [1, 2]), new CpuSample(1, 1, SampleState.Managed, [1])],
            1);

        var root = profile.BuildCallTree(new ProfileFilter { JustMyCode = true, IsUserCode = IsApp });

        var external = Assert.Single(root.Children);
        Assert.Equal(CallTreeNode.ExternalCodeFrameId, external.FrameId);
        Assert.Equal(2, external.Total);
    }

    [Fact]
    public void Filter_RestrictsToATimeRange()
    {
        var profile = Build();

        var root = profile.BuildCallTree(new ProfileFilter { StartMs = 2, EndMs = 4 });

        Assert.Equal(2, root.Total); // the samples at t=2 and t=3; the end is exclusive
    }

    [Fact]
    public void Filter_RestrictsToAThread()
    {
        var profile = Build();

        Assert.Equal(4, profile.CountSamples(new ProfileFilter { ThreadId = 1 }));
        Assert.Equal(1, profile.CountSamples(new ProfileFilter { ThreadId = 2 }));
        Assert.Equal(2, profile.CountSamples(new ProfileFilter { ThreadId = 2, IncludeExternalSamples = true }));
    }

    [Fact]
    public void Functions_ReportInclusiveAndSelfSamples()
    {
        var functions = Build().BuildFunctions(ProfileFilter.Default);

        var run = functions.Single(f => f.FrameId == Run);
        Assert.Equal(4, run.Total);
        Assert.Equal(1, run.Self);
        var helper = functions.Single(f => f.FrameId == Helper);
        Assert.Equal(3, helper.Total); // two under Run plus the one on thread 2
        Assert.Equal(3, helper.Self);
        Assert.Equal(Helper, functions[0].FrameId); // most self time first
    }

    [Fact]
    public void Functions_CountARecursiveFunctionOncePerSample()
    {
        var profile = new CpuProfile(
            [new("App.Recurse()", "App")],
            [new CpuSample(0, 1, SampleState.Managed, [0, 0, 0])],
            0);

        var only = Assert.Single(profile.BuildFunctions(ProfileFilter.Default));

        Assert.Equal(1, only.Total);
        Assert.Equal(1, only.Self);
    }

    [Fact]
    public void CallerCallee_SplitsATimeIntoCallersCalleesAndBody()
    {
        var view = Build().BuildCallerCallee(Run, ProfileFilter.Default);

        Assert.NotNull(view);
        Assert.Equal(4, view.Function.Total);
        Assert.Equal(1, view.Function.Self);
        var caller = Assert.Single(view.Callers);
        Assert.Equal((Main, 4), (caller.FrameId, caller.Total));
        Assert.Equal([(Helper, 2L), (Wait, 1L)], view.Callees.Select(c => (c.FrameId, c.Total)));
    }

    [Fact]
    public void CallerCallee_IsNullForAFunctionNotInTheRange()
    {
        var view = Build().BuildCallerCallee(Wait, new ProfileFilter { StartMs = 0, EndMs = 3 });

        Assert.Null(view);
    }

    [Fact]
    public void HotPath_FollowsTheHeaviestChildrenWhileTheyDominate()
    {
        var root = Build().BuildCallTree(ProfileFilter.Default);

        var path = root.HotPath();

        Assert.Equal([ThreadStart, Main, Run, Helper], path.Select(n => n.FrameId));
    }

    [Fact]
    public void HotPath_StopsWhenNoChildDominates()
    {
        var profile = new CpuProfile(
            [new("A", "App"), new("B", "App"), new("C", "App")],
            [
                new CpuSample(0, 1, SampleState.Managed, [0]),
                new CpuSample(1, 1, SampleState.Managed, [1]),
                new CpuSample(2, 1, SampleState.Managed, [2]),
                new CpuSample(3, 1, SampleState.Managed, [2]),
            ],
            3);

        Assert.Empty(profile.BuildCallTree(ProfileFilter.Default).HotPath(minFraction: 0.6));
    }

    [Fact]
    public void Timeline_BinsSamplesAcrossTheWholeCaptureIgnoringTheRange()
    {
        var profile = Build();

        var bins = profile.BuildTimeline(5, new ProfileFilter { StartMs = 3, EndMs = 4 });

        // t=0..5 over 5 bins of width 1; the blocked sample at t=4 is excluded by default.
        Assert.Equal([1, 1, 1, 1, 1], bins);
    }

    [Fact]
    public void Depth_CountsAncestors()
    {
        var root = Build().BuildCallTree(ProfileFilter.Default);

        Assert.Equal(0, root.Depth);
        Assert.Equal(3, Child(Child(Child(root, ThreadStart), Main), Run).Depth);
    }
}
