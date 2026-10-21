using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;

namespace OpenDevelop.Profiler.Cpu;

/// <summary>Reads the runtime's sample-profiler events out of an EventPipe <c>.nettrace</c> file.</summary>
public static class NettraceCpuReader
{
    public const string SampleProfilerProvider = "Microsoft-DotNETCore-SampleProfiler";

    public static CpuProfile Read(string nettracePath)
    {
        // TraceLog resolves method names from the capture's own rundown events, which a plain
        // EventPipeEventSource cannot do. It writes a side file; keep that out of the user's folder.
        var etlxPath = Path.Combine(Path.GetTempPath(), $"opendevelop-profiler-{Guid.NewGuid():N}.etlx");
        try
        {
            TraceLog.CreateFromEventPipeDataFile(nettracePath, etlxPath);
            using var log = new TraceLog(etlxPath);
            return Convert(log);
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    static CpuProfile Convert(TraceLog log)
    {
        var frames = new List<FrameInfo>();
        var frameByKey = new Dictionary<(string Method, string Module), int>();
        var frameByAddress = new Dictionary<CodeAddressIndex, int>();
        var samples = new List<CpuSample>();
        var scratch = new List<int>();
        double firstMs = double.NaN, lastMs = 0;

        foreach (var data in log.Events)
        {
            if (data.ProviderName != SampleProfilerProvider)
                continue;
            var callStack = data.CallStack();
            if (callStack is null)
                continue;

            scratch.Clear();
            for (var frame = callStack; frame is not null; frame = frame.Caller)
            {
                var address = frame.CodeAddress;
                if (!frameByAddress.TryGetValue(address.CodeAddressIndex, out var id))
                {
                    var key = (Describe(address), address.ModuleName ?? string.Empty);
                    if (!frameByKey.TryGetValue(key, out id))
                    {
                        id = frames.Count;
                        frames.Add(new FrameInfo(key.Item1, key.Item2));
                        frameByKey[key] = id;
                    }
                    frameByAddress[address.CodeAddressIndex] = id;
                }
                scratch.Add(id);
            }
            scratch.Reverse(); // TraceCallStack walks leaf to root; the model stores root to leaf.

            if (double.IsNaN(firstMs))
                firstMs = data.TimeStampRelativeMSec;
            var time = data.TimeStampRelativeMSec - firstMs;
            lastMs = Math.Max(lastMs, time);

            var state = data is ClrThreadSampleTraceData { Type: ClrThreadSampleType.External }
                ? SampleState.External
                : SampleState.Managed;
            samples.Add(new CpuSample(time, data.ThreadID, state, scratch.ToArray()));
        }

        return new CpuProfile(frames, samples, lastMs);
    }

    static string Describe(TraceCodeAddress address)
    {
        var name = address.FullMethodName;
        return string.IsNullOrEmpty(name) ? $"0x{address.Address:X}" : name;
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
