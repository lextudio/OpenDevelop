using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Debugging a net11.0-android app on an Android emulator (Debugger.AddIn's AndroidDebugProjectBehavior, see
/// doc/technotes/debugging-mobile.md): build for the device's ABI, install, launch stopped, attach the
/// NativeAOT SharpDbg agent from the device-side library the payload carries, stop at a breakpoint, step and
/// evaluate, then check that stop left no debug-app setting or adb forward behind.
///
/// Needs an SDK 11 root that carries the android workload and exactly one connected device; it skips otherwise.
/// The CI lane 'mobile-android' boots a headless x86_64 emulator on a Linux runner, the only runner family with KVM.
/// </summary>
[Collection("94 Android debugging (isolated IDE)")]
public sealed class AndroidDebuggingIntegrationTests
{
	[Fact]
	public async Task AndroidApp_F5StopsStepsEvaluatesAndStopsCleanly()
	{
		string? located = FindAdb();
		Assert.SkipUnless(located != null, "The Android SDK platform tools (adb) are not installed; set ANDROID_HOME/ANDROID_SDK_ROOT or put adb on PATH.");
		string adb = located!;
		var devices = (await CommandAsync(adb, "devices")).Split('\n').Select(line => line.TrimEnd()).Where(line => line.EndsWith("\tdevice", StringComparison.Ordinal)).ToArray();
		Assert.SkipUnless(devices.Length == 1, "Exactly one Android device must be connected.");
		string serial = devices[0].Split('\t')[0];
		string work = Path.Combine(Path.GetTempPath(), "opendevelop-android-it-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(work);
		try {
			using var ide = await IsolatedIde.StartAsync(Directory.CreateDirectory(Path.Combine(work, "config")).FullName);
			// .NET 11 is not discovered automatically (DotNetSdkService.HighestAutomaticallyDiscoveredSdkMajor):
			// opt in the folder that holds it, like Tools > Options > .NET SDK > Add Custom Path.
			foreach (var candidate in SdkRootCandidates.WithSdk11())
				await ide.InvokeAsync("od.sdk.add-custom-root", candidate);
			var sdks = (await ide.InvokeAsync("od.sdk.list")).GetProperty("sdks");
			(string? root, string? version) = FindSdk(sdks);
			Assert.SkipWhen(root == null, "No SDK 11 root with the Android workload is installed.");
			await File.WriteAllTextAsync(Path.Combine(work, "global.json"), "{\"sdk\":{\"version\":\"" + version + "\",\"allowPrerelease\":true,\"rollForward\":\"disable\"}}");
			string app = Directory.CreateDirectory(Path.Combine(work, "AndroidApp")).FullName;
			await File.WriteAllTextAsync(Path.Combine(app, "Directory.Build.props"), "<Project><PropertyGroup><BaseOutputPath>custom-bin/</BaseOutputPath><BaseIntermediateOutputPath>custom-obj/</BaseIntermediateOutputPath></PropertyGroup></Project>");
			string project = Path.Combine(app, "AndroidApp.csproj");
			await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0-android</TargetFramework><SupportedOSPlatformVersion>24</SupportedOSPlatformVersion><OutputType>Exe</OutputType><ApplicationId>com.opendevelop.androiddebugtest</ApplicationId><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>");
			string source = Path.Combine(app, "MainActivity.cs");
			await File.WriteAllTextAsync(source, "using Android.App;\nusing Android.OS;\nnamespace AndroidApp;\n[Activity(Label = \"AndroidApp\", MainLauncher = true)]\npublic class MainActivity : Activity\n{\n    protected override void OnCreate(Bundle? state)\n    {\n        base.OnCreate(state);\n        int answer = 123;\n        Android.Util.Log.Info(\"AndroidApp\", answer.ToString());\n    }\n}\n");
			string solution = Path.Combine(app, "AndroidApp.slnx");
			await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"AndroidApp.csproj\" /></Solution>");
			var opened = await ide.InvokeAsync("od.open-solution", solution);
			Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
			await WaitForItemsAsync(ide);
			var choices = await ide.InvokeAsync("od.android-debug.choices");
			Assert.True(choices.GetProperty("success").GetBoolean(), choices.ToString());
			Assert.Contains(choices.GetProperty("choices").EnumerateArray(), choice => choice.GetProperty("serial").ValueKind == JsonValueKind.String && choice.GetProperty("serial").GetString() == serial);
			var selected = await ide.InvokeAsync("od.android-debug.select", serial);
			Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
			choices = await ide.InvokeAsync("od.android-debug.choices");
			Assert.Contains(choices.GetProperty("choices").EnumerateArray(), choice => choice.GetProperty("serial").ValueKind == JsonValueKind.String && choice.GetProperty("serial").GetString() == serial && choice.GetProperty("isChecked").GetBoolean());
			var breakpoint = await ide.InvokeAsync("od.debug.set-breakpoint", source, 10);
			Assert.True(breakpoint.GetProperty("success").GetBoolean(), breakpoint.ToString());
			string baselineForwards = NormalizeForwards(await CommandAsync(adb, "-s " + serial + " forward --list"));
			var started = await ide.InvokeAsync("od.debug.start", project, true, 420);
			try {
				Assert.True(started.GetProperty("stopped").GetBoolean(), started.ToString());
				Assert.EndsWith("MainActivity.cs", started.GetProperty("currentFile").GetString());
				Assert.Equal(10, started.GetProperty("currentLine").GetInt32());
				var stepped = await ide.InvokeAsync("od.debug.step-over", 30);
				Assert.True(stepped.GetProperty("stopped").GetBoolean(), stepped.ToString());
				var evaluated = await ide.InvokeAsync("od.debug.evaluate", "answer + 1");
				Assert.Equal("124", evaluated.GetProperty("Value").GetString());
			} finally {
				await ide.InvokeAsync("od.debug.stop");
			}
			Assert.False((await ide.InvokeAsync("od.debug.service-info")).GetProperty("isDebugging").GetBoolean());
			await AssertDeviceIsCleanAsync();
			// A repeated session must start from that clean state and hit the same breakpoint again.
			started = await ide.InvokeAsync("od.debug.start", project, true, 420);
			try {
				Assert.True(started.GetProperty("stopped").GetBoolean(), started.ToString());
				Assert.Equal(10, started.GetProperty("currentLine").GetInt32());
			} finally {
				await ide.InvokeAsync("od.debug.stop");
			}
			Assert.False((await ide.InvokeAsync("od.debug.service-info")).GetProperty("isDebugging").GetBoolean());
			await AssertDeviceIsCleanAsync();

			async Task AssertDeviceIsCleanAsync()
			{
				// Stop disposes the device session asynchronously; give its adb cleanup a moment. Forwards the
				// session did not create (the developer's own) are in the baseline and must stay untouched.
				string debugApp = "", forwards = "";
				for (int i = 0; i < 20; i++) {
					debugApp = await CommandAsync(adb, "-s " + serial + " shell dumpsys activity processes");
					forwards = await CommandAsync(adb, "-s " + serial + " forward --list");
					if (!debugApp.Contains("mDebugApp=com.opendevelop.androiddebugtest") && NormalizeForwards(forwards) == baselineForwards) return;
					await Task.Delay(500);
				}
				Assert.DoesNotContain("mDebugApp=com.opendevelop.androiddebugtest", debugApp);
				Assert.Equal(baselineForwards, NormalizeForwards(forwards));
			}
		} finally {
			try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
		}
	}

	static (string? Root, string? Version) FindSdk(JsonElement sdks)
	{
		foreach (var sdk in sdks.EnumerateArray()) {
			string? root = sdk.GetProperty("RootPath").GetString();
			if (root == null || !Directory.Exists(Path.Combine(root, "sdk")) || !Directory.Exists(Path.Combine(root, "packs"))) continue;
			if (!Directory.GetDirectories(Path.Combine(root, "packs"), "Microsoft.Android.Sdk.*").Any()) continue;
			string? version = Directory.GetDirectories(Path.Combine(root, "sdk")).Select(Path.GetFileName).Where(v => v?.StartsWith("11.", StringComparison.Ordinal) == true).OrderByDescending(v => v, StringComparer.Ordinal).FirstOrDefault();
			if (version != null) return (root, version);
		}
		return (null, null);
	}

	static async Task WaitForItemsAsync(IsolatedIde ide)
	{
		var deadline = DateTime.UtcNow.AddSeconds(90);
		while (DateTime.UtcNow < deadline) {
			var tree = await ide.InvokeAsync("od.solution-tree");
			if (tree.GetProperty("projects").EnumerateArray().Any(p => p.GetProperty("name").GetString() == "AndroidApp" && p.GetProperty("files").GetArrayLength() > 0)) return;
			await Task.Delay(500);
		}
		Assert.Fail("Android project did not load.");
	}

	static string NormalizeForwards(string list) =>
		string.Join("\n", list.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).OrderBy(line => line, StringComparer.Ordinal));

	/// <summary>The Android SDK's adb: ANDROID_HOME/ANDROID_SDK_ROOT, the well-known SDK folders, then PATH - the order
	/// AndroidEmulatorHostProcess.LocateAdb uses, so the journey and the IDE it drives find the same tool.</summary>
	static string? FindAdb()
	{
		string name = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
		var roots = new List<string>();
		foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" }) {
			var root = Environment.GetEnvironmentVariable(variable);
			if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);
		}
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		roots.Add(OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Android", "sdk")
			: OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk")
			: Path.Combine(home, "Android", "Sdk"));
		foreach (var root in roots) {
			var candidate = Path.Combine(root, "platform-tools", name);
			if (File.Exists(candidate)) return candidate;
		}
		foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)) {
			if (directory.Length == 0) continue;
			var candidate = Path.Combine(directory, name);
			if (File.Exists(candidate)) return candidate;
		}
		return null;
	}

	static async Task<string> CommandAsync(string executable, string argument)
	{
		using var process = Process.Start(new ProcessStartInfo(executable, argument) { UseShellExecute = false, RedirectStandardOutput = true })!;
		string output = await process.StandardOutput.ReadToEndAsync();
		await process.WaitForExitAsync();
		return output;
	}
}
