using System.Globalization;
using System.Text;
using OpenDevelop.Profiler.Cpu;
using OpenDevelop.Profiler.Heap;

namespace OpenDevelop.Profiler;

/// <summary>
/// Report tables as CSV, for a spreadsheet or a diff. Numbers use the invariant culture so a file means the same on every
/// machine; the unit of a weight is in its column heading, so a reader does not have to guess it.
/// </summary>
public static class ReportExport
{
    /// <summary>One line per function: inclusive and self weight and share, heaviest self first.</summary>
    public static string FunctionsCsv(CpuProfile profile, ProfileFilter filter)
    {
        var functions = profile.BuildFunctions(filter);
        var total = Math.Max(1, profile.CountSamples(filter));
        var unit = UnitName(profile.Unit);
        var csv = new StringBuilder();
        Row(csv, "Function", "Module", $"Total ({unit})", "Total (%)", $"Self ({unit})", "Self (%)");
        foreach (var function in functions)
            Row(
                csv, function.Name, function.Module,
                Weight(function.Total, profile), Percent(function.Total, total),
                Weight(function.Self, profile), Percent(function.Self, total));
        return csv.ToString();
    }

    /// <summary>One line per call tree node, in tree order, with its depth so the nesting can be rebuilt.</summary>
    public static string CallTreeCsv(CpuProfile profile, ProfileFilter filter)
    {
        var root = profile.BuildCallTree(filter);
        var total = Math.Max(1, root.Total);
        var unit = UnitName(profile.Unit);
        var csv = new StringBuilder();
        Row(csv, "Depth", "Function", "Module", $"Total ({unit})", "Total (%)", $"Self ({unit})", "Self (%)");
        // Iterative, so a deep stack cannot overflow ours.
        var pending = new Stack<(CallTreeNode Node, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            Row(
                csv, depth.ToString(CultureInfo.InvariantCulture), node.Name, node.Module,
                Weight(node.Total, profile), Percent(node.Total, total),
                Weight(node.Self, profile), Percent(node.Self, total));
            for (var i = node.Children.Count - 1; i >= 0; i--)
                pending.Push((node.Children[i], depth + 1));
        }
        return csv.ToString();
    }

    /// <summary>One line per type: count, shallow size and, when the graph is available, retained size.</summary>
    public static string HeapTypesCsv(HeapSnapshot snapshot)
    {
        var retained = snapshot.Graph?.RetainedByType().ToDictionary(r => r.TypeName, r => r.RetainedSize);
        var csv = new StringBuilder();
        Row(csv, "Type", "Count", "Size (bytes)", "Size (%)", "Retained size (bytes)");
        foreach (var type in snapshot.Types.OrderByDescending(t => t.Size))
            Row(
                csv, type.Name, Number(type.Count), Number(type.Size), Percent(type.Size, Math.Max(1, snapshot.HeapSize)),
                retained is not null && retained.TryGetValue(type.Name, out var size) ? Number(size) : "");
        return csv.ToString();
    }

    static string UnitName(ProfileUnit unit) => unit == ProfileUnit.Kilobytes ? "KB" : "ms";

    static string Weight(long samples, CpuProfile profile) =>
        (samples * profile.SampleIntervalMs).ToString("0.###", CultureInfo.InvariantCulture);

    static string Percent(long part, long whole) =>
        (100.0 * part / whole).ToString("0.##", CultureInfo.InvariantCulture);

    static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    static void Row(StringBuilder csv, params string[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
                csv.Append(',');
            csv.Append(Escape(cells[i]));
        }
        csv.Append("\r\n");
    }

    // RFC 4180: a cell with a comma, quote or line break is quoted, and its quotes doubled. Type and method names contain
    // commas (generic arguments, parameter lists), so this is the usual case, not the exception.
    static string Escape(string cell) =>
        cell.AsSpan().IndexOfAny(",\"\r\n") < 0 ? cell : "\"" + cell.Replace("\"", "\"\"") + "\"";
}
