using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("94 Android debugging (isolated IDE)")]
public sealed class AndroidDebuggingIntegrationTests
{
	[Fact]
	public async Task AndroidApp_F5StopsStepsEvaluatesAndStopsCleanly()
	{
		Assert.SkipUnless(OperatingSystem.IsMacOS(), "This journey needs the local Android emulator and SDK workload.");
		string adb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Android", "sdk", "platform-tools", "adb");
		Assert.SkipUnless(File.Exists(adb), "The Android SDK platform tools are not installed.");
		var devices = (await CommandAsync(adb, "devices")).Split('\n').Select(line => line.TrimEnd()).Where(line => line.EndsWith("\tdevice", StringComparison.Ordinal)).ToArray();
		Assert.SkipUnless(devices.Length == 1, "Exactly one Android device must be connected.");
		string serial = devices[0].Split('\t')[0];
		string work = Path.Combine(Path.GetTempPath(), "opendevelop-android-it-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(work);
		try {
			using var ide = await IsolatedIde.StartAsync(Directory.CreateDirectory(Path.Combine(work, "config")).FullName);
			var sdks = (await ide.InvokeAsync("od.sdk.list")).GetProperty("sdks");
			(string? root, string? version) = FindSdk(sdks);
			Assert.SkipWhen(root == null, "No discovered SDK 11 root has the Android workload.");
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

	static async Task<string> CommandAsync(string executable, string argument)
	{
		using var process = Process.Start(new ProcessStartInfo(executable, argument) { UseShellExecute = false, RedirectStandardOutput = true })!;
		string output = await process.StandardOutput.ReadToEndAsync();
		await process.WaitForExitAsync();
		return output;
	}
}
