using System.Text;

namespace ICSharpCode.SharpDevelop.Designer.Devices;

/// <summary>Where a device runs. <see cref="Host"/> is this computer (a native desktop preview).</summary>
public enum DevicePlatform
{
    Host,
    IosSimulator,
    AndroidEmulator,
}

/// <summary>The form factor a device belongs to; picker sections are grouped by it.</summary>
public enum DeviceFamily
{
    Phone,
    Tablet,
    Desktop,
}

/// <summary>What the machine knows about a device right now.</summary>
public enum DeviceState
{
    /// <summary>The provider cannot tell (for example an AVD whose emulator state is not queried).</summary>
    Unknown,
    Shutdown,
    Booting,
    Booted,
}

/// <summary>
/// One device a designer can preview on: this computer, an iOS Simulator device or an Android
/// emulator AVD. Providers produce these; designers map them to their own preview target.
/// </summary>
/// <param name="Platform">Where it runs.</param>
/// <param name="Id">Stable identity of this instance: "host", "ios:&lt;udid&gt;" or "android:&lt;avd&gt;".</param>
/// <param name="Name">Display name without runtime decoration, e.g. "iPhone 16 Pro".</param>
/// <param name="Family">Phone, tablet or desktop.</param>
/// <param name="RuntimeName">"iOS", "Android" or "AppKit".</param>
/// <param name="RuntimeVersion">Runtime version text ("26.1", "35"); empty when meaningless.</param>
/// <param name="State">Machine state.</param>
public sealed record Device(
    DevicePlatform Platform,
    string Id,
    string Name,
    DeviceFamily Family,
    string RuntimeName,
    string RuntimeVersion,
    DeviceState State = DeviceState.Unknown)
{
    public static Device Host { get; } =
        new(DevicePlatform.Host, "host", "This computer", DeviceFamily.Desktop, "Host", "", DeviceState.Booted);

    /// <summary>The device model without its runtime instance. Favorites and recents are stored by
    /// this key, so they survive an iOS runtime upgrade or a recreated simulator. For an iOS
    /// simulator it is derived from the name; other platforms use the id.</summary>
    public string ModelKey => Platform == DevicePlatform.IosSimulator ? "ios:" + Slug(Name) : Id;

    /// <summary>Whether the device is up. The host computer is always running; pickers show no state
    /// dot for it.</summary>
    public bool IsRunning => State == DeviceState.Booted;

    /// <summary>"AppKit", "iOS 26.1" or "Android API 35".</summary>
    public string RuntimeLabel => RuntimeVersion.Length == 0 ? RuntimeName : RuntimeName + " " + RuntimeVersion;

    /// <summary>Orders runtime versions without a System.Version dependency (e.g. "26.1").</summary>
    public Version ParsedRuntimeVersion =>
        Version.TryParse(RuntimeVersion, out var version) ? version
        : int.TryParse(RuntimeVersion, out var major) ? new Version(major, 0)
        : new Version(0, 0);

    /// <summary>"iPhone 16 Pro (2nd generation)" becomes "iphone-16-pro-2nd-generation".</summary>
    public static string Slug(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }

        return builder.ToString().TrimEnd('-');
    }
}
