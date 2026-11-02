// Shared host-side edit plumbing for the designers whose document is a source model (GTK, MewUI): every
// design/* mutation validates the session and the caller's base version, applies one editor operation, and
// either bumps the version and answers with the new state or refuses with a message.

using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>A document session that carries the version every mutation is checked against.</summary>
	public interface ISourceModelSession
	{
		long Version { get; set; }
	}

	public static class SourceModelHost
	{
		/// <summary>Validates the session identity, finds the open document and checks <paramref name="baseVersion"/>
		/// against its current version ("Stale version X; current is Y." - the same text every host reported).</summary>
		public static TSession Open<TSession>(this DesignerDocumentRegistry<TSession> documents, string sessionId, string documentId, long baseVersion)
			where TSession : class, ISourceModelSession
		{
			documents.ValidateSession(sessionId);
			var session = documents.Get(sessionId, documentId);
			if (baseVersion != session.Version)
				throw new InvalidOperationException($"Stale version {baseVersion}; current is {session.Version}.");
			return session;
		}

		/// <summary>Applies one mutation. A refusal is a normal result, not an exception - the same convention the WPF and Uno
		/// hosts use: the state comes back with <c>Accepted = false</c>, the reason in <c>Error</c> and the diagnostics, and the
		/// version unchanged, so the caller can show the reason and keep editing. A stale <paramref name="baseVersion"/> is refused
		/// the same way ("Stale version X; current is Y."), and so is an <paramref name="apply"/> that returns false (the message
		/// is <paramref name="rejected"/>, plus <paramref name="detail"/> - the editor's own reason - when it gives one). An unknown
		/// session or document is a protocol error and still throws. An <paramref name="apply"/> that throws
		/// <see cref="InvalidOperationException"/> (input validation with a readable reason) is refused with that reason. An accepted edit runs <paramref name="edited"/> (default: the
		/// version goes up by one) and returns the new state.</summary>
		public static DesignerSessionState Edit<TSession>(this DesignerDocumentRegistry<TSession> documents, string sessionId, string documentId, long baseVersion,
			Func<TSession, bool> apply, string rejected, Func<TSession, DesignerSessionState> state, Func<TSession, string?>? detail = null, Action<TSession>? edited = null)
			where TSession : class, ISourceModelSession
		{
			documents.ValidateSession(sessionId);
			var session = documents.Get(sessionId, documentId);
			if (baseVersion != session.Version)
				return Refuse(state(session), $"Stale version {baseVersion}; current is {session.Version}.");
			bool applied;
			try {
				applied = apply(session);
			} catch (InvalidOperationException ex) {
				// An operation that validates its input throws with a readable reason (a value GTK would reject, say) before the
				// document changes: that is a refusal like any other, not a failure of the call.
				return Refuse(state(session), ex.Message);
			}
			if (!applied) {
				var reason = detail?.Invoke(session);
				return Refuse(state(session), string.IsNullOrEmpty(reason) ? rejected : rejected + " " + reason);
			}
			if (edited != null) edited(session); else session.Version++;
			return state(session);
		}

		/// <summary>Marks a state as a refused edit: not accepted, with the reason.</summary>
		public static DesignerSessionState Refuse(DesignerSessionState state, string error)
		{
			state.Accepted = false;
			state.CreatedElementId = null;   // nothing was created; a stale id from an earlier edit must not leak into a refusal
			state.CreatedElementIds = new List<string>();
			state.Error = error;
			state.Diagnostics.Add(new DesignerDiagnostic { Severity = "Error", Message = error });
			return state;
		}
	}
}
