// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using ICSharpCode.SharpDevelop.Project.HotReload;

namespace Debugger.AddIn.Service.Dap
{
	/// <summary>
	/// Hot Reload's code-delta applier while the application runs under SharpDbg: the delta goes to the
	/// debugger (<c>sharpdbg/applyDeltas</c>, <c>ICorDebugModule2::ApplyChanges</c>) instead of the agent,
	/// because a debugged process takes metadata updates only through its debugger. SharpDbg synchronizes
	/// a running debuggee for the update and lets it run on, so the user need not pause first.
	/// </summary>
	public sealed class SharpDbgDeltaApplier : IHotReloadDeltaApplier
	{
		readonly Func<DapSession> session;

		public SharpDbgDeltaApplier(Func<DapSession> session)
		{
			this.session = session ?? throw new ArgumentNullException(nameof(session));
		}

		public async Task<HotReloadApplyResult> ApplyAsync(HotReloadModuleDelta delta, CancellationToken token)
		{
			var current = session();
			if (current == null || !current.IsRunning)
				return new HotReloadApplyResult(HotReloadOutcome.RestartRequired, "The debug session has ended.");
			try {
				await current.ApplyDeltasAsync(delta.Mvid, delta.Metadata, delta.IL, delta.Pdb, delta.UpdatedMethodTokens, token, delta.LineMaps).ConfigureAwait(false);
				await RunMetadataUpdateHandlersAsync(current, delta, token).ConfigureAwait(false);
				return new HotReloadApplyResult(HotReloadOutcome.Applied, "Applied through SharpDbg.");
			} catch (Exception ex) {
				return new HotReloadApplyResult(HotReloadOutcome.Failed, ex.Message, ex.ToString());
			}
		}
		/// <summary>
		/// A debugger-applied EnC delta does not run the runtime's MetadataUpdater notification path.
		/// While the debuggee is paused, invoke the assembly's MetadataUpdateHandler methods through
		/// the expression evaluator so caches can be cleared and UI can be refreshed. Failures are
		/// diagnostics, never a reason to report the applied code change as failed.
		/// </summary>
		static async Task RunMetadataUpdateHandlersAsync(DapSession session, HotReloadModuleDelta delta, CancellationToken token)
		{
			if (delta.MetadataUpdateHandlers is not { Length: > 0 } || !session.IsPaused || session.ActiveFrameId == 0)
				return;
			var types = delta.UpdatedTypeNames is { Length: > 0 }
				? "new[] { " + string.Join(", ", delta.UpdatedTypeNames.Take(64).Select(name => "typeof(" + name + ")")) + " }"
				: "null";
			foreach (var handler in delta.MetadataUpdateHandlers) {
				await TryEvaluateAsync(session, handler + ".ClearCache(" + types + ")", token).ConfigureAwait(false);
			}
			foreach (var handler in delta.MetadataUpdateHandlers) {
				await TryEvaluateAsync(session, handler + ".UpdateApplication(" + types + ")", token).ConfigureAwait(false);
			}
		}

		static async Task TryEvaluateAsync(DapSession session, string expression, CancellationToken token)
		{
			try {
				await session.EvaluateAsync(expression, session.ActiveFrameId, "watch").ConfigureAwait(false);
			} catch (Exception ex) {
				LoggingService.Warn("Hot Reload: MetadataUpdateHandler call failed (" + expression + "): " + ex.Message);
			}
		}
	}
}
