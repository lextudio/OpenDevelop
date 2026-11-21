namespace ICSharpCode.SharpDevelop.Designer.Devices;

/// <summary>The outcome of a device action: success, or a one-line reason fit for the manager's
/// status strip (tool output reduced to its first meaningful line).</summary>
public sealed record DeviceActionResult(bool Ok, string? Error = null)
{
    public static DeviceActionResult Success { get; } = new(true);

    public static DeviceActionResult Failure(string error) => new(false, error);
}

/// <summary>Optional capability of a provider: start, stop and delete its devices. Implemented
/// where the tooling lives, like the provider itself.</summary>
public interface IDeviceManagement
{
    Task<DeviceActionResult> BootAsync(Device device, CancellationToken cancellationToken = default);

    Task<DeviceActionResult> ShutdownAsync(Device device, CancellationToken cancellationToken = default);

    Task<DeviceActionResult> DeleteAsync(Device device, CancellationToken cancellationToken = default);
}

/// <summary>A hardware model a new device can be created from ("iPhone 17 Pro").</summary>
public sealed record DeviceTypeOption(string Id, string Name, DeviceFamily Family);

/// <summary>A runtime a new device can run ("iOS 26.1").</summary>
public sealed record RuntimeOption(string Id, string Name, string Version, IReadOnlySet<string>? SupportedDeviceTypeIds = null)
{
    /// <summary>Whether this runtime can host the given model. Unknown support (null) allows it:
    /// the tool then has the last word and its error is shown.</summary>
    public bool Supports(string deviceTypeId) => SupportedDeviceTypeIds == null || SupportedDeviceTypeIds.Contains(deviceTypeId);
}

public sealed record DeviceCreateOptions(IReadOnlyList<DeviceTypeOption> Types, IReadOnlyList<RuntimeOption> Runtimes);

/// <summary>Optional capability: create a device from a hardware model and runtime.</summary>
public interface IDeviceCreation
{
    Task<DeviceCreateOptions> GetCreateOptionsAsync(CancellationToken cancellationToken = default);

    Task<DeviceActionResult> CreateAsync(string name, string deviceTypeId, string runtimeId, CancellationToken cancellationToken = default);
}

/// <summary>Optional capability for platforms whose device creation is its own tool window
/// (Android's AVD editor): the manager shows a button that opens it instead of its own form.</summary>
public interface IExternalDeviceCreator
{
    string Label { get; }

    /// <summary>
    /// Opens the platform's own creation window and completes when it closes, so the manager can refresh its list
    /// afterwards and report a failure (for example a missing SDK) in its status strip.
    /// </summary>
    Task<DeviceActionResult> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Optional capability: edit an existing device in the platform's own editor (the Android
/// AVD hardware editor). The manager shows an Edit button for the selected device when its
/// provider supports this and accepts that device.</summary>
public interface IExternalDeviceEditor
{
    /// <summary>Button caption ("Edit AVD…").</summary>
    string EditLabel { get; }

    bool CanEdit(Device device);

    /// <summary>
    /// Opens the platform editor for the device and completes when it closes. Asynchronous because finding the
    /// device can involve a command-line tool (avdmanager); the manager shows its busy state meanwhile and surfaces a
    /// failure in its status strip instead of letting an exception escape a void click handler.
    /// </summary>
    Task<DeviceActionResult> EditAsync(Device device, CancellationToken cancellationToken = default);
}
