// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the
// Software without restriction, including without limitation the rights to use, copy,
// modify, merge, publish, distribute, sublicense, and/or sell copies of the Software,
// and to permit persons to whom the Software is furnished to do so, subject to the
// following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies
// or substantial portions of the Software.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using ICSharpCode.SharpDevelop.Project.HotReload;
using ProtocolHotReloadDeltaStatus = ICSharpCode.SharpDevelop.LanguageServices.Protocol.HotReloadDeltaStatus;

namespace Debugger.AddIn.Service.Dap
{
	/// <summary>
	/// Hot Reload for a plain C# project under the SharpDbg debugger. Unlike the XAML sessions,
	/// there is no in-process agent: Roslyn produces the EnC delta from the live workspace and
	/// SharpDbg applies it through <c>sharpdbg/applyDeltas</c>. Readiness means "the launch
	/// baseline has been captured", not "a pipe answered".
	/// </summary>
	public sealed class CSharpHotReloadSession : ICodeDeltaHotReloadSession
	{
		readonly HotReloadCapabilities capabilities;
		readonly SemaphoreSlim codeDeltaGate = new SemaphoreSlim(1, 1);
		readonly TaskCompletionSource<bool> baselineReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		readonly CancellationTokenSource disposal = new CancellationTokenSource();
		IRoslynLanguageProtocol codeDeltaProtocol;
		HotReloadProjectId codeDeltaProject;
		IHotReloadDeltaApplier debuggerDeltaApplier;
		bool previousDeltaApplied;
		bool baselineUnavailable;
		HotReloadSessionState state = HotReloadSessionState.Starting;

		public CSharpHotReloadSession(IApplicationHotReloadAdapter adapter, HotReloadCapabilities capabilities)
		{
			Framework = adapter?.Framework ?? "C#";
			this.capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
		}

		public string Framework { get; }

		public HotReloadCapabilities Capabilities => capabilities;

		public HotReloadSessionState State {
			get => state;
			private set {
				if (state == value)
					return;
				state = value;
				StateChanged?.Invoke(this, value);
			}
		}

		public int? ProcessId { get; internal set; }

		public string LastError { get; private set; }

		public event EventHandler<HotReloadSessionState> StateChanged;

		public IReadOnlyDictionary<string, string> GetDiagnostics() => new Dictionary<string, string> {
			["project"] = codeDeltaProject?.ProjectFileName ?? "",
			["targetFramework"] = codeDeltaProject?.TargetFramework ?? "",
			["state"] = State.ToString(),
			["lastError"] = LastError ?? "",
		};

		public void ConfigureCodeDelta(IRoslynLanguageProtocol protocol, HotReloadProjectId project,
			IHotReloadDeltaApplier debuggerDeltaApplier = null)
		{
			codeDeltaProtocol = protocol;
			codeDeltaProject = project;
			this.debuggerDeltaApplier = debuggerDeltaApplier;
		}

		public void NotifyBaselineCaptured(bool success, string diagnostic)
		{
			if (success) {
				baselineUnavailable = false;
				State = HotReloadSessionState.Ready;
				baselineReady.TrySetResult(true);
				return;
			}
			LastError = diagnostic ?? "The launch baseline could not be captured.";
			baselineUnavailable = true;
			State = HotReloadSessionState.Failed;
			baselineReady.TrySetResult(false);
		}

		public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken token)
		{
			if (State == HotReloadSessionState.Ready)
				return true;
			if (baselineReady.Task.IsCompleted)
				return baselineReady.Task.Result;

			using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, disposal.Token);
			var delay = Task.Delay(timeout, linked.Token);
			var completed = await Task.WhenAny(baselineReady.Task, delay).ConfigureAwait(false);
			if (completed != baselineReady.Task) {
				LastError = $"The C# Hot Reload baseline was not captured within {timeout}.";
				throw new TimeoutException(LastError);
			}
			return await baselineReady.Task.ConfigureAwait(false);
		}

		public async Task<HotReloadApplyResult> ApplyAsync(HotReloadDocumentChange change, CancellationToken token)
		{
			if (change == null)
				throw new ArgumentNullException(nameof(change));
			// A C# edit is one project-wide code delta; the text itself is already in the Roslyn
			// workspace, so the classification produced for XAML is not used here.
			if (change.Kind == HotReloadChangeKind.RestartRequired) {
				State = HotReloadSessionState.RestartRequired;
				return new HotReloadApplyResult(HotReloadOutcome.RestartRequired,
					"This edit cannot be applied while debugging; restart the application.");
			}
			return await ApplyPendingChangesAsync(token).ConfigureAwait(false);
		}

		public async Task<HotReloadApplyResult> ApplyPendingChangesAsync(CancellationToken token)
		{
			if (State is HotReloadSessionState.Disconnected or HotReloadSessionState.Stopped)
				return new HotReloadApplyResult(HotReloadOutcome.Disconnected, "The Hot Reload session has ended.");
			// Availability problems are not edits: never hold up resume because the launch could
			// not capture a baseline or no Roslyn producer/debugger applier exists.
			if (baselineUnavailable)
				return new HotReloadApplyResult(HotReloadOutcome.NoChanges, LastError ?? "Hot Reload is not available in this session.");
			if (codeDeltaProtocol is null || codeDeltaProject is null)
				return new HotReloadApplyResult(HotReloadOutcome.NoChanges, "No Roslyn EnC producer is available for this launch.");
			if (debuggerDeltaApplier is null)
				return new HotReloadApplyResult(HotReloadOutcome.NoChanges, "No debugger is available to apply the code delta.");

			using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, disposal.Token);
			try {
				if (!await WaitForReadyAsync(TimeSpan.FromSeconds(5), linked.Token).ConfigureAwait(false))
					return new HotReloadApplyResult(HotReloadOutcome.NoChanges, LastError ?? "The C# Hot Reload session is not ready.");
			} catch (TimeoutException ex) {
				return new HotReloadApplyResult(HotReloadOutcome.NoChanges, ex.Message);
			}

			await codeDeltaGate.WaitAsync(linked.Token).ConfigureAwait(false);
			State = HotReloadSessionState.Applying;
			try {
				var delta = await codeDeltaProtocol.RoslynHotReloadProduceDeltaAsync(
					new HotReloadDeltaRequest(codeDeltaProject, null, previousDeltaApplied), linked.Token).ConfigureAwait(false);
				previousDeltaApplied = false;
				if (delta.Status == ProtocolHotReloadDeltaStatus.NoChanges) {
					State = HotReloadSessionState.Ready;
					return new HotReloadApplyResult(HotReloadOutcome.NoChanges, delta.Diagnostic ?? "No changes.");
				}
				if (delta.Status == ProtocolHotReloadDeltaStatus.RudeEdit) {
					State = HotReloadSessionState.RestartRequired;
					return new HotReloadApplyResult(HotReloadOutcome.RestartRequired, delta.Diagnostic);
				}
				if (delta.Status == ProtocolHotReloadDeltaStatus.CompilationError) {
					State = HotReloadSessionState.Ready;
					return new HotReloadApplyResult(HotReloadOutcome.BuildRequired, delta.Diagnostic);
				}
				if (delta.Status != ProtocolHotReloadDeltaStatus.Applied || delta.Delta is null) {
					State = HotReloadSessionState.Ready;
					return new HotReloadApplyResult(HotReloadOutcome.Failed, delta.Diagnostic ?? "Could not produce a code delta.");
				}

				var applied = await ApplyDeltaAsync(delta.Delta, linked.Token).ConfigureAwait(false);
				previousDeltaApplied = applied.Outcome == HotReloadOutcome.Applied;
				return applied;
			} catch (OperationCanceledException) {
				State = HotReloadSessionState.Disconnected;
				return new HotReloadApplyResult(HotReloadOutcome.Disconnected, "The Hot Reload session has ended.");
			} catch (Exception ex) {
				LastError = ex.Message;
				// A rejected delta must not disable the session: the chain discards the pending
				// generation, so the next resume can produce and apply a fresh delta.
				State = HotReloadSessionState.Ready;
				return new HotReloadApplyResult(HotReloadOutcome.Failed, ex.Message, ex.ToString());
			} finally {
				codeDeltaGate.Release();
			}
		}

		async Task<HotReloadApplyResult> ApplyDeltaAsync(HotReloadModuleDelta delta, CancellationToken token)
		{
			var result = await debuggerDeltaApplier.ApplyAsync(delta, token).ConfigureAwait(false);
			// A failed apply is retryable (the next produce discards the pending generation), so the
			// session returns to Ready and the failure is reported for this resume only.
			State = result.Outcome == HotReloadOutcome.RestartRequired
				? HotReloadSessionState.RestartRequired
				: HotReloadSessionState.Ready;
			if (!result.IsSuccess && result.Outcome != HotReloadOutcome.NoChanges)
				LastError = result.Message;
			return result;
		}

		public ValueTask DisposeAsync()
		{
			if (!disposal.IsCancellationRequested) {
				State = HotReloadSessionState.Stopped;
				disposal.Cancel();
				disposal.Dispose();
				codeDeltaGate.Dispose();
			}
			return ValueTask.CompletedTask;
		}
	}
}
