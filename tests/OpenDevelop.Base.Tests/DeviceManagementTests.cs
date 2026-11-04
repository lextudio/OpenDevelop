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
