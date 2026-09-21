using System;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Project.HotReload.Wpf
{
	/// <summary>
	/// A live session with the in-process WPF agent. Readiness is proved by the agent answering on
	/// its pipe, never by the application process merely existing.
	/// </summary>
	sealed class WpfHotReloadSession : IHotReloadSession
	{
		readonly WpfHotReloadPipeClient client;
		readonly CancellationTokenSource disposal = new CancellationTokenSource();
		HotReloadSessionState state = HotReloadSessionState.Starting;

		public WpfHotReloadSession(IApplicationHotReloadAdapter adapter, HotReloadCapabilities capabilities,
			string pipeName, string logFile)
		{
			Framework = adapter.Framework;
			Capabilities = capabilities;
			PipeName = pipeName;
			LogFile = logFile;
			client = new WpfHotReloadPipeClient(pipeName);
		}

		public string Framework { get; }

		public HotReloadCapabilities Capabilities { get; }

		public string PipeName { get; }

		public string LogFile { get; }

		public int? ProcessId { get; internal set; }

		/// <summary>Why the last readiness or apply attempt failed, for the status surface and tests.</summary>
		public string LastError { get; private set; }

		public HotReloadSessionState State {
			get => state;
			private set {
				if (state == value)
					return;
				state = value;
				StateChanged?.Invoke(this, value);
			}
		}

		public event EventHandler<HotReloadSessionState> StateChanged;

		public System.Collections.Generic.IReadOnlyDictionary<string, string> GetDiagnostics()
			=> new System.Collections.Generic.Dictionary<string, string> {
				["endpoint"] = PipeName,
				["agentLog"] = LogFile,
				["state"] = State.ToString(),
				["lastError"] = LastError ?? "",
			};

		public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken token)
		{
			if (State == HotReloadSessionState.Ready)
				return true;

			State = HotReloadSessionState.Connecting;
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, disposal.Token);
			try {
				// "agent.ready" is answered on the UI thread of the target application, so a reply
				// also proves the application is up and pumping, not merely that a socket exists.
				var value = await client.QueryAsync("agent.ready", timeout, linked.Token).ConfigureAwait(false);
				if (value == "1") {
					State = HotReloadSessionState.Ready;
					return true;
				}
				State = HotReloadSessionState.Failed;
				return false;
			} catch (OperationCanceledException) {
				State = HotReloadSessionState.Disconnected;
				return false;
			} catch (Exception ex) {
				LastError = ex.Message;
				LoggingService.Warn($"WPF Hot Reload: agent did not become ready: {ex.Message}");
				State = HotReloadSessionState.Failed;
				return false;
			}
		}

		public async Task<HotReloadApplyResult> ApplyAsync(HotReloadDocumentChange change, CancellationToken token)
		{
			if (change == null)
				throw new ArgumentNullException(nameof(change));
			if (State == HotReloadSessionState.Disconnected || State == HotReloadSessionState.Stopped)
				return new HotReloadApplyResult(HotReloadOutcome.Disconnected, "The Hot Reload session has ended.");

			using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, disposal.Token);
			if (State != HotReloadSessionState.Ready
				&& !await WaitForReadyAsync(TimeSpan.FromSeconds(30), linked.Token).ConfigureAwait(false)) {
				return new HotReloadApplyResult(HotReloadOutcome.Failed, "The WPF Hot Reload agent is not ready.");
			}

			State = HotReloadSessionState.Applying;
			try {
				var response = await client.SendAsync(new WpfHotReloadPipeClient.AgentRequest {
					Kind = "apply",
					FilePath = change.FilePath,
					XamlText = change.CurrentText,
					PreviousXamlText = change.PreviousAcceptedText,
					ChangeKind = change.Kind.ToString(),
				}, TimeSpan.FromSeconds(30), linked.Token).ConfigureAwait(false);

				// The agent's own wording decides the outcome; a delivered request is not success.
				if (!response.IsOk) {
					var message = response.Result ?? "The agent rejected the change.";
					var outcome = message.IndexOf("restart", StringComparison.OrdinalIgnoreCase) >= 0
						? HotReloadOutcome.RestartRequired
						: HotReloadOutcome.Failed;
					State = outcome == HotReloadOutcome.RestartRequired
						? HotReloadSessionState.RestartRequired
						: HotReloadSessionState.Failed;
					return new HotReloadApplyResult(outcome, message, message);
				}

				State = HotReloadSessionState.Applied;
				State = HotReloadSessionState.Ready;
				return new HotReloadApplyResult(HotReloadOutcome.Applied, response.Result, response.Result);
			} catch (OperationCanceledException) {
				State = HotReloadSessionState.Disconnected;
				return new HotReloadApplyResult(HotReloadOutcome.Disconnected, "The Hot Reload session has ended.");
			} catch (TimeoutException ex) {
				// The endpoint stopped answering. Closing an application that was started WITHOUT a
				// debugger raises no IDE event, so this is the only signal that the session is over;
				// reporting Failed instead would leave Apply enabled against a dead process.
				LastError = ex.Message;
				State = HotReloadSessionState.Disconnected;
				return new HotReloadApplyResult(HotReloadOutcome.Disconnected,
					"The application is no longer reachable; its Hot Reload session has ended.", ex.Message);
			} catch (Exception ex) {
				LastError = ex.Message;
				State = HotReloadSessionState.Failed;
				return new HotReloadApplyResult(HotReloadOutcome.Failed, ex.Message, ex.ToString());
			}
		}

		public ValueTask DisposeAsync()
		{
			// The agent lives and dies with the application; there is nothing to tear down
			// remotely, so ending the session only stops this side talking to it.
			disposal.Cancel();
			State = HotReloadSessionState.Stopped;
			disposal.Dispose();
			return default;
		}
	}
}
