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
using System.Reflection.PortableExecutable;
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

		// Last few stderr lines of the adapter, quoted when it dies so the failure names its cause
		readonly Queue<string> adapterStderrTail = new Queue<string>();
		const int AdapterStderrTailLines = 20;

		// SharpDbg's own diagnostics, requested per session with --engineLogging. It writes nothing
		// unless asked, which is why a session that hangs can otherwise only report "the request
		// timed out" - the evidence is in the adapter, nobody is reading it. The file is deleted when
		// the session ends without incident, so the steady state is no leftover.
		string adapterLogPath;
		bool adapterLogReported;

		string launchTarget;
		static readonly TimeSpan RuntimeLoadNoticeDelay = TimeSpan.FromSeconds(30);
		const int AdapterLogTailLines = 30;

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
			IEnumerable<string> arguments = null,
			DapLaunchMode launchMode = DapLaunchMode.Launch, CancellationToken cancellationToken = default,
			IEnumerable<KeyValuePair<string, string>> launchEnvironment = null,
			bool noDebug = false)
		{
			var argumentList = arguments != null ? arguments.ToList() : new List<string>();
			string adapterDll = ResolveAdapterDll();
			if (adapterDll == null) {
				throw new FileNotFoundException("SharpDbg.Cli.dll was not found. Build OpenDevelop after initializing externals/sharpdbg.");
			}

			this.launchMode = launchMode;
			launchTarget = targetPath;
			cancellationTokenSource = new CancellationTokenSource();
			string debuggeeHost = ResolveDebuggeeHost(targetPath);
			adapterLogPath = CreateAdapterLogPath();
			adapterProcess = LaunchAdapter(adapterDll, debuggeeHost, adapterLogPath);
			// Surface the adapter's (and, since the debuggee inherits it, the debuggee's) stderr to
			// the caller so it can be shown in the Debug output channel. Without this an adapter
			// crash or a debuggee launch failure (e.g. "the specified framework was not found")
			// was completely invisible - the session just died and the UI kept stale markers.
			adapterProcess.ErrorDataReceived += (s, e) => {
				if (string.IsNullOrEmpty(e.Data))
					return;
				lock (adapterStderrTail) {
					adapterStderrTail.Enqueue(e.Data);
					while (adapterStderrTail.Count > AdapterStderrTailLines)
						adapterStderrTail.Dequeue();
				}
				OutputReceived?.Invoke(e.Data + Environment.NewLine);
			};
			adapterProcess.BeginErrorReadLine();
			client = new DapClient(adapterProcess.StandardOutput.BaseStream, adapterProcess.StandardInput.BaseStream, log);
			client.EventReceived += OnDapEvent;
			var adapter = adapterProcess;
			var connection = client;
			client.Disconnected += () => connection.FailPendingRequests(DescribeAdapterExit(adapter));
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
				debuggeeProcess = LaunchDebuggeeSuspended(debuggeeHost, targetPath, workingDirectory, argumentList, launchEnvironment);
				await client.SendRequestAsync("attach", new JsonObject {
					["processId"] = debuggeeProcess.Id,
					["console"] = "internalConsole",
					["justMyCode"] = true
				}, cancellationToken).ConfigureAwait(false);
			} else {
				var args = new JsonArray();
				foreach (var argument in argumentList) {
					args.Add(argument);
				}
				var env = new JsonObject();
				foreach (var variable in launchEnvironment ?? Enumerable.Empty<KeyValuePair<string, string>>()) {
					env[variable.Key] = variable.Value;
				}
				await client.SendRequestAsync("launch", new JsonObject {
					["program"] = targetPath,
					["args"] = args,
					["cwd"] = workingDirectory ?? Path.GetDirectoryName(targetPath),
					["env"] = env,
					["stopAtEntry"] = breakAtBeginning,
					["console"] = "internalConsole",
					["noDebug"] = noDebug
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
			// In Launch mode SharpDbg answers "configurationDone" only once the debuggee's .NET runtime
			// has started - for a native host (EXCEL.EXE loading an add-in) that can take arbitrarily
			// long. An adapter that dies instead still fails this promptly via DapClient.Disconnected.
			Task configurationDone = client.SendRequestAsync("configurationDone", null, cancellationToken,
				launchMode == DapLaunchMode.Launch ? Timeout.InfiniteTimeSpan : (TimeSpan?)null);
			// No timeout, but no silence either: say once what the session is waiting on
			if (launchMode == DapLaunchMode.Launch) {
				using (var noticeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
					var notice = Task.Delay(RuntimeLoadNoticeDelay, noticeCts.Token);
					if (await Task.WhenAny(configurationDone, notice).ConfigureAwait(false) == notice && notice.Status == TaskStatus.RanToCompletion) {
						OutputReceived?.Invoke("Still waiting for " + Path.GetFileName(launchTarget) +
							" to load the .NET runtime. Debugging starts when it does; press Stop to cancel." + Environment.NewLine);
						// Nothing above says WHY it has not happened. The adapter knows - it is the one
						// that launched the host and is waiting on its runtime - so quote it here rather
						// than let the session sit silent until someone guesses.
						string logTail = ReadAdapterLogTail();
						if (logTail != null) {
							OutputReceived?.Invoke("Adapter log (last " + AdapterLogTailLines + " lines):" + Environment.NewLine +
								logTail + Environment.NewLine);
						}
					}
					noticeCts.Cancel();
				}
			}
			await configurationDone.ConfigureAwait(false);

			// DapLaunchMode.AttachToSuspendedProcess left the debuggee's runtime suspended at
			// startup precisely so breakpoints/configuration land before any of its code runs -
			// resume it now that configuration is done, the same point a plain "launch" adapter
			// would start the debuggee at.
			if (launchMode == DapLaunchMode.AttachToSuspendedProcess && debuggeeProcess != null) {
				new DiagnosticsClient(debuggeeProcess.Id).ResumeRuntime();
			}

			Started?.Invoke();
		}

		public async Task SetExceptionBreakpointsAsync(IEnumerable<string> filters, CancellationToken cancellationToken = default)
		{
			var filterArray = new JsonArray();
			foreach (var filter in filters ?? Array.Empty<string>()) {
				filterArray.Add(filter);
			}
			await client.SendRequestAsync("setExceptionBreakpoints", new JsonObject {
				["filters"] = filterArray
			}, cancellationToken).ConfigureAwait(false);
		}

		Process LaunchDebuggeeSuspended(string dotnetHost, string targetDll, string workingDirectory, IEnumerable<string> arguments,
			IEnumerable<KeyValuePair<string, string>> launchEnvironment)
		{
			var processStartInfo = new ProcessStartInfo {
				FileName = dotnetHost,
				RedirectStandardInput = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(targetDll) ?? Environment.CurrentDirectory
			};
			processStartInfo.ArgumentList.Add(targetDll);
			foreach (var argument in arguments) {
				processStartInfo.ArgumentList.Add(argument);
			}
			// Suspends the runtime immediately at startup (before Main runs) so the DAP session
			// can attach and land breakpoints before any debuggee code executes; resumed by
			// ConfigurationDoneAsync above once the DAP configuration window closes.
			processStartInfo.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
			UseDotNetHost(processStartInfo, dotnetHost);
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
			// Apply explicit per-launch values after debugger sanitisation. In particular,
			// Uno Hot Reload requires DOTNET_MODIFIABLE_ASSEMBLIES=debug, which is otherwise
			// intentionally removed above to avoid leaking an IDE-wide setting into debuggees.
			if (launchEnvironment != null) {
				foreach (var variable in launchEnvironment) {
					processStartInfo.Environment[variable.Key] = variable.Value;
				}
			}

			var process = new Process { StartInfo = processStartInfo, EnableRaisingEvents = true };
			process.Start();
			process.OutputDataReceived += (s, e) => {
				if (!string.IsNullOrEmpty(e.Data))
					OutputReceived?.Invoke(e.Data + Environment.NewLine);
			};
			process.ErrorDataReceived += (s, e) => {
				if (!string.IsNullOrEmpty(e.Data))
					OutputReceived?.Invoke(e.Data + Environment.NewLine);
			};
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
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
			_ = client.SendRequestAsync(command, new JsonObject { ["threadId"] = ActiveThreadId })
				.ContinueWith(t => log("Control request '" + command + "' failed: " + t.Exception), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
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

		/// <summary>Restart the debug session (relaunch or re-attach).</summary>
		public async Task RestartAsync()
		{
			if (client == null) return;
			await client.SendRequestAsync("restart", new JsonObject()).ConfigureAwait(false);
		}

		/// <summary>Candidate positions ("Set Next Statement" targets) on a source line.</summary>
		public async Task<IReadOnlyList<DapGotoTarget>> GetGotoTargetsAsync(string fileName, int line, int? column = null)
		{
			if (client == null) return Array.Empty<DapGotoTarget>();
			var args = new JsonObject { ["source"] = new JsonObject { ["path"] = fileName }, ["line"] = line };
			if (column is int c) args["column"] = c;
			JsonObject response = await client.SendRequestAsync("gotoTargets", args).ConfigureAwait(false);
			var body = response?["body"] as JsonObject;
			var targets = body?["targets"] as JsonArray;
			var result = new List<DapGotoTarget>();
			if (targets != null) {
				foreach (var t in targets.OfType<JsonObject>()) {
					result.Add(new DapGotoTarget {
						Id = t["id"] != null ? t["id"].GetValue<int>() : 0,
						Label = t["label"] != null ? t["label"].GetValue<string>() : null,
						Line = t["line"] != null ? t["line"].GetValue<int>() : 0,
						Column = t["column"] != null ? t["column"].GetValue<int>() : 0
					});
				}
			}
			return result;
		}

		/// <summary>Move the instruction pointer to a goto target ("Set Next Statement").</summary>
		public async Task GotoAsync(int threadId, int targetId)
		{
			if (client == null) return;
			await client.SendRequestAsync("goto", new JsonObject { ["threadId"] = threadId, ["targetId"] = targetId }).ConfigureAwait(false);
		}

		/// <summary>Source files known to the debuggee (from loaded symbols).</summary>
		public async Task<IReadOnlyList<string>> GetLoadedSourcesAsync()
		{
			if (client == null) return Array.Empty<string>();
			JsonObject response = await client.SendRequestAsync("loadedSources", new JsonObject()).ConfigureAwait(false);
			var body = response?["body"] as JsonObject;
			var sources = body?["sources"] as JsonArray;
			var result = new List<string>();
			if (sources != null) {
				foreach (var s in sources.OfType<JsonObject>()) {
					var path = s["path"] != null ? s["path"].GetValue<string>() : null;
					if (!string.IsNullOrEmpty(path)) result.Add(path);
				}
			}
			return result;
		}

		/// <summary>Valid breakpoint positions in a source range.</summary>
		public async Task<IReadOnlyList<DapBreakpointLocation>> GetBreakpointLocationsAsync(string fileName, int line, int? endLine = null)
		{
			if (client == null) return Array.Empty<DapBreakpointLocation>();
			var args = new JsonObject { ["source"] = new JsonObject { ["path"] = fileName }, ["line"] = line };
			if (endLine is int el) args["endLine"] = el;
			JsonObject response = await client.SendRequestAsync("breakpointLocations", args).ConfigureAwait(false);
			var body = response?["body"] as JsonObject;
			var locations = body?["breakpoints"] as JsonArray;
			var result = new List<DapBreakpointLocation>();
			if (locations != null) {
				foreach (var l in locations.OfType<JsonObject>()) {
					result.Add(new DapBreakpointLocation {
						Line = l["line"] != null ? l["line"].GetValue<int>() : 0,
						Column = l["column"] != null ? l["column"].GetValue<int>() : 0,
						EndLine = l["endLine"] != null ? l["endLine"].GetValue<int>() : 0,
						EndColumn = l["endColumn"] != null ? l["endColumn"].GetValue<int>() : 0
					});
				}
			}
			return result;
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
			// A session that never surfaced its adapter log leaves nothing behind; one that did keeps
			// the file, because that log is the only post-mortem left of a session that went quiet.
			if (!adapterLogReported && !string.IsNullOrEmpty(adapterLogPath)) {
				try {
					File.Delete(adapterLogPath);
				} catch (IOException) {
				} catch (UnauthorizedAccessException) {
				}
			}
		}

		/// <param name="debuggeeHost">The dotnet host matching the debuggee's architecture. SharpDbg loads
		/// the dbgshim build matching its own process, and dbgshim cannot debug a process of another
		/// architecture, so the adapter's bitness follows the debuggee, never the IDE's.</param>
		/// <param name="logPath">Where to ask SharpDbg to write its own diagnostics. It logs nothing
		/// unless told to, so this is the only record of what the adapter was doing while a session
		/// was making no progress.</param>
		static Process LaunchAdapter(string adapterDll, string debuggeeHost, string logPath)
		{
			var processStartInfo = new ProcessStartInfo {
				FileName = debuggeeHost,
				Arguments = "\"" + adapterDll + "\" --interpreter=vscode --engineLogging=\"" + logPath + "\"",
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			UseDotNetHost(processStartInfo, debuggeeHost);
			var process = new Process {
				StartInfo = processStartInfo,
				EnableRaisingEvents = true
			};
			process.Start();
			return process;
		}

		static string CreateAdapterLogPath()
		{
			try {
				string directory = Path.Combine(Path.GetTempPath(), "OpenDevelop-DebugAdapter");
				Directory.CreateDirectory(directory);
				// CleanupSession only runs when the session is disposed, and a hard kill of the IDE
				// (which is how a test run ends) skips it. Sweep the stale ones so a crash cannot
				// accumulate them; a live session's file is seconds old, so nothing in flight matches.
				foreach (string stale in Directory.GetFiles(directory, "sharpdbg-*.log")) {
					try {
						if (DateTime.Now - File.GetLastWriteTime(stale) > TimeSpan.FromDays(1))
							File.Delete(stale);
					} catch (IOException) {
					} catch (UnauthorizedAccessException) {
					}
				}
				return Path.Combine(directory, $"sharpdbg-{DateTime.Now:yyyyMMdd-HHmmssfff}-pid{Environment.ProcessId}.log");
			} catch (IOException) {
				return null;
			} catch (UnauthorizedAccessException) {
				return null;
			}
		}

		/// <summary>The tail of the adapter's own log, or null if it wrote none. Marks the log as
		/// reported so it survives for post-mortem instead of being deleted with the session.</summary>
		string ReadAdapterLogTail()
		{
			if (string.IsNullOrEmpty(adapterLogPath) || !File.Exists(adapterLogPath))
				return null;
			string[] lines;
			try {
				lines = File.ReadAllLines(adapterLogPath);
			} catch (IOException) {
				return null;
			} catch (UnauthorizedAccessException) {
				return null;
			}
			if (lines.Length == 0)
				return null;
			adapterLogReported = true;
			return string.Join(Environment.NewLine, lines, Math.Max(0, lines.Length - AdapterLogTailLines), Math.Min(lines.Length, AdapterLogTailLines));
		}

		Exception DescribeAdapterExit(Process adapter)
		{
			string message = "The debug adapter (SharpDbg) closed the connection";
			try {
				// stdout ends slightly before the process is reaped; give it a moment for the exit code.
				// Bounded only: a debuggee that inherited the adapter's stderr pipe keeps it open, so an
				// unbounded WaitForExit() (which waits for that stream's EOF) could never return.
				if (adapter.WaitForExit(2000) && adapter.HasExited)
					message += " and exited with code " + adapter.ExitCode;
			} catch (InvalidOperationException) {
			} catch (System.ComponentModel.Win32Exception) {
			}
			string[] stderr;
			lock (adapterStderrTail) {
				stderr = adapterStderrTail.ToArray();
			}
			message += stderr.Length > 0
				? "." + Environment.NewLine + string.Join(Environment.NewLine, stderr)
				: ". It wrote nothing to stderr.";
			// The adapter's own log is the only place its internal steps are recorded, and a dead
			// adapter's last words there are what the stderr tail above usually just truncates.
			string logTail = ReadAdapterLogTail();
			if (logTail != null)
				message += Environment.NewLine + "Adapter log (last " + AdapterLogTailLines + " lines):" + Environment.NewLine + logTail;
			return new IOException(message);
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

		/// <summary>
		/// Points a child process at <paramref name="host"/>'s install. Processes inherit this
		/// environment, and a DOTNET_ROOT left pointing at the IDE's install would make a debuggee's
		/// runtime resolve from the IDE's architecture instead of its own.
		/// </summary>
		static void UseDotNetHost(ProcessStartInfo startInfo, string host)
		{
			if (!Path.IsPathRooted(host))
				return;
			foreach (string name in new[] { "DOTNET_ROOT", "DOTNET_ROOT(x86)", "DOTNET_ROOT_X86", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" })
				startInfo.Environment.Remove(name);
			startInfo.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(host);
			startInfo.Environment["DOTNET_HOST_PATH"] = host;
		}

		/// <summary>
		/// The dotnet host whose architecture matches <paramref name="target"/>: a native program's
		/// machine, or a managed assembly's platform target. Only an AnyCPU assembly (or a target that
		/// cannot be read) runs under the configured SDK's host.
		/// </summary>
		static string ResolveDebuggeeHost(string target)
		{
			string defaultHost = ResolveDotNetHost();
			// Linux keeps the configured host: no multi-architecture install layout is probed there.
			if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
				return defaultHost;
			// An AnyCPU app.dll runs as whatever its app.exe launcher is - the SDK that built it picks
			// that, not the IDE (an x64 OpenDevelop on ARM64 still sees ARM64 apphosts from CLI builds)
			Machine? required = ReadTargetMachine(target) ?? ReadApphostMachine(target);
			if (required == null || required == ReadMachine(defaultHost))
				return defaultHost;
			string archName;
			switch (required.Value) {
				case Machine.I386: archName = "x86"; break;
				case Machine.Amd64: archName = "x64"; break;
				case Machine.Arm64: archName = "arm64"; break;
				default: return defaultHost;
			}
			// A program the OS cannot run at all deserves that answer, not "install a runtime".
			// OSArchitecture is the real OS even from a WOW64 or x64-emulated IDE process.
			var os = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
			if (!CanRunOn(required.Value, os)) {
				throw new InvalidOperationException(
					Path.GetFileName(target) + " is an " + archName + " program and cannot run on this " +
					os + " machine. Build it for AnyCPU or this machine's architecture to debug it here.");
			}
			foreach (string root in CandidateDotNetRoots(archName)) {
				string candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
				if (ReadMachine(candidate) == required && HasRuntime(root))
					return candidate;
			}
			throw new InvalidOperationException(
				Path.GetFileName(target) + " is a " + archName + " program, but no " + archName +
				" .NET runtime is installed. Install the " + archName + " .NET runtime to debug it.");
		}

		/// <summary>
		/// Whether <paramref name="root"/> holds a .NET runtime, not just the dotnet muxer. Measured on
		/// macOS: /usr/local/share/dotnet/x64 can contain an x86_64 dotnet with empty host/fxr and
		/// shared folders, and an adapter started under it dies before it can say why.
		/// </summary>
		static bool HasRuntime(string root)
		{
			try {
				string runtimes = Path.Combine(root, "shared", "Microsoft.NETCore.App");
				return Directory.Exists(runtimes) && Directory.EnumerateDirectories(runtimes).Any();
			} catch (IOException) {
				return false;
			} catch (UnauthorizedAccessException) {
				return false;
			}
		}

		/// <summary>x86 runs on any Windows (WOW64) and never on macOS, x64 on x64 and ARM64 (Windows
		/// emulation, Rosetta 2), ARM64 only on ARM64.</summary>
		static bool CanRunOn(Machine program, System.Runtime.InteropServices.Architecture os)
		{
			switch (program) {
				case Machine.I386: return OperatingSystem.IsWindows();
				case Machine.Amd64: return os == System.Runtime.InteropServices.Architecture.X64 || os == System.Runtime.InteropServices.Architecture.Arm64;
				case Machine.Arm64: return os == System.Runtime.InteropServices.Architecture.Arm64;
				default: return false;
			}
		}

		static IEnumerable<string> CandidateDotNetRoots(string archName)
		{
			if (OperatingSystem.IsMacOS()) {
				// The installer puts the native runtime in /usr/local/share/dotnet and an x64 one on
				// Apple Silicon in its x64 subfolder; DOTNET_ROOT_<ARCH> is the documented override.
				string overridden = Environment.GetEnvironmentVariable("DOTNET_ROOT_" + archName.ToUpperInvariant());
				if (!string.IsNullOrEmpty(overridden))
					yield return overridden;
				yield return Path.Combine("/usr/local/share/dotnet", archName);
				yield return "/usr/local/share/dotnet";
				yield break;
			}
			// The installer records each architecture's location in the 32-bit registry view
			string registered = null;
			try {
				using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32))
				using (var key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\" + archName))
					registered = key?.GetValue("InstallLocation") as string;
			} catch (System.Security.SecurityException) {
			} catch (UnauthorizedAccessException) {
			}
			if (!string.IsNullOrEmpty(registered))
				yield return registered;
			string programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
			if (archName == "x86") {
				yield return Path.Combine(programFilesX86, "dotnet");
			} else {
				yield return Path.Combine(programFiles, "dotnet", archName); // emulated x64 on ARM64
				yield return Path.Combine(programFiles, "dotnet");
			}
		}

		/// <summary>The architecture <paramref name="path"/> must run as, or null for AnyCPU / unreadable.</summary>
		static Machine? ReadTargetMachine(string path)
		{
			try {
				if (!File.Exists(path))
					return null;
				// A native host on macOS (the counterpart of EXCEL.EXE) is a Mach-O image
				if (ReadMachOMachine(path, out var machO))
					return machO;
				using (var stream = File.OpenRead(path))
				using (var peReader = new PEReader(stream)) {
					var headers = peReader.PEHeaders;
					if (headers.CorHeader == null)
						return headers.CoffHeader.Machine;
					var flags = headers.CorHeader.Flags;
					if (headers.CoffHeader.Machine != Machine.I386)
						return headers.CoffHeader.Machine; // PlatformTarget x64 / ARM64
					// x86 sets 32BITREQUIRED; AnyCPU "Prefer 32-bit" adds 32BITPREFERRED and still
					// runs 64-bit on a 64-bit OS, so only the former pins the bitness.
					bool requires32Bit = (flags & CorFlags.Requires32Bit) != 0 && (flags & CorFlags.Prefers32Bit) == 0;
					return requires32Bit ? Machine.I386 : (Machine?)null;
				}
			} catch (BadImageFormatException) {
				return null;
			} catch (IOException) {
				return null;
			} catch (UnauthorizedAccessException) {
				return null;
			}
		}

		/// <summary>The machine of the native launcher next to a managed app.dll, or null if there is none.</summary>
		static Machine? ReadApphostMachine(string target)
		{
			if (!string.Equals(Path.GetExtension(target), ".dll", StringComparison.OrdinalIgnoreCase))
				return null;
			// The apphost has no extension outside Windows
			string apphost = OperatingSystem.IsWindows() ? Path.ChangeExtension(target, ".exe") : Path.ChangeExtension(target, null);
			// A managed .exe (net48-style) is not an apphost and says nothing about the bitness
			return File.Exists(apphost) && !ICSharpCode.SharpDevelop.Services.WindowsDebugger.IsManagedAssembly(apphost) ? ReadMachine(apphost) : null;
		}

		/// <summary>The machine of an executable: a PE image's COFF machine, or a thin Mach-O image's CPU
		/// type (a native host or apphost on macOS). Null for a universal binary, which runs natively.</summary>
		static Machine? ReadMachine(string path)
		{
			try {
				if (!File.Exists(path))
					return null;
				if (ReadMachOMachine(path, out var machO))
					return machO;
				using (var stream = File.OpenRead(path))
				using (var peReader = new PEReader(stream))
					return peReader.PEHeaders.CoffHeader.Machine;
			} catch (BadImageFormatException) {
				return null;
			} catch (IOException) {
				return null;
			} catch (UnauthorizedAccessException) {
				return null;
			}
		}

		/// <summary>True when <paramref name="path"/> is a Mach-O image; <paramref name="machine"/> is then its
		/// CPU (null for a fat/universal binary or a CPU this does not map).</summary>
		static bool ReadMachOMachine(string path, out Machine? machine)
		{
			const uint MachO64 = 0xFEEDFACF, MachO32 = 0xFEEDFACE, FatMagic = 0xCAFEBABE, FatMagic64 = 0xCAFEBABF;
			const uint CpuX86 = 7, CpuX64 = 0x01000007, CpuArm64 = 0x0100000C;
			machine = null;
			using (var reader = new BinaryReader(File.OpenRead(path))) {
				if (reader.BaseStream.Length < 8)
					return false;
				uint magic = reader.ReadUInt32();
				// Fat headers are big-endian, so read as little-endian their magic comes out byte-swapped
				if (magic == FatMagic || magic == FatMagic64 || magic == 0xBEBAFECA || magic == 0xBFBAFECA)
					return true;
				if (magic != MachO64 && magic != MachO32)
					return false;
				switch (reader.ReadUInt32()) {
					case CpuX64: machine = Machine.Amd64; break;
					case CpuArm64: machine = Machine.Arm64; break;
					case CpuX86: machine = Machine.I386; break;
				}
				return true;
			}
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
