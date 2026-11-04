using System.Diagnostics;
using System.Text.Json;

namespace ICSharpCode.SharpDevelop.Designer.Devices;

/// <summary>iOS Simulator devices from <c>xcrun simctl list devices available -j</c>. Only iOS
/// runtimes with an iPhone or iPad are returned; clones and unavailable devices are dropped
/// because their names are not stable targets.</summary>
public sealed class IosSimulatorProvider : IDeviceProvider, IDeviceManagement, IDeviceCreation
{
    const string Xcrun = "/usr/bin/xcrun";

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    readonly Func<CancellationToken, Task<string?>> runSimctl;
    readonly Func<IReadOnlyList<string>, CancellationToken, Task<SimctlResult>> runAction;
    readonly TimeSpan timeout;

    readonly bool realTool;

    public IosSimulatorProvider() : this(RunSimctlAsync, Timeout)
    {
        realTool = true;
    }

    /// <summary>For tests: supplies the simctl JSON (null when it failed) and the time limit.</summary>
    public IosSimulatorProvider(Func<CancellationToken, Task<string?>> runSimctl, TimeSpan timeout)
        : this(runSimctl, timeout, RunSimctlArgsAsync)
    {
    }

    /// <summary>For tests: also supplies how an action (boot, create, ...) runs simctl.</summary>
    public IosSimulatorProvider(
        Func<CancellationToken, Task<string?>> runSimctl,
        TimeSpan timeout,
        Func<IReadOnlyList<string>, CancellationToken, Task<SimctlResult>> runAction)
    {
        this.runSimctl = runSimctl;
        this.timeout = timeout;
        this.runAction = runAction;
    }

    /// <summary>What a simctl invocation produced.</summary>
    public sealed record SimctlResult(int ExitCode, string Output, string Error);

    public string DisplayName => "iOS Simulator";

    public DevicePlatform Platform => DevicePlatform.IosSimulator;

    public string? Unavailable =>
        !OperatingSystem.IsMacOS() ? "iOS simulators need macOS."
        : !File.Exists(Xcrun) ? "Xcode command line tools are not installed (xcode-select --install)."
        : null;

    public async Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (realTool && Unavailable != null)
            return [];
        try
        {
            // Bound the call: a hung simctl must not block the device refresh forever.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            string? json = await runSimctl(limit.Token).ConfigureAwait(false);
            return json == null ? [] : Parse(json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return []; // simctl timed out
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    static async Task<string?> RunSimctlAsync(CancellationToken cancellationToken)
    {
        var result = await RunSimctlArgsAsync(["list", "devices", "available", "-j"], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.Output : null;
    }

    /// <summary>Runs <c>xcrun simctl &lt;args&gt;</c>. Both pipes drain concurrently (a full stderr
    /// pipe cannot stall the child) and the whole process tree is killed when the token fires.</summary>
    static async Task<SimctlResult> RunSimctlArgsAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(Xcrun)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("simctl");
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo);
        if (process == null)
            return new SimctlResult(-1, "", "simctl could not be started.");

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new SimctlResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            }

            // Observe both reads so an abandoned one cannot surface as an unobserved exception.
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (Exception) { }
        }
    }

    static string Udid(Device device) => device.Id.StartsWith("ios:", StringComparison.Ordinal) ? device.Id[4..] : device.Id;

    /// <summary>First non-empty line of simctl's complaint, which is the part worth showing.</summary>
    static string Reason(SimctlResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return line ?? $"simctl exited with code {result.ExitCode}.";
    }

    async Task<DeviceActionResult> ActionAsync(IReadOnlyList<string> args, string? okWhenErrorContains, CancellationToken cancellationToken)
    {
        if (realTool && Unavailable is { } unavailable)
            return DeviceActionResult.Failure(unavailable);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var result = await runAction(args, limit.Token).ConfigureAwait(false);
            if (result.ExitCode == 0)
                return DeviceActionResult.Success;
            // "Unable to boot device in current state: Booted" means the wish is already true.
            if (okWhenErrorContains != null && result.Error.Contains(okWhenErrorContains, StringComparison.Ordinal))
                return DeviceActionResult.Success;
            return DeviceActionResult.Failure(Reason(result));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DeviceActionResult.Failure("simctl did not answer in time.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DeviceActionResult.Failure(exception.Message);
        }
    }

    public Task<DeviceActionResult> BootAsync(Device device, CancellationToken cancellationToken = default) =>
        ActionAsync(["boot", Udid(device)], "current state: Booted", cancellationToken);

    public Task<DeviceActionResult> ShutdownAsync(Device device, CancellationToken cancellationToken = default) =>
        ActionAsync(["shutdown", Udid(device)], "current state: Shutdown", cancellationToken);

    public Task<DeviceActionResult> DeleteAsync(Device device, CancellationToken cancellationToken = default) =>
        ActionAsync(["delete", Udid(device)], null, cancellationToken);

    public async Task<DeviceActionResult> CreateAsync(string name, string deviceTypeId, string runtimeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return DeviceActionResult.Failure("Give the simulator a name.");
        return await ActionAsync(["create", name.Trim(), deviceTypeId, runtimeId], null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceCreateOptions> GetCreateOptionsAsync(CancellationToken cancellationToken = default)
    {
        if (realTool && Unavailable != null)
            return new DeviceCreateOptions([], []);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            var types = await runAction(["list", "devicetypes", "-j"], limit.Token).ConfigureAwait(false);
            var runtimes = await runAction(["list", "runtimes", "-j"], limit.Token).ConfigureAwait(false);
            return new DeviceCreateOptions(
                types.ExitCode == 0 ? ParseDeviceTypes(types.Output) : [],
                runtimes.ExitCode == 0 ? ParseRuntimes(runtimes.Output) : []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new DeviceCreateOptions([], []);
        }
    }

    /// <summary>iPhone and iPad models from <c>simctl list devicetypes -j</c>, newest-named first.</summary>
    public static IReadOnlyList<DeviceTypeOption> ParseDeviceTypes(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("devicetypes", out var types) || types.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<DeviceTypeOption>();
        foreach (var type in types.EnumerateArray())
        {
            string id = type.TryGetProperty("identifier", out var i) ? i.GetString() ?? "" : "";
            string name = type.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            bool phone = name.StartsWith("iPhone", StringComparison.Ordinal);
            bool tablet = name.StartsWith("iPad", StringComparison.Ordinal);
            if (id.Length > 0 && (phone || tablet))
                result.Add(new DeviceTypeOption(id, name, tablet ? DeviceFamily.Tablet : DeviceFamily.Phone));
        }

        // Phones before tablets, then the highest generation number first ("iPhone 17" before
        // "iPhone 9": ordinal name order would get that wrong), then by name.
        return result
            .OrderByDescending(t => t.Family == DeviceFamily.Phone)
            .ThenByDescending(t => Generation(t.Name))
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();
    }

    static int Generation(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"\d+");
        return match.Success && int.TryParse(match.Value, out var number) ? number : 0;
    }

    /// <summary>Available iOS runtimes from <c>simctl list runtimes -j</c>, newest first.</summary>
    public static IReadOnlyList<RuntimeOption> ParseRuntimes(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("runtimes", out var runtimes) || runtimes.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<RuntimeOption>();
        foreach (var runtime in runtimes.EnumerateArray())
        {
            string id = runtime.TryGetProperty("identifier", out var i) ? i.GetString() ?? "" : "";
            string name = runtime.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            bool available = !runtime.TryGetProperty("isAvailable", out var a) || a.ValueKind != JsonValueKind.False;
            var (family, version) = ParseRuntime(id);
            if (id.Length > 0 && available && family == "iOS")
                result.Add(new RuntimeOption(id, name.Length > 0 ? name : "iOS " + version, version));
        }

        return result.OrderByDescending(r => Version.TryParse(r.Version, out var v) ? v : new Version(0, 0)).ToArray();
    }

    public static IReadOnlyList<Device> Parse(string simctlListJson)
    {
        if (string.IsNullOrWhiteSpace(simctlListJson))
            return [];

        using var document = JsonDocument.Parse(simctlListJson);
        if (!document.RootElement.TryGetProperty("devices", out var runtimes) || runtimes.ValueKind != JsonValueKind.Object)
            return [];

        var devices = new List<Device>();
        foreach (var runtime in runtimes.EnumerateObject())
        {
            var (family, version) = ParseRuntime(runtime.Name);
            if (family != "iOS" || runtime.Value.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var entry in runtime.Value.EnumerateArray())
            {
                string udid = entry.GetProperty("udid").GetString() ?? "";
                string name = entry.GetProperty("name").GetString() ?? "";
                string state = entry.GetProperty("state").GetString() ?? "";
                bool unavailable = entry.TryGetProperty("isAvailable", out var available)
                    && available.ValueKind == JsonValueKind.False;
                bool phone = name.StartsWith("iPhone", StringComparison.Ordinal);
                bool tablet = name.StartsWith("iPad", StringComparison.Ordinal);
                if (udid.Length == 0 || unavailable || !(phone || tablet))
                    continue;
                devices.Add(new Device(DevicePlatform.IosSimulator, "ios:" + udid, name,
                    tablet ? DeviceFamily.Tablet : DeviceFamily.Phone, "iOS", version, ParseState(state)));
            }
        }

        return devices
            .OrderByDescending(d => d.ParsedRuntimeVersion)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
    }

    static DeviceState ParseState(string state) => state switch
    {
        "Booted" => DeviceState.Booted,
        "Booting" => DeviceState.Booting,
        "Shutdown" => DeviceState.Shutdown,
        _ => DeviceState.Unknown,
    };

    /// <summary>"com.apple.CoreSimulator.SimRuntime.iOS-26-1" → ("iOS", "26.1").</summary>
    public static (string Family, string Version) ParseRuntime(string runtimeIdentifier)
    {
        const string Marker = "SimRuntime.";
        int index = runtimeIdentifier.LastIndexOf(Marker, StringComparison.Ordinal);
        string tail = index < 0 ? runtimeIdentifier : runtimeIdentifier[(index + Marker.Length)..];
        int dash = tail.IndexOf('-');
        if (dash <= 0)
            return (tail, "0");
        return (tail[..dash], tail[(dash + 1)..].Replace('-', '.'));
    }
}
