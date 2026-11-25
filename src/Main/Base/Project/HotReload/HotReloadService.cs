using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.ILSpy.Util;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using ProtocolHotReloadDeltaStatus = ICSharpCode.SharpDevelop.LanguageServices.Protocol.HotReloadDeltaStatus;

namespace ICSharpCode.SharpDevelop.Project.HotReload
{
	/// <summary>
	/// Resolves the Hot Reload adapter for a project and owns the live session.
	/// This exists so no shared launch code names a framework: adding a framework is an AddIn
	/// contribution to /SharpDevelop/HotReload/Adapters, not an edit to DefaultProjectBehavior.
	/// </summary>
	public static class HotReloadService
	{
		static IReadOnlyList<IApplicationHotReloadAdapter> adapters;
		static readonly object adaptersLock = new object();

		public static IReadOnlyList<IApplicationHotReloadAdapter> Adapters {
			get {
				lock (adaptersLock) {
					if (adapters == null) {
						try {
							adapters = AddInTree.BuildItems<IApplicationHotReloadAdapter>(
								"/SharpDevelop/HotReload/Adapters", null, false);
						} catch (Exception ex) {
							// A broken contribution must not take Run/Debug down with it.
							LoggingService.Warn("Hot Reload: could not build the adapter list: " + ex.Message);
							adapters = Array.Empty<IApplicationHotReloadAdapter>();
						}
					}
					return adapters;
				}
			}
		}

		/// <summary>The session for the application currently launched with Hot Reload, if any.</summary>
		public static IHotReloadSession CurrentSession { get; private set; }

		/// <summary>
		/// When the current session's launch was configured, after its build: a source file written later is
		/// no longer the text the running application was compiled from.
		/// </summary>
		public static DateTime CurrentSessionStartedUtc { get; private set; }

		/// <summary>Optional debugger-addin bridge for applying a delta through DAP.</summary>
		public static Func<IHotReloadDeltaApplier> DebuggerDeltaApplierFactory { get; set; }

		/// <summary>Visual Studio's "Enable Hot Reload" option; read when a launch is configured.</summary>
		public static bool IsEnabled {
			get => PropertyService?.Get("Debugger.HotReload.Enabled", true) ?? true;
			set => PropertyService?.Set("Debugger.HotReload.Enabled", value);
		}

		/// <summary>Visual Studio's "Hot Reload on file save" option.</summary>
		public static bool ApplyOnFileSave {
			get => PropertyService?.Get("Debugger.HotReload.ApplyOnFileSave", false) ?? false;
			set => PropertyService?.Set("Debugger.HotReload.ApplyOnFileSave", value);
		}

		static IPropertyService PropertyService {
			get {
				try { return SD.GetService<IPropertyService>(); } catch { return null; }
			}
		}

		public static event EventHandler CurrentSessionChanged;

		static int lifecycleHooked;

		/// <summary>
		/// A session outlives nothing: once the application it belongs to is gone, keeping it would
		/// leave Apply enabled and send edits into a dead endpoint. Subscribed lazily on the first
		/// launch so this costs nothing in a run that never uses Hot Reload.
		/// </summary>
		static void EnsureLifecycleHooked()
		{
			if (Interlocked.Exchange(ref lifecycleHooked, 1) != 0)
				return;

			MessageBus<SolutionClosedMessageEventArgs>.Subscribers += (_, _) => DisposeCurrentSession();
			FileUtility.FileSaved += OnFileSaved;

			// A debugger-hosted application dies with its debug session. A Run-without-debugging
			// launch is not covered by this, which is why the session also treats a dead endpoint
			// as Disconnected rather than trusting this signal alone.
			try {
				var debugger = SD.Debugger;
				if (debugger != null)
					debugger.DebugStopped += (_, _) => DisposeCurrentSession();
			} catch (Exception ex) {
				LoggingService.Warn("Hot Reload: could not observe the debugger lifecycle: " + ex.Message);
			}
		}

		/// <summary>
		/// First adapter that claims the project, or null. Cheap enough for command enablement.
		/// </summary>
		public static IApplicationHotReloadAdapter FindAdapter(IProject project, out string diagnostic)
			=> FindAdapter(project, withDebugger: false, out diagnostic);

		public static IApplicationHotReloadAdapter FindAdapter(IProject project, bool withDebugger, out string diagnostic)
		{
			diagnostic = null;
			if (project == null) {
				diagnostic = "No startup project is selected.";
				return null;
			}

			var context = new HotReloadLaunchContext(project, withDebugger);
			string lastDiagnostic = null;
			foreach (var adapter in Adapters) {
				try {
					if (adapter.CanHandle(context, out var adapterDiagnostic))
						return adapter;
					lastDiagnostic = adapterDiagnostic ?? lastDiagnostic;
				} catch (Exception ex) {
					LoggingService.Warn($"Hot Reload: adapter '{adapter.Framework}' failed CanHandle: {ex.Message}");
				}
			}

			diagnostic = lastDiagnostic ?? "No Hot Reload adapter supports this project.";
			return null;
		}

		public static bool IsSupported(IProject project) => FindAdapter(project, out _) != null;

		public static bool IsSupported(IProject project, bool withDebugger) => FindAdapter(project, withDebugger, out _) != null;

		/// <summary>
		/// Configures <paramref name="startInfo"/> for Hot Reload and starts the session. Returns
		/// false when no adapter applies or the adapter could not start; Hot Reload is a launch
		/// enhancement, so a failure here must never prevent the application from running.
		/// </summary>
		public static bool TryConfigureLaunch(IProject project, ProcessStartInfo startInfo, bool withDebugger)
		{
			if (startInfo == null)
				return false;
			if (!IsEnabled) {
				LoggingService.Info("Hot Reload was not enabled: the Debugger.HotReload.Enabled option is off.");
				return false;
			}

			var adapter = FindAdapter(project, withDebugger, out var diagnostic);
			if (adapter == null) {
				LoggingService.Warn("Hot Reload was not enabled: " + diagnostic);
				return false;
			}

			try {
				EnsureLifecycleHooked();
				DisposeCurrentSession();
				var context = new HotReloadLaunchContext(project, withDebugger);
				// The launch path that calls this is synchronous, and the adapter's own start work
				// must not be marshalled back onto this thread, so run it off the UI context.
				var session = Task.Run(() => adapter.StartAsync(context, startInfo, CancellationToken.None))
					.GetAwaiter().GetResult();
				if (session == null) {
					LoggingService.Warn($"Hot Reload: adapter '{adapter.Framework}' returned no session.");
					return false;
				}

				CurrentSession = session;
				CurrentSessionStartedUtc = DateTime.UtcNow;
				if (!TryCaptureLaunchSnapshot(project, startInfo.FileName, out var protocol, out var request, out var snapshotDiagnostic)) {
					// Code edits cannot be applied in this session: say why where Apply results go.
					if (!string.IsNullOrEmpty(snapshotDiagnostic))
						HotReloadOutput.Write(adapter.Framework, "Code changes cannot be applied in this session: " + snapshotDiagnostic);
					(session as ICodeDeltaHotReloadSession)?.NotifyBaselineCaptured(false, snapshotDiagnostic);
				}
				else {
					var debuggerDeltaApplier = withDebugger ? new DeferredDebuggerDeltaApplier() : null;
					if (session is AgentPipeHotReloadSession agentSession)
						agentSession.ConfigureCodeDelta(protocol!, request!.Project, debuggerDeltaApplier);
					else if (session is ICodeDeltaHotReloadSession codeSession)
						codeSession.ConfigureCodeDelta(protocol!, request!.Project, debuggerDeltaApplier);
					SendLaunchBaselineAsync(protocol!, request!, project.Name, adapter.Framework, session).FireAndForget();
				}
				CurrentSessionChanged?.Invoke(null, EventArgs.Empty);
				LoggingService.Info($"Hot Reload: session started for {adapter.Framework}.");
				// The launch has only been CONFIGURED at this point; the application starts after
				// this returns. Drive readiness in the background so the session actually reaches
				// Ready and the status surface can say so - without it the session sits in Starting
				// forever and only an Apply would ever discover the agent was up all along.
				ObserveReadinessAsync(session).FireAndForget();
				return true;
			} catch (Exception ex) {
				LoggingService.Warn($"Hot Reload was not enabled: {ex.Message}");
				return false;
			}
		}

		/// <summary>
		/// Starts a Hot Reload session for an already attached process. There is no launch snapshot:
		/// the caller supplies the loaded module path and the matching project, and the baseline is
		/// captured from those files. Returns false when the baseline inputs are unavailable.
		/// </summary>
		public static async Task<bool> StartAttachedSessionAsync(IHotReloadSession session, IProject project, string modulePath)
		{
			if (!IsEnabled || session == null || project == null || string.IsNullOrEmpty(modulePath))
				return false;
			try {
				EnsureLifecycleHooked();
				if (!TryCaptureLaunchSnapshot(project, modulePath, out var protocol, out var request, out var diagnostic)) {
					HotReloadOutput.Write(session.Framework, "Code changes cannot be applied in this session: " + diagnostic);
					(session as ICodeDeltaHotReloadSession)?.NotifyBaselineCaptured(false, diagnostic);
					return false;
				}
				// Unlike launch, attach has no debugger lifecycle event that can retry a failed baseline:
				// prove the baseline before exposing the session, so a normal attach is never blocked by
				// a Hot Reload apply that can only fail.
				var baseline = await HotReloadLaunchBaseline.SendAsync(protocol!, request!, CancellationToken.None).ConfigureAwait(false);
				if (baseline.Status != ProtocolHotReloadDeltaStatus.Applied) {
					HotReloadOutput.Write(session.Framework, "Code changes cannot be applied in this session: " + baseline.Diagnostic);
					(session as ICodeDeltaHotReloadSession)?.NotifyBaselineCaptured(false, baseline.Diagnostic);
					return false;
				}
				DisposeCurrentSession();
				CurrentSession = session;
				CurrentSessionStartedUtc = DateTime.UtcNow;
				var applier = new DeferredDebuggerDeltaApplier();
				if (session is ICodeDeltaHotReloadSession codeSession) {
					codeSession.ConfigureCodeDelta(protocol!, request!.Project, applier);
					codeSession.NotifyBaselineCaptured(true, baseline.Diagnostic);
				}
				CurrentSessionChanged?.Invoke(null, EventArgs.Empty);
				ObserveReadinessAsync(session).FireAndForget();
				LoggingService.Info($"Hot Reload: attached session started for {project.Name}.");
				return true;
			} catch (Exception ex) {
				LoggingService.Warn("Hot Reload: could not start the attached session: " + ex.Message);
				return false;
			}
		}

		// The host already owns the evaluated project graph. The IDE supplies only the PE/PDB which
		// this launch will load, preventing a later build from changing the EnC baseline underneath
		// a live target. Delta application remains deliberately separate (E1/E3).
		static bool TryCaptureLaunchSnapshot(IProject project, string launchModulePath,
			out IRoslynLanguageProtocol? protocol, out HotReloadBaselineRequest? request, out string diagnostic)
		{
			protocol = null;
			request = null;
			diagnostic = string.Empty;
			if (project is null || (!string.Equals(project.Language, "C#", StringComparison.OrdinalIgnoreCase)
				&& !string.Equals(project.Language, "VB", StringComparison.OrdinalIgnoreCase)))
				return false;
			try {
				var registry = SD.GetService<LanguageServiceRegistry>();
				var languageExtension = string.Equals(project.Language, "VB", StringComparison.OrdinalIgnoreCase) ? ".vb" : ".cs";
				if (registry == null || !registry.TryGetProtocol(languageExtension, out protocol)) {
					diagnostic = "No Roslyn language host is available.";
					return false;
				}
				var key = new HotReloadProjectId(project.FileName.ToString(),
					ProjectTargetFrameworkService.GetActiveTargetFramework(project));
				return HotReloadLaunchBaseline.TrySnapshot(key, launchModulePath, out request, out diagnostic);
			} catch (Exception ex) {
				diagnostic = ex.Message;
				return false;
			}
		}

		static async Task SendLaunchBaselineAsync(IRoslynLanguageProtocol protocol, HotReloadBaselineRequest request,
			string projectName, string framework, IHotReloadSession session)
		{
			try {
				var result = await HotReloadLaunchBaseline.SendAsync(protocol, request, CancellationToken.None).ConfigureAwait(false);
				var applied = result.Status == ProtocolHotReloadDeltaStatus.Applied;
				if (!applied)
					HotReloadOutput.Write(framework, $"Code changes cannot be applied in this session ({projectName}): " + result.Diagnostic);
				else LoggingService.Info("Hot Reload: EnC baseline captured for " + projectName + ".");
				(session as ICodeDeltaHotReloadSession)?.NotifyBaselineCaptured(applied, result.Diagnostic);
			} catch (Exception ex) {
				HotReloadOutput.Write(framework, $"Code changes cannot be applied in this session ({projectName}): " + ex.Message);
				(session as ICodeDeltaHotReloadSession)?.NotifyBaselineCaptured(false, ex.Message);
			}
		}

		static async Task ObserveReadinessAsync(IHotReloadSession session)
		{
			var ready = await session.WaitForReadyAsync(ReadinessTimeout, CancellationToken.None)
				.ConfigureAwait(false);
			if (!ReferenceEquals(CurrentSession, session))
				return; // Superseded by another launch while we were waiting.

			LoggingService.Info(ready
				? $"Hot Reload: {session.Framework} is ready."
				: $"Hot Reload: {session.Framework} did not become ready ({session.State}).");
		}

		/// <summary>
		/// Generous on purpose: this covers the application's whole start-up, which for a debugger
		/// launch includes the adapter handshake before the agent can answer at all.
		/// </summary>
		static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);

		static void OnFileSaved(object sender, FileNameEventArgs e)
		{
			if (!IsEnabled || !ApplyOnFileSave)
				return;
			var session = CurrentSession;
			if (session is not ICodeDeltaHotReloadSession)
				return;
			if (e == null || !e.FileName.HasExtension(".cs") && !e.FileName.HasExtension(".vb"))
				return;
			Task.Run(() => {
				var result = ApplyPendingChangesBeforeResume();
				if (result is null || result.Outcome == HotReloadOutcome.NoChanges)
					return;
				HotReloadOutput.Write(session.Framework, $"{e.FileName.GetFileName()}: {result.Outcome} - {result.Message}");
				if (result.Outcome is HotReloadOutcome.RestartRequired or HotReloadOutcome.BuildRequired
					or HotReloadOutcome.Unsupported or HotReloadOutcome.Failed) {
					SD.MainThread.InvokeAsyncAndForget(() => MessageService.ShowWarning(result.Message));
				}
			}).FireAndForget();
		}

		/// <summary>
		/// Applies every pending code edit before the debugger resumes (Continue/Step/Set Next
		/// Statement), matching Visual Studio's break-mode Hot Reload gesture. Returns null when
		/// no code-delta session is active; NoChanges means there was nothing to apply.
		/// </summary>
		public static HotReloadApplyResult ApplyPendingChangesBeforeResume()
		{
			if (CurrentSession is not ICodeDeltaHotReloadSession session)
				return null;
			try {
				return Task.Run(() => session.ApplyPendingChangesAsync(CancellationToken.None)).GetAwaiter().GetResult();
			} catch (Exception ex) {
				LoggingService.Warn("Hot Reload: applying pending changes before resume failed: " + ex.Message);
				return new HotReloadApplyResult(HotReloadOutcome.NoChanges, ex.Message, ex.ToString());
			}
		}

		public static void DisposeCurrentSession()
		{
			var session = CurrentSession;
			if (session == null)
				return;
			CurrentSession = null;
			try {
				Task.Run(() => session.DisposeAsync().AsTask()).GetAwaiter().GetResult();
			} catch (Exception ex) {
				LoggingService.Warn("Hot Reload: disposing the session failed: " + ex.Message);
			}
			CurrentSessionChanged?.Invoke(null, EventArgs.Empty);
		}

		/// <summary>
		/// Resolves the debugger applier at apply time, not at launch time: the Debugger addin's
		/// service (which owns the DAP session) may be constructed after the launch adapter runs.
		/// </summary>
		sealed class DeferredDebuggerDeltaApplier : IHotReloadDeltaApplier
		{
			public Task<HotReloadApplyResult> ApplyAsync(HotReloadModuleDelta delta, CancellationToken token)
			{
				var applier = DebuggerDeltaApplierFactory?.Invoke();
				if (applier is null)
					return Task.FromResult(new HotReloadApplyResult(HotReloadOutcome.Unsupported,
						"The SharpDbg debugger is not available to apply the code delta."));
				return applier.ApplyAsync(delta, token);
			}
		}

		/// <summary>Test seam: replaces the AddIn-tree lookup with an explicit adapter list.</summary>
		public static void SetAdaptersForTesting(IReadOnlyList<IApplicationHotReloadAdapter> testAdapters)
		{
			lock (adaptersLock) {
				adapters = testAdapters;
			}
		}
	}
}
