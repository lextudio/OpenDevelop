// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop;
using Microsoft.Diagnostics.NETCore.Client;

namespace Debugger.AddIn.Service.Dap
{
	/// <summary>
	/// How <see cref="DapSession.StartAsync"/> hands the debuggee to the adapter.
	/// </summary>
	public enum DapLaunchMode
	{
		/// <summary>
		/// The adapter itself spawns the debuggee via the DAP "launch" request. Simplest, and what
		/// every DAP adapter is required to support - the default.
		/// </summary>
		Launch,

		/// <summary>
		/// The session spawns the debuggee itself, suspended (via
		/// DOTNET_DefaultDiagnosticPortSuspend), and hands the adapter its process id via "attach"
		/// instead of "launch". <see cref="DapSession.ConfigurationDoneAsync"/> resumes the runtime
		/// (<see cref="DiagnosticsClient.ResumeRuntime"/>) once the DAP configuration window closes.
		/// Matches SharpDbg's own out-of-process test practice more closely than a plain "launch".
		/// </summary>
		AttachToSuspendedProcess
	}

	/// <summary>
	/// A single Debug Adapter Protocol debugging session against the bundled SharpDbg.Cli adapter.
	/// Replaces Debugger.Core's ICorDebug-based NDebugger/Process/Thread/StackFrame/Value engine.
	/// </summary>
	public sealed class DapSession : IDisposable
	{
		Process adapterProcess;
		Process debuggeeProcess;
		DapLaunchMode launchMode;
		DapClient client;
		CancellationTokenSource cancellationTokenSource;
		readonly string clientId;
		readonly Action<string> log;
		readonly string[] sharpDbgArtifactsSegments;

		// SharpDbg (and DAP adapters generally) report loaded modules by pushing "module" *events*
		// as assemblies load, and does NOT answer a "modules" *request* - issuing that request hung
		// GetModulesAsync forever (no response ever came), freezing the Loaded Modules pad and any
		// caller. Accumulate modules from the events instead, keyed by id, honoring the event's
		// reason (new/changed/removed). Ordered so first-seen order is preserved for display.
		readonly object modulesLock = new object();
		readonly List<DapModuleInfo> modules = new List<DapModuleInfo>();

		public bool IsRunning { get { return adapterProcess != null && !adapterProcess.HasExited; } }
		public bool IsPaused { get; private set; }
		public int ActiveThreadId { get; private set; }
		public int ActiveFrameId { get; set; }

		/// <summary>
		/// Capabilities reported by the debug adapter's "initialize" response. Defaults to all-false
		/// until <see cref="StartAsync"/> completes. Not every adapter this engine talks to (or will
		/// talk to in the future) supports every optional DAP feature, so callers should check this
		/// rather than assume support.
		/// </summary>
		public DapCapabilities Capabilities { get; private set; } = new DapCapabilities();

		public event Action Started;
		public event Action<DapStoppedEventArgs> Stopped;
		public event Action Continued;
		public event Action Exited;
		public event Action<string> OutputReceived;

		/// <param name="clientId">Sent as the DAP "clientID"/"clientName" - purely informational
		/// (shows up in adapter logs), but distinguishes which host started the session.</param>
		/// <param name="log">Optional SEND/RECV/error trace sink, forwarded to the underlying
		/// <see cref="DapClient"/>. No-op by default.</param>
		/// <param name="sharpDbgArtifactsPathFromRepoRoot">Path segments from the repo root
		/// (found by walking up from <see cref="AppContext.BaseDirectory"/> looking for
		/// ".gitmodules") down to the sharpdbg submodule's "artifacts" dir, used as a fallback
		/// when no bundled adapter is found. Differs by host: OpenDevelop's own repo root has the
		/// submodule directly under "externals/sharpdbg", but UnoDevelop's repo root sees it nested
		/// one level deeper under "externals/OpenDevelop/externals/sharpdbg" (defaulted here).</param>
		public DapSession(string clientId = "OpenDevelop", Action<string> log = null,
			string[] sharpDbgArtifactsPathFromRepoRoot = null)
		{
			this.clientId = clientId;
			this.log = log ?? (_ => { });
			sharpDbgArtifactsSegments = sharpDbgArtifactsPathFromRepoRoot ?? new[] { "externals", "sharpdbg" };
		}

		public async Task StartAsync(string targetPath, string workingDirectory, bool breakAtBeginning,
			DapLaunchMode launchMode = DapLaunchMode.Launch, CancellationToken cancellationToken = default)
		{
			string adapterDll = ResolveAdapterDll();
			if (adapterDll == null) {
				throw new FileNotFoundException("SharpDbg.Cli.dll was not found. Build OpenDevelop after initializing externals/sharpdbg.");
			}

			this.launchMode = launchMode;
			cancellationTokenSource = new CancellationTokenSource();
			adapterProcess = LaunchAdapter(adapterDll);
			// Surface the adapter's (and, since the debuggee inherits it, the debuggee's) stderr to
			// the caller so it can be shown in the Debug output channel. Without this an adapter
			// crash or a debuggee launch failure (e.g. "the specified framework was not found")
			// was completely invisible - the session just died and the UI kept stale markers.
			adapterProcess.ErrorDataReceived += (s, e) => {
				if (!string.IsNullOrEmpty(e.Data))
					OutputReceived?.Invoke(e.Data + Environment.NewLine);
			};
			adapterProcess.BeginErrorReadLine();
			client = new DapClient(adapterProcess.StandardOutput.BaseStream, adapterProcess.StandardInput.BaseStream, log);
			client.EventReceived += OnDapEvent;
			client.Start();
			adapterProcess.Exited += AdapterProcessExited;

			JsonObject initializeResponse = await client.SendRequestAsync("initialize", new JsonObject {
				["clientID"] = clientId,
				["clientName"] = clientId,
				["adapterID"] = "sharpdbg",
				["linesStartAt1"] = true,
				["columnsStartAt1"] = true,
				["supportsRunInTerminalRequest"] = false
			}, cancellationToken).ConfigureAwait(false);
			Capabilities = ParseCapabilities(initializeResponse);

			if (launchMode == DapLaunchMode.AttachToSuspendedProcess) {
				debuggeeProcess = LaunchDebuggeeSuspended(targetPath, workingDirectory);
				await client.SendRequestAsync("attach", new JsonObject {
					["processId"] = debuggeeProcess.Id,
					["console"] = "internalConsole",
					["justMyCode"] = true
				}, cancellationToken).ConfigureAwait(false);
			} else {
				await client.SendRequestAsync("launch", new JsonObject {
					["program"] = targetPath,
					["cwd"] = workingDirectory ?? Path.GetDirectoryName(targetPath),
					["stopAtEntry"] = breakAtBeginning,
					["console"] = "internalConsole"
				}, cancellationToken).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Sends the DAP "configurationDone" request, telling the adapter the IDE is done
		/// configuring the session (setting breakpoints, exception filters, etc.) and the
		/// debuggee may now actually start running. Must be called after <see cref="StartAsync"/>
		/// and after any breakpoints have been sent via <see cref="SetBreakpointsAsync"/> -
		/// most adapters (including SharpDbg) silently ignore breakpoints set afterwards.
		/// </summary>
		public async Task ConfigurationDoneAsync(CancellationToken cancellationToken = default)
		{
			await client.SendRequestAsync("configurationDone", null, cancellationToken).ConfigureAwait(false);

			// DapLaunchMode.AttachToSuspendedProcess left the debuggee's runtime suspended at
			// startup precisely so breakpoints/configuration land before any of its code runs -
			// resume it now that configuration is done, the same point a plain "launch" adapter
			// would start the debuggee at.
			if (launchMode == DapLaunchMode.AttachToSuspendedProcess && debuggeeProcess != null) {
				new DiagnosticsClient(debuggeeProcess.Id).ResumeRuntime();
			}

			Started?.Invoke();
		}

		static Process LaunchDebuggeeSuspended(string targetDll, string workingDirectory)
		{
			var processStartInfo = new ProcessStartInfo {
				FileName = ResolveDotNetHost(),
				RedirectStandardInput = false,
				RedirectStandardOutput = false,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(targetDll) ?? Environment.CurrentDirectory
			};
			processStartInfo.ArgumentList.Add(targetDll);
			// Suspends the runtime immediately at startup (before Main runs) so the DAP session
			// can attach and land breakpoints before any debuggee code executes; resumed by
			// ConfigurationDoneAsync above once the DAP configuration window closes.
			processStartInfo.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
			// Diagnostic/telemetry env vars that can interfere with a suspended-attach session
			// (forced tiering/GC modes, ReadyToRun disabling) if inherited from the IDE's own process.
			foreach (string envVar in new[] {
				"COMPLUS_FORCEENC", "COMPLUS_ReadyToRun", "COMPLUS_ZapDisable",
				"DOTNET_GCConserveMemory", "DOTNET_GCHeapCount", "DOTNET_GCNoAffinitize",
				"DOTNET_MODIFIABLE_ASSEMBLIES", "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_TieredPGO",
				"DOTNET_gcServer", "_NO_DEBUG_HEAP"
			}) {
				processStartInfo.Environment.Remove(envVar);
			}

			var process = new Process { StartInfo = processStartInfo, EnableRaisingEvents = true };
			process.Start();
			return process;
		}

		public void Stop()
		{
			cancellationTokenSource?.Cancel();
			try {
				client?.SendRequestAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true }).Wait(1000);
			} catch {
			}
			try {
				if (adapterProcess != null && !adapterProcess.HasExited) {
					adapterProcess.Kill(true);
				}
			} catch {
			}
			try {
				// In DapLaunchMode.AttachToSuspendedProcess the session owns the debuggee process
				// directly (it isn't the adapter's child) - "disconnect terminateDebuggee" above only
				// reaches a process the adapter itself launched via "launch", so it must be killed
				// here too or a stopped/paused debuggee is orphaned running forever.
				if (debuggeeProcess != null && !debuggeeProcess.HasExited) {
					debuggeeProcess.Kill(true);
				}
			} catch {
			}
			CleanupSession();
		}

		public void Break()
		{
			SendControlRequest("pause");
		}

		public void Continue()
		{
			SendControlRequest("continue");
		}

		public void StepInto()
		{
			SendControlRequest("stepIn");
		}

		public void StepOver()
		{
			SendControlRequest("next");
		}

		public void StepOut()
		{
			SendControlRequest("stepOut");
		}

		void SendControlRequest(string command)
		{
			if (client == null || ActiveThreadId == 0) {
				return;
			}
			// Fire-and-forget: the caller (Break/Continue/StepInto/StepOver/StepOut) is a void method
			// on this class's own public API, and the DAP "continued"/"stopped" event that actually
			// follows is what callers observe, not this request's response. Not
			// ICSharpCode.SharpDevelop.SharpDevelopExtensions.FireAndForget() - that extension isn't
			// linked into every host this class is shared with (e.g. UnoDevelop links only this Dap/
			// subtree, not all of SharpDevelopExtensions.cs), so surface a fault the same way inline.
			client.SendRequestAsync(command, new JsonObject { ["threadId"] = ActiveThreadId })
				.ContinueWith(t => log("Control request '" + command + "' failed: " + t.Exception), TaskContinuationOptions.OnlyOnFaulted);
		}

		/// <summary>
		/// Replaces the full set of breakpoints for a single source file (DAP semantics: this is
		/// not incremental - the whole list for the file is sent every time). Disabled breakpoints
		/// should simply be omitted from <paramref name="breakpoints"/> by the caller. Conditions
		/// and hit conditions are evaluated by the adapter itself, not by the IDE - and only sent
		/// at all if <see cref="Capabilities"/> says the connected adapter supports them.
		/// </summary>
		public async Task<IReadOnlyList<DapBreakpointVerification>> SetBreakpointsAsync(string fileName, IReadOnlyList<(int Line, string Condition, string HitCondition)> breakpoints)
		{
			if (client == null) {
				return Array.Empty<DapBreakpointVerification>();
			}

			bool wantsCondition = breakpoints.Any(bp => !string.IsNullOrEmpty(bp.Condition));
			bool wantsHitCondition = breakpoints.Any(bp => !string.IsNullOrEmpty(bp.HitCondition));
			if (wantsCondition && !Capabilities.SupportsConditionalBreakpoints) {
				OutputReceived?.Invoke("> Warning: the connected debug adapter does not support conditional breakpoints; conditions will be ignored.\n");
			}
			if (wantsHitCondition && !Capabilities.SupportsHitConditionalBreakpoints) {
				OutputReceived?.Invoke("> Warning: the connected debug adapter does not support hit-count breakpoints; hit conditions will be ignored.\n");
			}

			var breakpointsArray = new JsonArray();
			foreach (var bp in breakpoints) {
				var entry = new JsonObject { ["line"] = bp.Line };
				if (!string.IsNullOrEmpty(bp.Condition) && Capabilities.SupportsConditionalBreakpoints) {
					entry["condition"] = bp.Condition;
				}
				if (!string.IsNullOrEmpty(bp.HitCondition) && Capabilities.SupportsHitConditionalBreakpoints) {
					entry["hitCondition"] = bp.HitCondition;
				}
				breakpointsArray.Add(entry);
			}

			JsonObject response = await client.SendRequestAsync("setBreakpoints", new JsonObject {
				["source"] = new JsonObject { ["path"] = fileName },
				["breakpoints"] = breakpointsArray
			}).ConfigureAwait(false);

			var result = new List<DapBreakpointVerification>();
			JsonArray verified = response?["body"]?["breakpoints"] as JsonArray;
			if (verified != null) {
				foreach (var node in verified) {
					var obj = node as JsonObject;
					if (obj == null) continue;
					result.Add(new DapBreakpointVerification {
						Line = obj["line"] != null ? obj["line"].GetValue<int>() : 0,
						Verified = obj["verified"] != null && obj["verified"].GetValue<bool>(),
						Message = obj["message"] != null ? obj["message"].GetValue<string>() : null
					});
				}
			}
			return result;
		}

		public async Task<IReadOnlyList<DapThreadInfo>> GetThreadsAsync()
		{
			if (client == null) {
				return Array.Empty<DapThreadInfo>();
			}
			JsonObject response = await client.SendRequestAsync("threads").ConfigureAwait(false);
			var threads = new List<DapThreadInfo>();
			JsonArray items = response?["body"]?["threads"] as JsonArray;
			if (items != null) {
				foreach (var node in items) {
					var obj = node as JsonObject;
					if (obj == null) continue;
					threads.Add(new DapThreadInfo {
						Id = obj["id"] != null ? obj["id"].GetValue<int>() : 0,
						Name = obj["name"] != null ? obj["name"].GetValue<string>() : string.Empty
					});
				}
			}
			return threads;
		}

		public async Task<IReadOnlyList<DapStackFrameInfo>> GetStackFramesAsync(int threadId, int startFrame = 0, int levels = 200)
		{
			if (client == null || threadId == 0) {
				return Array.Empty<DapStackFrameInfo>();
			}
			JsonObject response = await client.SendRequestAsync("stackTrace", new JsonObject {
				["threadId"] = threadId,
				["startFrame"] = startFrame,
				["levels"] = levels
			}).ConfigureAwait(false);

			var frames = new List<DapStackFrameInfo>();
			JsonArray items = response?["body"]?["stackFrames"] as JsonArray;
			if (items != null) {
				foreach (var node in items) {
					var obj = node as JsonObject;
					if (obj == null) continue;
					JsonObject source = obj["source"] as JsonObject;
					frames.Add(new DapStackFrameInfo {
						Id = obj["id"] != null ? obj["id"].GetValue<int>() : 0,
						ThreadId = threadId,
						Name = obj["name"] != null ? obj["name"].GetValue<string>() : string.Empty,
						FilePath = source?["path"] != null ? source["path"].GetValue<string>() : null,
						Line = obj["line"] != null ? obj["line"].GetValue<int>() : 0,
						Column = obj["column"] != null ? obj["column"].GetValue<int>() : 0,
						EndLine = obj["endLine"] != null ? obj["endLine"].GetValue<int>() : 0,
						EndColumn = obj["endColumn"] != null ? obj["endColumn"].GetValue<int>() : 0
					});
				}
			}
			return frames;
		}

		public async Task<IReadOnlyList<DapScopeInfo>> GetScopesAsync(int frameId)
		{
			if (client == null) {
				return Array.Empty<DapScopeInfo>();
			}
			JsonObject response = await client.SendRequestAsync("scopes", new JsonObject { ["frameId"] = frameId }).ConfigureAwait(false);
			var scopes = new List<DapScopeInfo>();
			JsonArray items = response?["body"]?["scopes"] as JsonArray;
			if (items != null) {
				foreach (var node in items) {
					var obj = node as JsonObject;
					if (obj == null) continue;
					scopes.Add(new DapScopeInfo {
						Name = obj["name"] != null ? obj["name"].GetValue<string>() : string.Empty,
						VariablesReference = obj["variablesReference"] != null ? obj["variablesReference"].GetValue<int>() : 0,
						Expensive = obj["expensive"] != null && obj["expensive"].GetValue<bool>()
					});
				}
			}
			return scopes;
		}

		public async Task<IReadOnlyList<DapVariableInfo>> GetVariablesAsync(int variablesReference)
		{
			if (client == null || variablesReference == 0) {
				return Array.Empty<DapVariableInfo>();
			}
			JsonObject response = await client.SendRequestAsync("variables", new JsonObject { ["variablesReference"] = variablesReference }).ConfigureAwait(false);
			var variables = new List<DapVariableInfo>();
			JsonArray items = response?["body"]?["variables"] as JsonArray;
			if (items != null) {
				foreach (var node in items) {
					var obj = node as JsonObject;
					if (obj == null) continue;
					variables.Add(new DapVariableInfo {
						Name = obj["name"] != null ? obj["name"].GetValue<string>() : string.Empty,
						Value = obj["value"] != null ? obj["value"].GetValue<string>() : string.Empty,
						Type = obj["type"] != null ? obj["type"].GetValue<string>() : null,
						VariablesReference = obj["variablesReference"] != null ? obj["variablesReference"].GetValue<int>() : 0,
						EvaluateName = obj["evaluateName"] != null ? obj["evaluateName"].GetValue<string>() : null
					});
				}
			}
			return variables;
		}

		public async Task<DapEvaluateResult> EvaluateAsync(string expression, int? frameId, string context)
		{
			if (client == null) {
				throw new InvalidOperationException("Not connected to a debug adapter.");
			}
			var arguments = new JsonObject {
				["expression"] = expression,
				["context"] = context ?? "watch"
			};
			if (frameId.HasValue) {
				arguments["frameId"] = frameId.Value;
			}
			JsonObject response = await client.SendRequestAsync("evaluate", arguments).ConfigureAwait(false);
			bool success = response?["success"] == null || response["success"].GetValue<bool>();
			if (!success) {
				string message = response?["message"] != null ? response["message"].GetValue<string>() : "Evaluation failed.";
				throw new DapEvaluationException(message);
			}
			JsonObject body = response?["body"] as JsonObject;
			return new DapEvaluateResult {
				Value = body?["result"] != null ? body["result"].GetValue<string>() : string.Empty,
				Type = body?["type"] != null ? body["type"].GetValue<string>() : null,
				VariablesReference = body?["variablesReference"] != null ? body["variablesReference"].GetValue<int>() : 0
			};
		}

		public Task<IReadOnlyList<DapModuleInfo>> GetModulesAsync()
		{
			// Return the set accumulated from "module" events (see HandleModuleEvent) rather than
			// issuing a "modules" request - SharpDbg never answers that request, so awaiting it hung
			// forever. No round-trip needed, so this completes synchronously.
			lock (modulesLock) {
				return Task.FromResult<IReadOnlyList<DapModuleInfo>>(modules.ToList());
			}
		}

		static DapModuleInfo ParseModule(JsonObject module)
		{
			if (module == null)
				return null;
			return new DapModuleInfo {
				Id = module["id"]?.ToString(),
				Name = module["name"] != null ? module["name"].GetValue<string>() : string.Empty,
				Path = module["path"] != null ? module["path"].GetValue<string>() : null,
				IsOptimized = module["isOptimized"] != null && module["isOptimized"].GetValue<bool>()
			};
		}

		void HandleModuleEvent(JsonObject body)
		{
			var module = ParseModule(body?["module"] as JsonObject);
			if (module == null)
				return;
			string reason = body?["reason"] != null ? body["reason"].GetValue<string>() : "new";
			lock (modulesLock) {
				int existing = modules.FindIndex(m => m.Id == module.Id);
				if (reason == "removed") {
					if (existing >= 0)
						modules.RemoveAt(existing);
				} else if (existing >= 0) {
					modules[existing] = module; // "changed"
				} else {
					modules.Add(module); // "new"
				}
			}
		}

		public async Task<DapExceptionInfo> GetExceptionInfoAsync(int threadId)
		{
			if (client == null) {
				return null;
			}
			try {
				JsonObject response = await client.SendRequestAsync("exceptionInfo", new JsonObject { ["threadId"] = threadId }).ConfigureAwait(false);
				JsonObject body = response?["body"] as JsonObject;
				if (body == null) {
					return null;
				}
				JsonObject details = body["details"] as JsonObject;
				return new DapExceptionInfo {
					ExceptionId = body["exceptionId"] != null ? body["exceptionId"].GetValue<string>() : null,
					Description = body["description"] != null ? body["description"].GetValue<string>() : null,
					StackTrace = details?["stackTrace"] != null ? details["stackTrace"].GetValue<string>() : null,
					IsUnhandled = body["breakMode"] != null && body["breakMode"].GetValue<string>() == "unhandled"
				};
			} catch {
				return null;
			}
		}

		void OnDapEvent(string eventName, JsonObject body)
		{
			switch (eventName) {
				case "output":
					string output = body?["output"] != null ? body["output"].GetValue<string>() : string.Empty;
					if (!string.IsNullOrEmpty(output)) {
						OutputReceived?.Invoke(output);
					}
					break;
				case "stopped": {
					int threadId = body?["threadId"] != null ? body["threadId"].GetValue<int>() : ActiveThreadId;
					ActiveThreadId = threadId;
					IsPaused = true;
					string reason = body?["reason"] != null ? body["reason"].GetValue<string>() : null;
					Stopped?.Invoke(new DapStoppedEventArgs { ThreadId = threadId, Reason = reason });
					break;
				}
				case "continued":
					IsPaused = false;
					Continued?.Invoke();
					break;
				case "module":
					HandleModuleEvent(body);
					break;
				case "terminated":
				case "exited":
					CleanupSession();
					Exited?.Invoke();
					break;
			}
		}

		void AdapterProcessExited(object sender, EventArgs e)
		{
			CleanupSession();
			Exited?.Invoke();
		}

		void CleanupSession()
		{
			IsPaused = false;
			ActiveThreadId = 0;
			ActiveFrameId = 0;
			lock (modulesLock) {
				modules.Clear();
			}
			if (adapterProcess != null) {
				adapterProcess.Exited -= AdapterProcessExited;
			}
			client?.Dispose();
			client = null;
			cancellationTokenSource?.Dispose();
			cancellationTokenSource = null;
			adapterProcess?.Dispose();
			adapterProcess = null;
			debuggeeProcess?.Dispose();
			debuggeeProcess = null;
		}

		static Process LaunchAdapter(string adapterDll)
		{
			var processStartInfo = new ProcessStartInfo {
				FileName = ResolveDotNetHost(),
				Arguments = "\"" + adapterDll + "\" --interpreter=vscode",
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
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
			// DOTNET_HOST_PATH remains a supported explicit override (e.g. for scripted/CI runs
			// that set it directly), but the primary source is now the same DotNetSdkService that
			// MinimalMSBuildEngine and MtpServerProcess use - so build, debug, and test always agree
			// on which SDK's dotnet host to run under, instead of the debugger silently reading a
			// leftover process-inherited env var while the others use a different resolution.
			string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
			if (!string.IsNullOrEmpty(host))
				return host;
			// This can run on a background thread (called from WindowsDebugger's fire-and-forget
			// StartAsync), but PropertyService (which DotNetSdkService reads through) is
			// UI-thread-affinitized - marshal over instead of throwing
			// "different thread owns it".
			return SD.MainThread.InvokeIfRequired(() =>
				ICSharpCode.SharpDevelop.Project.Sdk.DotNetSdkService.ResolveEffectiveSdk().DotnetExecutablePath);
		}

		string ResolveAdapterDll()
		{
			string addInDirectory = Path.GetDirectoryName(typeof(DapSession).Assembly.Location);
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
				string artifactsDir = Path.Combine(new[] { repoRoot }.Concat(sharpDbgArtifactsSegments).ToArray());
				foreach (string configuration in new[] { "debug", "release" }) {
					string devPath = Path.Combine(artifactsDir, "artifacts", "bin", "SharpDbg.Cli", configuration, "SharpDbg.Cli.dll");
					if (File.Exists(devPath)) {
						return devPath;
					}
				}
			}

			return null;
		}

		static DapCapabilities ParseCapabilities(JsonObject initializeResponse)
		{
			JsonObject body = initializeResponse?["body"] as JsonObject;
			var capabilities = new DapCapabilities { Raw = body };
			if (body != null) {
				capabilities.SupportsConditionalBreakpoints = body["supportsConditionalBreakpoints"] != null && body["supportsConditionalBreakpoints"].GetValue<bool>();
				capabilities.SupportsHitConditionalBreakpoints = body["supportsHitConditionalBreakpoints"] != null && body["supportsHitConditionalBreakpoints"].GetValue<bool>();
				capabilities.SupportsFunctionBreakpoints = body["supportsFunctionBreakpoints"] != null && body["supportsFunctionBreakpoints"].GetValue<bool>();
				capabilities.SupportsLogPoints = body["supportsLogPoints"] != null && body["supportsLogPoints"].GetValue<bool>();
			}
			return capabilities;
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

		public void Dispose()
		{
			Stop();
		}
	}

	public sealed class DapEvaluationException : Exception
	{
		public DapEvaluationException(string message) : base(message)
		{
		}
	}
}
