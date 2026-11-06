using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Debugging a net11.0-ios app in the iOS Simulator (Debugger.AddIn's IosSimulatorProjectBehavior, see
/// doc/technotes/uno-mobile-designer.md section 4c): build for iossimulator-arm64, install, launch stopped, attach
/// SharpDbg with the app's own retargeted mscordbi, stop at a breakpoint in FinishedLaunching.
///
/// Needs macOS, an SDK 11 root that carries the .NET 11 iOS workload, and an iPhone simulator; it skips otherwise
/// (CI has none of them). Installing the workload here would cost gigabytes, so the test only uses one that exists.
/// </summary>
[Collection("93 iOS Simulator debugging (isolated IDE)")]
public sealed class IosSimulatorDebuggingIntegrationTests
{
    [Fact]
    public async Task IosApp_DebugsInTheSimulator_AndStopsAtABreakpointInFinishedLaunching()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The iOS Simulator exists on macOS only.");
        Assert.SkipUnless(await HasIphoneSimulatorAsync(), "No available iPhone simulator (xcrun simctl).");

        string work = Path.Combine(Path.GetTempPath(), "opendevelop-ios-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string config = Directory.CreateDirectory(Path.Combine(work, "config")).FullName;
            using var ide = await IsolatedIde.StartAsync(config);

            // .NET 11 is not discovered automatically (DotNetSdkService.HighestAutomaticallyDiscoveredSdkMajor):
            // opt in the folder that holds it, like Tools > Options > .NET SDK > Add Custom Path.
            foreach (var candidate in SdkRootCandidates.WithSdk11())
                await ide.InvokeAsync("od.sdk.add-custom-root", candidate);

            var (sdkRoot, sdkVersion) = FindSdk11WithIosWorkload(await ide.InvokeAsync("od.sdk.list"));
            Assert.SkipWhen(sdkRoot == null, "No SDK 11 root with the .NET 11 iOS workload is installed (packs/Microsoft.iOS.Sdk.net11.0_*).");

            var (solution, appDelegate, breakLine) = await CreateIosAppAsync(work, sdkRoot!, sdkVersion!);
            var open = await ide.InvokeAsync("od.open-solution", solution);
            Assert.True(open.GetProperty("success").GetBoolean(), open.ToString());
            await WaitForProjectItemsAsync(ide, "IosApp");

            var breakpoint = await ide.InvokeAsync("od.debug.set-breakpoint", appDelegate, breakLine);
            Assert.True(breakpoint.GetProperty("success").GetBoolean(), breakpoint.ToString());

            // A cold simulator boot plus the first device build can take minutes
            var start = await ide.InvokeAsync("od.debug.start", Path.Combine(work, "IosApp", "IosApp.csproj"), true, 420);
            try
            {
                Assert.True(start.GetProperty("stopped").GetBoolean(), "The app never stopped at the breakpoint: " + start);
                // The debugger reports the resolved path (/private/var/... for a /var/... temp folder on macOS)
                Assert.EndsWith(Path.Combine("IosApp", "AppDelegate.cs"), start.GetProperty("currentFile").GetString());
                Assert.Equal(breakLine, start.GetProperty("currentLine").GetInt32());

                var stack = await ide.InvokeAsync("od.debug.call-stack");
                Assert.Contains(stack.EnumerateArray(), frame =>
                    (frame.GetProperty("Name").GetString() ?? "").Contains("AppDelegate.FinishedLaunching", StringComparison.Ordinal));
            }
            finally
            {
                await ide.InvokeAsync("od.debug.stop");
            }
            Assert.False((await ide.InvokeAsync("od.debug.service-info")).GetProperty("isDebugging").GetBoolean());
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { /* a lingering handle; the temp folder is swept eventually */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A discovered root with an 11.x SDK and the net11 iOS SDK pack, and that SDK version, or nulls.</summary>
    static (string? Root, string? Version) FindSdk11WithIosWorkload(JsonElement sdkList)
    {
        foreach (var sdk in sdkList.GetProperty("sdks").EnumerateArray())
        {
            string? root = sdk.GetProperty("RootPath").GetString();
            if (root == null || !Directory.Exists(Path.Combine(root, "sdk")) || !Directory.Exists(Path.Combine(root, "packs")))
                continue;
            if (!Directory.GetDirectories(Path.Combine(root, "packs"), "Microsoft.iOS.Sdk.net11.0_*").Any())
                continue;
            string? version = Directory.GetDirectories(Path.Combine(root, "sdk")).Select(Path.GetFileName)
                .Where(name => name != null && name.StartsWith("11.", StringComparison.Ordinal))
                .OrderByDescending(name => name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (version != null)
                return (root, version);
        }
        return (null, null);
    }

    /// <summary>`dotnet new ios` from that root, pinned to its SDK by global.json, with a slnx; returns the line of
    /// FinishedLaunching's <c>return true;</c>.</summary>
    static async Task<(string Solution, string AppDelegate, int BreakLine)> CreateIosAppAsync(string work, string sdkRoot, string sdkVersion)
    {
        await File.WriteAllTextAsync(Path.Combine(work, "global.json"),
            "{ \"sdk\": { \"version\": \"" + sdkVersion + "\", \"allowPrerelease\": true, \"rollForward\": \"disable\" } }\n");
        var create = new ProcessStartInfo(Path.Combine(sdkRoot, "dotnet")) {
            WorkingDirectory = work,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "new", "ios", "-n", "IosApp", "-o", "IosApp" })
            create.ArgumentList.Add(argument);
        create.Environment["DOTNET_ROOT"] = sdkRoot;
        using (var process = Process.Start(create)!)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, "dotnet new ios failed: " + await output + await error);
        }

        string appDelegate = Path.Combine(work, "IosApp", "AppDelegate.cs");
        var lines = await File.ReadAllLinesAsync(appDelegate);
        int finishedLaunching = Array.FindIndex(lines, line => line.Contains("FinishedLaunching", StringComparison.Ordinal));
        int returnTrue = Array.FindIndex(lines, Math.Max(finishedLaunching, 0), line => line.Trim() == "return true;");
        Assert.True(finishedLaunching >= 0 && returnTrue > finishedLaunching, "The ios template's AppDelegate changed shape:\n" + string.Join("\n", lines));

        string solution = Path.Combine(work, "IosApp", "IosApp.slnx");
        await File.WriteAllTextAsync(solution, "<Solution>\n  <Project Path=\"IosApp.csproj\" />\n</Solution>\n");
        return (solution, appDelegate, returnTrue + 1);
    }

    static async Task WaitForProjectItemsAsync(IsolatedIde ide, string name)
    {
        JsonElement tree = default;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            tree = await ide.InvokeAsync("od.solution-tree");
            if (tree.GetProperty("projects").EnumerateArray().Any(p =>
                    p.GetProperty("name").GetString() == name && p.GetProperty("files").GetArrayLength() > 0))
                return;
            await Task.Delay(500);
        }
        Assert.Fail(name + " never loaded with its items: " + tree);
    }

    static async Task<bool> HasIphoneSimulatorAsync()
    {
        try
        {
            var list = new ProcessStartInfo("/usr/bin/xcrun") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "simctl", "list", "devices", "available", "-j" })
                list.ArgumentList.Add(argument);
            using var process = Process.Start(list)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 && output.Contains("\"name\" : \"iPhone", StringComparison.Ordinal);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
