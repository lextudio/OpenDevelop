using Microsoft.Diagnostics.NETCore.Client;

namespace OpenDevelop.Profiler.Collection;

/// <summary>Turns the diagnostics client's low-level failures into messages a user can act on.</summary>
internal static class DiagnosticsErrors
{
    public static ProfilingException Wrap(Exception exception, int processId) => exception switch
    {
        ProfilingException profiling => profiling,
        ServerNotAvailableException => new ProfilingException(
            $"Process {processId} does not expose a diagnostic port. It must be a running .NET (CoreCLR) process, "
            + "started by the same user, with diagnostics enabled (DOTNET_EnableDiagnostics is not 0).", exception),
        UnsupportedCommandException => new ProfilingException(
            $"Process {processId} runs a .NET runtime that does not support EventPipe profiling.", exception),
        TimeoutException => new ProfilingException(
            $"Process {processId} did not answer the diagnostics request in time. If it runs in a container or under "
            + "a different TMPDIR, the diagnostic port is not reachable from here.", exception),
        UnauthorizedAccessException => new ProfilingException(
            $"Access to process {processId} was denied. Run OpenDevelop as the same user as the target.", exception),
        _ => new ProfilingException($"Could not profile process {processId}: {exception.Message}", exception),
    };

    /// <summary>The failures that simply mean the target (or its stream) is already gone.</summary>
    public static bool IsTargetGone(Exception exception) =>
        exception is EndOfStreamException or IOException or ServerNotAvailableException
            or ServerErrorException or ObjectDisposedException or TimeoutException or OperationCanceledException
            or InvalidOperationException;
}
