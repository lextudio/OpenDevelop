using ICSharpCode.SharpDevelop.Designer.Devices;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class DeviceCatalogTests
{
    static Device Ios(string udid, string name, string version, DeviceState state = DeviceState.Shutdown) =>
        new(DevicePlatform.IosSimulator, "ios:" + udid, name,
            name.StartsWith("iPad") ? DeviceFamily.Tablet : DeviceFamily.Phone, "iOS", version, state);

    static Device Droid(string avd, DeviceFamily family = DeviceFamily.Phone) =>
        new(DevicePlatform.AndroidEmulator, "android:" + avd, avd, family, "Android", "35");

    static readonly Device[] All =
    [
        Device.Host,
        Ios("a1", "iPhone 16 Pro", "18.4"),
        Ios("a2", "iPhone 16 Pro", "26.1"),
        Ios("a3", "iPhone 9", "26.1"),
        Ios("a4", "iPad Air 11-inch", "26.1"),
        Droid("pixel_6"),
        Droid("pixel_tablet", DeviceFamily.Tablet),
    ];

    [Fact]
    public void SameModelAcrossRuntimesFoldsIntoOneEntry()
    {
        var phones = DeviceCatalog.Group(All).Single(g => g.Title == "iOS · iPhone");
        var pro = phones.Entries.Single(e => e.Best.Name == "iPhone 16 Pro");
        Assert.Equal("ios:a2", pro.Best.Id);
        Assert.Equal("ios:a1", pro.OtherRuntimes.Single().Id);
    }

    [Fact]
    public void ARunningInstanceBeatsANewerRuntime()
    {
        var best = DeviceCatalog.PickBest([Ios("old", "iPhone 16", "18.4", DeviceState.Booted), Ios("new", "iPhone 16", "26.1")]);
        Assert.Equal("ios:old", best.Id);
    }

    [Fact]
    public void GroupsAreOrderedAndNumbersSortNumerically()
    {
        var groups = DeviceCatalog.Group(All);
        Assert.Equal(["This computer", "iOS · iPhone", "iOS · iPad", "Android · Phone", "Android · Tablet"],
            groups.Select(g => g.Title).ToArray());
        Assert.Equal(["iPhone 9", "iPhone 16 Pro"], groups[1].Entries.Select(e => e.Best.Name).ToArray());
    }

    [Fact]
    public void SearchMatchesEveryWordAcrossNameAndRuntime()
    {
        Assert.True(DeviceCatalog.Matches(All[1], "16 pro"));
        Assert.True(DeviceCatalog.Matches(All[1], "ios 18"));
        Assert.False(DeviceCatalog.Matches(All[1], "pixel"));
        Assert.True(DeviceCatalog.Matches(All[5], "android"));
        Assert.True(DeviceCatalog.Matches(All[0], ""));
    }

    [Fact]
    public void ModelKeyIgnoresTheRuntimeSoFavoritesSurviveAnUpgrade()
    {
        Assert.Equal(All[1].ModelKey, All[2].ModelKey);
        Assert.Equal("ios:iphone-16-pro", All[1].ModelKey);
        Assert.NotEqual(All[1].Id, All[2].Id);
    }
}

public sealed class DevicePreferencesTests
{
    static Device Ios(string udid, string name, string version) =>
        new(DevicePlatform.IosSimulator, "ios:" + udid, name,
            name.StartsWith("iPad") ? DeviceFamily.Tablet : DeviceFamily.Phone, "iOS", version, DeviceState.Shutdown);

    [Fact]
    public void OnlyTheFiveMostRecentDevicesAreKept()
    {
        var prefs = DevicePreferences.Empty;
        foreach (var key in new[] { "a", "b", "c", "d", "e", "f" })
            prefs = prefs.NoteUsed(key);
        Assert.Equal(["f", "e", "d", "c", "b"], prefs.Recents);
        prefs = prefs.NoteUsed("c");
        Assert.Equal(["c", "f", "e", "d", "b"], prefs.Recents);
    }

    [Fact]
    public void FavoritesToggleAndKeepTheirOrder()
    {
        var prefs = DevicePreferences.Empty.ToggleFavorite("x").ToggleFavorite("y").ToggleFavorite("z");
        Assert.Equal(["x", "y", "z"], prefs.Favorites);
        prefs = prefs.ToggleFavorite("y");
        Assert.Equal(["x", "z"], prefs.Favorites);
        Assert.False(prefs.IsFavorite("y"));
        Assert.True(prefs.HasFavorites);
    }

    [Fact]
    public void AFavoriteResolvesToTheBestInstanceAfterARuntimeUpgrade()
    {
        var favorite = Ios("old", "iPhone 16", "18.4").ModelKey;
        Assert.Equal("ios:new", DevicePreferences.Resolve(favorite, [Ios("new", "iPhone 16", "26.1")])!.Id);
        Assert.Null(DevicePreferences.Resolve(favorite, []));
    }

    [Fact]
    public void HiddenDevicesAreNotVisible()
    {
        var a = Ios("a", "iPhone 16", "26.1");
        var b = Ios("b", "iPhone 15", "26.1");
        var prefs = DevicePreferences.Empty.Hide(a.Id);
        Assert.Equal([b], prefs.Visible([a, b]));
        Assert.Equal([a, b], prefs.Unhide(a.Id).Visible([a, b]));
    }

    [Fact]
    public void DefaultFavoritesAreHostNewestPhoneNewestTabletAndFirstEmulator()
    {
        Device[] all =
        [
            Device.Host,
            Ios("1", "iPhone 15", "17.2"), Ios("2", "iPhone 17 Pro", "26.1"),
            Ios("3", "iPad Pro 13-inch (M4)", "26.1"),
            new(DevicePlatform.AndroidEmulator, "android:pixel_6", "pixel_6", DeviceFamily.Phone, "Android", "35"),
        ];
        Assert.Equal(["host", "ios:iphone-17-pro", "ios:ipad-pro-13-inch-m4", "android:pixel_6"],
            DevicePreferences.DefaultFavorites(all));
    }

    [Fact]
    public void DefaultFavoritePhoneIsTheNewestGenerationWithinOneRuntime()
    {
        Device[] all = [Ios("1", "iPhone 16", "26.1"), Ios("2", "iPhone 16 Pro", "26.1"), Ios("3", "iPhone 17 Pro", "26.1")];
        Assert.Equal(["ios:iphone-17-pro"], DevicePreferences.DefaultFavorites(all));
    }

    [Fact]
    public void TheHostCountsAsRunning() => Assert.True(Device.Host.IsRunning);

    [Fact]
    public void SerializationRoundTrips()
    {
        var list = new[] { "host", "ios:iphone-16", "android:pixel_6" };
        Assert.Equal(list, DevicePreferences.Deserialize(DevicePreferences.Serialize(list)));
        Assert.Empty(DevicePreferences.Deserialize(null));
    }
}

public sealed class IosSimulatorProviderTests
{
    const string Json = """
    { "devices": {
      "com.apple.CoreSimulator.SimRuntime.iOS-26-1": [
        { "udid": "U1", "name": "iPhone 17 Pro", "state": "Booted", "isAvailable": true },
        { "udid": "U2", "name": "iPad Air 11-inch (M3)", "state": "Shutdown", "isAvailable": true },
        { "udid": "U3", "name": "iPhone 14 (clone 1)", "state": "Shutdown", "isAvailable": false },
        { "udid": "U4", "name": "Apple Vision", "state": "Shutdown", "isAvailable": true }
      ],
      "com.apple.CoreSimulator.SimRuntime.tvOS-26-0": [
        { "udid": "U5", "name": "Apple TV", "state": "Shutdown", "isAvailable": true }
      ] } }
    """;

    [Fact]
    public void ParsesIosPhonesAndTabletsOnly()
    {
        var devices = IosSimulatorProvider.Parse(Json);
        Assert.Equal(["ios:U1", "ios:U2"], devices.Select(d => d.Id).OrderBy(x => x).ToArray());
        var phone = devices.Single(d => d.Id == "ios:U1");
        Assert.Equal(DeviceState.Booted, phone.State);
        Assert.Equal("iOS 26.1", phone.RuntimeLabel);
        Assert.Equal(DeviceFamily.Tablet, devices.Single(d => d.Id == "ios:U2").Family);
    }

    [Fact]
    public async Task AHungSimctlTimesOutToAnEmptyList()
    {
        var provider = new IosSimulatorProvider(async token => { await Task.Delay(Timeout.Infinite, token); return null; },
            TimeSpan.FromMilliseconds(100));
        Assert.Empty(await provider.ListAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var provider = new IosSimulatorProvider(async token => { await Task.Delay(Timeout.Infinite, token); return null; },
            TimeSpan.FromMinutes(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListAsync(cts.Token));
    }

    [Fact]
    public async Task ListAsyncParsesWhateverSimctlPrinted()
    {
        var provider = new IosSimulatorProvider(_ => Task.FromResult<string?>(Json), TimeSpan.FromSeconds(5));
        Assert.Equal(2, (await provider.ListAsync()).Count);
        var failing = new IosSimulatorProvider(_ => Task.FromResult<string?>(null), TimeSpan.FromSeconds(5));
        Assert.Empty(await failing.ListAsync());
    }

    [Theory]
    [InlineData("com.apple.CoreSimulator.SimRuntime.iOS-26-1", "iOS", "26.1")]
    [InlineData("com.apple.CoreSimulator.SimRuntime.watchOS-10-4", "watchOS", "10.4")]
    public void ParsesRuntimeIdentifiers(string id, string family, string version) =>
        Assert.Equal((family, version), IosSimulatorProvider.ParseRuntime(id));
}

public sealed class DeviceServiceTests
{
    sealed class Fake(DevicePlatform platform, params Device[] devices) : IDeviceProvider
    {
        public string DisplayName => "fake";
        public DevicePlatform Platform => platform;
        public string? Unavailable => null;
        public Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Device>>(devices);
    }

    sealed class Broken : IDeviceProvider
    {
        public string DisplayName => "broken";
        public DevicePlatform Platform => DevicePlatform.AndroidEmulator;
        public string? Unavailable => null;
        public Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task RefreshMergesProvidersAndRaisesChangedOnlyOnChange()
    {
        var service = new DeviceService();
        var phone = new Device(DevicePlatform.IosSimulator, "ios:1", "iPhone 16", DeviceFamily.Phone, "iOS", "26.1");
        service.Register(new Fake(DevicePlatform.IosSimulator, phone));
        int raised = 0;
        service.Changed += (_, _) => raised++;

        var first = await service.RefreshAsync();
        await service.RefreshAsync();

        Assert.Equal([Device.Host, phone], first);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task ABrokenProviderDoesNotHideTheOthers()
    {
        var service = new DeviceService();
        var phone = new Device(DevicePlatform.IosSimulator, "ios:1", "iPhone 16", DeviceFamily.Phone, "iOS", "26.1");
        service.Register(new Broken());
        service.Register(new Fake(DevicePlatform.IosSimulator, phone));
        Assert.Equal([Device.Host, phone], await service.RefreshAsync());
    }

    sealed class Hung : IDeviceProvider
    {
        public string DisplayName => "hung";
        public DevicePlatform Platform => DevicePlatform.AndroidEmulator;
        public string? Unavailable => null;
        public Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken = default) =>
            new TaskCompletionSource<IReadOnlyList<Device>>().Task;
    }

    [Fact]
    public async Task AHungProviderTimesOutAndDoesNotBlockTheOthers()
    {
        var service = new DeviceService();
        var phone = new Device(DevicePlatform.IosSimulator, "ios:1", "iPhone 16", DeviceFamily.Phone, "iOS", "26.1");
        service.Register(new Hung());
        service.Register(new Fake(DevicePlatform.IosSimulator, phone));
        var devices = await service.RefreshAsync(providerTimeout: TimeSpan.FromMilliseconds(150))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([Device.Host, phone], devices);
    }

    [Fact]
    public async Task RegisteringAPlatformAgainReplacesTheProvider()
    {
        var service = new DeviceService();
        var a = new Device(DevicePlatform.IosSimulator, "ios:a", "iPhone A", DeviceFamily.Phone, "iOS", "26.1");
        var b = new Device(DevicePlatform.IosSimulator, "ios:b", "iPhone B", DeviceFamily.Phone, "iOS", "26.1");
        service.Register(new Fake(DevicePlatform.IosSimulator, a));
        service.Register(new Fake(DevicePlatform.IosSimulator, b));
        Assert.Equal([Device.Host, b], await service.RefreshAsync());
    }
}
