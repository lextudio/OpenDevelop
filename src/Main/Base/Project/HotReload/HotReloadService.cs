using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.ILSpy.Util;

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
		{
			diagnostic = null;
			if (project == null) {
				diagnostic = "No startup project is selected.";
				return null;
			}

			var context = new HotReloadLaunchContext(project, withDebugger: false);
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

		/// <summary>
		/// Configures <paramref name="startInfo"/> for Hot Reload and starts the session. Returns
		/// false when no adapter applies or the adapter could not start; Hot Reload is a launch
		/// enhancement, so a failure here must never prevent the application from running.
		/// </summary>
		public static bool TryConfigureLaunch(IProject project, ProcessStartInfo startInfo, bool withDebugger)
		{
			if (startInfo == null)
				return false;

			var adapter = FindAdapter(project, out var diagnostic);
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

		/// <summary>Test seam: replaces the AddIn-tree lookup with an explicit adapter list.</summary>
		public static void SetAdaptersForTesting(IReadOnlyList<IApplicationHotReloadAdapter> testAdapters)
		{
			lock (adaptersLock) {
				adapters = testAdapters;
			}
		}
	}
}
