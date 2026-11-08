using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Debugging;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Project.Sdk;
using ICSharpCode.SharpDevelop.Services;

namespace Debugger.AddIn.Android
{
	/// <summary>Builds, installs and debugs a .NET 11 CoreCLR Android app on a connected device.</summary>
	public sealed class AndroidDebugProjectBehavior : ProjectBehavior
	{
		public const string DevicePreferenceKey = "AndroidDebugSerial";
		static int starting;
		string ActiveTarget => AndroidDebugSupport.IsDebuggableTargetFramework(ProjectTargetFrameworkService.GetActiveTargetFramework(Project))
			? ProjectTargetFrameworkService.GetActiveTargetFramework(Project) : null;
		public override bool IsStartable => ActiveTarget != null || base.IsStartable;

		public override void Start(bool withDebugging)
		{
			string target = ActiveTarget;
			if (target == null || !withDebugging) { base.Start(withDebugging); return; }
			if (SD.Debugger.IsDebugging) { MessageService.ShowMessage("A debug session is already active."); return; }
			if (Interlocked.Exchange(ref starting, 1) != 0) { BaseDebuggerService.PrintDebugMessage("> An Android launch is already in progress.\n"); return; }
			BaseDebuggerService.ActivateDebugCategory();
			StartAsync(target).ContinueWith(task => {
				Interlocked.Exchange(ref starting, 0);
				if (task.Exception != null) BaseDebuggerService.PrintDebugMessage("ERROR: " + task.Exception.GetBaseException().Message + "\n");
			}, TaskScheduler.Default);
		}

		async Task StartAsync(string target)
		{
			if (Project is not MSBuildBasedProject) throw new InvalidOperationException("Android debugging requires an MSBuild project.");
			string adb = AndroidEmulatorHostProcess.LocateAdb() ?? throw new FileNotFoundException("Android SDK platform-tools (adb) were not found.");
			string devicesOutput = await RunAsync(adb, new[] { "devices" }, Project.Directory.ToString()).ConfigureAwait(false);
			var devices = AndroidEmulatorHostProcess.ParseDevices(devicesOutput);
			string serial = SelectSerial(devices, Project.Preferences.Get(DevicePreferenceKey, string.Empty));
			string abi = (await RunAsync(adb, new[] { "-s", serial, "shell", "getprop", "ro.product.cpu.abi" }, Project.Directory.ToString()).ConfigureAwait(false)).Trim();
			string rid = abi switch { "arm64-v8a" => "android-arm64", "armeabi-v7a" => "android-arm", "x86_64" => "android-x64", _ => null };
			if (rid == null) throw new InvalidOperationException("The Android debugger does not support ABI " + abi + ".");
			string addinDirectory = Path.GetDirectoryName(typeof(AndroidDebugProjectBehavior).Assembly.Location);
			string agent = Path.Combine(addinDirectory, "android", abi, "libSharpDbg.AndroidAgent.so");
			string configuration = Project.ActiveConfiguration.Configuration ?? "Debug";
			string projectDirectory = Project.Directory.ToString();
			string temporary = Path.Combine(Path.GetTempPath(), "opendevelop-android-" + Guid.NewGuid().ToString("N"));
			try {
				var sdk = DotNetSdkService.ResolveSdkForProject(Project)?.Sdk;
				string dotnet = sdk?.DotnetExecutablePath;
				if (string.IsNullOrEmpty(dotnet) || !File.Exists(dotnet)) dotnet = "dotnet";
				var platformTools = Path.GetDirectoryName(adb);
				var androidSdk = platformTools == null ? null : Directory.GetParent(platformTools)?.FullName;
				var query = new List<string> { "msbuild", Project.FileName.ToString(), "-getProperty:ApplicationId,AndroidPackageName,OutputPath,IntermediateOutputPath,CustomAfterMicrosoftCommonTargets",
					"-p:TargetFramework=" + target, "-p:RuntimeIdentifier=" + rid, "-p:Configuration=" + configuration };
				if (androidSdk != null && Directory.Exists(Path.Combine(androidSdk, "platforms")))
					query.Add("-p:AndroidSdkDirectory=" + androidSdk);
				string evaluated = await RunAsync(dotnet, query, projectDirectory, sdk?.RootPath).ConfigureAwait(false);
				using var properties = System.Text.Json.JsonDocument.Parse(evaluated);
				var values = properties.RootElement.GetProperty("Properties");
				string package = values.GetProperty("ApplicationId").GetString();
				if (string.IsNullOrWhiteSpace(package)) package = values.GetProperty("AndroidPackageName").GetString();
				if (string.IsNullOrWhiteSpace(package)) throw new InvalidOperationException("Set ApplicationId in the Android project before debugging.");
				string priorTargets = values.GetProperty("CustomAfterMicrosoftCommonTargets").GetString();
				if (!string.IsNullOrWhiteSpace(priorTargets) && !Path.IsPathRooted(priorTargets)) priorTargets = Path.GetFullPath(priorTargets, projectDirectory);
				string injection = AndroidDebugSupport.WriteBuildInjection(temporary, agent, abi, package, priorTargets);
				var arguments = new List<string> { "build", Project.FileName.ToString(), "-f", target, "-r", rid, "-c", configuration,
					"-p:CustomAfterMicrosoftCommonTargets=" + injection, "-v:minimal" };
				if (androidSdk != null && Directory.Exists(Path.Combine(androidSdk, "platforms"))) arguments.Add("-p:AndroidSdkDirectory=" + androidSdk);
				BaseDebuggerService.PrintDebugMessage("> Building Android app for " + abi + "...\n");
				await RunAsync(dotnet, arguments, projectDirectory, sdk?.RootPath, true).ConfigureAwait(false);
				string output = ResolveBuildPath(values.GetProperty("OutputPath").GetString(), projectDirectory);
				string apk = FindBuiltApk(output, rid, package);
				string obj = ResolveBuildPath(values.GetProperty("IntermediateOutputPath").GetString(), projectDirectory);
				string assets = new[] { Path.Combine(obj, "android", "assets", abi), Path.Combine(obj, rid, "android", "assets", abi) }.FirstOrDefault(Directory.Exists)
					?? throw new DirectoryNotFoundException("The Android build produced no module directory for " + abi + ".");
				BaseDebuggerService.PrintDebugMessage("> Installing Android app on " + serial + "...\n");
				var session = await AndroidDebugSession.StartAsync(adb, serial, package, apk, assets).ConfigureAwait(false);
				SD.MainThread.InvokeAsyncAndForget(() => {
					if (SD.Debugger is not WindowsDebugger debugger || !debugger.StartAndroidDebugging(session))
						_ = Task.Run(async () => await session.DisposeAsync().ConfigureAwait(false));
				});
			} finally {
				try { Directory.Delete(temporary, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
			}
		}

		public static string SelectSerial(IReadOnlyList<string> devices, string preferred)
		{
			if (!string.IsNullOrWhiteSpace(preferred))
				return devices.FirstOrDefault(device => string.Equals(device, preferred, StringComparison.Ordinal))
					?? throw new InvalidOperationException("The selected Android device is not connected: " + preferred + ".");
			if (devices.Count != 1) throw new InvalidOperationException("Select an Android device in the Debug menu; connected devices: " + devices.Count + ".");
			return devices[0];
		}

		public static string ResolveBuildPath(string path, string projectDirectory) =>
			Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), projectDirectory);

		public static string FindBuiltApk(string output, string rid, string package)
		{
			string[] expectedApks = { package + "-Signed.apk", package + ".apk" };
			var candidates = expectedApks.SelectMany((name, nameRank) => new[] {
				new { Path = Path.Combine(output, name), NameRank = nameRank, RidRank = 1 },
				new { Path = Path.Combine(output, rid, name), NameRank = nameRank, RidRank = 0 }
			}).Where(candidate => File.Exists(candidate.Path));
			if (Directory.Exists(output)) {
				candidates = candidates.Concat(Directory.EnumerateFiles(output, "*.apk", SearchOption.AllDirectories)
					.Select(path => new { Path = path, NameRank = Array.FindIndex(expectedApks, name => string.Equals(name, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)), RidRank = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(rid, StringComparer.OrdinalIgnoreCase) ? 0 : 2 })
					.Where(candidate => candidate.NameRank >= 0));
			}
			return candidates.OrderBy(candidate => candidate.RidRank).ThenBy(candidate => candidate.NameRank).ThenBy(candidate => candidate.Path, StringComparer.Ordinal).Select(candidate => candidate.Path).FirstOrDefault()
				?? throw new FileNotFoundException("The Android build produced no APK for " + package + ".");
		}

		static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory, string sdkRoot = null, bool showOutput = false)
		{
			var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
			foreach (var argument in arguments) info.ArgumentList.Add(argument);
			foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_HOST_PATH", "MSBuildSDKsPath", "MSBuildExtensionsPath", "MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET", "MSBUILD_NUGET_PATH", "MSBuildEnableWorkloadResolver", "MSBUILD_EXE_PATH" }) info.Environment.Remove(name);
			if (sdkRoot != null) info.Environment["DOTNET_ROOT"] = sdkRoot;
			using (var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + executable + ".")) {
				var stdout = process.StandardOutput.ReadToEndAsync();
				var stderr = process.StandardError.ReadToEndAsync();
				await process.WaitForExitAsync().ConfigureAwait(false);
				string output = await stdout.ConfigureAwait(false);
				string error = await stderr.ConfigureAwait(false);
				if (showOutput && output.Length > 0) BaseDebuggerService.PrintDebugMessage(output);
				if (showOutput && error.Length > 0) BaseDebuggerService.PrintDebugMessage(error);
				if (process.ExitCode != 0) throw new InvalidOperationException(Path.GetFileName(executable) + " failed (" + process.ExitCode + "): " + error.Trim());
				return output;
			}
		}
	}

	public sealed class AndroidDebugProjectCondition : IConditionEvaluator
	{
		public bool IsValid(object owner, Condition condition)
		{
			var project = owner as IProject ?? ProjectService.CurrentProject;
			return project != null && ProjectTargetFrameworkService.GetTargetFrameworks(project).Any(AndroidDebugSupport.IsDebuggableTargetFramework);
		}
	}

	public sealed class AndroidDebugStartupProjectCondition : IConditionEvaluator
	{
		public bool IsValid(object owner, Condition condition)
		{
			var project = SD.ProjectService.CurrentSolution?.StartupProject;
			return project != null && ProjectTargetFrameworkService.GetTargetFrameworks(project).Any(AndroidDebugSupport.IsDebuggableTargetFramework);
		}
	}
}
