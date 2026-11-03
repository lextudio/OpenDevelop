namespace ICSharpCode.SharpDevelop.Designer.Devices;

/// <summary>
/// The machine-wide device list shared by every designer (MAUI, Uno, ...). Providers register once;
/// <see cref="RefreshAsync"/> asks them all and publishes one snapshot. Safe to call from any
/// thread; <see cref="Changed"/> is raised on the thread that finished the refresh.
/// </summary>
public sealed class DeviceService
{
    readonly object gate = new();
    readonly List<IDeviceProvider> providers = [];
    IReadOnlyList<Device> devices = [Device.Host];

    /// <summary>The shared instance. It starts with the iOS Simulator provider (no SDK dependency);
    /// add-ins register theirs (Android Device Manager registers the AVD provider).</summary>
    public static DeviceService Default { get; } = CreateDefault();

    static DeviceService CreateDefault()
    {
        var service = new DeviceService();
        service.Register(new IosSimulatorProvider());
        return service;
    }

    /// <summary>Raised after a refresh produced a different list.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<IDeviceProvider> Providers
    {
        get { lock (gate) return providers.ToArray(); }
    }

    /// <summary>The last snapshot (starts as just the host computer).</summary>
    public IReadOnlyList<Device> Devices
    {
        get { lock (gate) return devices; }
    }

    /// <summary>Registers a provider once per platform; a second one for the same platform replaces
    /// the first (an add-in reload must not duplicate devices).</summary>
    public void Register(IDeviceProvider provider)
    {
        lock (gate)
        {
            providers.RemoveAll(p => p.Platform == provider.Platform);
            providers.Add(provider);
        }
    }

    /// <summary>Asks every provider at once and publishes one snapshot. A provider that throws or
    /// does not answer within <paramref name="providerTimeout"/> (default 60 s) contributes nothing
    /// instead of holding up the others.</summary>
    public async Task<IReadOnlyList<Device>> RefreshAsync(CancellationToken cancellationToken = default,
        TimeSpan? providerTimeout = null)
    {
        var timeout = providerTimeout ?? TimeSpan.FromSeconds(60);
        var lists = await Task.WhenAll(Providers.Select(p => ListSafelyAsync(p, timeout, cancellationToken)))
            .ConfigureAwait(false);
        var snapshot = new List<Device> { Device.Host };
        foreach (var list in lists)
            snapshot.AddRange(list);

        bool changed;
        lock (gate)
        {
            changed = !devices.SequenceEqual(snapshot);
            devices = snapshot;
        }

        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
        return snapshot;
    }

    static async Task<IReadOnlyList<Device>> ListSafelyAsync(IDeviceProvider provider, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var listing = Task.Run(() => provider.ListAsync(cancellationToken), cancellationToken);
            var finished = await Task.WhenAny(listing, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (finished != listing)
            {
                // Observe a late failure so it does not surface as an unobserved task exception.
                _ = listing.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return [];
            }

            return await listing.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return []; // one broken provider must not hide the others' devices
        }
    }
}
