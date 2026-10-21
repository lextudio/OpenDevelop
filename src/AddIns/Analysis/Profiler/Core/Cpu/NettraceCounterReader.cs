using Microsoft.Diagnostics.Tracing;

namespace OpenDevelop.Profiler.Cpu;

/// <summary>One runtime counter over the capture: its value at each reporting interval.</summary>
/// <param name="Name">The counter's id, e.g. <c>gc-heap-size</c>.</param>
/// <param name="DisplayName">The runtime's own label, e.g. <c>GC Heap Size</c>.</param>
/// <param name="Units">What the values are measured in (<c>MB</c>, <c>%</c>, ...), or empty.</param>
/// <param name="TimesMs">Milliseconds from the start of the capture session.</param>
public sealed record CounterSeries(string Name, string DisplayName, string Units, IReadOnlyList<double> TimesMs, IReadOnlyList<double> Values)
{
    public double Max => Values.Count == 0 ? 0 : Values.Max();

    public double Last => Values.Count == 0 ? 0 : Values[^1];
}

/// <summary>
/// Reads the <c>System.Runtime</c> event counters (CPU usage, GC heap size, collections per generation, allocation
/// rate, exceptions, thread pool) out of an EventPipe <c>.nettrace</c> file, where the capture asked for them.
/// </summary>
public static class NettraceCounterReader
{
    public const string RuntimeCounterProvider = "System.Runtime";

    public static IReadOnlyList<CounterSeries> Read(string nettracePath)
    {
        var points = new Dictionary<string, (string Display, string Units, List<double> Times, List<double> Values)>();
        var order = new List<string>();
        using var source = new EventPipeEventSource(nettracePath);
        source.Dynamic.All += data =>
        {
            if (data.ProviderName != RuntimeCounterProvider || data.EventName != "EventCounters")
                return;
            if (data.PayloadValue(0) is not IDictionary<string, object> envelope
                || envelope["Payload"] is not IDictionary<string, object> fields
                || fields["Name"] is not string name)
                return;

            // A counter that is sampled reports a mean; one that counts reports how much it grew over the interval.
            var value = fields.TryGetValue("Mean", out var mean)
                ? System.Convert.ToDouble(mean, System.Globalization.CultureInfo.InvariantCulture)
                : fields.TryGetValue("Increment", out var increment)
                    ? System.Convert.ToDouble(increment, System.Globalization.CultureInfo.InvariantCulture)
                    : double.NaN;
            if (double.IsNaN(value))
                return;

            if (!points.TryGetValue(name, out var series))
            {
                series = (
                    fields.TryGetValue("DisplayName", out var display) ? display as string ?? name : name,
                    fields.TryGetValue("DisplayUnits", out var units) ? units as string ?? string.Empty : string.Empty,
                    [], []);
                points[name] = series;
                order.Add(name);
            }
            series.Times.Add(data.TimeStampRelativeMSec);
            series.Values.Add(value);
        };
        source.Process();

        return order
            .Select(name => new CounterSeries(name, points[name].Display, points[name].Units, points[name].Times, points[name].Values))
            .ToList();
    }
}
