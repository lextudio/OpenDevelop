using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace OpenDevelop.Profiler.Collection;

/// <summary>
/// Takes one managed-heap snapshot of a running process. Enabling the GC heap-snapshot keywords makes
/// the runtime perform a full (generation 2) garbage collection and emit the whole object graph as
/// events, so the target is paused for the length of that collection.
/// </summary>
public static class HeapCapture
{
    // How long to wait for the event stream to end after asking the session to stop. A target that is frozen (held at a
    // breakpoint, or at startup) never acknowledges, and the capture must still return.
    static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    public static Task<CaptureResult> CaptureAsync(
        int processId,
        string outputPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        CaptureAsync(new DiagnosticsClient(processId), processId, outputPath, timeout, cancellationToken);

    /// <summary>Takes the snapshot through an existing client, e.g. the one of a <see cref="SuspendedLaunch"/>.</summary>
    public static async Task<CaptureResult> CaptureAsync(
        DiagnosticsClient client,
        int processId,
        string outputPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var clock = Stopwatch.StartNew();
        EventPipeSession session;
        try
        {
            var provider = new EventPipeProvider(
                "Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, (long)ClrTraceEventParser.Keywords.GCHeapSnapshot);
            session = client.StartEventPipeSession([provider], requestRundown: false, circularBufferMB: 1024);
        }
        catch (Exception exception)
        {
            throw DiagnosticsErrors.Wrap(exception, processId);
        }

        using (session)
        {
            var sawSnapshot = false;
            var stopRequested = 0;
            var snapshotDone = 0; // set on the event thread itself, so it is already true when the stream ends after it

            // Runs on pool threads (the stop event, the cancellation registration), where an escaping exception would
            // be unhandled and end the host. The session being closed or the target gone is the normal outcome of a
            // stop; a real failure shows up as the event stream failing, which is reported below.
            void Stop()
            {
                if (Interlocked.Exchange(ref stopRequested, 1) != 0)
                    return;
                try
                {
                    session.Stop();
                }
                catch (Exception)
                {
                }
            }

            await using var file = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var tee = new TeeReadStream(session.EventStream, file);
            var source = new EventPipeEventSource(tee);
            Task? processing = null;
            try
            {
            source.Clr.GCBulkNode += _ => sawSnapshot = true;
            // The snapshot GC is over once its stop event arrives after the node events. Stop from another
            // thread: the session cannot be stopped from inside its own event callback.
            source.Clr.GCStop += _ =>
            {
                if (sawSnapshot)
                {
                    Volatile.Write(ref snapshotDone, 1);
                    Task.Run(Stop);
                }
            };

            using var cancelRegistration = cancellationToken.Register(Stop);
            processing = Task.Run(() =>
            {
                try
                {
                    source.Process();
                }
                catch (Exception exception) when (DiagnosticsErrors.IsTargetGone(exception))
                {
                }
            });

            // Race the stream against the timeout and against cancellation, so neither waits on a target that is not answering.
            using var timeoutOrCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var finished = await Task.WhenAny(processing, Task.Delay(timeout, timeoutOrCancel.Token)).ConfigureAwait(false);
            timeoutOrCancel.Cancel(); // release the delay timer if the stream finished first
            var timedOut = finished != processing && !cancellationToken.IsCancellationRequested;
            try
            {
                if (finished != processing)
                {
                    Stop();
                    await EndAsync(processing, session).ConfigureAwait(false);
                }

                if (processing.IsCompleted)
                    await processing.ConfigureAwait(false);
            }
            catch (SinkWriteException exception)
            {
                throw new ProfilingException($"Could not write the heap snapshot to '{outputPath}': {exception.Message}", exception);
            }
            catch (Exception exception) when (exception is not (ProfilingException or OperationCanceledException))
            {
                throw new ProfilingException($"Reading the heap snapshot of process {processId} failed: {exception.Message}", exception);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var problem = Judge(
                timedOut, sawSnapshot, Volatile.Read(ref snapshotDone) != 0, Volatile.Read(ref stopRequested) != 0, processId, timeout);
            if (problem is not null)
                throw new ProfilingException(problem);
            }
            finally
            {
                // Disposing the source waits for its reader. When the target never answered, that reader is still blocked
                // on the dead stream, so it is left to end with the process rather than hanging the capture here.
                if (processing is null || processing.IsCompleted)
                {
                    source.Dispose();
                    tee.Dispose();
                }
            }
        }

        return new CaptureResult(outputPath, TargetExited: false, clock.Elapsed);
    }

    /// <summary>Decides whether a finished capture is a complete snapshot; returns the reason when it is not.</summary>
    internal static string? Judge(bool timedOut, bool sawSnapshot, bool snapshotDone, bool stopRequested, int processId, TimeSpan timeout)
    {
        if (timedOut)
            return $"The heap snapshot of process {processId} did not finish within {timeout.TotalSeconds:0} seconds.";
        if (!sawSnapshot)
            return $"Process {processId} ended before it reported a heap snapshot.";
        // The stream ended without this side asking for it to (the snapshot GC finishing, the timeout and cancellation
        // all stop it first): the target went away part way through the dump, so the file is incomplete.
        if (!snapshotDone && !stopRequested)
            return $"Process {processId} ended while its heap snapshot was being written; the snapshot is incomplete.";
        return null;
    }

    // After a stop request the stream normally ends within moments. If it does not, the target is not servicing the
    // session: close the session ourselves, which fails the blocked read, and give up on the reader if even that does not
    // end it, so that the capture returns instead of waiting for a process that never answers.
    static async Task EndAsync(Task processing, EventPipeSession session)
    {
        try
        {
            await processing.WaitAsync(StopGrace).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
        }

        session.Dispose();
        try
        {
            await processing.WaitAsync(StopGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }
}
