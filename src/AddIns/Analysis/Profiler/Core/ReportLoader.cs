using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using OpenDevelop.Profiler.Cpu;
using OpenDevelop.Profiler.Heap;

namespace OpenDevelop.Profiler;

/// <summary>What a <c>.nettrace</c> file holds, as far as the Profiler can show it.</summary>
public enum CaptureKind
{
    Unknown,
    Cpu,
    Heap,
}

/// <summary>A parsed capture: exactly one of <see cref="Cpu"/> and <see cref="Heap"/> is set.</summary>
public sealed class ProfilingReport
{
    public ProfilingReport(CpuProfile cpu)
    {
        Kind = CaptureKind.Cpu;
        Cpu = cpu;
    }

    public ProfilingReport(HeapSnapshot heap)
    {
        Kind = CaptureKind.Heap;
        Heap = heap;
    }

    public CaptureKind Kind { get; }
    public CpuProfile? Cpu { get; }
    public HeapSnapshot? Heap { get; }
}

/// <summary>Recognises and parses a capture file. Blocking; call it off the UI thread.</summary>
public static class ReportLoader
{

    /// <summary>Looks at the first events only, so it is cheap even for a large capture.</summary>
    public static CaptureKind Detect(string path)
    {
        try
        {
            using var source = new EventPipeEventSource(path);
            var kind = CaptureKind.Unknown;
            var anyEvent = false;
            // Typed handlers rather than a name check on AllEvents: without a registered parser the
            // events carry no provider or event names to compare.
            new SampleProfilerTraceEventParser(source).ThreadSample += _ =>
            {
                kind = CaptureKind.Cpu;
                source.StopProcessing();
            };
            source.Clr.GCBulkNode += _ =>
            {
                kind = CaptureKind.Heap;
                source.StopProcessing();
            };
            // A capture of a program that ended before the sampler fired has no samples but is still a CPU capture.
            source.AllEvents += _ => anyEvent = true;
            source.Process();
            return kind == CaptureKind.Unknown && anyEvent ? CaptureKind.Cpu : kind;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Arbitrary files get offered to this: anything the trace reader rejects is "not a capture".
            return CaptureKind.Unknown;
        }
    }

    public static ProfilingReport Load(string path)
    {
        var kind = Detect(path);
        if (kind is not (CaptureKind.Cpu or CaptureKind.Heap))
            throw new ProfilingException($"'{Path.GetFileName(path)}' is not a CPU or heap capture the Profiler can show.");
        try
        {
            return kind == CaptureKind.Cpu
                ? new ProfilingReport(NettraceCpuReader.Read(path))
                : new ProfilingReport(NettraceHeapReader.Read(path));
        }
        catch (Exception exception) when (exception is not (ProfilingException or OutOfMemoryException))
        {
            // A corrupt or truncated capture, or a temp folder the trace reader cannot write to.
            throw new ProfilingException($"Could not read '{Path.GetFileName(path)}': {exception.Message}", exception);
        }
    }
}
