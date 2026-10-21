using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace OpenDevelop.Profiler.Cpu;

/// <summary>
/// Reads the runtime's allocation-tick events out of an EventPipe <c>.nettrace</c> file. The runtime emits one roughly
/// every 100 KB it hands out, naming the type being allocated at that moment and the stack allocating it, so the
/// ticks are an unbiased sample of where memory goes. Each becomes one sample whose stack ends in the allocated type,
/// which lets every call-tree view of a CPU profile answer "who allocates how much of what" unchanged.
/// </summary>
public static class NettraceAllocationReader
{
    public const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";

    /// <summary>The module name that marks a frame as an allocated type rather than a method.</summary>
    public const string AllocatedTypeModule = "[Allocated type]";

    public static CpuProfile Read(string nettracePath)
    {
        long ticks = 0;
        double kilobytes = 0;
        return NettraceCpuReader.ReadStacks(
            nettracePath,
            data =>
            {
                if (data is not GCAllocationTickTraceData tick)
                    return null;
                ticks++;
                kilobytes += tick.AllocationAmount64 / 1024.0;
                var type = string.IsNullOrEmpty(tick.TypeName) ? "[unknown type]" : tick.TypeName;
                return new NettraceCpuReader.SampleSelection(SampleState.Managed, (type, AllocatedTypeModule));
            },
            // One sample stands for the average amount a tick covers (about 100 KB), so counts convert to sizes.
            () => (ticks == 0 ? 100.0 : kilobytes / ticks, ProfileUnit.Kilobytes));
    }
}
