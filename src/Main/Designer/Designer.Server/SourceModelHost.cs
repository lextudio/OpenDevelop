// Shared host-side edit plumbing for the designers whose document is a source model (GTK, MewUI): every
// design/* mutation validates the session and the caller's base version, applies one editor operation, and
// either bumps the version and answers with the new state or refuses with a message.

using System;

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

		/// <summary>Applies one mutation: <paramref name="apply"/> returns false to refuse it, which throws
		/// <paramref name="rejected"/> (plus <paramref name="detail"/>, the editor's own reason, when it gives one).
		/// An accepted edit runs <paramref name="edited"/> (default: the version goes up by one) and returns the state.</summary>
		public static DesignerSessionState Edit<TSession>(this DesignerDocumentRegistry<TSession> documents, string sessionId, string documentId, long baseVersion,
			Func<TSession, bool> apply, string rejected, Func<TSession, DesignerSessionState> state, Func<TSession, string?>? detail = null, Action<TSession>? edited = null)
			where TSession : class, ISourceModelSession
		{
			var session = documents.Open(sessionId, documentId, baseVersion);
			if (!apply(session)) {
				var reason = detail?.Invoke(session);
				throw new InvalidOperationException(string.IsNullOrEmpty(reason) ? rejected : rejected + " " + reason);
			}
			if (edited != null) edited(session); else session.Version++;
			return state(session);
		}
	}
}
