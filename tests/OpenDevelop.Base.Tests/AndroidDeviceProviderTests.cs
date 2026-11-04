using ICSharpCode.AndroidDeviceManager;
using ICSharpCode.SharpDevelop.Designer.Devices;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class AndroidDeviceProviderTests
{
    [Fact]
    public void AnAvdBecomesAPhoneWithItsApiLevel()
    {
        var device = AndroidDeviceProvider.ToDevice(new AvdInfo
        {
            Name = "maui_preview_35",
            Device = "pixel_6 (Google)",
            Target = "Google APIs (Google Inc.)",
            BasedOn = "Android API 35",
        });
        Assert.Equal("android:maui_preview_35", device.Id);
        Assert.Equal(DevicePlatform.AndroidEmulator, device.Platform);
        Assert.Equal(DeviceFamily.Phone, device.Family);
        Assert.Equal("maui_preview_35", device.Name);
        Assert.Equal("Android API 35", device.RuntimeLabel);
    }

    [Fact]
    public void WhenTheTextNamesNoApiLevelTheAvdConfigIsRead()
    {
        string dir = Path.Combine(Path.GetTempPath(), "avd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.ini"),
                "hw.device.name=pixel_6\nimage.sysdir.1=system-images/android-34/google_apis/arm64-v8a/\n");
            var device = AndroidDeviceProvider.ToDevice(new AvdInfo
            {
                Name = "p6", Path = dir, Target = "Google APIs (Google Inc.)", BasedOn = "Android 14.0 (\"UpsideDownCake\")",
            });
            Assert.Equal("34", device.RuntimeVersion);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Android 15.0 (API level 35)", "35")]
    [InlineData("Based on: Android API 34", "34")]
    [InlineData("Google APIs", "")]
    public void TheApiLevelIsReadFromTheTargetText(string target, string api)
    {
        var device = AndroidDeviceProvider.ToDevice(new AvdInfo { Name = "x", Target = target });
        Assert.Equal(api, device.RuntimeVersion);
    }

    [Fact]
    public void ATabletProfileOrNameIsATablet()
    {
        Assert.Equal(DeviceFamily.Tablet,
            AndroidDeviceProvider.ToDevice(new AvdInfo { Name = "x", Device = "pixel_tablet" }).Family);
        Assert.Equal(DeviceFamily.Tablet,
            AndroidDeviceProvider.ToDevice(new AvdInfo { Name = "My_Tablet_API_34", Device = "pixel_6" }).Family);
    }
}

public sealed class AndroidEmulatorProcessTests
{
    [Fact]
    public void OnlyOnlineEmulatorSerialsAreListed()
    {
        const string output = "List of devices attached\nemulator-5554\tdevice\nemulator-5556\toffline\nR58M123\tdevice\n\n";
        Assert.Equal(["emulator-5554"], AndroidDeviceProvider.ParseEmulatorSerials(output));
    }

    [Fact]
    public void AnOfflineEmulatorIsUnidentifiable()
    {
        Assert.True(AndroidDeviceProvider.HasUnidentifiableEmulator("List of devices attached\nemulator-5556\toffline\n"));
        Assert.False(AndroidDeviceProvider.HasUnidentifiableEmulator("List of devices attached\nemulator-5554\tdevice\nR58M123\toffline\n"));
    }

    [Fact]
    public void TheAvdNameIsTheFirstLineBeforeOk()
    {
        Assert.Equal("maui_preview_35", AndroidDeviceProvider.ParseAvdName("maui_preview_35\r\nOK\r\n"));
        Assert.Null(AndroidDeviceProvider.ParseAvdName("OK\n"));
        Assert.Null(AndroidDeviceProvider.ParseAvdName("KO: unknown command\r\n"));
        Assert.Null(AndroidDeviceProvider.ParseAvdName(null));
    }

    [Fact]
    public void ARunningAvdIsBooted()
    {
        var avd = new AvdInfo { Name = "a", Device = "pixel_6", BasedOn = "Android API 35" };
        Assert.Equal(DeviceState.Booted, AndroidDeviceProvider.ToDevice(avd, true).State);
        Assert.Equal(DeviceState.Shutdown, AndroidDeviceProvider.ToDevice(avd, false).State);
    }
}

public sealed class AndroidDeviceSafetyTests
{
    static readonly Device Avd = new(DevicePlatform.AndroidEmulator, "android:pixel", "pixel", DeviceFamily.Phone, "Android API", "35");

    static AndroidDeviceProvider Provider(bool adbInstalled, Func<string, string?> adb, List<string> deleted, List<string>? adbCalls = null) =>
        new(
            () => "/sdk",
            _ => adbInstalled,
            (_, args, _) =>
            {
                adbCalls?.Add(args);
                return Task.FromResult(adb(args));
            },
            _ => Task.FromResult<IReadOnlyList<AvdInfo>>([new AvdInfo { Name = "pixel", Device = "pixel_6", BasedOn = "Android API 35" }]),
            (_, name) =>
            {
                deleted.Add(name);
                return Task.FromResult(true);
            });

    static string? RunningPixel(string args) => args switch
    {
        "devices" => "List of devices attached\nemulator-5554\tdevice\n",
        "-s emulator-5554 emu avd name" => "pixel\r\nOK\r\n",
        _ => "OK\r\n",
    };

    [Fact]
    public async Task DeleteIsRefusedWhileTheEmulatorRuns()
    {
        var deleted = new List<string>();
        var result = await Provider(true, RunningPixel, deleted).DeleteAsync(Avd, TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Contains("Stop the emulator", result.Error);
        Assert.Empty(deleted);
    }

    [Fact]
    public async Task DeleteFailsClosedWhenAdbIsInstalledButCannotAnswer()
    {
        var deleted = new List<string>();
        var result = await Provider(true, _ => null, deleted).DeleteAsync(Avd, TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Empty(deleted);
    }

    [Fact]
    public async Task DeleteFailsClosedWhenARunningEmulatorCannotBeIdentified()
    {
        var deleted = new List<string>();
        var provider = Provider(true, args => args == "devices" ? "emulator-5554\tdevice\n" : "KO: no\n", deleted);
        Assert.False((await provider.DeleteAsync(Avd, TestContext.Current.CancellationToken)).Ok);
        Assert.Empty(deleted);
    }

    [Fact]
    public async Task DeleteIsRefusedWhileAnEmulatorIsStillBooting()
    {
        var deleted = new List<string>();
        var provider = Provider(true, args => args == "devices" ? "List of devices attached\nemulator-5554\toffline\n" : null, deleted);
        var result = await provider.DeleteAsync(Avd, TestContext.Current.CancellationToken);
        Assert.False(result.Ok);
        Assert.Empty(deleted);
        Assert.Equal(DeviceState.Unknown, (await provider.ListAsync(TestContext.Current.CancellationToken)).Single().State);
    }

    [Fact]
    public async Task DeleteProceedsWhenAdbIsNotInstalled()
    {
        var deleted = new List<string>();
        var result = await Provider(false, _ => null, deleted).DeleteAsync(Avd, TestContext.Current.CancellationToken);
        Assert.True(result.Ok);
        Assert.Equal(["pixel"], deleted);
    }

    [Fact]
    public async Task DeleteProceedsWhenTheEmulatorIsStopped()
    {
        var deleted = new List<string>();
        var result = await Provider(true, args => args == "devices" ? "List of devices attached\n\n" : null, deleted).DeleteAsync(Avd, TestContext.Current.CancellationToken);
        Assert.True(result.Ok);
        Assert.Equal(["pixel"], deleted);
    }

    [Fact]
    public async Task ShutdownFailsWhenAdbIsMissingOrCannotList()
    {
        var missing = await Provider(false, _ => null, []).ShutdownAsync(Avd, TestContext.Current.CancellationToken);
        Assert.False(missing.Ok);
        Assert.Contains("not installed", missing.Error);

        var broken = await Provider(true, _ => null, []).ShutdownAsync(Avd, TestContext.Current.CancellationToken);
        Assert.False(broken.Ok);
    }

    [Fact]
    public async Task ShutdownKillsTheMatchingSerialAndIsAlreadyDoneWhenStopped()
    {
        var calls = new List<string>();
        Assert.True((await Provider(true, RunningPixel, [], calls).ShutdownAsync(Avd, TestContext.Current.CancellationToken)).Ok);
        Assert.Contains("-s emulator-5554 emu kill", calls);

        var stoppedCalls = new List<string>();
        var stopped = await Provider(true, args => args == "devices" ? "List of devices attached\n" : null, [], stoppedCalls)
            .ShutdownAsync(Avd, TestContext.Current.CancellationToken);
        Assert.True(stopped.Ok);
        Assert.DoesNotContain(stoppedCalls, c => c.Contains("emu kill"));
    }

    [Fact]
    public async Task ListReportsRunningStoppedAndUnknown()
    {
        var running = await Provider(true, RunningPixel, []).ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeviceState.Booted, running.Single().State);

        var stopped = await Provider(true, args => args == "devices" ? "List of devices attached\n" : null, []).ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeviceState.Shutdown, stopped.Single().State);

        var unknown = await Provider(true, _ => null, []).ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeviceState.Unknown, unknown.Single().State);

        var noAdb = await Provider(false, _ => null, []).ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeviceState.Unknown, noAdb.Single().State);
    }
}
