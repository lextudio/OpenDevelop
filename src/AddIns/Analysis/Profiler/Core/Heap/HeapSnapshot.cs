namespace OpenDevelop.Profiler.Heap;

/// <summary>Live objects of one type in a snapshot.</summary>
public sealed record HeapTypeStat(string Name, long Count, long Size);

/// <summary>One type's change between two snapshots.</summary>
public sealed record HeapTypeDiff(string Name, long Count, long CountDiff, long Size, long SizeDiff);

/// <summary>A managed-heap snapshot reduced to what the Types view needs: per-type object counts and sizes.</summary>
public sealed class HeapSnapshot
{
    public HeapSnapshot(IReadOnlyList<HeapTypeStat> types, HeapGraph? graph = null)
    {
        Graph = graph;
        Types = types.OrderByDescending(t => t.Size).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        ObjectCount = types.Sum(t => t.Count);
        HeapSize = types.Sum(t => t.Size);
    }

    public IReadOnlyList<HeapTypeStat> Types { get; }

    /// <summary>The object graph, or <c>null</c> when the capture was truncated and the edges do not add up.</summary>
    public HeapGraph? Graph { get; }

    /// <summary>Live objects in the snapshot.</summary>
    public long ObjectCount { get; }

    /// <summary>Total size of those objects in bytes (shallow, as the runtime reports them).</summary>
    public long HeapSize { get; }

    /// <summary>
    /// Per-type difference to a baseline, largest absolute size change first. A type present in only one
    /// snapshot counts as zero in the other.
    /// </summary>
    public IReadOnlyList<HeapTypeDiff> Compare(HeapSnapshot baseline)
    {
        var before = baseline.Types.ToDictionary(t => t.Name);
        var current = Types.ToDictionary(t => t.Name);
        var names = new HashSet<string>(before.Keys);
        names.UnionWith(current.Keys);

        return names
            .Select(name =>
            {
                var now = current.GetValueOrDefault(name) ?? new HeapTypeStat(name, 0, 0);
                var then = before.GetValueOrDefault(name) ?? new HeapTypeStat(name, 0, 0);
                return new HeapTypeDiff(name, now.Count, now.Count - then.Count, now.Size, now.Size - then.Size);
            })
            .Where(d => d.CountDiff != 0 || d.SizeDiff != 0)
            .OrderByDescending(d => Math.Abs(d.SizeDiff))
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
    }
}
