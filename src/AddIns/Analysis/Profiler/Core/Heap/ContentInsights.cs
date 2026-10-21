using System.Text.Json;

namespace OpenDevelop.Profiler.Heap;

/// <summary>Many equal strings on the heap: every copy beyond the first is memory that holding one instance would save.</summary>
/// <param name="Preview">The start of the string, for recognising it.</param>
/// <param name="Length">The string's length in characters.</param>
/// <param name="Copies">How many instances hold this value.</param>
/// <param name="WastedBytes">The size of every copy but one.</param>
public sealed record DuplicateString(string Preview, int Length, long Copies, long WastedBytes);

/// <summary>An array that is almost all default values (zeros or nulls), so most of its memory holds nothing.</summary>
/// <param name="Type">The array's type, e.g. <c>System.Int32[]</c>.</param>
/// <param name="Length">Number of elements.</param>
/// <param name="EmptyElements">How many of them are default.</param>
/// <param name="WastedBytes">The memory those default elements take.</param>
public sealed record SparseArray(string Type, long Length, long EmptyElements, long WastedBytes);

/// <summary>What reading the contents of the heap found. Written beside the capture, so it reopens with it.</summary>
public sealed class ContentInsights
{
    public const string FileName = "heap.contents.json";

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<DuplicateString> DuplicateStrings { get; init; } = [];
    public IReadOnlyList<SparseArray> SparseArrays { get; init; } = [];

    /// <summary>How many heap objects were looked at.</summary>
    public long ObjectsScanned { get; init; }

    public long TotalWastedByDuplicateStrings => DuplicateStrings.Sum(s => s.WastedBytes);
    public long TotalWastedBySparseArrays => SparseArrays.Sum(a => a.WastedBytes);

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>Reads a saved result, or <c>null</c> when there is none or it cannot be read; the capture itself never depends on it.</summary>
    public static ContentInsights? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ContentInsights>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
