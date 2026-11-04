using ICSharpCode.SharpDevelop.Designer.Devices;

using Xunit;

using static ICSharpCode.SharpDevelop.Designer.Devices.IosSimulatorProvider;

namespace OpenDevelop.Base.Tests;

public sealed class IosSimulatorManagementTests
{
    static readonly Device Phone = new(DevicePlatform.IosSimulator, "ios:ABC-123", "iPhone 17", DeviceFamily.Phone, "iOS", "26.1", DeviceState.Shutdown);

    static IosSimulatorProvider Provider(List<string> calls, Func<IReadOnlyList<string>, SimctlResult> answer) =>
        new(_ => Task.FromResult<string?>(null), TimeSpan.FromSeconds(5), (args, _) =>
        {
            calls.Add(string.Join(" ", args));
            return Task.FromResult(answer(args));
        });

    [Fact]
    public async Task BootShutdownAndDeleteSendTheUdid()
    {
        var calls = new List<string>();
        var provider = Provider(calls, _ => new SimctlResult(0, "", ""));
        Assert.True((await provider.BootAsync(Phone, TestContext.Current.CancellationToken)).Ok);
        Assert.True((await provider.ShutdownAsync(Phone, TestContext.Current.CancellationToken)).Ok);
        Assert.True((await provider.DeleteAsync(Phone, TestContext.Current.CancellationToken)).Ok);
        Assert.Equal(["boot ABC-123", "shutdown ABC-123", "delete ABC-123"], calls);
    }

    [Fact]
    public async Task BootingABootedDeviceIsAlreadyTrue()
    {
        var provider = Provider([], _ => new SimctlResult(149, "", "An error was encountered processing the command (domain=NSPOSIXErrorDomain, code=149):\nUnable to boot device in current state: Booted"));
        Assert.True((await provider.BootAsync(Phone, TestContext.Current.CancellationToken)).Ok);
    }

    [Fact]
    public async Task AFailureCarriesTheFirstLineOfTheReason()
    {
        var provider = Provider([], _ => new SimctlResult(1, "", "Invalid device: ABC-123\nsecond line"));
        var result = await provider.DeleteAsync(Phone, TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Equal("Invalid device: ABC-123", result.Error);
    }

    [Fact]
    public async Task CreateNeedsANameAndPassesTypeAndRuntime()
    {
        var calls = new List<string>();
        var provider = Provider(calls, _ => new SimctlResult(0, "NEW-UDID\n", ""));
        Assert.False((await provider.CreateAsync("  ", "type", "rt", TestContext.Current.CancellationToken)).Ok);
        Assert.Empty(calls);
        Assert.True((await provider.CreateAsync(" My Phone ", "com.apple.CoreSimulator.SimDeviceType.iPhone-17", "com.apple.CoreSimulator.SimRuntime.iOS-26-1", TestContext.Current.CancellationToken)).Ok);
        Assert.Equal(["create My Phone com.apple.CoreSimulator.SimDeviceType.iPhone-17 com.apple.CoreSimulator.SimRuntime.iOS-26-1"], calls);
    }

    [Fact]
    public async Task ATimeoutBecomesAFailureButCallerCancellationPropagates()
    {
        var hang = new IosSimulatorProvider(
            _ => Task.FromResult<string?>(null),
            TimeSpan.FromMilliseconds(50),
            async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new SimctlResult(0, "", "");
            });
        var timedOut = await hang.BootAsync(Phone, TestContext.Current.CancellationToken);
        Assert.False(timedOut.Ok);
        Assert.Contains("in time", timedOut.Error);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hang.BootAsync(Phone, cancelled.Token));
    }

    [Fact]
    public void DeviceTypesKeepIPhonesAndIPadsPhonesFirst()
    {
        const string json = """
            {"devicetypes":[
              {"name":"Apple Watch Ultra","identifier":"w"},
              {"name":"iPad Pro 13-inch","identifier":"ipad"},
              {"name":"iPhone 16","identifier":"p16"},
              {"name":"iPhone 9","identifier":"p9"},
              {"name":"iPhone 17","identifier":"p17"}]}
            """;
        var types = ParseDeviceTypes(json);
        Assert.Equal(["p17", "p16", "p9", "ipad"], types.Select(t => t.Id));
        Assert.Equal(DeviceFamily.Tablet, types[3].Family);
    }

    [Fact]
    public async Task TheReasonIsFoundEvenWhenHeaderAndReasonAreOnDifferentStreams()
    {
        var provider = Provider([], _ => new SimctlResult(1, "Incompatible device", "An error was encountered processing the command (code=403):"));
        // The header ends in a colon but nothing follows it on stderr; stdout carries the reason.
        Assert.Equal("Incompatible device", (await provider.CreateAsync("n", "t", "r", TestContext.Current.CancellationToken)).Error);
    }

    [Fact]
    public async Task TheReasonIsTheLineAfterAnErrorHeader()
    {
        var provider = Provider([], _ => new SimctlResult(1, "", "An error was encountered processing the command (domain=com.apple.CoreSimulator.SimError, code=403):\nIncompatible device\nUnable to create a device for device type: x"));
        Assert.Equal("Incompatible device", (await provider.CreateAsync("n", "t", "r", TestContext.Current.CancellationToken)).Error);
    }

    [Fact]
    public void ACustomNamedSimulatorIsStillAnIPhoneOrIPad()
    {
        const string json = """
            {"devices":{"com.apple.CoreSimulator.SimRuntime.iOS-26-1":[
              {"udid":"A","name":"My Phone","state":"Shutdown","isAvailable":true,"deviceTypeIdentifier":"com.apple.CoreSimulator.SimDeviceType.iPhone-17"},
              {"udid":"B","name":"Lab slate","state":"Shutdown","isAvailable":true,"deviceTypeIdentifier":"com.apple.CoreSimulator.SimDeviceType.iPad-Pro-11-inch-M5-8GB"},
              {"udid":"C","name":"Watch","state":"Shutdown","isAvailable":true,"deviceTypeIdentifier":"com.apple.CoreSimulator.SimDeviceType.Apple-Watch-Series-10-46mm"}]}}
            """;
        var devices = IosSimulatorProvider.Parse(json);
        Assert.Equal(["My Phone", "Lab slate"], devices.Select(d => d.Name).OrderByDescending(n => n));
        Assert.Equal(DeviceFamily.Tablet, devices.Single(d => d.Name == "Lab slate").Family);
    }

    [Fact]
    public void RuntimesRememberWhichModelsTheySupport()
    {
        const string json = """
            {"runtimes":[
              {"identifier":"com.apple.CoreSimulator.SimRuntime.iOS-26-5","name":"iOS 26.5","isAvailable":true,
               "supportedDeviceTypes":[{"identifier":"com.apple.CoreSimulator.SimDeviceType.iPhone-17"}]},
              {"identifier":"com.apple.CoreSimulator.SimRuntime.iOS-17-2","name":"iOS 17.2","isAvailable":true}]}
            """;
        var runtimes = ParseRuntimes(json);
        Assert.True(runtimes[0].Supports("com.apple.CoreSimulator.SimDeviceType.iPhone-17"));
        Assert.False(runtimes[0].Supports("com.apple.CoreSimulator.SimDeviceType.iPhone-18-Pro"));
        // No support list: unknown, so allowed (simctl decides and its reason is shown).
        Assert.True(runtimes[1].Supports("anything"));
    }

    [Fact]
    public void RuntimesKeepAvailableIosNewestFirst()
    {
        const string json = """
            {"runtimes":[
              {"identifier":"com.apple.CoreSimulator.SimRuntime.iOS-18-6","name":"iOS 18.6","isAvailable":true},
              {"identifier":"com.apple.CoreSimulator.SimRuntime.iOS-26-1","name":"iOS 26.1","isAvailable":true},
              {"identifier":"com.apple.CoreSimulator.SimRuntime.iOS-17-0","name":"iOS 17.0","isAvailable":false},
              {"identifier":"com.apple.CoreSimulator.SimRuntime.watchOS-11-0","name":"watchOS 11.0","isAvailable":true}]}
            """;
        Assert.Equal(["iOS 26.1", "iOS 18.6"], ParseRuntimes(json).Select(r => r.Name));
    }

    [Fact]
    public async Task OptionsComeFromTheTwoListCommands()
    {
        var provider = Provider([], args => args[1] == "devicetypes"
            ? new SimctlResult(0, """{"devicetypes":[{"name":"iPhone 17","identifier":"p17"}]}""", "")
            : new SimctlResult(0, """{"runtimes":[{"identifier":"com.apple.CoreSimulator.SimRuntime.iOS-26-1","name":"iOS 26.1"}]}""", ""));
        var options = await provider.GetCreateOptionsAsync(TestContext.Current.CancellationToken);
        Assert.Single(options.Types);
        Assert.Single(options.Runtimes);
    }
}

public sealed class DeviceManagerModelTests
{
    sealed class FakeProvider(DevicePlatform platform, params Device[] devices) : IDeviceProvider, IDeviceManagement
    {
        public List<string> Calls { get; } = [];

        public string DisplayName => "fake";

        public DevicePlatform Platform { get; } = platform;

        public string? Unavailable => null;

        public Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Device>>(devices);

        public Task<DeviceActionResult> BootAsync(Device device, CancellationToken cancellationToken = default)
        {
            Calls.Add("boot " + device.Id);
            return Task.FromResult(DeviceActionResult.Success);
        }

        public Task<DeviceActionResult> ShutdownAsync(Device device, CancellationToken cancellationToken = default)
        {
            Calls.Add("shutdown " + device.Id);
            return Task.FromResult(DeviceActionResult.Success);
        }

        public Task<DeviceActionResult> DeleteAsync(Device device, CancellationToken cancellationToken = default)
        {
            Calls.Add("delete " + device.Id);
            return Task.FromResult(DeviceActionResult.Failure("nope"));
        }
    }

    static Device Ios(string id, DeviceState state) =>
        new(DevicePlatform.IosSimulator, id, id, DeviceFamily.Phone, "iOS", "26.1", state);

    [Fact]
    public async Task ActionsFollowTheDeviceState()
    {
        var stopped = Ios("ios:a", DeviceState.Shutdown);
        var running = Ios("ios:b", DeviceState.Booted);
        var starting = Ios("ios:c", DeviceState.Booting);
        var unknown = Ios("ios:d", DeviceState.Unknown);
        var service = new DeviceService();
        service.Register(new FakeProvider(DevicePlatform.IosSimulator, stopped, running, starting, unknown));
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        var model = new DeviceManagerModel(service);

        var rows = model.Rows(service.Devices, DevicePreferences.Empty).ToDictionary(r => r.Device.Id);
        Assert.True(rows["ios:a"].CanStart && !rows["ios:a"].CanStop && rows["ios:a"].CanDelete);
        Assert.True(!rows["ios:b"].CanStart && rows["ios:b"].CanStop && !rows["ios:b"].CanDelete);
        Assert.True(!rows["ios:c"].CanStart && rows["ios:c"].CanStop && !rows["ios:c"].CanDelete);
        Assert.True(rows["ios:d"].CanStart && !rows["ios:d"].CanStop && rows["ios:d"].CanDelete);
        Assert.Equal("Running", rows["ios:b"].StateText);
        Assert.Equal("Starting…", rows["ios:c"].StateText);
    }

    [Fact]
    public async Task TheHostComputerCanBeNeitherManagedNorHidden()
    {
        var service = new DeviceService();
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        var row = new DeviceManagerModel(service).Rows(service.Devices, DevicePreferences.Empty).Single(r => r.Device.Platform == DevicePlatform.Host);
        Assert.False(row.CanStart || row.CanStop || row.CanDelete || row.CanHide);
        Assert.Equal("This computer", row.StateText);
    }

    [Fact]
    public async Task HiddenDevicesAreListedButFlagged()
    {
        var a = Ios("ios:a", DeviceState.Shutdown);
        var service = new DeviceService();
        service.Register(new FakeProvider(DevicePlatform.IosSimulator, a));
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        var rows = new DeviceManagerModel(service).Rows(service.Devices, DevicePreferences.Empty.Hide("ios:a"));
        Assert.True(rows.Single(r => r.Device.Id == "ios:a").Hidden);
        Assert.True(rows.Single(r => r.Device.Id == "ios:a").CanHide);
    }

    sealed class ListOnlyProvider(params Device[] devices) : IDeviceProvider
    {
        public string DisplayName => "list only";

        public DevicePlatform Platform => DevicePlatform.AndroidEmulator;

        public string? Unavailable => null;

        public Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Device>>(devices);
    }

    [Fact]
    public async Task ADeviceWhoseProviderCannotManageItStillCanBeHidden()
    {
        var avd = new Device(DevicePlatform.AndroidEmulator, "android:a", "a", DeviceFamily.Phone, "Android API", "35", DeviceState.Shutdown);
        var service = new DeviceService();
        service.Register(new ListOnlyProvider(avd));
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        var row = new DeviceManagerModel(service).Rows(service.Devices, DevicePreferences.Empty).Single(r => r.Device.Id == "android:a");
        Assert.False(row.CanStart || row.CanStop || row.CanDelete);
        Assert.True(row.CanHide);
    }

    [Fact]
    public async Task RowsListTheHostFirstThenNewestRuntimes()
    {
        var service = new DeviceService();
        service.Register(new FakeProvider(
            DevicePlatform.IosSimulator,
            new Device(DevicePlatform.IosSimulator, "ios:old", "iPhone 15", DeviceFamily.Phone, "iOS", "17.2", DeviceState.Shutdown),
            new Device(DevicePlatform.IosSimulator, "ios:new", "iPhone 17", DeviceFamily.Phone, "iOS", "26.1", DeviceState.Shutdown)));
        await service.RefreshAsync(TestContext.Current.CancellationToken);
        var rows = new DeviceManagerModel(service).Rows(service.Devices, DevicePreferences.Empty);
        Assert.Equal(["host", "ios:new", "ios:old"], rows.Select(r => r.Device.Id));
    }

    [Fact]
    public async Task ACreationThatThrowsBecomesAFailedResult()
    {
        var service = new DeviceService();
        service.Register(new ThrowingCreator());
        var result = await new DeviceManagerModel(service).CreateAsync(DevicePlatform.IosSimulator, "n", "t", "r", TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Equal("boom", result.Error);
    }

    sealed class ThrowingCreator : IDeviceProvider, IDeviceCreation
    {
        public string DisplayName => "throwing";

        public DevicePlatform Platform => DevicePlatform.IosSimulator;

        public string? Unavailable => null;

        public Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Device>>([]);

        public Task<DeviceCreateOptions> GetCreateOptionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceCreateOptions([], []));

        public Task<DeviceActionResult> CreateAsync(string name, string deviceTypeId, string runtimeId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task ExecuteRoutesToTheProviderAndRefreshes()
    {
        var a = Ios("ios:a", DeviceState.Shutdown);
        var provider = new FakeProvider(DevicePlatform.IosSimulator, a);
        var service = new DeviceService();
        service.Register(provider);
        var refreshed = 0;
        service.Changed += (_, _) => refreshed++;
        var model = new DeviceManagerModel(service);

        Assert.True((await model.ExecuteAsync(DeviceAction.Start, a, TestContext.Current.CancellationToken)).Ok);
        var failed = await model.ExecuteAsync(DeviceAction.Delete, a, TestContext.Current.CancellationToken);
        Assert.False(failed.Ok);
        Assert.Equal("nope", failed.Error);
        Assert.Equal(["boot ios:a", "delete ios:a"], provider.Calls);
        Assert.True(refreshed >= 1);
    }

    [Fact]
    public async Task APlatformWithoutManagementReportsWhyInsteadOfThrowing()
    {
        var service = new DeviceService();
        var result = await new DeviceManagerModel(service).ExecuteAsync(DeviceAction.Start, Ios("ios:z", DeviceState.Shutdown), TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Contains("cannot be managed", result.Error);
    }
}
