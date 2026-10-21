using Microsoft.Diagnostics.Runtime;

namespace OpenDevelop.Profiler.Heap;

/// <summary>
/// Looks at heap objects one by one, as a dump is walked, and collects what the GC heap events cannot show: equal strings
/// held many times over, and arrays that are almost all default values. It is fed by whoever is walking the heap, so the
/// opt-in scan after a snapshot and the full dump reader share one definition of both findings.
/// </summary>
internal sealed class ContentScanner
{
    const int ReportedEntries = 100;
    const int MinStringLength = 8;
    const long MinWastedBytes = 1024;
    const int MinArrayLength = 16;
    const double MinEmptyFraction = 0.9;
    const int MaxStringCharacters = 4096;
    const long MaxArrayBytesRead = 64L * 1024 * 1024;

    readonly Dictionary<(ulong Hash, int Length), StringGroup> strings = new();
    readonly List<SparseArray> sparse = [];
    readonly byte[] buffer = new byte[64 * 1024];
    long scanned;

    sealed class StringGroup(string preview, int length, long size)
    {
        public string Preview { get; } = preview;
        public int Length { get; } = length;
        public long Size { get; } = size;
        public long Copies { get; set; }
    }

    public void Add(ClrObject obj, ClrHeap heap)
    {
        scanned++;
        var type = obj.Type;
        if (type is null)
            return;

        if (type.IsString)
        {
            var text = obj.AsString(MaxStringCharacters);
            if (text is null || text.Length < MinStringLength)
                return;
            var key = (Hash(text), text.Length);
            if (!strings.TryGetValue(key, out var group))
                strings[key] = group = new StringGroup(text.Length > 80 ? text[..80] + "…" : text, text.Length, (long)obj.Size);
            group.Copies++;
        }
        else if (type.IsArray && type.ComponentType is { } element && element.ElementType != ClrElementType.Struct)
        {
            var array = obj.AsArray();
            if (array.Rank != 1 || array.Length < MinArrayLength || type.ComponentSize <= 0)
                return;
            var empty = CountEmptyElements(heap, type, obj.Address, array.Length, type.ComponentSize);
            var waste = empty * type.ComponentSize;
            if (empty >= array.Length * MinEmptyFraction && waste >= MinWastedBytes)
                sparse.Add(new SparseArray(type.Name ?? "[]", array.Length, empty, waste));
        }
    }

    public ContentInsights Build() => new()
    {
        ObjectsScanned = scanned,
        DuplicateStrings = strings.Values
            .Where(g => g.Copies > 1 && (g.Copies - 1) * g.Size >= MinWastedBytes)
            .Select(g => new DuplicateString(g.Preview, g.Length, g.Copies, (g.Copies - 1) * g.Size))
            .OrderByDescending(s => s.WastedBytes)
            .Take(ReportedEntries)
            .ToList(),
        SparseArrays = sparse.OrderByDescending(a => a.WastedBytes).Take(ReportedEntries).ToList(),
    };

    // The elements are read as raw memory and an element counts as empty when all of its bytes are zero: null for a
    // reference, zero for a number. Reading is capped so one giant array cannot stall the scan; a capped array is judged by
    // its start, which is enough to see that it is mostly empty.
    long CountEmptyElements(ClrHeap heap, ClrType type, ulong address, int length, int elementSize)
    {
        var start = type.GetArrayElementAddress(address, 0);
        long toRead = Math.Min((long)length * elementSize, MaxArrayBytesRead);
        long empty = 0, done = 0;
        while (done < toRead)
        {
            var chunk = (int)Math.Min(buffer.Length - buffer.Length % elementSize, toRead - done);
            var read = heap.Runtime.DataTarget.DataReader.Read(start + (ulong)done, buffer.AsSpan(0, chunk));
            if (read < elementSize)
                break;
            for (var offset = 0; offset + elementSize <= read; offset += elementSize)
            {
                var all = true;
                for (var i = 0; i < elementSize && all; i++)
                    all = buffer[offset + i] == 0;
                if (all)
                    empty++;
            }
            done += read - read % elementSize;
        }

        var examined = Math.Max(done / elementSize, 1);
        return examined == length ? empty : (long)(length * (double)empty / examined);
    }

    // FNV-1a over the characters: a hash instead of the string itself keeps memory flat when a heap has millions of strings.
    static ulong Hash(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in text)
            hash = (hash ^ c) * 1099511628211UL;
        return hash;
    }
}
