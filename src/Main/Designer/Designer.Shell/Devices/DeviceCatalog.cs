namespace ICSharpCode.SharpDevelop.Designer.Devices;

/// <summary>One picker row: a device model with the runtime instance chosen for it and any other
/// runtimes of the same model folded underneath.</summary>
public sealed record DeviceEntry(string ModelKey, Device Best, IReadOnlyList<Device> OtherRuntimes);

/// <summary>A picker section: one platform and form factor, e.g. "iOS · iPhone".</summary>
public sealed record DeviceGroup(DevicePlatform Platform, DeviceFamily Family, string Title, IReadOnlyList<DeviceEntry> Entries);

/// <summary>Groups a flat device list into picker sections and folds the same model across
/// runtimes into one entry; also the single place that implements picker search.</summary>
public static class DeviceCatalog
{
    /// <summary>The best instance of a model: a running one first, then the newest runtime.</summary>
    public static Device PickBest(IEnumerable<Device> sameModel) =>
        sameModel
            .OrderByDescending(d => d.IsRunning)
            .ThenByDescending(d => d.ParsedRuntimeVersion)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .First();

    /// <summary>Platform order, then Phone, Tablet, Desktop; entries sorted by name, with numbers
    /// compared numerically so "iPhone 9" sorts before "iPhone 16".</summary>
    public static IReadOnlyList<DeviceGroup> Group(IEnumerable<Device> devices)
    {
        var groups = new List<DeviceGroup>();
        foreach (var bucket in devices
            .GroupBy(d => (d.Platform, d.Family))
            .OrderBy(g => g.Key.Platform).ThenBy(g => g.Key.Family))
        {
            var entries = bucket
                .GroupBy(d => d.ModelKey)
                .Select(model =>
                {
                    var best = PickBest(model);
                    var others = model.Where(d => d.Id != best.Id)
                        .OrderByDescending(d => d.ParsedRuntimeVersion)
                        .ToArray();
                    return new DeviceEntry(model.Key, best, others);
                })
                .OrderBy(e => e.Best.Name, NaturalComparer.Instance)
                .ToArray();
            var (platform, family) = bucket.Key;
            groups.Add(new DeviceGroup(platform, family, Title(platform, family), entries));
        }

        return groups;
    }

    static string Title(DevicePlatform platform, DeviceFamily family) => platform switch
    {
        DevicePlatform.Host => "This computer",
        DevicePlatform.IosSimulator => family == DeviceFamily.Tablet ? "iOS · iPad" : "iOS · iPhone",
        _ => family == DeviceFamily.Tablet ? "Android · Tablet" : "Android · Phone",
    };

    /// <summary>Every whitespace-separated word of the query must occur in the device's name,
    /// runtime or platform (case-insensitive). An empty query matches everything.</summary>
    public static bool Matches(Device device, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        string haystack = device.Name + " " + device.RuntimeLabel + " " + device.Platform + " " + device.Family;
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Case-insensitive name order with digit runs compared numerically.</summary>
    public static IComparer<string> NaturalOrder => NaturalComparer.Instance;

    sealed class NaturalComparer : IComparer<string>
    {
        public static NaturalComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            x ??= ""; y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    var a = x[si..i].TrimStart('0'); var b = y[sj..j].TrimStart('0');
                    int byLength = a.Length.CompareTo(b.Length);
                    if (byLength != 0) return byLength;
                    int byDigits = string.CompareOrdinal(a, b);
                    if (byDigits != 0) return byDigits;
                }
                else
                {
                    int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
