namespace ICSharpCode.SharpDevelop.Designer.Devices;

/// <summary>
/// The user's device preferences: ordered favorites and the most recent picks, both by model
/// key, plus hidden device ids. Immutable; every change returns a new instance for the caller to
/// persist. Hidden devices are not selectable anywhere.
/// </summary>
public sealed class DevicePreferences
{
    /// <summary>Only this many recent devices are recorded.</summary>
    public const int MaxRecents = 5;

    public static DevicePreferences Empty { get; } = new([], [], []);

    public DevicePreferences(IEnumerable<string> favorites, IEnumerable<string> recents, IEnumerable<string> hidden,
        bool favoritesInitialized = false)
    {
        HasFavorites = favoritesInitialized;
        Favorites = favorites.Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        Recents = recents.Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).Take(MaxRecents).ToArray();
        Hidden = new HashSet<string>(hidden.Where(k => k.Length > 0), StringComparer.Ordinal);
    }

    /// <summary>False until the favorites were seeded or the user changed them; an empty list the
    /// user chose is different from a list never set up.</summary>
    public bool HasFavorites { get; }

    /// <summary>Model keys, in the order the user arranged them.</summary>
    public IReadOnlyList<string> Favorites { get; }

    /// <summary>Model keys, most recent first, at most <see cref="MaxRecents"/>.</summary>
    public IReadOnlyList<string> Recents { get; }

    /// <summary>Device ids (<see cref="Device.Id"/>).</summary>
    public IReadOnlySet<string> Hidden { get; }

    public bool IsFavorite(string modelKey) => Favorites.Contains(modelKey, StringComparer.Ordinal);

    public bool IsHidden(Device device) => Hidden.Contains(device.Id);

    /// <summary>Seeds the first-run favorites (only meaningful while <see cref="HasFavorites"/> is false).</summary>
    public DevicePreferences WithDefaultFavorites(IReadOnlyList<Device> available) =>
        new(DefaultFavorites(available), Recents, Hidden, favoritesInitialized: true);

    public DevicePreferences ToggleFavorite(string modelKey) => IsFavorite(modelKey)
        ? new(Favorites.Where(k => k != modelKey), Recents, Hidden, true)
        : new(Favorites.Append(modelKey), Recents, Hidden, true);

    /// <summary>Moves a model to the front of the recents, evicting the oldest beyond five.</summary>
    public DevicePreferences NoteUsed(string modelKey) =>
        new(Favorites, Recents.Where(k => k != modelKey).Prepend(modelKey), Hidden, HasFavorites);

    public DevicePreferences Hide(string id) => new(Favorites, Recents, Hidden.Append(id), HasFavorites);

    public DevicePreferences Unhide(string id) => new(Favorites, Recents, Hidden.Where(h => h != id), HasFavorites);

    /// <summary>The visible devices: everything not hidden.</summary>
    public IReadOnlyList<Device> Visible(IEnumerable<Device> devices) =>
        devices.Where(d => !IsHidden(d)).ToArray();

    /// <summary>Resolves a model key to the best instance among the given (already visible)
    /// devices, or null when this machine has no such device.</summary>
    public static Device? Resolve(string modelKey, IEnumerable<Device> available)
    {
        var sameModel = available.Where(d => d.ModelKey == modelKey).ToArray();
        return sameModel.Length == 0 ? null : DeviceCatalog.PickBest(sameModel);
    }

    /// <summary>The devices for a list of model keys, skipping keys with no device here.</summary>
    public static IReadOnlyList<Device> ResolveAll(IEnumerable<string> modelKeys, IReadOnlyList<Device> available) =>
        modelKeys.Select(k => Resolve(k, available)).OfType<Device>().ToArray();

    /// <summary>First-run favorites: this computer, the newest iPhone, the newest iPad and the
    /// first Android emulator. After this the user owns the list.</summary>
    public static IReadOnlyList<string> DefaultFavorites(IReadOnlyList<Device> available)
    {
        var keys = new List<string>();
        foreach (var host in available.Where(d => d.Platform == DevicePlatform.Host).Take(1))
            keys.Add(host.ModelKey);
        foreach (var family in new[] { DeviceFamily.Phone, DeviceFamily.Tablet })
        {
            var newest = available
                .Where(d => d.Platform == DevicePlatform.IosSimulator && d.Family == family)
                .OrderByDescending(d => d.ParsedRuntimeVersion)
                .ThenByDescending(d => d.Name, DeviceCatalog.NaturalOrder)   // newest generation within one runtime
                .FirstOrDefault();
            if (newest != null)
                keys.Add(newest.ModelKey);
        }

        var android = available.FirstOrDefault(d => d.Platform == DevicePlatform.AndroidEmulator);
        if (android != null)
            keys.Add(android.ModelKey);
        return keys;
    }

    public static string Serialize(IEnumerable<string> values) => string.Join('\n', values);

    public static IReadOnlyList<string> Deserialize(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
