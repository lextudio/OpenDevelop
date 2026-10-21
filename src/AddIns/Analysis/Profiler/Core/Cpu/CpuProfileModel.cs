namespace OpenDevelop.Profiler.Cpu;

/// <summary>A distinct method in a captured profile.</summary>
public sealed record FrameInfo(string Method, string Module);

/// <summary>What the sampled thread was doing at the sample instant.</summary>
public enum SampleState : byte
{
    /// <summary>Executing managed code.</summary>
    Managed = 0,

    /// <summary>In native code or blocked (waiting, sleeping, doing I/O). Not CPU work by itself.</summary>
    External = 1,
}

/// <summary>One sampled stack. <see cref="Stack"/> holds frame ids ordered root to leaf.</summary>
public sealed class CpuSample
{
    public CpuSample(double timeMs, int threadId, SampleState state, int[] stack)
    {
        TimeMs = timeMs;
        ThreadId = threadId;
        State = state;
        Stack = stack;
    }

    public double TimeMs { get; }
    public int ThreadId { get; }
    public SampleState State { get; }
    public int[] Stack { get; }
}

/// <summary>Selects which samples a report view aggregates, and how frames are presented.</summary>
public sealed class ProfileFilter
{
    public static ProfileFilter Default { get; } = new();

    /// <summary>Inclusive start of the time range, in milliseconds from the first sample.</summary>
    public double StartMs { get; init; } = double.NegativeInfinity;

    /// <summary>Exclusive end of the time range, in milliseconds from the first sample.</summary>
    public double EndMs { get; init; } = double.PositiveInfinity;

    /// <summary>Restrict to one thread, or <c>null</c> for all threads.</summary>
    public int? ThreadId { get; init; }

    /// <summary>
    /// Include samples taken while the thread was in native code or blocked. Off by default: a thread
    /// parked in a wait is not consuming CPU, and an idle UI thread would otherwise dominate every view.
    /// </summary>
    public bool IncludeExternalSamples { get; init; }

    /// <summary>
    /// Fold frames that <see cref="IsUserCode"/> rejects into a single <c>[External Code]</c> node
    /// (Visual Studio's "Show Just My Code"). Ignored when <see cref="IsUserCode"/> is <c>null</c>.
    /// </summary>
    public bool JustMyCode { get; init; }

    /// <summary>Decides whether a frame belongs to the user's own code.</summary>
    public Func<FrameInfo, bool>? IsUserCode { get; init; }

    internal bool Accepts(CpuSample sample)
    {
        if (sample.TimeMs < StartMs || sample.TimeMs >= EndMs)
            return false;
        if (ThreadId is { } thread && sample.ThreadId != thread)
            return false;
        return IncludeExternalSamples || sample.State == SampleState.Managed;
    }
}

/// <summary>A node of the aggregated call tree. Weights are sample counts.</summary>
public sealed class CallTreeNode
{
    public const int RootFrameId = -1;
    public const int ExternalCodeFrameId = -2;
    public const string ExternalCodeName = "[External Code]";

    internal CallTreeNode(int frameId, string name, string module, CallTreeNode? parent)
    {
        FrameId = frameId;
        Name = name;
        Module = module;
        Parent = parent;
    }

    public int FrameId { get; }
    public string Name { get; }
    public string Module { get; }
    public CallTreeNode? Parent { get; }

    /// <summary>Samples in which this node was on the stack (including everything it called).</summary>
    public long Total { get; internal set; }

    /// <summary>Samples in which this node was the innermost frame.</summary>
    public long Self { get; internal set; }

    public IReadOnlyList<CallTreeNode> Children { get; internal set; } = Array.Empty<CallTreeNode>();

    public int Depth
    {
        get
        {
            var depth = 0;
            for (var node = Parent; node != null; node = node.Parent)
                depth++;
            return depth;
        }
    }

    /// <summary>
    /// The nodes from this node down along the heaviest children, for "Expand Hot Path". Stops when the
    /// heaviest child holds less than <paramref name="minFraction"/> of its parent's samples.
    /// </summary>
    public IReadOnlyList<CallTreeNode> HotPath(double minFraction = 0.2)
    {
        var path = new List<CallTreeNode>();
        var node = this;
        while (node.Children.Count > 0)
        {
            var heaviest = node.Children[0]; // children are sorted heaviest first
            if (node.Total == 0 || (double)heaviest.Total / node.Total < minFraction)
                break;
            path.Add(heaviest);
            node = heaviest;
        }
        return path;
    }

    public override string ToString() => $"{Name} total={Total} self={Self}";
}

/// <summary>Per-function totals across the whole filtered profile.</summary>
public sealed record FunctionStat(int FrameId, string Name, string Module, long Total, long Self);

/// <summary>The neighbours of one function: who called it and what it called.</summary>
public sealed class CallerCalleeView
{
    internal CallerCalleeView(
        FunctionStat function,
        IReadOnlyList<FunctionStat> callers,
        IReadOnlyList<FunctionStat> callees)
    {
        Function = function;
        Callers = callers;
        Callees = callees;
    }

    /// <summary>The selected function. <see cref="FunctionStat.Self"/> is its own body time.</summary>
    public FunctionStat Function { get; }

    /// <summary>Calling functions, weighted by the samples that reached the function through them.</summary>
    public IReadOnlyList<FunctionStat> Callers { get; }

    /// <summary>Called functions, weighted by the samples spent below them.</summary>
    public IReadOnlyList<FunctionStat> Callees { get; }
}
