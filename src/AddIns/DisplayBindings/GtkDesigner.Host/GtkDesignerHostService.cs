using System.Security.Cryptography;
using System.Threading;
using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;

namespace ICSharpCode.GtkDesigner.Host;

sealed class GtkDesignerHostService : IDesignerChildService
{
	readonly string expectedToken;
	readonly ManualResetEventSlim shutdown = new(false);
	readonly GtkUiDocumentEditor editor = new();
	string sessionId = "", documentId = "", fileName = "";
	long version;

	public GtkDesignerHostService(string expectedToken) => this.expectedToken = expectedToken;

	[JsonRpcMethod("initialize")]
	public HostHandshake Initialize(string token, int protocolVersion, string sessionId)
	{
		if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedToken), Convert.FromHexString(token))) throw new UnauthorizedAccessException();
		if (protocolVersion != DesignerProtocol.Version) throw new NotSupportedException();
		this.sessionId = sessionId;
		return new HostHandshake { ProtocolVersion = DesignerProtocol.Version, Runtime = "GTK 4 document model", ProcessId = Environment.ProcessId, SessionId = sessionId };
	}

	[JsonRpcMethod("session/open")]
	public DesignerSessionState Open(DesignerDocumentSnapshot snapshot) => Load(snapshot);
	[JsonRpcMethod("session/update")]
	public DesignerSessionState Update(DesignerDocumentSnapshot snapshot) => Load(snapshot);
	DesignerSessionState Load(DesignerDocumentSnapshot snapshot)
	{
		EnsureSession(snapshot.SessionId, snapshot.DocumentId);
		documentId = snapshot.DocumentId; version = snapshot.Version; fileName = snapshot.PrimaryFileName;
		editor.Reset(snapshot.Files.FirstOrDefault()?.Text ?? "");
		return State();
	}

	[JsonRpcMethod("design/set-property")]
	public DesignerSessionState SetProperty(long baseVersion, string elementId, string propertyName, string value)
	{
		EnsureVersion(baseVersion);
		var changed = propertyName == "$id" ? editor.Rename(elementId, value) : editor.SetProperty(elementId, propertyName, value);
		if (!changed) throw new InvalidOperationException("GTK property mutation was rejected.");
		version++; return State();
	}

	[JsonRpcMethod("design/add-element")]
	public DesignerSessionState AddElement(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y)
	{
		EnsureVersion(baseVersion);
		if (!editor.Add(parentId, string.IsNullOrEmpty(item.TypeName) ? item.Name : item.TypeName)) throw new InvalidOperationException("GTK element insertion was rejected.");
		version++; return State();
	}

	[JsonRpcMethod("design/delete-elements")]
	public DesignerSessionState DeleteElements(long baseVersion, string[] elementIds)
	{
		EnsureVersion(baseVersion);
		foreach (var id in elementIds) if (!editor.Remove(id)) throw new InvalidOperationException("GTK element deletion was rejected: " + id);
		version++; return State();
	}

	[JsonRpcMethod("design/rename")]
	public DesignerSessionState Rename(long baseVersion, string elementId, string newName) => SetProperty(baseVersion, elementId, "$id", newName);
	[JsonRpcMethod("design/undo")]
	public DesignerSessionState Undo(long baseVersion) { EnsureVersion(baseVersion); if (!editor.Undo()) throw new InvalidOperationException("Nothing to undo."); version++; return State(); }
	[JsonRpcMethod("design/redo")]
	public DesignerSessionState Redo(long baseVersion) { EnsureVersion(baseVersion); if (!editor.Redo()) throw new InvalidOperationException("Nothing to redo."); version++; return State(); }

	[JsonRpcMethod("session/flush")]
	public DesignerEditSet Flush(long baseVersion)
	{
		EnsureVersion(baseVersion);
		return new DesignerEditSet { SessionId = sessionId, DocumentId = documentId, BaseVersion = version,
			Files = { new DesignerSourceFileSnapshot { FileName = fileName, Kind = "Designer", Text = editor.Text } } };
	}

	DesignerSessionState State()
	{
		var roots = editor.Roots.Select(Node).ToList();
		var tree = roots.Count == 1 ? roots[0] : new DesignerElementNode { Id = "$interface", Name = "interface", Type = "GtkInterface", Children = roots };
		return new DesignerSessionState { SessionId = sessionId, DocumentId = documentId, Version = version, Accepted = string.IsNullOrEmpty(editor.Error), Error = editor.Error, RootType = tree.Type, ComponentCount = Count(tree), Tree = tree };
	}
	static DesignerElementNode Node(GtkUiNode node) => new() { Id = node.Id, Name = node.Id, Type = node.ClassName,
		Properties = node.Properties.Select(p => new DesignerPropertyInfo { Name = p.Key, DisplayName = p.Key, Value = p.Value, Category = "GTK" }).Prepend(new DesignerPropertyInfo { Name = "$id", DisplayName = "ID", Value = node.Id, Category = "GTK" }).ToList(),
		Children = node.Children.Select(Node).ToList() };
	static int Count(DesignerElementNode node) => 1 + node.Children.Sum(Count);
	void EnsureSession(string candidate, string document) { if (candidate != sessionId) throw new InvalidOperationException("Stale designer session."); if (documentId.Length != 0 && document != documentId) throw new InvalidOperationException("Wrong document."); }
	void EnsureVersion(long candidate) { if (candidate != version) throw new InvalidOperationException($"Stale version {candidate}; current is {version}."); }
	[JsonRpcMethod("ping")] public object Ping() => new();
	[JsonRpcMethod("shutdown")] public object Shutdown() { shutdown.Set(); return new(); }
	public void WaitForShutdown() => shutdown.Wait();
}
