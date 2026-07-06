using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Debugging;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.Bookmarks;
using ICSharpCode.SharpDevelop.Project;

namespace OpenDevelop.Debugger
{
	public sealed class DapDebuggerService : BaseDebuggerService
	{
		Process adapterProcess;
		DapClient dapClient;
		CancellationTokenSource cancellationTokenSource;
		int activeThreadId;
		bool isProcessRunning;
		
		public override bool IsDebugging {
			get { return adapterProcess != null && !adapterProcess.HasExited; }
		}
		
		public override bool IsProcessRunning {
			get { return IsDebugging && isProcessRunning; }
		}
		
		public override bool BreakAtBeginning { get; set; }
		
		public override bool IsAttached {
			get { return false; }
		}
		
		public override bool CanDebug(IProject project)
		{
			return project != null && File.Exists(project.FileName);
		}
		
		public override bool Supports(DebuggerFeatures feature)
		{
			switch (feature) {
				case DebuggerFeatures.Start:
				case DebuggerFeatures.StartWithoutDebugging:
				case DebuggerFeatures.Stop:
				case DebuggerFeatures.ExecutionControl:
				case DebuggerFeatures.Stepping:
					return true;
				case DebuggerFeatures.Attaching:
				case DebuggerFeatures.Detaching:
					return false;
				default:
					throw new ArgumentOutOfRangeException("feature");
			}
		}
		
		public override void Start(ProcessStartInfo processStartInfo)
		{
			if (IsDebugging) {
				return;
			}
			
			OnDebugStarting(EventArgs.Empty);
			StartAsync(processStartInfo).FireAndForget();
		}
		
		async Task StartAsync(ProcessStartInfo processStartInfo)
		{
			try {
				PrintDebugMessage("> Building for debug...\n");
				
				IProject project = GetStartupProject();
				string targetPath = await ResolveTargetPathAsync(project).ConfigureAwait(false);
				if (string.IsNullOrEmpty(targetPath)) {
					targetPath = processStartInfo.FileName;
				}
				if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath)) {
					throw new FileNotFoundException("Debug target not found.", targetPath);
				}
				
				string adapterDll = ResolveAdapterDll();
				if (adapterDll == null) {
					throw new FileNotFoundException("SharpDbg.Cli.dll was not found. Build OpenDevelop after initializing externals/sharpdbg.");
				}
				
				PrintDebugMessage("> Starting debug adapter...\n");
				cancellationTokenSource = new CancellationTokenSource();
				adapterProcess = LaunchAdapter(adapterDll);
				dapClient = new DapClient(adapterProcess.StandardOutput.BaseStream, adapterProcess.StandardInput.BaseStream);
				dapClient.EventReceived += OnDapEvent;
				dapClient.Start();
				
				adapterProcess.Exited += AdapterProcessExited;
				
				string workingDirectory = !string.IsNullOrEmpty(processStartInfo.WorkingDirectory)
					? processStartInfo.WorkingDirectory
					: Path.GetDirectoryName(targetPath);
				
				await HandshakeAsync(targetPath, workingDirectory, cancellationTokenSource.Token).ConfigureAwait(false);
				
				isProcessRunning = true;
				SD.MainThread.InvokeAsyncAndForget(() => {
					OnDebugStarted(EventArgs.Empty);
					OnIsProcessRunningChanged(EventArgs.Empty);
				});
				PrintDebugMessage("> Debugging: " + Path.GetFileName(targetPath) + "\n");
			} catch (Exception ex) {
				PrintDebugMessage("ERROR: " + ex.Message + "\n");
				SD.MainThread.InvokeAsyncAndForget(() => OnDebugStopped(EventArgs.Empty));
				Stop();
			}
		}
		
		public override void StartWithoutDebugging(ProcessStartInfo processStartInfo)
		{
			Process.Start(processStartInfo);
		}
		
		public override void Stop()
		{
			cancellationTokenSource?.Cancel();
			try {
				if (dapClient != null) {
					dapClient.SendRequestAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true }).Wait(1000);
				}
			} catch {
			}
			try {
				if (adapterProcess != null && !adapterProcess.HasExited) {
					adapterProcess.Kill(true);
				}
			} catch {
			}
			CleanupSession();
			SD.MainThread.InvokeAsyncAndForget(() => OnDebugStopped(EventArgs.Empty));
		}
		
		public override void Break()
		{
			SendControlRequest("pause");
		}
		
		public override void Continue()
		{
			SendControlRequest("continue");
		}
		
		public override void StepInto()
		{
			SendControlRequest("stepIn");
		}
		
		public override void StepOver()
		{
			SendControlRequest("next");
		}
		
		public override void StepOut()
		{
			SendControlRequest("stepOut");
		}
		
		void SendControlRequest(string command)
		{
			if (dapClient == null || activeThreadId == 0) {
				return;
			}
			dapClient.SendRequestAsync(command, new JsonObject { ["threadId"] = activeThreadId }).FireAndForget();
		}
		
		public override void ShowAttachDialog()
		{
			MessageService.ShowMessage("Attach is not available in the DAP debugger MVP.");
		}
		
		public override void Attach(Process process)
		{
			throw new NotSupportedException();
		}
		
		public override void Detach()
		{
			throw new NotSupportedException();
		}
		
		public override bool SetInstructionPointer(string filename, int line, int column, bool dryRun)
		{
			return false;
		}
		
		public override void ToggleBreakpointAt(ITextEditor editor, int lineNumber)
		{
			var fileName = editor.FileName;
			if (!SD.BookmarkManager.RemoveBookmarkAt(fileName, lineNumber, b => b.GetType() == typeof(Bookmark))) {
				SD.BookmarkManager.AddMark(new Bookmark(), editor.Document, lineNumber);
			}
			
			if (IsDebugging) {
				SyncBreakpointsForFileAsync(fileName.ToString()).FireAndForget();
			}
		}
		
		public override void RemoveCurrentLineMarker()
		{
		}
		
		public override void HandleToolTipRequest(ToolTipRequestEventArgs e)
		{
		}
		
		async Task HandshakeAsync(string targetPath, string workingDirectory, CancellationToken cancellationToken)
		{
			await dapClient.SendRequestAsync("initialize", new JsonObject {
				["clientID"] = "OpenDevelop",
				["clientName"] = "OpenDevelop",
				["adapterID"] = "sharpdbg",
				["linesStartAt1"] = true,
				["columnsStartAt1"] = true,
				["supportsRunInTerminalRequest"] = false
			}, cancellationToken).ConfigureAwait(false);
			
			await SyncAllBreakpointsAsync().ConfigureAwait(false);
			
			await dapClient.SendRequestAsync("launch", new JsonObject {
				["program"] = targetPath,
				["cwd"] = workingDirectory,
				["stopAtEntry"] = BreakAtBeginning,
				["console"] = "internalConsole"
			}, cancellationToken).ConfigureAwait(false);
			
			await dapClient.SendRequestAsync("configurationDone", null, cancellationToken).ConfigureAwait(false);
		}
		
		async Task SyncAllBreakpointsAsync()
		{
			var breakpointsByFile = SD.MainThread.InvokeIfRequired(() =>
				SD.BookmarkManager.Bookmarks
					.OfType<Bookmark>()
					.Where(b => b.FileName != null)
					.GroupBy(b => b.FileName.ToString(), StringComparer.OrdinalIgnoreCase)
					.ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(b => b.LineNumber).Distinct().OrderBy(l => l).ToList(), StringComparer.OrdinalIgnoreCase));
			
			foreach (var pair in breakpointsByFile) {
				await SetBreakpointsAsync(pair.Key, pair.Value).ConfigureAwait(false);
			}
		}
		
		async Task SyncBreakpointsForFileAsync(string fileName)
		{
			if (dapClient == null) {
				return;
			}
			var lines = SD.MainThread.InvokeIfRequired(() =>
				(IReadOnlyList<int>)SD.BookmarkManager.GetBookmarks(FileName.Create(fileName))
					.OfType<Bookmark>()
					.Select(b => b.LineNumber)
					.Distinct()
					.OrderBy(l => l)
					.ToList());
			await SetBreakpointsAsync(fileName, lines).ConfigureAwait(false);
		}
		
		Task SetBreakpointsAsync(string fileName, IReadOnlyList<int> lines)
		{
			var breakpoints = new JsonArray();
			foreach (int line in lines) {
				breakpoints.Add(new JsonObject { ["line"] = line });
			}
			
			return dapClient.SendRequestAsync("setBreakpoints", new JsonObject {
				["source"] = new JsonObject { ["path"] = fileName },
				["breakpoints"] = breakpoints
			});
		}
		
		void OnDapEvent(string eventName, JsonObject body)
		{
			switch (eventName) {
				case "output":
					string output = body != null && body["output"] != null ? body["output"].GetValue<string>() : string.Empty;
					if (!string.IsNullOrEmpty(output)) {
						PrintDebugMessage(output);
					}
					break;
				case "stopped":
					activeThreadId = body != null && body["threadId"] != null ? body["threadId"].GetValue<int>() : activeThreadId;
					isProcessRunning = false;
					SD.MainThread.InvokeAsyncAndForget(() => OnIsProcessRunningChanged(EventArgs.Empty));
					ShowTopStackFrameAsync(activeThreadId).FireAndForget();
					break;
				case "continued":
					isProcessRunning = true;
					SD.MainThread.InvokeAsyncAndForget(() => {
						RemoveCurrentLineMarker();
						OnIsProcessRunningChanged(EventArgs.Empty);
					});
					break;
				case "terminated":
				case "exited":
					Stop();
					break;
			}
		}
		
		async Task ShowTopStackFrameAsync(int threadId)
		{
			if (dapClient == null || threadId == 0) {
				return;
			}
			try {
				JsonObject response = await dapClient.SendRequestAsync("stackTrace", new JsonObject {
					["threadId"] = threadId,
					["startFrame"] = 0,
					["levels"] = 1
				}).ConfigureAwait(false);
				JsonArray stackFrames = response?["body"]?["stackFrames"] as JsonArray;
				JsonObject frame = stackFrames != null && stackFrames.Count > 0 ? stackFrames[0] as JsonObject : null;
				JsonObject source = frame?["source"] as JsonObject;
				string path = source?["path"] != null ? source["path"].GetValue<string>() : null;
				int line = frame?["line"] != null ? frame["line"].GetValue<int>() : 1;
				int column = frame?["column"] != null ? frame["column"].GetValue<int>() : 1;
				if (!string.IsNullOrEmpty(path)) {
					SD.MainThread.InvokeAsyncAndForget(() => JumpToCurrentLine(path, line, column, line, column));
				}
			} catch (Exception ex) {
				LoggingService.Warn("DAP stackTrace failed", ex);
			}
		}
		
		void AdapterProcessExited(object sender, EventArgs e)
		{
			CleanupSession();
			SD.MainThread.InvokeAsyncAndForget(() => OnDebugStopped(EventArgs.Empty));
		}
		
		void CleanupSession()
		{
			isProcessRunning = false;
			activeThreadId = 0;
			if (adapterProcess != null) {
				adapterProcess.Exited -= AdapterProcessExited;
			}
			dapClient?.Dispose();
			dapClient = null;
			cancellationTokenSource?.Dispose();
			cancellationTokenSource = null;
			adapterProcess?.Dispose();
			adapterProcess = null;
		}
		
		static Process LaunchAdapter(string adapterDll)
		{
			var processStartInfo = new ProcessStartInfo {
				FileName = ResolveDotNetHost(),
				Arguments = "\"" + adapterDll + "\" --interpreter=vscode",
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = false,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			var process = new Process {
				StartInfo = processStartInfo,
				EnableRaisingEvents = true
			};
			process.Start();
			return process;
		}
		
		static string ResolveDotNetHost()
		{
			string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
			return !string.IsNullOrEmpty(host) ? host : "dotnet";
		}
		
		static IProject GetStartupProject()
		{
			return SD.ProjectService.CurrentSolution?.StartupProject ?? SD.ProjectService.CurrentProject;
		}
		
		static async Task<string> ResolveTargetPathAsync(IProject project)
		{
			if (project == null || string.IsNullOrEmpty(project.FileName)) {
				return null;
			}
			
			string targetPath = await QueryTargetPathAsync(project.FileName).ConfigureAwait(false);
			if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath)) {
				return targetPath;
			}
			
			PrintDebugMessage("> Build output not found, running dotnet build...\n");
			await RunDotNetBuildAsync(project.FileName).ConfigureAwait(false);
			targetPath = await QueryTargetPathAsync(project.FileName).ConfigureAwait(false);
			return !string.IsNullOrEmpty(targetPath) && File.Exists(targetPath) ? targetPath : null;
		}
		
		static async Task<string> QueryTargetPathAsync(string projectFile)
		{
			var processStartInfo = new ProcessStartInfo {
				FileName = ResolveDotNetHost(),
				Arguments = "msbuild \"" + projectFile + "\" -getProperty:TargetPath -p:Configuration=Debug -nologo",
				WorkingDirectory = Path.GetDirectoryName(projectFile),
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			using (var process = Process.Start(processStartInfo)) {
				string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
				string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
				await process.WaitForExitAsync().ConfigureAwait(false);
				if (process.ExitCode != 0) {
					LoggingService.Warn("dotnet msbuild -getProperty:TargetPath failed: " + error);
					return null;
				}
				return output.Trim();
			}
		}
		
		static async Task RunDotNetBuildAsync(string projectFile)
		{
			var processStartInfo = new ProcessStartInfo {
				FileName = ResolveDotNetHost(),
				Arguments = "build \"" + projectFile + "\" -c Debug",
				WorkingDirectory = Path.GetDirectoryName(projectFile),
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			using (var process = Process.Start(processStartInfo)) {
				string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
				string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
				await process.WaitForExitAsync().ConfigureAwait(false);
				PrintDebugMessage(output);
				if (!string.IsNullOrEmpty(error)) {
					PrintDebugMessage(error);
				}
				if (process.ExitCode != 0) {
					throw new InvalidOperationException("dotnet build failed.");
				}
			}
		}
		
		static string ResolveAdapterDll()
		{
			string addInDirectory = Path.GetDirectoryName(typeof(DapDebuggerService).Assembly.Location);
			if (!string.IsNullOrEmpty(addInDirectory)) {
				string addInBundled = Path.Combine(addInDirectory, "SharpDbg.Cli.dll");
				if (File.Exists(addInBundled)) {
					return addInBundled;
				}
			}
			
			string bundled = Path.Combine(AppContext.BaseDirectory, "Debugger", "SharpDbg.Cli.dll");
			if (File.Exists(bundled)) {
				return bundled;
			}
			
			string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
			if (repoRoot != null) {
				foreach (string configuration in new[] { "debug", "release" }) {
					string devPath = Path.Combine(repoRoot, "externals", "sharpdbg", "artifacts", "bin", "SharpDbg.Cli", configuration, "SharpDbg.Cli.dll");
					if (File.Exists(devPath)) {
						return devPath;
					}
				}
			}
			
			return null;
		}
		
		static string FindRepoRoot(string startDirectory)
		{
			string directory = startDirectory;
			while (!string.IsNullOrEmpty(directory)) {
				if (File.Exists(Path.Combine(directory, ".gitmodules"))) {
					return directory;
				}
				directory = Path.GetDirectoryName(directory);
			}
			return null;
		}
		
		public override void Dispose()
		{
			Stop();
			base.Dispose();
		}
	}
}
