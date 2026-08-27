using System.Activities;
using System.Security.Cryptography;
using System.Threading;
using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;

namespace ICSharpCode.WorkflowDesigner.Host;

sealed class WorkflowDesignerHostService : IDesignerChildService
{
	readonly string expectedToken;
	readonly ManualResetEventSlim shutdown = new(false);
	readonly DesignerDocumentRegistry<DocumentSession> documents = new();
	string sessionId = "";

	public WorkflowDesignerHostService(string expectedToken) => this.expectedToken = expectedToken;

	[JsonRpcMethod("initialize")]
	public HostHandshake Initialize(string token, int protocolVersion, string sessionId)
	{
		if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedToken), Convert.FromHexString(token)))
			throw new UnauthorizedAccessException();
		if (protocolVersion != DesignerProtocol.Version) throw new NotSupportedException();
		documents.Initialize(sessionId);
		this.sessionId = sessionId;
		return new HostHandshake { ProtocolVersion = DesignerProtocol.Version, Runtime = "CoreWF ActivityBuilder", ProcessId = Environment.ProcessId, SessionId = sessionId };
	}

	[JsonRpcMethod("session/open")] public DesignerSessionState Open(DesignerDocumentSnapshot snapshot) => Load(snapshot);
	[JsonRpcMethod("session/update")] public DesignerSessionState Update(DesignerDocumentSnapshot snapshot) => Load(snapshot);

	DesignerSessionState Load(DesignerDocumentSnapshot snapshot)
	{
		documents.ValidateSession(snapshot.SessionId);
		var session = documents.GetOrAdd(snapshot.SessionId, snapshot.DocumentId, () => new DocumentSession(snapshot.DocumentId));
		session.Version = snapshot.Version;
		var file = snapshot.Files.FirstOrDefault(f => f.Kind == "Designer") ?? snapshot.Files.FirstOrDefault();
		session.FileName = file?.FileName ?? snapshot.DesignerFileName;
		session.Document.Reset(file?.Text ?? "");
		session.Undo.Clear();
		session.Redo.Clear();
		return State(session);
	}

	[JsonRpcMethod("design/set-property")]
	public DesignerSessionState SetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName, string value)
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		if (!session.Document.SetProperty(elementId, propertyName, value))
			throw new InvalidOperationException("Workflow property mutation was rejected.");
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("design/rename")]
	public DesignerSessionState Rename(string sessionId, string documentId, long baseVersion, string elementId, string newName)
		=> SetProperty(sessionId, documentId, baseVersion, elementId, "$displayName", newName);

	[JsonRpcMethod("design/add-element")]
	public DesignerSessionState AddElement(string sessionId, string documentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y)
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		var addedId = session.Document.AddChild(parentId, string.IsNullOrEmpty(item.TypeName) ? item.Name : item.TypeName);
		if (addedId == null) throw new InvalidOperationException("Workflow element insertion was rejected.");
		RecordMutation(session, before);
		var state = State(session);
		state.CreatedElementId = addedId;
		return state;
	}

	[JsonRpcMethod("design/delete-elements")]
	public DesignerSessionState DeleteElements(string sessionId, string documentId, long baseVersion, string[] elementIds)
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
		EnsureVersion(session, baseVersion);
		var before = session.Document.ToXaml();
		foreach (var id in elementIds)
			if (!session.Document.Remove(id))
				throw new InvalidOperationException("Workflow element deletion was rejected: " + id);
		RecordMutation(session, before);
		return State(session);
	}

	[JsonRpcMethod("design/undo")]
	public DesignerSessionState Undo(string sessionId, string documentId, long baseVersion) { documents.ValidateSession(sessionId); return RestoreHistory(Get(documentId), baseVersion, undo: true); }
	[JsonRpcMethod("design/redo")]
	public DesignerSessionState Redo(string sessionId, string documentId, long baseVersion) { documents.ValidateSession(sessionId); return RestoreHistory(Get(documentId), baseVersion, undo: false); }

	DesignerSessionState RestoreHistory(DocumentSession session, long baseVersion, bool undo)
	{
		EnsureVersion(session, baseVersion);
		var source = undo ? session.Undo : session.Redo;
		var destination = undo ? session.Redo : session.Undo;
		if (source.Count == 0) throw new InvalidOperationException(undo ? "Nothing to undo." : "Nothing to redo.");
		destination.Push(session.Document.ToXaml());
		session.Document.Reset(source.Pop());
		if (!session.Document.LastParseSucceeded) throw new InvalidOperationException("Workflow history could not be restored.");
		session.Version++;
		return State(session);
	}

	static void RecordMutation(DocumentSession session, string before)
	{
		session.Undo.Push(before);
		session.Redo.Clear();
		session.Version++;
	}

	[JsonRpcMethod("session/flush")]
	public DesignerEditSet Flush(string sessionId, string documentId, long baseVersion)
	{
		documents.ValidateSession(sessionId);
		var session = Get(documentId);
		EnsureVersion(session, baseVersion);
		return new DesignerEditSet {
			SessionId = sessionId, DocumentId = documentId, BaseVersion = session.Version,
			Files = { new DesignerSourceFileSnapshot { FileName = session.FileName, Kind = "Designer", Text = session.Document.ToXaml() } }
		};
	}

	[JsonRpcMethod("session/close")]
	public object Close(string sessionId, string documentId)
	{
		documents.ValidateSession(sessionId);
		documents.Remove(documentId, _ => { });
		return new();
	}

	DesignerSessionState State(DocumentSession session)
	{
		var root = session.Document.Root;
		return new DesignerSessionState {
			SessionId = sessionId, DocumentId = session.DocumentId, Version = session.Version,
			Accepted = session.Document.LastParseSucceeded, Error = session.Document.Error, CanUndo = session.Undo.Count > 0, CanRedo = session.Redo.Count > 0,
			RootType = root?.GetType().Name ?? "", ComponentCount = root == null ? 0 : Count(root),
			Tree = root == null ? null : Node(session.Document, root, "")
		};
	}

	DesignerElementNode Node(WorkflowDocument document, Activity activity, string path)
	{
		var children = WorkflowInspectionServices.GetActivities(activity).ToArray();
		var properties = document.GetProperties(activity)
			.Select(p => new DesignerPropertyInfo { Name = p.Name, DisplayName = p.Name, Value = p.Value, TypeName = p.TypeName, IsReadOnly = p.IsReadOnly, Category = "Workflow" })
			.Prepend(new DesignerPropertyInfo { Name = "$displayName", DisplayName = "DisplayName", Value = activity.DisplayName, Category = "Identity" })
			.ToList();
		return new DesignerElementNode {
			Id = path, Name = activity.DisplayName, Type = activity.GetType().Name, Path = path,
			Properties = properties,
			Children = children.Select((child, index) => Node(document, child, path.Length == 0 ? index.ToString() : path + "." + index)).ToList()
		};
	}

	static int Count(Activity activity) => 1 + WorkflowInspectionServices.GetActivities(activity).Sum(Count);

	DocumentSession Get(string documentId) => documents.Get(documentId);
	static void EnsureVersion(DocumentSession session, long candidate)
	{
		if (candidate != session.Version) throw new InvalidOperationException($"Stale version {candidate}; current is {session.Version}.");
	}

	[JsonRpcMethod("ping")] public object Ping() => new();
	[JsonRpcMethod("shutdown")] public object Shutdown() { documents.CloseAll(_ => { }); shutdown.Set(); return new(); }
	public void WaitForShutdown() => shutdown.Wait();
	public void OnParentDisconnected() => shutdown.Set();

	sealed class DocumentSession
	{
		public DocumentSession(string documentId) => DocumentId = documentId;
		public string DocumentId { get; }
		public WorkflowDocument Document { get; } = new();
		public string FileName = "";
		public long Version;
		public Stack<string> Undo { get; } = new();
		public Stack<string> Redo { get; } = new();
	}
}
