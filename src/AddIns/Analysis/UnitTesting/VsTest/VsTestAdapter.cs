using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.Interfaces;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Utilities;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.Core;

namespace ICSharpCode.UnitTesting
{
	abstract class VsTestAdapter
	{
		protected IDataSerializer dataSerializer = JsonDataSerializer.Instance;
		Process vsTestConsoleProcess;
		protected SocketCommunicationManager communicationManager;
		int clientConnectionTimeOut = 15000;
		Thread messageProcessingThread;
		CancellationTokenSource restartTokenSource = new CancellationTokenSource();

		protected static string GetRunSettings(IProject project)
		{
			return "<RunSettings>" +
				new RunConfiguration {
					DisableAppDomain = true,
					ResultsDirectory = Path.Combine(
						project.Directory ?? Environment.CurrentDirectory,
						"TestResults"),
					TestAdaptersPaths = GetTestAdapters(project),
					// vstest.console's own architecture auto-detection defaults to X64 in this
					// environment (launched via "dotnet vstest.console.dll" rather than a native
					// per-arch apphost), so it tries to find an X64 dotnet muxer/testhost on an
					// arm64 machine and fails with "Could not find 'dotnet' host for the 'X64'
					// architecture." Set it explicitly from the current process's real arch.
					TargetPlatform = GetCurrentArchitecture(),
					// Left unset, this defaults to ".NETCoreApp,Version=v1.0", which never matches
					// any real project's TFM (net11.0 here) -- vstest.console detects the mismatch
					// against the actual test DLL and logs a warning, so this isn't the whole story
					// for a testhost that fails to discover anything, but it's a real, cheap-to-fix
					// discrepancy worth removing so it can't be a contributing factor.
					TargetFramework = GetTargetFramework(project)
				}.ToXml().OuterXml +
				"</RunSettings>";
		}

		static Framework GetTargetFramework(IProject project)
		{
			var getEval = project.GetType().GetMethod("GetEvaluatedProperty", BindingFlags.Instance | BindingFlags.Public);
			var tfm = getEval?.Invoke(project, new object[] { "TargetFramework" }) as string;
			if (string.IsNullOrEmpty(tfm) || !tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
				return Framework.DefaultFramework;
			var version = tfm.Substring(3);
			return Framework.FromString($".NETCoreApp,Version=v{version}") ?? Framework.DefaultFramework;
		}

		static Architecture GetCurrentArchitecture()
		{
			switch (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture) {
				case System.Runtime.InteropServices.Architecture.Arm64:
					return Architecture.ARM64;
				case System.Runtime.InteropServices.Architecture.X64:
					return Architecture.X64;
				case System.Runtime.InteropServices.Architecture.X86:
					return Architecture.X86;
				default:
					return Architecture.Default;
			}
		}

		static Dictionary<string, string> adaptersCache = new Dictionary<string, string>();

		protected static string GetTestAdapters(IProject project)
		{
			// This always returned empty, so vstest.console had no TestAdaptersPaths and could
			// never find the xunit.runner.visualstudio (or any other) test adapter DLL -- it
			// would connect, run discovery, and silently report zero tests with no error. The
			// adapter DLLs the SDK already copies into the test project's own output directory
			// (alongside the test assembly) are exactly what vstest needs here.
			var dir = Path.GetDirectoryName(GetAssemblyFileName(project));
			return dir ?? string.Empty;
		}

		Task startTask;

		protected Task Start()
		{
			if (startTask == null)
				startTask = PrivateStart();
			return startTask;
		}

		protected virtual void OnStop()
		{
		}

		public void Stop()
		{
			OnStop();
			restartTokenSource.Cancel();

			try {
				if (communicationManager != null) {
					communicationManager.StopServer();
					communicationManager = null;
				}
			} catch (Exception ex) {
				SD.Log.Warn("TestPlatformCommunicationManager stop error.", ex);
			}

			try {
				if (vsTestConsoleProcess != null && !vsTestConsoleProcess.HasExited) {
					vsTestConsoleProcess.Kill();
					vsTestConsoleProcess = null;
				}
			} catch (Exception ex) {
				SD.Log.Warn("VSTest process dispose error.", ex);
			}
		}

		async Task PrivateStart()
		{
			var token = restartTokenSource.Token;
			startedSource = new TaskCompletionSource<bool>();
			communicationManager = new SocketCommunicationManager();
			var endPoint = communicationManager.HostServer(new IPEndPoint(IPAddress.Loopback, 0));
			communicationManager.AcceptClientAsync();
			vsTestConsoleProcess = StartVsTestConsoleExe(endPoint.Port);

			var sw = Stopwatch.StartNew();
			if (!await Task.Run(() => {
				while (!token.IsCancellationRequested) {
					if (communicationManager.WaitForClientConnection(100))
						return true;
					if (clientConnectionTimeOut < sw.ElapsedMilliseconds)
						return false;
				}
				return false;
			})) {
				sw.Stop();
				throw new TimeoutException("vstest.console failed to connect.");
			}
			sw.Stop();

			if (token.IsCancellationRequested)
				return;

			messageProcessingThread =
				new Thread(ReceiveMessages) {
					IsBackground = true
				};
			messageProcessingThread.Start(token);

			var timeoutDelay = Task.Delay(clientConnectionTimeOut);
			if (await Task.WhenAny(startedSource.Task, timeoutDelay) == timeoutDelay)
				throw new TimeoutException("vstest.console failed to respond.");
		}

		Process StartVsTestConsoleExe(int port)
		{
			// This never had a bundled "VsTestConsole/vstest.console.exe" -- nothing in
			// UnitTesting.csproj ever copied one there, and it's a Windows-only native binary
			// anyway. The .NET SDK already ships a cross-platform "vstest.console.dll" (the same
			// one "dotnet test" uses internally) right next to MSBuild.dll in the SDK's own
			// layout folder; launch that through the dotnet host of the user-selected SDK
			// (DotNetSdkService - the same source MinimalMSBuildEngine/DapSession use, so build,
			// debug, and test all agree on which SDK to run under) instead of trying to exec a
			// nonexistent native console app.
			var sdk = ICSharpCode.SharpDevelop.Project.Sdk.DotNetSdkService.ResolveEffectiveSdk();
			var dotnetHost = sdk.DotnetExecutablePath;
			var dotnetRoot = sdk.RootPath;
			string vsTestConsoleDll = null;
			if (!string.IsNullOrEmpty(dotnetRoot)) {
				var sdkDir = Directory.GetDirectories(Path.Combine(dotnetRoot, "sdk"))
					.OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
					.LastOrDefault();
				if (sdkDir != null) {
					var candidate = Path.Combine(sdkDir, "vstest.console.dll");
					if (File.Exists(candidate))
						vsTestConsoleDll = candidate;
				}
			}

			ProcessStartInfo psi;
			if (vsTestConsoleDll != null && !string.IsNullOrEmpty(dotnetHost)) {
				psi = new ProcessStartInfo(dotnetHost) {
					Arguments = $"\"{vsTestConsoleDll}\" /parentprocessid:{Process.GetCurrentProcess().Id} /port:{port}",
					WorkingDirectory = Path.GetDirectoryName(vsTestConsoleDll),
					UseShellExecute = false,
					CreateNoWindow = true
				};
			} else {
				// Fall back to the legacy bundled-native-exe convention, in case a future
				// packaging step does copy one in (e.g. for a real Windows deployment).
				var location = System.Reflection.Assembly.GetExecutingAssembly().Location;
				string vsTestConsoleExeFolder = Path.Combine(
					Path.GetDirectoryName(location),
					"VsTestConsole");
				string vsTestConsoleExe = Path.Combine(vsTestConsoleExeFolder, "vstest.console.exe");

				if (!File.Exists(vsTestConsoleExe))
					SD.Log.Warn("vstest.console.exe not found: " + vsTestConsoleExe);

				psi = new ProcessStartInfo(vsTestConsoleExe) {
					Arguments = $"/parentprocessid:{Process.GetCurrentProcess().Id} /port:{port}",
					WorkingDirectory = vsTestConsoleExeFolder,
					UseShellExecute = false,
					CreateNoWindow = true
				};
			}

			var process = Process.Start(psi);
			return process;
		}

		void ReceiveMessages(object obj)
		{
			var token = (CancellationToken)obj;
			while (!token.IsCancellationRequested) {
				try {
					Message message = communicationManager.ReceiveMessage();
					ProcessMessage(message);
				} catch (IOException) {
				} catch (Exception ex) {
					SD.Log.Warn("TestPlatformAdapter receive message error.", ex);
				}
			}
		}

		protected void SendExtensionList(string[] extensions)
		{
			communicationManager.SendMessage(MessageType.ExtensionsInitialize, extensions);
		}

		protected virtual void ProcessMessage(Message message)
		{
			switch (message.MessageType) {
				case MessageType.SessionConnected:
					OnSessionConnected();
					break;
				default:
					SD.Log.Warn($"Unprocessed vstest message {message.MessageType}");
					break;
			}
		}

		TaskCompletionSource<bool> startedSource;

		void OnSessionConnected()
		{
			startedSource?.SetResult(true);
		}

		protected void SetStartedSource(TaskCompletionSource<bool> source)
		{
			startedSource = source;
		}

		protected static string GetAssemblyFileName(IProject project)
		{
			// VSTest discovery/execution always needs the managed assembly (.dll), regardless of
			// the project's OutputType. Modern test projects (Microsoft.NET.Test.Sdk) set
			// OutputType=Exe so "dotnet test"/"dotnet exec" can run them as a test host, but don't
			// necessarily produce a native apphost for every TFM/platform -- so
			// project.OutputAssemblyFullPath (which follows OutputType's Exe/WinExe/.exe-or-apphost
			// naming convention) can point at a file that was never built. The managed assembly
			// next to it is always "<AssemblyName>.dll".
			var dir = Path.GetDirectoryName(project.OutputAssemblyFullPath?.ToString());
			return dir != null ? Path.Combine(dir, project.AssemblyName + ".dll") : project.OutputAssemblyFullPath;
		}
	}
}
