using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using OpenDevelop.Profiler.Cpu;
using OpenDevelop.Profiler.Dump;
using OpenDevelop.Profiler.Heap;

namespace OpenDevelop.Profiler;

/// <summary>What a <c>.nettrace</c> file holds, as far as the Profiler can show it.</summary>
public enum CaptureKind
{
    Unknown,
    Cpu,
    Heap,
    Allocation,
    Dump,
}

/// <summary>
/// A parsed capture: <see cref="Dump"/> for a memory dump, <see cref="Heap"/> for a heap snapshot, otherwise <see cref="Cpu"/>, which also carries an allocation
/// profile (<see cref="CpuProfile.Unit"/> says which).
/// </summary>
public sealed class ProfilingReport
{
    public ProfilingReport(CpuProfile cpu, CaptureKind kind = CaptureKind.Cpu, IReadOnlyList<CounterSeries>? counters = null)
    {
        Kind = kind;
        Cpu = cpu;
        Counters = counters ?? [];
    }

    public ProfilingReport(HeapSnapshot heap)
    {
        Kind = CaptureKind.Heap;
        Heap = heap;
    }

    public ProfilingReport(DumpReport dump)
    {
        Kind = CaptureKind.Dump;
        Dump = dump;
    }

    public CaptureKind Kind { get; }
    public DumpReport? Dump { get; }
    public CpuProfile? Cpu { get; }
    public HeapSnapshot? Heap { get; }

    /// <summary>The runtime counters recorded alongside a CPU or allocation capture; empty when there are none.</summary>
    public IReadOnlyList<CounterSeries> Counters { get; } = [];
}

/// <summary>Recognises and parses a capture file. Blocking; call it off the UI thread.</summary>
public static class ReportLoader
{
    /// <summary>
    /// A capture at least this big is worth a warning before opening: the whole file is read and, for a heap snapshot, the
    /// whole object graph is built in memory, several times the size of the file.
    /// </summary>
    public const long LargeCaptureBytes = 200L * 1024 * 1024;

    public static bool IsLarge(long fileBytes, long thresholdBytes = LargeCaptureBytes) => fileBytes >= thresholdBytes;


    /// <summary>Looks at the first events only, so it is cheap even for a large capture.</summary>
    public static CaptureKind Detect(string path)
    {
        // A memory dump is recognised by its header, whatever it is called (a Linux core file is just "core").
        if (DumpReader.IsDumpFile(path))
            return CaptureKind.Dump;

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
            source.Clr.GCAllocationTick += _ =>
            {
                if (kind == CaptureKind.Unknown)
                    kind = CaptureKind.Allocation;
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

    // The counters are an extra: a capture whose counters cannot be read is still a good CPU or allocation report.
    static IReadOnlyList<CounterSeries> ReadCounters(string path)
    {
        try
        {
            return NettraceCounterReader.Read(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return [];
        }
    }

    public static ProfilingReport Load(string path)
    {
        var kind = Detect(path);
        if (kind is not (CaptureKind.Cpu or CaptureKind.Heap or CaptureKind.Allocation or CaptureKind.Dump))
            throw new ProfilingException($"'{Path.GetFileName(path)}' is not a CPU or heap capture the Profiler can show.");
        try
        {
            return kind switch
            {
                CaptureKind.Cpu => new ProfilingReport(NettraceCpuReader.Read(path), CaptureKind.Cpu, ReadCounters(path)),
                CaptureKind.Allocation => new ProfilingReport(NettraceAllocationReader.Read(path), CaptureKind.Allocation, ReadCounters(path)),
                CaptureKind.Dump => new ProfilingReport(DumpReader.Read(path)),
                _ => new ProfilingReport(NettraceHeapReader.Read(path)),
            };
        }
        catch (Exception exception) when (exception is not (ProfilingException or OutOfMemoryException))
        {
            // A corrupt or truncated capture, or a temp folder the trace reader cannot write to.
            throw new ProfilingException($"Could not read '{Path.GetFileName(path)}': {exception.Message}", exception);
        }
    }
}
