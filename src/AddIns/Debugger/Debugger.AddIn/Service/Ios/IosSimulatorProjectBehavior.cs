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
using ICSharpCode.SharpDevelop.Designer.Devices;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Project.Sdk;
using ICSharpCode.SharpDevelop.Services;

namespace Debugger.AddIn.Ios
{
	/// <summary>
	/// Runs and debugs a project whose active target framework is net11.0-ios or later in the iOS
	/// Simulator: builds that framework for the simulator, installs it, starts it (stopped, when
	/// debugging) and hands the process to the DAP debugger. Any other active framework keeps the
	/// project's normal start behavior.
	/// </summary>
	public sealed class IosSimulatorProjectBehavior : ProjectBehavior
	{
		/// <summary>Project preference naming the simulator to use (a simctl UDID).</summary>
		public const string SimulatorPreferenceKey = "IosSimulatorUdid";

		static int starting;

		string ActiveIosTargetFramework {
			get {
				var targetFramework = ProjectTargetFrameworkService.GetActiveTargetFramework(Project);
				return IosSimulatorDebugSupport.IsDebuggableTargetFramework(targetFramework) ? targetFramework : null;
			}
		}

		public override bool IsStartable => ActiveIosTargetFramework != null || base.IsStartable;

		public override void Start(bool withDebugging)
		{
			var targetFramework = ActiveIosTargetFramework;
			if (targetFramework == null) {
				base.Start(withDebugging);
				return;
			}
			if (withDebugging && SD.Debugger.IsDebugging) {
				MessageService.ShowMessage("${res:XML.MainMenu.DebugMenu.Error.IsDebugging}");
				return;
			}
			if (Interlocked.Exchange(ref starting, 1) == 1) {
				BaseDebuggerService.PrintDebugMessage("> An iOS Simulator launch is already in progress.\n");
				return;
			}
			BaseDebuggerService.ActivateDebugCategory();
			StartAsync(targetFramework, withDebugging).ContinueWith(task => {
				Interlocked.Exchange(ref starting, 0);
				if (task.Exception != null)
					BaseDebuggerService.PrintDebugMessage("ERROR: " + task.Exception.GetBaseException().Message + "\n");
			}, TaskScheduler.Default);
		}

		async Task StartAsync(string targetFramework, bool withDebugging)
		{
			var cancellationToken = CancellationToken.None;
			var configuration = Project.ActiveConfiguration.Configuration ?? "Debug";
			var assemblyName = (Project as MSBuildBasedProject)?.GetEvaluatedProperty("AssemblyName") ?? Path.GetFileNameWithoutExtension(Project.FileName.ToString());

			BaseDebuggerService.PrintDebugMessage("> Building " + Project.Name + " (" + targetFramework + ", " + IosSimulatorDebugSupport.RuntimeIdentifier + ")...\n");
			await BuildAsync(targetFramework, configuration, cancellationToken).ConfigureAwait(false);
			var appBundle = IosSimulatorDebugSupport.FindAppBundle(Project.Directory.ToString(), configuration, targetFramework, assemblyName)
				?? throw new FileNotFoundException("The build produced no .app bundle for " + targetFramework + ".");
			var bundleId = await IosSimulatorDebugSupport.ReadBundleIdAsync(appBundle, cancellationToken).ConfigureAwait(false);

			var simulator = await SelectSimulatorAsync(cancellationToken).ConfigureAwait(false);
			var udid = SimctlUdid(simulator);
			BaseDebuggerService.PrintDebugMessage("> Installing on " + simulator.Name + " (iOS " + simulator.RuntimeVersion + ")...\n");
			await IosSimulatorDebugSupport.BootAndInstallAsync(udid, appBundle, cancellationToken).ConfigureAwait(false);

			if (!withDebugging) {
				var runningId = await IosSimulatorDebugSupport.LaunchAsync(udid, bundleId, stopped: false, cancellationToken).ConfigureAwait(false);
				BaseDebuggerService.PrintDebugMessage("> Started " + bundleId + " (process " + runningId + ").\n");
				return;
			}

			// Everything that can fail without the app comes first: a launch stopped for the debugger stays stopped
			var debuggerLibraries = await IosSimulatorDebugSupport.PrepareDebuggerLibrariesAsync(appBundle, cancellationToken).ConfigureAwait(false);
			var processId = await IosSimulatorDebugSupport.LaunchAsync(udid, bundleId, stopped: true, cancellationToken).ConfigureAwait(false);
			string containerTemp;
			try {
				containerTemp = await IosSimulatorDebugSupport.GetContainerTempDirectoryAsync(udid, bundleId, cancellationToken).ConfigureAwait(false);
			} catch (Exception) {
				await IosSimulatorDebugSupport.TerminateAsync(udid, bundleId).ConfigureAwait(false);
				throw;
			}
			BaseDebuggerService.PrintDebugMessage("> Started " + bundleId + " stopped (process " + processId + "); attaching...\n");
			SD.MainThread.InvokeAsyncAndForget(() => {
				// A session another start began while this one was building refuses the attach; end the stopped app then
				if (SD.Debugger is not WindowsDebugger debugger) {
					BaseDebuggerService.PrintDebugMessage("ERROR: The active debugger cannot debug iOS Simulator apps.\n");
					IosSimulatorDebugSupport.TerminateAsync(udid, bundleId).FireAndForget();
				} else if (!debugger.StartIosSimulatorDebugging(processId, debuggerLibraries, containerTemp)) {
					IosSimulatorDebugSupport.TerminateAsync(udid, bundleId).FireAndForget();
				}
			});
		}

		async Task<Device> SelectSimulatorAsync(CancellationToken cancellationToken)
		{
			var devices = await DeviceService.Default.RefreshAsync(cancellationToken).ConfigureAwait(false);
			var preferred = Project.Preferences.Get(SimulatorPreferenceKey, string.Empty);
			var simulator = SelectSimulator(devices, preferred);
			return simulator ?? throw new InvalidOperationException("No iOS Simulator is available. Create one in Xcode or the Device Manager.");
		}

		/// <summary>The preferred simulator when it exists, else a booted iPhone, else the iPhone with the newest runtime.</summary>
		public static Device SelectSimulator(IEnumerable<Device> devices, string preferredUdid)
		{
			var simulators = devices.Where(d => d.Platform == DevicePlatform.IosSimulator).ToList();
			return simulators.FirstOrDefault(d => !string.IsNullOrEmpty(preferredUdid)
					&& (string.Equals(d.Id, preferredUdid, StringComparison.OrdinalIgnoreCase) || string.Equals(SimctlUdid(d), preferredUdid, StringComparison.OrdinalIgnoreCase)))
				?? simulators.FirstOrDefault(d => d.State == DeviceState.Booted && d.Family == DeviceFamily.Phone)
				?? simulators.Where(d => d.Family == DeviceFamily.Phone)
					.OrderByDescending(d => Version.TryParse(d.RuntimeVersion, out var version) ? version : new Version(0, 0))
					.FirstOrDefault();
		}

		/// <summary>DeviceService names a simulator "ios:&lt;udid&gt;"; simctl takes the bare UDID.</summary>
		public static string SimctlUdid(Device device) =>
			device.Id.StartsWith("ios:", StringComparison.Ordinal) ? device.Id.Substring(4) : device.Id;

		/// <summary>
		/// The simulator build goes through the CLI of the SDK this project resolves to (a pre-release
		/// SDK 11 for net11.0), not the IDE's in-process MSBuild, which builds no device RID.
		/// </summary>
		async Task BuildAsync(string targetFramework, string configuration, CancellationToken cancellationToken)
		{
			var sdk = DotNetSdkService.ResolveSdkForProject(Project)?.Sdk;
			var dotnet = sdk?.DotnetExecutablePath;
			if (string.IsNullOrEmpty(dotnet) || !File.Exists(dotnet))
				dotnet = "dotnet";
			var startInfo = new ProcessStartInfo(dotnet) {
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				WorkingDirectory = Project.Directory.ToString(),
			};
			foreach (var argument in new[] { "build", Project.FileName.ToString(), "-f", targetFramework, "-r", IosSimulatorDebugSupport.RuntimeIdentifier,
				"-c", configuration, "-nologo", "-v:minimal" })
				startInfo.ArgumentList.Add(argument);
			// The IDE's in-process MSBuild settings point at its own SDK and break a child build on another one
			foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_HOST_PATH", "MSBuildSDKsPath", "MSBuildExtensionsPath",
				"MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET", "MSBUILD_NUGET_PATH", "MSBuildEnableWorkloadResolver", "MSBUILD_EXE_PATH" })
				startInfo.Environment.Remove(name);
			if (sdk?.RootPath != null)
				startInfo.Environment["DOTNET_ROOT"] = sdk.RootPath;

			using (var process = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet build could not be started.")) {
				var errors = new List<string>();
				process.OutputDataReceived += (s, e) => {
					if (string.IsNullOrEmpty(e.Data))
						return;
					if (e.Data.Contains(": error ", StringComparison.Ordinal))
						lock (errors) errors.Add(e.Data.Trim());
					BaseDebuggerService.PrintDebugMessage(e.Data + "\n");
				};
				process.ErrorDataReceived += (s, e) => {
					if (!string.IsNullOrEmpty(e.Data))
						BaseDebuggerService.PrintDebugMessage(e.Data + "\n");
				};
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
				if (process.ExitCode != 0)
					throw new InvalidOperationException("The iOS Simulator build failed (exit " + process.ExitCode + ")" +
						(errors.Count > 0 ? ": " + errors[0] : "."));
			}
		}
	}

	/// <summary>A project that declares a net11.0-ios or later target framework, on macOS.</summary>
	public sealed class IosSimulatorProjectCondition : IConditionEvaluator
	{
		public bool IsValid(object owner, Condition condition)
		{
			if (!OperatingSystem.IsMacOS())
				return false;
			var project = owner as IProject ?? ProjectService.CurrentProject;
			return project != null && ProjectTargetFrameworkService.GetTargetFrameworks(project).Any(IosSimulatorDebugSupport.IsDebuggableTargetFramework);
		}
	}
}
