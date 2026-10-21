using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;

namespace OpenDevelop.Profiler.Collection;

public sealed class CpuCaptureOptions
{
    /// <summary>Stop on its own after this long. <c>null</c> keeps capturing until <see cref="CpuCapture.StopAsync"/>.</summary>
    public TimeSpan? Duration { get; init; }

    public int CircularBufferMegabytes { get; init; } = 256;
}

/// <summary>
/// A running CPU sampling session against one process, streaming the EventPipe data straight into a
/// <c>.nettrace</c> file. This is what <c>dotnet-trace collect</c> does, driven in-process so the IDE
/// controls Start and Stop and no extra tool has to be installed.
/// </summary>
public sealed class CpuCapture : IAsyncDisposable
{
    // The runtime provider carries the module-load and JIT events that name every sampled frame. This
    // is the keyword set of dotnet-trace's dotnet-common profile, validated against a real capture.
    const long RuntimeKeywords = 0x100003801D;

    readonly EventPipeSession session;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly string outputPath;
    int stopped;

    CpuCapture(EventPipeSession session, string outputPath, CpuCaptureOptions options)
    {
        this.session = session;
        this.outputPath = outputPath;
        Completion = Pump(options.Duration);
    }

    /// <summary>Completes when the capture file is fully written (after a stop, a duration, or the target exiting).</summary>
    public Task<CaptureResult> Completion { get; }

    public static CpuCapture Start(int processId, string outputPath, CpuCaptureOptions? options = null) =>
        Start(new DiagnosticsClient(processId), processId, outputPath, options);

    /// <summary>Starts sampling through an existing client, e.g. the one of a <see cref="SuspendedLaunch"/>.</summary>
    public static CpuCapture Start(DiagnosticsClient client, int processId, string outputPath, CpuCaptureOptions? options = null)
    {
        options ??= new CpuCaptureOptions();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        try
        {
            var providers = new[]
            {
                new EventPipeProvider(Cpu.NettraceCpuReader.SampleProfilerProvider, EventLevel.Informational),
                new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, RuntimeKeywords),
            };
            var session = client
                .StartEventPipeSession(providers, requestRundown: true, options.CircularBufferMegabytes);
            return new CpuCapture(session, outputPath, options);
        }
        catch (Exception exception)
        {
            throw DiagnosticsErrors.Wrap(exception, processId);
        }
    }

    /// <summary>Ends the capture and waits until the file is complete. Safe to call more than once.</summary>
    public async Task<CaptureResult> StopAsync()
    {
        RequestStop();
        return await Completion.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ProfilingException or IOException)
        {
            // Nothing useful to do while disposing; the caller observes failures through Completion.
        }
    }

    // Called from a thread-pool timer and from stop requests. Whatever goes wrong here (the session already closed, the
    // target gone, an IPC error) must not escape: an exception on a pool thread would be unhandled and end the IDE.
    // A real failure still surfaces, because the pump reading the stream fails or ends and reports it through Completion.
    void RequestStop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
            return;
        try
        {
            session.Stop();
        }
        catch (Exception)
        {
        }
    }

    async Task<CaptureResult> Pump(TimeSpan? duration)
    {
        var timer = duration is { } limit
            ? new Timer(_ => RequestStop(), null, limit, Timeout.InfiniteTimeSpan)
            : null;
        try
        {
            await using var file = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var buffer = new byte[81920];
            while (true)
            {
                // Reading and writing are separate steps so that only the stream side can mean "the target went away".
                // A failure to write the capture file (a full disk) is a failed capture, not an exited program, and
                // must not be handed back as a finished file.
                int read;
                try
                {
                    read = await session.EventStream.ReadAsync(buffer).ConfigureAwait(false);
                }
                catch (Exception exception) when (DiagnosticsErrors.IsTargetGone(exception))
                {
                    break; // the stream ended abruptly because the target went away; keep what was written
                }
                if (read == 0)
                    break;
                await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
        }
        finally
        {
            // The timer goes first: its callback uses the session, which is about to be closed.
            if (timer is not null)
                await timer.DisposeAsync().ConfigureAwait(false);
            session.Dispose();
        }

        // If nobody asked the capture to stop (user or duration), the stream only ended because the target did.
        var targetExited = Volatile.Read(ref stopped) == 0;
        return new CaptureResult(outputPath, targetExited, clock.Elapsed);
    }
}
