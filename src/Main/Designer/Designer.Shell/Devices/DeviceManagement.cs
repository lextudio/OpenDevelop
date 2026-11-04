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
public sealed record RuntimeOption(string Id, string Name, string Version);

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

    void Open();
}
