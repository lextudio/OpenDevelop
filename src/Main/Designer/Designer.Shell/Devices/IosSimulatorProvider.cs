using System.Diagnostics;
using System.Text.Json;

namespace ICSharpCode.SharpDevelop.Designer.Devices;

/// <summary>iOS Simulator devices from <c>xcrun simctl list devices available -j</c>. Only iOS
/// runtimes with an iPhone or iPad are returned; clones and unavailable devices are dropped
/// because their names are not stable targets.</summary>
public sealed class IosSimulatorProvider : IDeviceProvider
{
    const string Xcrun = "/usr/bin/xcrun";

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    readonly Func<CancellationToken, Task<string?>> runSimctl;
    readonly TimeSpan timeout;

    readonly bool realTool;

    public IosSimulatorProvider() : this(RunSimctlAsync, Timeout)
    {
        realTool = true;
    }

    /// <summary>For tests: supplies the simctl JSON (null when it failed) and the time limit.</summary>
    public IosSimulatorProvider(Func<CancellationToken, Task<string?>> runSimctl, TimeSpan timeout)
    {
        this.runSimctl = runSimctl;
        this.timeout = timeout;
    }

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

    /// <summary>Runs <c>xcrun simctl list devices available -j</c>; returns its JSON, or null when
    /// it failed. Both pipes drain concurrently (a full stderr pipe cannot stall the child) and the
    /// whole process tree is killed when the token fires.</summary>
    static async Task<string?> RunSimctlAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(Xcrun, "simctl list devices available -j")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo);
        if (process == null)
            return null;

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string json = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return process.ExitCode == 0 ? json : null;
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
