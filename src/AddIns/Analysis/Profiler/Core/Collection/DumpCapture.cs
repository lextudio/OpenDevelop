using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace OpenDevelop.Profiler.Collection;

/// <summary>
/// Writes a memory dump of a running .NET process through its diagnostic port, with the heap, so the managed objects can be
/// read from it later. The process is held still while the dump is written, and the file is as large as the process's memory.
/// </summary>
public static class DumpCapture
{
    public static Task<CaptureResult> CaptureAsync(int processId, string outputPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Capture(processId, outputPath, cancellationToken), cancellationToken);

    static CaptureResult Capture(int processId, string outputPath, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            new DiagnosticsClient(processId).WriteDump(DumpType.WithHeap, outputPath);
        }
        catch (Exception exception)
        {
            TryDelete(outputPath);
            throw DiagnosticsErrors.Wrap(exception, processId);
        }
        // Writing cannot be interrupted part way; a cancel that arrives during it is honoured by discarding the result.
        if (cancellationToken.IsCancellationRequested)
        {
            TryDelete(outputPath);
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new ProfilingException($"Process {processId} did not write a memory dump.");
        return new CaptureResult(outputPath, TargetExited: false, clock.Elapsed);
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
