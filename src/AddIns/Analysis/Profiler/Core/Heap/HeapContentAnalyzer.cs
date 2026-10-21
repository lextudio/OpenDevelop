using Microsoft.Diagnostics.Runtime;
using OpenDevelop.Profiler.Collection;

namespace OpenDevelop.Profiler.Heap;

/// <summary>
/// Reads the contents of the managed heap of a process from a memory dump, to find what the GC heap events cannot show:
/// equal strings held many times over, and arrays that are almost all default values. The process is held still while its
/// dump is written, and the dump (as large as the process's memory) is deleted as soon as it has been read.
/// </summary>
public static class HeapContentAnalyzer
{
    const long MaxObjects = 20_000_000;

    /// <summary>Writes a dump of the process, reads its heap, and returns what it found.</summary>
    public static async Task<ContentInsights> AnalyzeProcessAsync(int processId, CancellationToken cancellationToken = default)
    {
        var dumpPath = Path.Combine(Path.GetTempPath(), $"opendevelop-heap-{Guid.NewGuid():N}.dmp");
        try
        {
            await DumpCapture.CaptureAsync(processId, dumpPath, cancellationToken).ConfigureAwait(false);
            return await Task.Run(() => AnalyzeDump(dumpPath, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(dumpPath);
        }
    }

    /// <summary>Reads the heap of an existing memory dump for these two findings only; cheaper than reading the whole dump.</summary>
    public static ContentInsights AnalyzeDump(string dumpPath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var target = DataTarget.LoadDump(dumpPath);
            var clr = target.ClrVersions.FirstOrDefault()
                ?? throw new ProfilingException("The dump has no .NET runtime, so there is no managed heap to read.");
            using var runtime = clr.CreateRuntime();
            var scanner = new ContentScanner();
            var visited = 0L;
            foreach (var obj in runtime.Heap.EnumerateObjects())
            {
                if ((++visited & 0xFFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                scanner.Add(obj, runtime.Heap);
                if (visited > MaxObjects)
                    throw new ProfilingException($"The heap has more than {MaxObjects:N0} objects; scanning it whole would need more memory than is reasonable.");
            }
            return scanner.Build();
        }
        catch (Exception exception) when (exception is not (ProfilingException or OperationCanceledException))
        {
            throw new ProfilingException($"Could not read the heap from the memory dump: {exception.Message}", exception);
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
