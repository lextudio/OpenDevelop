namespace ICSharpCode.SharpDevelop.Designer.Devices;

/// <summary>Lists the devices of one platform. Providers live where their tooling lives (the iOS
/// Simulator provider here, the Android one in the Android Device Manager add-in) and register
/// with <see cref="DeviceService"/>.</summary>
public interface IDeviceProvider
{
    /// <summary>Short name for the manager's page and for diagnostics ("iOS Simulator").</summary>
    string DisplayName { get; }

    DevicePlatform Platform { get; }

    /// <summary>The devices installed on this machine right now. Never throws: a missing tool or
    /// SDK yields an empty list (see <see cref="Unavailable"/> for why).</summary>
    Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Why this provider has nothing to offer on this machine (tool not installed, wrong
    /// OS), or null when it works. Shown greyed in the picker instead of hiding the platform.</summary>
    string? Unavailable { get; }
}
