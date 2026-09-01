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
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
			ArgumentNullException.ThrowIfNull(factory);
			lock (gate) {
				ValidateSessionCore(requestSessionId);
				if (documents.TryGetValue(documentId, out var existing)) return existing;
				var created = factory() ?? throw new InvalidOperationException("The designer document factory returned null.");
				documents.Add(documentId, created);
				return created;
			}
		}

		/// <summary>Gets an already-open document after validating the caller's session identity.
		/// Unlike <see cref="GetOrAdd(string,string,Func{TSession})"/>, this never creates a
		/// document as a side effect of a read, mutation, or close-adjacent request.</summary>
		public TSession Get(string requestSessionId, string documentId)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
			lock (gate) {
				ValidateSessionCore(requestSessionId);
				return documents.TryGetValue(documentId, out var existing)
					? existing
					: throw new InvalidOperationException("Unknown designer document.");
			}
		}

		public bool Remove(string requestSessionId, string documentId, Action<TSession> close)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
			ArgumentNullException.ThrowIfNull(close);
			TSession? removed;
			lock (gate) {
				ValidateSessionCore(requestSessionId);
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
