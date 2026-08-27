// Common document-scoped DDP calls. Designer adapters retain their runtime-specific payloads,
// but share the session/document identity envelope and the stable editing protocol here.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>
	/// Wraps the protocol operations whose request shape is identical for every out-of-process
	/// designer. Runtime adapters use their own calls for host-specific operations and document
	/// snapshots, then can delegate the ordinary edit lifecycle to this class.
	/// </summary>
	public sealed class DesignerDocumentRpcClient
	{
		DesignerHostProcessClient connection;

		public DesignerDocumentRpcClient(DesignerHostProcessClient connection, string sessionId, string documentId)
		{
			this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
			SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
			DocumentId = documentId ?? throw new ArgumentNullException(nameof(documentId));
		}

		public string SessionId { get; }
		public string DocumentId { get; }

		/// <summary>Rebinds a surviving document lease after its shared host was recovered.</summary>
		public void ReplaceConnection(DesignerHostProcessClient replacement)
			=> connection = replacement ?? throw new ArgumentNullException(nameof(replacement));

		public Task<DesignerSessionState> OpenAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
		{
			SetIdentity(snapshot);
			return connection.InvokeAsync<DesignerSessionState>("session/open", new { snapshot }, cancellationToken);
		}

		public Task<DesignerSessionState> UpdateAsync(DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken = default)
		{
			SetIdentity(snapshot);
			return connection.InvokeAsync<DesignerSessionState>("session/update", new { snapshot }, cancellationToken);
		}

		public Task<DesignerEditSet> FlushAsync(long baseVersion, CancellationToken cancellationToken = default)
			=> connection.InvokeAsync<DesignerEditSet>("session/flush", new { sessionId = SessionId, documentId = DocumentId, baseVersion }, cancellationToken);

		public Task<DesignerSessionState> SetPropertyAsync(long baseVersion, string elementId, string propertyName, string value, CancellationToken cancellationToken = default)
			=> connection.InvokeAsync<DesignerSessionState>("design/set-property", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, propertyName, value }, cancellationToken);

		public Task<DesignerSessionState> SetEventAsync(long baseVersion, string elementId, string eventName, string handlerName, CancellationToken cancellationToken = default)
			=> connection.InvokeAsync<DesignerSessionState>("design/set-event", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, eventName, handlerName }, cancellationToken);

		public Task<DesignerSessionState> AddElementAsync(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, CancellationToken cancellationToken = default)
			=> connection.InvokeAsync<DesignerSessionState>("design/add-element", new { sessionId = SessionId, documentId = DocumentId, baseVersion, parentId, item, proposedName, x, y }, cancellationToken);

		public Task<DesignerSessionState> SetBoundsAsync(long baseVersion, string elementId, double x, double y, double width, double height, CancellationToken cancellationToken = default)
			=> connection.InvokeAsync<DesignerSessionState>("design/set-bounds", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, x, y, width, height }, cancellationToken);

		public Task<DesignerSessionState> DeleteElementsAsync(long baseVersion, string[] elementIds, CancellationToken cancellationToken = default)
			=> connection.InvokeAsync<DesignerSessionState>("design/delete-elements", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementIds }, cancellationToken);

		public Task<DesignerSessionState> RenameAsync(long baseVersion, string elementId, string newName, CancellationToken cancellationToken = default)
			=> connection.InvokeAsync<DesignerSessionState>("design/rename", new { sessionId = SessionId, documentId = DocumentId, baseVersion, elementId, newName }, cancellationToken);

		public Task<DesignerHitTestResult> HitTestAsync(long baseVersion, double x, double y, CancellationToken cancellationToken = default)
			=> connection.InvokeAsync<DesignerHitTestResult>("design/hit-test", new { sessionId = SessionId, documentId = DocumentId, baseVersion, x, y }, cancellationToken);

		public Task CloseAsync(CancellationToken cancellationToken = default)
			=> connection.InvokeAsync<object>("session/close", new { sessionId = SessionId, documentId = DocumentId }, cancellationToken, TimeSpan.FromSeconds(3));

		void SetIdentity(DesignerDocumentSnapshot snapshot)
		{
			if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
			snapshot.SessionId = SessionId;
			snapshot.DocumentId = DocumentId;
		}
	}
}
