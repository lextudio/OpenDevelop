namespace ICSharpCode.SharpDevelop.Designer.Devices;

/// <summary>One platform's devices as the manager shows them: a collapsible root and its rows.</summary>
public sealed record DeviceManagerGroup(DevicePlatform Platform, IReadOnlyList<DeviceManagerRow> Rows);

/// <summary>
/// Grouping and expansion state for the Device Manager's tree, kept out of the window so it can be tested: the macOS
/// iOS simulator list is dozens of rows, so each platform is one collapsible root (Host first, then the model's order).
/// </summary>
public static class DeviceManagerGroups
{
    /// <summary>
    /// Groups rows by platform, preserving the model's order. Rows are expected ordered by platform (see
    /// <see cref="DeviceManagerModel.Rows"/>); consecutive rows of the same platform form one group.
    /// </summary>
    public static IReadOnlyList<DeviceManagerGroup> Build(IEnumerable<DeviceManagerRow> rows)
    {
        var groups = new List<DeviceManagerGroup>();
        var current = new List<DeviceManagerRow>();
        DevicePlatform? platform = null;
        foreach (var row in rows)
        {
            if (platform != row.Device.Platform)
            {
                if (platform is { } completed)
                    groups.Add(new DeviceManagerGroup(completed, current));
                current = new List<DeviceManagerRow>();
                platform = row.Device.Platform;
            }
            current.Add(row);
        }
        if (platform is { } last)
            groups.Add(new DeviceManagerGroup(last, current));
        return groups;
    }
}

/// <summary>Remembers which platform roots the user collapsed; roots start expanded.</summary>
public sealed class DeviceGroupExpansion
{
    readonly HashSet<DevicePlatform> collapsed = new();

    public bool IsExpanded(DevicePlatform platform) => !collapsed.Contains(platform);

    public void Set(DevicePlatform platform, bool expanded)
    {
        if (expanded)
            collapsed.Remove(platform);
        else
            collapsed.Add(platform);
    }
}
