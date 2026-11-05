using System.Xml.Linq;
using Debugger.AddIn.Android;
using Xunit;

namespace OpenDevelop.Base.Tests;

public class AndroidDebugSupportTests
{
	[Theory]
	[InlineData("net11.0-android", true)]
	[InlineData("net11.0-android35.0", true)]
	[InlineData("net12.0-android", true)]
	[InlineData("net10.0-android", false)]
	[InlineData("net11.0-ios", false)]
	[InlineData("net11.0-android-invalid", false)]
	public void DebuggableFrameworks_RequireAndroidCoreClr(string framework, bool expected) =>
		Assert.Equal(expected, AndroidDebugSupport.IsDebuggableTargetFramework(framework));

	[Fact]
	public void DeviceChoices_MapSelectedSerialWithoutFallingBackToAnotherDevice()
	{
		var choices = AndroidDebugDeviceMenuBuilder.BuildChoices(new[] { "emulator-5556", "R58M123", "emulator-5554" }, "emulator-5556", "Automatic");
		Assert.Equal("emulator-5556", Assert.Single(choices, choice => choice.IsChecked).Serial);
		Assert.Equal("emulator-5556", AndroidDebugProjectBehavior.SelectSerial(new[] { "emulator-5554", "emulator-5556" }, "emulator-5556"));
		Assert.Equal("R58M123", AndroidDebugProjectBehavior.SelectSerial(new[] { "R58M123" }, ""));
		Assert.Throws<InvalidOperationException>(() => AndroidDebugProjectBehavior.SelectSerial(new[] { "emulator-5554" }, "emulator-5556"));
		Assert.Throws<InvalidOperationException>(() => AndroidDebugProjectBehavior.SelectSerial(new[] { "emulator-5554", "emulator-5556" }, ""));
	}

	[Fact]
	public void BuildInjection_EscapesPathsAndDoesNotEmbedSessionSecrets()
	{
		string directory = Path.Combine(Path.GetTempPath(), "android debug & " + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			string agent = Path.Combine(directory, "libSharpDbg.AndroidAgent.so");
			File.WriteAllText(agent, "agent");
			string targets = AndroidDebugSupport.WriteBuildInjection(directory, agent, "arm64-v8a", "com.example.app");
			var xml = XDocument.Load(targets);
			var library = xml.Descendants("AndroidNativeLibrary").Single();
			Assert.Equal(agent, (string?)library.Attribute("Include"));
			Assert.Equal("arm64-v8a/libSharpDbg.AndroidAgent.so", (string?)library.Attribute("Link"));
			string env = File.ReadAllText(Path.Combine(directory, "android-debug.env"));
			Assert.Contains("OD_ANDROID_DEBUG_PACKAGE=com.example.app", env);
			Assert.DoesNotContain("OD_ANDROID_DAP_TOKEN", env);
			Assert.Empty(xml.Descendants("Import"));
			string original = Path.Combine(directory, "original.targets");
			File.WriteAllText(original, "<Project />");
			AndroidDebugSupport.WriteBuildInjection(directory, agent, "arm64-v8a", "com.example.app", original);
			Assert.Equal(original, (string?)XDocument.Load(targets).Descendants("Import").Single().Attribute("Project"));
			AndroidDebugSupport.WriteBuildInjection(directory, agent, "arm64-v8a", "com.example.app", Path.Combine(directory, "missing.targets"));
			Assert.Empty(XDocument.Load(targets).Descendants("Import"));
			Assert.Throws<ArgumentException>(() => AndroidDebugSupport.WriteBuildInjection(directory, agent, "arm64-v8a", "../bad"));
			Assert.Throws<ArgumentException>(() => AndroidDebugSupport.WriteBuildInjection(directory, agent, "unknown", "com.example.app"));
		} finally {
			Directory.Delete(directory, true);
		}
	}

	[Fact]
	public async Task DeviceSession_DeploysPrivateConfigurationAndCleansUp()
	{
		if (OperatingSystem.IsWindows()) return;
		string directory = Path.Combine(Path.GetTempPath(), "android-adb-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			string adb = Path.Combine(directory, "adb");
			string log = Path.Combine(directory, "calls");
			string config = Path.Combine(directory, "config");
			string marker = Path.Combine(directory, "marked");
			File.WriteAllText(adb, "#!/bin/sh\n" +
				"printf '%s\\n' \"$*\" >> '" + log + "'\n" +
				"case \"$*\" in\n" +
				"  *'get-current-user') echo 0;;\n" +
				"  *'dumpsys activity processes') if [ -f '" + marker + "' ]; then echo 'mDebugApp=com.example.app/orig=null'; else echo 'mDebugApp=null/orig=null'; fi;;\n" +
				"  *'am set-debug-app'*) touch '" + marker + "';;\n" +
				"  *'forward tcp:0'*) echo 45678;;\n" +
				"  *'resolve-activity'*) echo 'com.example.app/.MainActivity';;\n" +
				"  *'sh -c'*) cat > '" + config + "';;\n" +
				"esac\n");
			File.SetUnixFileMode(adb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			string apk = Path.Combine(directory, "app.apk");
			File.WriteAllText(apk, "apk");
			string modules = Path.Combine(directory, "modules");
			Directory.CreateDirectory(modules);
			var session = await AndroidDebugSession.StartAsync(adb, "emulator-5554", "com.example.app", apk, modules, TestContext.Current.CancellationToken);
			Assert.Equal(45678, session.HostPort);
			string[] lines = await File.ReadAllLinesAsync(config, TestContext.Current.CancellationToken);
			Assert.Equal(session.DevicePort.ToString(), lines[0]);
			Assert.Equal(session.Token, lines[1]);
			Assert.Contains("/com.example.app/files/sharpdbg/", lines[2]);
			await session.DisposeAsync();
			await session.DisposeAsync();
			string calls = await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken);
			Assert.Contains("-s emulator-5554 install -r", calls);
			Assert.Contains("forward --remove tcp:45678", calls);
			Assert.Contains("am clear-debug-app", calls);
			Assert.DoesNotContain(session.Token, calls);
			Assert.Equal(2, calls.Split("am force-stop com.example.app").Length - 1);
		} finally {
			Directory.Delete(directory, true);
		}
	}

	[Fact]
	public async Task DeviceSession_LeavesAnotherDebugAppUntouched()
	{
		if (OperatingSystem.IsWindows()) return;
		string directory = Path.Combine(Path.GetTempPath(), "android-adb-conflict-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			string adb = Path.Combine(directory, "adb"), log = Path.Combine(directory, "calls");
			File.WriteAllText(adb, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> '" + log + "'\ncase \"$*\" in *'get-current-user') echo 0;; *'dumpsys activity processes') echo 'mDebugApp=com.other.app/orig=null';; esac\n");
			File.SetUnixFileMode(adb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			string apk = Path.Combine(directory, "app.apk");
			File.WriteAllText(apk, "apk");
			await Assert.ThrowsAsync<InvalidOperationException>(() => AndroidDebugSession.StartAsync(adb, "emulator-5554", "com.example.app", apk, directory, TestContext.Current.CancellationToken));
			string calls = await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken);
			Assert.DoesNotContain("install -r", calls);
			Assert.DoesNotContain("force-stop", calls);
			Assert.DoesNotContain("clear-debug-app", calls);
		} finally {
			Directory.Delete(directory, true);
		}
	}

	[Fact]
	public async Task DeviceSession_TreatsMissingDebugAppLineInCompleteDumpAsUnset()
	{
		if (OperatingSystem.IsWindows()) return;
		string directory = Path.Combine(Path.GetTempPath(), "android-adb-fresh-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			string adb = Path.Combine(directory, "adb");
			string marker = Path.Combine(directory, "marked");
			// A freshly booted device prints no mDebugApp line at all until a debug app has been set.
			File.WriteAllText(adb, "#!/bin/sh\n" +
				"case \"$*\" in\n" +
				"  *'get-current-user') echo 0;;\n" +
				"  *'dumpsys activity processes') if [ -f '" + marker + "' ]; then echo 'mDebugApp=com.example.app/orig=null'; else echo 'mProcessesReady=true mSystemReady=true'; fi;;\n" +
				"  *'am set-debug-app'*) touch '" + marker + "';;\n" +
				"  *'forward tcp:0'*) echo 45678;;\n" +
				"  *'resolve-activity'*) echo 'com.example.app/.MainActivity';;\n" +
				"esac\n");
			File.SetUnixFileMode(adb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			string apk = Path.Combine(directory, "app.apk");
			File.WriteAllText(apk, "apk");
			var session = await AndroidDebugSession.StartAsync(adb, "emulator-5554", "com.example.app", apk, directory, TestContext.Current.CancellationToken);
			Assert.Equal(45678, session.HostPort);
			await session.DisposeAsync();
		} finally {
			Directory.Delete(directory, true);
		}
	}

	[Fact]
	public async Task DeviceSession_RejectsIncompleteDebugAppDump()
	{
		if (OperatingSystem.IsWindows()) return;
		string directory = Path.Combine(Path.GetTempPath(), "android-adb-incomplete-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			string adb = Path.Combine(directory, "adb"), log = Path.Combine(directory, "calls");
			File.WriteAllText(adb, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> '" + log + "'\ncase \"$*\" in *'get-current-user') echo 0;; *'dumpsys activity processes') echo 'Unable to connect to the activity service';; esac\n");
			File.SetUnixFileMode(adb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			string apk = Path.Combine(directory, "app.apk");
			File.WriteAllText(apk, "apk");
			var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AndroidDebugSession.StartAsync(adb, "emulator-5554", "com.example.app", apk, directory, TestContext.Current.CancellationToken));
			Assert.Contains("Could not inspect the Android debug-app setting", error.Message);
			Assert.DoesNotContain("install -r", await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken));
		} finally {
			Directory.Delete(directory, true);
		}
	}

	// A stateful fake adb: set-debug-app/clear-debug-app flip a marker that the dumpsys output reflects,
	// every call is logged, and chosen steps block so a test can cancel while they run.
	static string WriteFakeAdb(string directory, string blockOn = "", string package = "com.example.app")
	{
		string adb = Path.Combine(directory, "adb"), log = Path.Combine(directory, "calls"), marker = Path.Combine(directory, "marked");
		string Block(string step) => blockOn == step ? "sleep 30; " : "";
		File.WriteAllText(adb, "#!/bin/sh\n" +
			"printf '%s\\n' \"$*\" >> '" + log + "'\n" +
			"case \"$*\" in\n" +
			"  *'get-current-user') echo 0;;\n" +
			"  *'dumpsys activity processes') if [ -f '" + marker + "' ]; then echo 'mProcessesReady=true mDebugApp=" + package + "/orig=null'; else echo 'mProcessesReady=true'; fi;;\n" +
			"  *'am set-debug-app'*) touch '" + marker + "';;\n" +
			"  *'am clear-debug-app'*) rm -f '" + marker + "';;\n" +
			"  *'forward tcp:0'*) echo 45678;;\n" +
			"  *'resolve-activity'*) " + Block("resolve-activity") + "echo '" + package + "/.MainActivity';;\n" +
			"  *'install -r'*) " + Block("install") + ";;\n" +
			"  *'sh -c'*) cat > /dev/null;;\n" +
			"esac\n");
		File.SetUnixFileMode(adb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		File.WriteAllText(Path.Combine(directory, "app.apk"), "apk");
		return adb;
	}

	static async Task<string> CallsAsync(string directory) =>
		File.Exists(Path.Combine(directory, "calls")) ? await File.ReadAllTextAsync(Path.Combine(directory, "calls"), TestContext.Current.CancellationToken) : "";

	static int Count(string text, string needle) => text.Split(needle).Length - 1;

	[Fact]
	public async Task DeviceSession_CancelledWhileLaunching_CleansUpEverythingItOwns()
	{
		if (OperatingSystem.IsWindows()) return;
		string directory = Path.Combine(Path.GetTempPath(), "android-adb-cancel-late-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			string adb = WriteFakeAdb(directory, blockOn: "resolve-activity");
			using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AndroidDebugSession.StartAsync(adb, "emulator-5554", "com.example.app", Path.Combine(directory, "app.apk"), directory, cancel.Token));
			string calls = await CallsAsync(directory);
			Assert.Contains("am set-debug-app com.example.app", calls);
			Assert.Contains("forward --remove tcp:45678", calls);
			Assert.Contains("am clear-debug-app", calls);
			Assert.Equal(2, Count(calls, "am force-stop com.example.app"));
			Assert.DoesNotContain("am start", calls);
			Assert.False(File.Exists(Path.Combine(directory, "marked")), "the debug-app setting must be cleared again");
		} finally {
			Directory.Delete(directory, true);
		}
	}

	[Fact]
	public async Task DeviceSession_CancelledBeforeAnythingIsOwned_LeavesTheDeviceSettingsAlone()
	{
		if (OperatingSystem.IsWindows()) return;
		string directory = Path.Combine(Path.GetTempPath(), "android-adb-cancel-early-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			string adb = WriteFakeAdb(directory, blockOn: "install");
			using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AndroidDebugSession.StartAsync(adb, "emulator-5554", "com.example.app", Path.Combine(directory, "app.apk"), directory, cancel.Token));
			string calls = await CallsAsync(directory);
			Assert.Contains("install -r", calls);
			Assert.DoesNotContain("set-debug-app", calls);
			Assert.DoesNotContain("clear-debug-app", calls);
			Assert.DoesNotContain("force-stop", calls);
			Assert.DoesNotContain("forward tcp:0", calls);
		} finally {
			Directory.Delete(directory, true);
		}
	}

	[Fact]
	public async Task DeviceSession_RepeatedSessionsStartAndCleanUpIndependently()
	{
		if (OperatingSystem.IsWindows()) return;
		string directory = Path.Combine(Path.GetTempPath(), "android-adb-repeat-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			string adb = WriteFakeAdb(directory), apk = Path.Combine(directory, "app.apk");
			var first = await AndroidDebugSession.StartAsync(adb, "emulator-5554", "com.example.app", apk, directory, TestContext.Current.CancellationToken);
			await first.DisposeAsync();
			Assert.False(File.Exists(Path.Combine(directory, "marked")));
			var second = await AndroidDebugSession.StartAsync(adb, "emulator-5554", "com.example.app", apk, directory, TestContext.Current.CancellationToken);
			Assert.NotEqual(first.Token, second.Token);
			await second.DisposeAsync();
			string calls = await CallsAsync(directory);
			Assert.Equal(2, Count(calls, "am set-debug-app"));
			Assert.Equal(2, Count(calls, "am clear-debug-app"));
			Assert.Equal(2, Count(calls, "forward --remove tcp:45678"));
			Assert.False(File.Exists(Path.Combine(directory, "marked")));
		} finally {
			Directory.Delete(directory, true);
		}
	}

	[Fact]
	public async Task DeviceSession_SecondConcurrentSessionIsRefusedAndDoesNotDisturbTheFirst()
	{
		if (OperatingSystem.IsWindows()) return;
		string directory = Path.Combine(Path.GetTempPath(), "android-adb-concurrent-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try {
			string adb = WriteFakeAdb(directory), apk = Path.Combine(directory, "app.apk");
			var first = await AndroidDebugSession.StartAsync(adb, "emulator-5554", "com.example.app", apk, directory, TestContext.Current.CancellationToken);
			string before = await CallsAsync(directory);
			var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AndroidDebugSession.StartAsync(adb, "emulator-5554", "com.example.app", apk, directory, TestContext.Current.CancellationToken));
			Assert.Contains("Another Android debug-app setting is active", error.Message);
			string after = (await CallsAsync(directory))[before.Length..];
			Assert.DoesNotContain("install -r", after);
			Assert.DoesNotContain("force-stop", after);
			Assert.DoesNotContain("clear-debug-app", after);
			Assert.True(File.Exists(Path.Combine(directory, "marked")), "the first session's setting must survive the refused second one");
			await first.DisposeAsync();
			Assert.False(File.Exists(Path.Combine(directory, "marked")));
		} finally {
			Directory.Delete(directory, true);
		}
	}

	[Theory]
	[InlineData(new[] { "emulator-5554" }, "", "emulator-5554")]
	[InlineData(new[] { "emulator-5554", "R5CT123" }, "R5CT123", "R5CT123")]
	[InlineData(new[] { "emulator-5554", "R5CT123" }, "emulator-5554", "emulator-5554")]
	public void SelectSerial_PicksTheChosenOrOnlyDevice(string[] devices, string preferred, string expected) =>
		Assert.Equal(expected, Debugger.AddIn.Android.AndroidDebugProjectBehavior.SelectSerial(devices, preferred));

	[Theory]
	[InlineData(new string[0], "", "connected devices: 0")]
	[InlineData(new[] { "emulator-5554", "R5CT123" }, "", "connected devices: 2")]
	[InlineData(new[] { "emulator-5554", "R5CT123" }, "ZY0000", "ZY0000")]
	[InlineData(new[] { "emulator-5554" }, "R5CT123", "R5CT123")]
	public void SelectSerial_RefusesToGuessOrFallBack(string[] devices, string preferred, string expectedInMessage)
	{
		var error = Assert.Throws<InvalidOperationException>(() => Debugger.AddIn.Android.AndroidDebugProjectBehavior.SelectSerial(devices, preferred));
		Assert.Contains(expectedInMessage, error.Message);
	}
}
