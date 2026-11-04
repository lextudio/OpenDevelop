namespace ICSharpCode.SharpDevelop.Designer.Devices;

public enum DeviceAction { Start, Stop, Delete }

/// <summary>One line of the Device Manager: the device, whether the user hid it, which actions
/// make sense for it right now, and the state as words.</summary>
public sealed record DeviceManagerRow(
    Device Device,
    bool Hidden,
    bool CanStart,
    bool CanStop,
    bool CanDelete,
    bool CanHide,
    string StateText);

/// <summary>The logic behind the Device Manager dialog, free of any window so it is testable:
/// which rows to show with which actions enabled, and running an action against the provider that
/// owns the device. Hiding is a preference, not a provider action.</summary>
public sealed class DeviceManagerModel
{
    readonly DeviceService service;

    public DeviceManagerModel(DeviceService service) => this.service = service;

    IDeviceManagement? ManagementFor(Device device) =>
        service.Providers.FirstOrDefault(p => p.Platform == device.Platform) as IDeviceManagement;

    /// <summary>Every device the machine has (hidden ones included, flagged), grouped by platform
    /// then runtime and name like the picker, the host computer first.</summary>
    public IReadOnlyList<DeviceManagerRow> Rows(IEnumerable<Device> devices, DevicePreferences preferences) =>
        devices
            .OrderBy(d => d.Platform)
            .ThenByDescending(d => d.ParsedRuntimeVersion)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .Select(device =>
            {
                bool managed = device.Platform != DevicePlatform.Host && ManagementFor(device) != null;
                bool starting = device.State == DeviceState.Booting;
                return new DeviceManagerRow(
                    device,
                    preferences.IsHidden(device),
                    CanStart: managed && device.State is DeviceState.Shutdown or DeviceState.Unknown,
                    CanStop: managed && device.State is DeviceState.Booted or DeviceState.Booting,
                    CanDelete: managed && !starting && device.State != DeviceState.Booted,
                    CanHide: device.Platform != DevicePlatform.Host,
                    StateText: StateText(device));
            })
            .ToArray();

    public static string StateText(Device device) => device.Platform == DevicePlatform.Host
        ? "This computer"
        : device.State switch
        {
            DeviceState.Booted => "Running",
            DeviceState.Booting => "Starting…",
            DeviceState.Shutdown => "Stopped",
            _ => "State unknown",
        };

    /// <summary>Runs an action through the owning provider and refreshes the shared device list
    /// afterwards so every picker sees the new state.</summary>
    public async Task<DeviceActionResult> ExecuteAsync(DeviceAction action, Device device, CancellationToken cancellationToken = default)
    {
        if (ManagementFor(device) is not { } management)
            return DeviceActionResult.Failure($"{device.Name} cannot be managed from here.");
        DeviceActionResult result;
        try
        {
            result = action switch
            {
                DeviceAction.Start => await management.BootAsync(device, cancellationToken).ConfigureAwait(false),
                DeviceAction.Stop => await management.ShutdownAsync(device, cancellationToken).ConfigureAwait(false),
                DeviceAction.Delete => await management.DeleteAsync(device, cancellationToken).ConfigureAwait(false),
                _ => DeviceActionResult.Failure("Unknown action."),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = DeviceActionResult.Failure(exception.Message);
        }

        await service.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>The provider that can create devices for <paramref name="platform"/> through a form
    /// of its own, or null.</summary>
    public IDeviceCreation? CreationFor(DevicePlatform platform) =>
        service.Providers.FirstOrDefault(p => p.Platform == platform) as IDeviceCreation;

    /// <summary>The provider whose creation lives in its own tool window (Android), or null.</summary>
    public IExternalDeviceCreator? ExternalCreatorFor(DevicePlatform platform) =>
        service.Providers.FirstOrDefault(p => p.Platform == platform) as IExternalDeviceCreator;

    public async Task<DeviceActionResult> CreateAsync(DevicePlatform platform, string name, string deviceTypeId, string runtimeId, CancellationToken cancellationToken = default)
    {
        if (CreationFor(platform) is not { } creation)
            return DeviceActionResult.Failure("This platform cannot create devices here.");
        DeviceActionResult result;
        try
        {
            result = await creation.CreateAsync(name, deviceTypeId, runtimeId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = DeviceActionResult.Failure(exception.Message);
        }

        await service.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
