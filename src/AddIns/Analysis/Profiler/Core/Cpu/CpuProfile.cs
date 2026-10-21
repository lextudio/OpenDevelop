namespace OpenDevelop.Profiler.Cpu;

/// <summary>The unit a profile's samples are measured in.</summary>
public enum ProfileUnit
{
    Milliseconds,
    Kilobytes,
}

/// <summary>
/// A sampled CPU profile: an interned frame table plus every sampled stack. Every report view is a
/// pure function of this object and a <see cref="ProfileFilter"/>, so range selection and
/// Just My Code simply recompute from the samples.
/// </summary>
public sealed class CpuProfile
{
    public CpuProfile(
        IReadOnlyList<FrameInfo> frames,
        IReadOnlyList<CpuSample> samples,
        double durationMs,
        double sampleIntervalMs = 1.0,
        ProfileUnit unit = ProfileUnit.Milliseconds,
        double originMs = 0)
    {
        Unit = unit;
        OriginMs = originMs;
        Frames = frames;
        Samples = samples;
        DurationMs = durationMs;
        SampleIntervalMs = sampleIntervalMs;
        ThreadIds = samples.Select(s => s.ThreadId).Distinct().Order().ToArray();
    }

    /// <summary>
    /// Where sample time zero sits on the capture file's own clock (milliseconds from the start of the session), so data
    /// read from the same file on that clock, such as runtime counters, can be lined up with the samples.
    /// </summary>
    public double OriginMs { get; }

    /// <summary>What one sample weighs: a millisecond of thread time, or the kilobytes one allocation sample stands for.</summary>
    public ProfileUnit Unit { get; }

    public IReadOnlyList<FrameInfo> Frames { get; }
    public IReadOnlyList<CpuSample> Samples { get; }

    /// <summary>Time of the last sample, in milliseconds from the first.</summary>
    public double DurationMs { get; }

    /// <summary>
    /// Nominal gap between two samples of one thread. Used only to turn sample counts into an
    /// approximate time; the runtime's sample profiler fires roughly every millisecond per thread.
    /// </summary>
    public double SampleIntervalMs { get; }

    public IReadOnlyList<int> ThreadIds { get; }

    public string NameOf(int frameId) => frameId switch
    {
        CallTreeNode.RootFrameId => "Root",
        CallTreeNode.ExternalCodeFrameId => CallTreeNode.ExternalCodeName,
        _ => Frames[frameId].Method,
    };

    public string ModuleOf(int frameId) => frameId >= 0 ? Frames[frameId].Module : string.Empty;

    /// <summary>Number of samples a filter accepts.</summary>
    public long CountSamples(ProfileFilter filter)
    {
        long count = 0;
        foreach (var sample in Samples)
            if (filter.Accepts(sample))
                count++;
        return count;
    }

    /// <summary>Builds the call tree. The returned root's <see cref="CallTreeNode.Total"/> is the accepted sample count.</summary>
    public CallTreeNode BuildCallTree(ProfileFilter filter)
    {
        var root = new Builder(CallTreeNode.RootFrameId);
        var projector = new StackProjector(this, filter);
        foreach (var sample in Samples)
        {
            if (!filter.Accepts(sample))
                continue;
            var node = root;
            node.Total++;
            foreach (var frame in projector.Project(sample.Stack))
            {
                node = node.Child(frame);
                node.Total++;
            }
            node.Self++;
        }
        return Freeze(root, null);
    }

    /// <summary>Aggregates by function: how often it was on the stack, and how often it was the innermost frame.</summary>
    public IReadOnlyList<FunctionStat> BuildFunctions(ProfileFilter filter)
    {
        var total = new Dictionary<int, long>();
        var self = new Dictionary<int, long>();
        var seen = new HashSet<int>();
        var projector = new StackProjector(this, filter);
        foreach (var sample in Samples)
        {
            if (!filter.Accepts(sample))
                continue;
            var stack = projector.Project(sample.Stack);
            if (stack.Count == 0)
                continue;
            seen.Clear();
            foreach (var frame in stack)
                if (seen.Add(frame))
                    total[frame] = total.GetValueOrDefault(frame) + 1;
            var leaf = stack[^1];
            self[leaf] = self.GetValueOrDefault(leaf) + 1;
        }

        return total
            .Select(pair => new FunctionStat(
                pair.Key, NameOf(pair.Key), ModuleOf(pair.Key), pair.Value, self.GetValueOrDefault(pair.Key)))
            .OrderByDescending(f => f.Self)
            .ThenByDescending(f => f.Total)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Compares this profile (filtered as given) with a baseline profile function by function, matched on name and module.
    /// The baseline is taken whole, with the same Just My Code and blocked-sample settings but not this profile's time
    /// range or thread, which refer to this capture only. Ordered by the largest change in self share first.
    /// </summary>
    public IReadOnlyList<FunctionDiff> CompareFunctions(CpuProfile baseline, ProfileFilter filter)
    {
        var baselineFilter = new ProfileFilter
        {
            IncludeExternalSamples = filter.IncludeExternalSamples,
            JustMyCode = filter.JustMyCode,
            IsUserCode = filter.IsUserCode,
        };
        var current = BuildFunctions(filter);
        var before = baseline.BuildFunctions(baselineFilter);
        double currentSamples = Math.Max(1, CountSamples(filter));
        double baselineSamples = Math.Max(1, baseline.CountSamples(baselineFilter));

        var previous = before.ToDictionary(f => (f.Name, f.Module));
        var diffs = new List<FunctionDiff>();
        foreach (var now in current)
        {
            previous.Remove((now.Name, now.Module), out var was);
            diffs.Add(new FunctionDiff(
                now.FrameId, now.Name, now.Module, now.Total, now.Self, was?.Total ?? 0, was?.Self ?? 0,
                now.Total / currentSamples, now.Self / currentSamples,
                (was?.Total ?? 0) / baselineSamples, (was?.Self ?? 0) / baselineSamples));
        }
        foreach (var gone in previous.Values)
            diffs.Add(new FunctionDiff(
                -1, gone.Name, gone.Module, 0, 0, gone.Total, gone.Self,
                0, 0, gone.Total / baselineSamples, gone.Self / baselineSamples));

        return diffs
            .OrderByDescending(d => Math.Abs(d.SelfFractionChange))
            .ThenByDescending(d => Math.Abs(d.TotalFractionChange))
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The callers and callees of one function. A recursive function is measured at its innermost
    /// activation, so it appears as its own caller rather than inflating its other callers.
    /// </summary>
    public CallerCalleeView? BuildCallerCallee(int frameId, ProfileFilter filter)
    {
        long total = 0, self = 0;
        var callers = new Dictionary<int, long>();
        var callees = new Dictionary<int, long>();
        var projector = new StackProjector(this, filter);
        foreach (var sample in Samples)
        {
            if (!filter.Accepts(sample))
                continue;
            var stack = projector.Project(sample.Stack);
            var index = -1;
            for (var i = stack.Count - 1; i >= 0; i--)
            {
                if (stack[i] == frameId)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
                continue;

            total++;
            if (index == stack.Count - 1)
                self++;
            else
                callees[stack[index + 1]] = callees.GetValueOrDefault(stack[index + 1]) + 1;
            if (index > 0)
                callers[stack[index - 1]] = callers.GetValueOrDefault(stack[index - 1]) + 1;
        }

        if (total == 0)
            return null;

        FunctionStat Neighbour(KeyValuePair<int, long> pair) =>
            new(pair.Key, NameOf(pair.Key), ModuleOf(pair.Key), pair.Value, 0);

        return new CallerCalleeView(
            new FunctionStat(frameId, NameOf(frameId), ModuleOf(frameId), total, self),
            callers.Select(Neighbour).OrderByDescending(f => f.Total).ThenBy(f => f.Name, StringComparer.Ordinal).ToList(),
            callees.Select(Neighbour).OrderByDescending(f => f.Total).ThenBy(f => f.Name, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Sample counts per equal-width time bin across the whole capture, for the timeline graph. The
    /// filter's time range is ignored on purpose: the timeline is what the user picks a range from.
    /// </summary>
    public double[] BuildTimeline(int bins, ProfileFilter? filter = null)
    {
        if (bins <= 0)
            throw new ArgumentOutOfRangeException(nameof(bins));
        var wholeRange = new ProfileFilter
        {
            ThreadId = filter?.ThreadId,
            IncludeExternalSamples = filter?.IncludeExternalSamples ?? false,
        };
        var counts = new double[bins];
        var width = DurationMs <= 0 ? 1 : DurationMs / bins;
        foreach (var sample in Samples)
        {
            if (!wholeRange.Accepts(sample))
                continue;
            var bin = (int)(sample.TimeMs / width);
            counts[Math.Clamp(bin, 0, bins - 1)]++;
        }
        return counts;
    }

    CallTreeNode Freeze(Builder builder, CallTreeNode? parent)
    {
        var name = NameOf(builder.FrameId);
        var node = new CallTreeNode(builder.FrameId, name, ModuleOf(builder.FrameId), parent)
        {
            Total = builder.Total,
            Self = builder.Self,
        };
        node.Children = builder.Children.Values
            .OrderByDescending(c => c.Total)
            .ThenBy(c => NameOf(c.FrameId), StringComparer.Ordinal)
            .Select(c => Freeze(c, node))
            .ToList();
        return node;
    }

    sealed class Builder(int frameId)
    {
        public int FrameId { get; } = frameId;
        public long Total;
        public long Self;
        public Dictionary<int, Builder> Children { get; } = new();

        public Builder Child(int childFrameId)
        {
            if (!Children.TryGetValue(childFrameId, out var child))
                Children[childFrameId] = child = new Builder(childFrameId);
            return child;
        }
    }

    /// <summary>Applies Just My Code folding to a raw stack. Reuses one buffer; consume before the next call.</summary>
    sealed class StackProjector
    {
        readonly bool[]? isUser;
        readonly List<int> buffer = new();

        public StackProjector(CpuProfile profile, ProfileFilter filter)
        {
            if (filter.JustMyCode && filter.IsUserCode is { } predicate)
            {
                isUser = new bool[profile.Frames.Count];
                for (var i = 0; i < isUser.Length; i++)
                    isUser[i] = predicate(profile.Frames[i]);
            }
        }

        public IReadOnlyList<int> Project(int[] stack)
        {
            buffer.Clear();
            if (isUser is null)
            {
                buffer.AddRange(stack);
                return buffer;
            }

            // Leading framework frames (thread start, dispatcher) are dropped; a run of them between
            // or after user frames becomes one [External Code] node so the sample totals still add up.
            var seenUser = false;
            var pendingExternal = false;
            foreach (var frame in stack)
            {
                if (isUser[frame])
                {
                    if (pendingExternal && seenUser)
                        buffer.Add(CallTreeNode.ExternalCodeFrameId);
                    pendingExternal = false;
                    seenUser = true;
                    buffer.Add(frame);
                }
                else
                {
                    pendingExternal = true;
                }
            }
            if (pendingExternal)
                buffer.Add(CallTreeNode.ExternalCodeFrameId);
            return buffer;
        }
    }
}
