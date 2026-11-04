using ICSharpCode.SharpDevelop.Designer.Remote;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class IosSimulatorHostProcessTests
{
    [Fact]
    public void TheLaunchScriptStartsTheAppWithThisAttemptsPortAndTokenAndWatchesIt()
    {
        var script = IosSimulatorHostProcess.LaunchScript("UDID-1", "com.example.host", "Uno", 51234, "tok");
        Assert.Contains("/usr/bin/xcrun simctl launch --terminate-running-process UDID-1 com.example.host --port 51234 --token tok", script);
        // The pattern hides its own first dash in a bracket so the watcher's grep cannot match itself.
        Assert.Contains("PAT=\"[-]-port 51234 --token tok\"", script);
        Assert.Contains("Uno iOS host: not running", script);
        Assert.StartsWith("set -e\n", script);
        Assert.DoesNotContain("\r", script);
    }

    [Fact]
    public void TheDisplayNameCannotBreakOutOfTheScriptsQuotes()
    {
        var script = IosSimulatorHostProcess.LaunchScript("U", "b", "x\" ; rm -rf ~ ; \"", 1, "t");
        Assert.DoesNotContain("x\" ;", script);
    }

    [Fact]
    public void TheBundleIdIsReadFromTheXmlInfoPlist()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bundle-" + Guid.NewGuid().ToString("N") + ".app");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Info.plist"),
                "<plist><dict><key>CFBundleName</key><string>Host</string><key>CFBundleIdentifier</key>\n  <string> com.example.host </string></dict></plist>");
            Assert.Equal("com.example.host", IosSimulatorHostProcess.ReadBundleId(directory, "fallback"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheExecutableNameComesFromThePlistWhenItDiffersFromTheBundleName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bundle-" + Guid.NewGuid().ToString("N") + ".app");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Info.plist"), "<plist><dict><key>CFBundleExecutable</key><string>RealName</string></dict></plist>");
            Assert.Equal("RealName", IosSimulatorHostProcess.ReadPlistString(directory, "CFBundleExecutable", "fallback"));
            Assert.Equal("fallback", IosSimulatorHostProcess.ReadPlistString(directory, "CFBundleName", "fallback"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    static async Task<(int ExitCode, string Error)> RunWatchAsync(string script, TimeSpan limit)
    {
        var path = Path.Combine(Path.GetTempPath(), "watch-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, script);
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(path);
            using var process = System.Diagnostics.Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(limit);
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("The watcher did not end (it sees itself?)."); }
            return (process.ExitCode, await error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TheWatcherReportsAnAppThatNeverAppearsAndDoesNotSeeItself()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(), "needs a Unix shell");
        // Nothing runs with this port/token. A watcher that matched its own grep would think the app
        // is running and wait forever instead of failing after one second.
        var (exit, error) = await RunWatchAsync(IosSimulatorHostProcess.WatchScript("Uno", 40123, "neverstarted", 1), TimeSpan.FromSeconds(20));
        Assert.Equal(1, exit);
        Assert.Contains("Uno iOS host: not running", error);
    }

    [Fact]
    public async Task TheWatcherEndsWhenTheAppEnds()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(), "needs a Unix shell");
        // A stand-in app whose command line carries the port and token, alive for ~3 seconds.
        var app = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var argument in new[] { "-c", "sleep 3; true", "app", "--port", "40124", "--token", "livetoken" })
            app.ArgumentList.Add(argument);
        using var standIn = System.Diagnostics.Process.Start(app)!;
        try
        {
            var (exit, _) = await RunWatchAsync(IosSimulatorHostProcess.WatchScript("Uno", 40124, "livetoken", 5), TimeSpan.FromSeconds(30));
            Assert.Equal(0, exit);
            Assert.True(standIn.WaitForExit(5000), "the watcher ended before the app did");
        }
        finally
        {
            if (!standIn.HasExited) standIn.Kill(true);
        }
    }

    [Fact]
    public void TheDisplayNameCannotInjectIntoTheWatcher()
    {
        var script = IosSimulatorHostProcess.WatchScript("x$(touch /tmp/pwned)`id`\n", 1, "t", 1);
        Assert.DoesNotContain("$(touch", script);
        Assert.DoesNotContain("`id`", script);
    }

    [Fact]
    public void ABinaryPlistIsConvertedBeforeReading()
    {
        Assert.SkipWhen(!OperatingSystem.IsMacOS(), "plutil is a macOS tool");
        var directory = Path.Combine(Path.GetTempPath(), "bundle-" + Guid.NewGuid().ToString("N") + ".app");
        Directory.CreateDirectory(directory);
        try
        {
            var plist = Path.Combine(directory, "Info.plist");
            File.WriteAllText(plist, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>CFBundleIdentifier</key><string>com.example.binary</string><key>CFBundleExecutable</key><string>Bin</string></dict></plist>");
            using (var convert = System.Diagnostics.Process.Start("/usr/bin/plutil", new[] { "-convert", "binary1", plist })!)
                convert.WaitForExit();
            Assert.StartsWith("bplist", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(plist), 0, 6));
            Assert.Equal("com.example.binary", IosSimulatorHostProcess.ReadBundleId(directory, "fallback"));
            Assert.Equal("Bin", IosSimulatorHostProcess.ReadPlistString(directory, "CFBundleExecutable", "fallback"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AMissingPlistFallsBack()
    {
        Assert.Equal("fallback", IosSimulatorHostProcess.ReadBundleId(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")), "fallback"));
    }

    [Theory]
    [InlineData("UDID; rm -rf ~")]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("$(id)")]
    public void AnUnsafeIdentifierIsRefusedBeforeItReachesAScript(string value)
    {
        Assert.Throws<ArgumentException>(() => IosSimulatorHostProcess.RequireSafeIdentifier(value, "udid"));
    }

    [Theory]
    [InlineData("0A1B2C3D-4E5F-6789-ABCD-EF0123456789")]
    [InlineData("com.example.host")]
    [InlineData("192.168.1.5:5555")]
    [InlineData("emulator-5554")]
    public void OrdinaryIdentifiersAreAccepted(string value)
    {
        IosSimulatorHostProcess.RequireSafeIdentifier(value, "udid");
    }

    [Fact]
    public async Task PreparingAMissingBundleFailsBeforeTouchingSimctl()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            IosSimulatorHostProcess.PrepareAsync("UDID", Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".app"), TestContext.Current.CancellationToken));
    }
}

public sealed class AndroidEmulatorHostProcessTests
{
    [Fact]
    public void TheLaunchScriptBridgesStartsWatchesAndCleansUp()
    {
        var script = AndroidEmulatorHostProcess.LaunchScript("/sdk/platform-tools/adb", "emulator-5554", "com.example.host", "Uno", 51234, "tok");
        Assert.Contains("ADB='/sdk/platform-tools/adb'", script);
        Assert.Contains("S='emulator-5554'", script);
        Assert.Contains("$ADB -s $S reverse tcp:51234 tcp:51234", script);
        Assert.Contains("am start -n \"$ACT\" --es port 51234 --es token tok", script);
        Assert.Contains("Uno Android host: not running", script);
        // The bridge comes down however the script ends, including when the app never reports a pid.
        var trap = script.IndexOf("trap '$ADB -s $S reverse --remove tcp:51234", StringComparison.Ordinal);
        Assert.True(trap > script.IndexOf("reverse tcp:51234 tcp:51234", StringComparison.Ordinal));
        Assert.Contains("' EXIT", script);
        Assert.True(trap < script.IndexOf("am start", StringComparison.Ordinal));
    }

    [Fact]
    public void APathWithASingleQuoteCannotBreakTheScript()
    {
        var script = AndroidEmulatorHostProcess.LaunchScript("/Users/o'brien/adb", "emulator-5554", "p", "x", 1, "t");
        Assert.Contains("ADB='/Users/o'\\''brien/adb'", script);
    }

    [Fact]
    public void OnlyOnlineDevicesAreListed()
    {
        const string output = "List of devices attached\nemulator-5554\tdevice\nemulator-5556\toffline\nR58M123\tunauthorized\nR58M999\tdevice\n";
        Assert.Equal(["emulator-5554", "R58M999"], AndroidEmulatorHostProcess.ParseDevices(output));
        Assert.Empty(AndroidEmulatorHostProcess.ParseDevices(null));
    }

    [Fact]
    public void AnExplicitSerialWinsWithoutAskingAdb()
    {
        Assert.Equal("emulator-9999", AndroidEmulatorHostProcess.ResolveSerial("/nonexistent/adb", "pixel", "emulator-9999"));
    }

    [Fact]
    public async Task InstallingAMissingApkFailsBeforeRunningAdb()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            AndroidEmulatorHostProcess.InstallAsync("/nonexistent/adb", "emulator-5554", Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".apk"), TestContext.Current.CancellationToken));
    }
}

public sealed class DesignerHostProcessDisposeTests
{
    sealed class Probe : DesignerHostProcessClient
    {
        public int Disposed;

        protected override string GetChildDllPath() => "/nonexistent";

        protected override void OnDisposed() => Disposed++;

        public Task Start() => StartAsync(CancellationToken.None);
    }

    [Fact]
    public void OnDisposedRunsOnceNoMatterHowOftenDisposeIsCalled()
    {
        var probe = new Probe();
        probe.Dispose();
        probe.Dispose();
        Assert.Equal(1, probe.Disposed);
    }

    sealed class ExitingChild : DesignerHostProcessClient
    {
        public int Disposed;

        protected override string GetChildDllPath() => "/bin/sh";

        protected override string DotnetHostPath => "/bin/sh";

        protected override string BuildCommandLine(string childDll, int port, string token) => "-c \"exit 3\"";

        protected override void OnDisposed() => Disposed++;

        public Task Start() => StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ARetriedStartReleasesItsOwnResourcesToo()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs /bin/sh");
        var child = new ExitingChild();
        await Assert.ThrowsAsync<InvalidOperationException>(() => child.Start());
        await Assert.ThrowsAsync<InvalidOperationException>(() => child.Start());
        Assert.Equal(2, child.Disposed);
        child.Dispose();
        Assert.Equal(2, child.Disposed);
    }

    [Fact]
    public async Task DisposingAnAndroidHostForceStopsTheAppOnce()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs /bin/sh");
        var directory = Path.Combine(Path.GetTempPath(), "fakeadb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, "calls.txt");
        var adb = Path.Combine(directory, "adb");
        File.WriteAllText(adb, "#!/bin/sh\necho \"$@\" >> '" + log + "'\n");
        File.SetUnixFileMode(adb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var host = new AndroidEmulatorHostProcess(adb, "emulator-5554", "/nonexistent.apk", "com.example.host", "Uno");
            host.Dispose();
            host.Dispose();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            var calls = File.ReadAllLines(log);
            Assert.Equal(1, calls.Count(c => c == "-s emulator-5554 shell am force-stop com.example.host"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AStartWhoseChildDiesBeforeConnectingStillReleasesWhatTheChildDidNotOwn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs /bin/sh");
        var child = new ExitingChild();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => child.Start());
        Assert.Contains("exited before connecting", failure.Message);
        Assert.Equal(1, child.Disposed);
        // Disposing afterwards must not release a second time.
        child.Dispose();
        Assert.Equal(1, child.Disposed);
    }
}
