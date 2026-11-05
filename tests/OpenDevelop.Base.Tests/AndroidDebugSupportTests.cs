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
}
