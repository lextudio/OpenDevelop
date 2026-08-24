// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>
	/// Owns the authenticated, connection-level collection of document sessions in a designer
	/// child. Creation is serialized so concurrent opens publish exactly one session.
	/// </summary>
	public sealed class DesignerDocumentRegistry<TSession> where TSession : class
	{
		readonly object gate = new();
		readonly Dictionary<string, TSession> documents = new(StringComparer.Ordinal);
		string? sessionId;
		bool closed;

		public int Count { get { lock (gate) return documents.Count; } }

		public void Initialize(string value)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(value);
			lock (gate) {
				if (closed) throw new ObjectDisposedException(nameof(DesignerDocumentRegistry<TSession>));
				if (sessionId != null && !StringComparer.Ordinal.Equals(sessionId, value))
					throw new InvalidOperationException("The designer document registry is already initialized for another session.");
				sessionId = value;
			}
		}

		public void ValidateSession(string requestSessionId)
		{
			lock (gate) ValidateSessionCore(requestSessionId);
		}

		public TSession GetOrAdd(string requestSessionId, string documentId, Func<TSession> factory)
			=> GetOrAddCore(requestSessionId, documentId, factory, validateRequestSession: true);

		/// <summary>Gets a document for protocols whose authenticated connection, rather than each
		/// operation, carries the session identity.</summary>
		public TSession GetOrAdd(string documentId, Func<TSession> factory)
			=> GetOrAddCore(null, documentId, factory, validateRequestSession: false);

		TSession GetOrAddCore(string? requestSessionId, string documentId, Func<TSession> factory, bool validateRequestSession)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
			ArgumentNullException.ThrowIfNull(factory);
			lock (gate) {
				if (validateRequestSession) ValidateSessionCore(requestSessionId!); else ValidateInitializedCore();
				if (documents.TryGetValue(documentId, out var existing)) return existing;
				var created = factory() ?? throw new InvalidOperationException("The designer document factory returned null.");
				documents.Add(documentId, created);
				return created;
			}
		}

		public TSession Get(string documentId)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
			lock (gate) {
				ValidateInitializedCore();
				return documents.TryGetValue(documentId, out var existing)
					? existing
					: throw new InvalidOperationException("Unknown designer document.");
			}
		}

		public bool Remove(string requestSessionId, string documentId, Action<TSession> close)
			=> RemoveCore(requestSessionId, documentId, close, validateRequestSession: true);

		public bool Remove(string documentId, Action<TSession> close)
			=> RemoveCore(null, documentId, close, validateRequestSession: false);

		bool RemoveCore(string? requestSessionId, string documentId, Action<TSession> close, bool validateRequestSession)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
			ArgumentNullException.ThrowIfNull(close);
			TSession? removed;
			lock (gate) {
				if (validateRequestSession) ValidateSessionCore(requestSessionId!); else ValidateInitializedCore();
				if (!documents.Remove(documentId, out removed)) return false;
			}
			close(removed);
			return true;
		}

		public void CloseAll(Action<TSession> close)
		{
			ArgumentNullException.ThrowIfNull(close);
			TSession[] remaining;
			lock (gate) {
				if (closed) return;
				closed = true;
				remaining = [.. documents.Values];
				documents.Clear();
			}
			foreach (var document in remaining) close(document);
		}

		void ValidateSessionCore(string requestSessionId)
		{
			ValidateInitializedCore();
			if (!StringComparer.Ordinal.Equals(sessionId, requestSessionId))
				throw new UnauthorizedAccessException("The request's session id does not match this designer host.");
		}

		void ValidateInitializedCore()
		{
			if (closed) throw new ObjectDisposedException(nameof(DesignerDocumentRegistry<TSession>));
			if (sessionId == null) throw new UnauthorizedAccessException("The designer host has not completed its handshake.");
		}
	}
}
