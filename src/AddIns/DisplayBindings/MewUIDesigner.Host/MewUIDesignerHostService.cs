using System.Security.Cryptography;
using System.Threading;
using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;

namespace ICSharpCode.MewUIDesigner.Host;

sealed class MewUIDesignerHostService : IDesignerChildService
{
	readonly string expectedToken; readonly ManualResetEventSlim shutdown = new(false); readonly MewUIDocumentEditor editor = new();
	string sessionId = "", documentId = "", fileName = ""; long version;
	public MewUIDesignerHostService(string expectedToken) => this.expectedToken = expectedToken;
	[JsonRpcMethod("initialize")] public HostHandshake Initialize(string token, int protocolVersion, string sessionId) { if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedToken), Convert.FromHexString(token))) throw new UnauthorizedAccessException(); if (protocolVersion != DesignerProtocol.Version) throw new NotSupportedException(); this.sessionId = sessionId; return new HostHandshake { ProtocolVersion = DesignerProtocol.Version, Runtime = "MewUI Roslyn model", ProcessId = Environment.ProcessId, SessionId = sessionId }; }
	[JsonRpcMethod("session/open")] public DesignerSessionState Open(DesignerDocumentSnapshot snapshot) => Load(snapshot); [JsonRpcMethod("session/update")] public DesignerSessionState Update(DesignerDocumentSnapshot snapshot) => Load(snapshot);
	DesignerSessionState Load(DesignerDocumentSnapshot snapshot) { EnsureSession(snapshot.SessionId, snapshot.DocumentId); documentId = snapshot.DocumentId; version = snapshot.Version; var file = snapshot.Files.FirstOrDefault(f => f.Kind == "Designer") ?? snapshot.Files.FirstOrDefault(); fileName = file?.FileName ?? snapshot.DesignerFileName; editor.Reset(file?.Text ?? "");
        System.Console.Error.WriteLine($"MEWUI-HOST-DIAG chars={(file?.Text ?? "").Length} elements={State().ComponentCount} err=[{editor.Error}]");
        return State(); }
	[JsonRpcMethod("design/set-property")] public DesignerSessionState SetProperty(long baseVersion, string elementId, string propertyName, string value) { EnsureVersion(baseVersion); var ok = propertyName == "$name" ? editor.Rename(elementId, value) : editor.SetProperty(elementId, propertyName, value); if (!ok) throw new InvalidOperationException("MewUI property mutation was rejected."); version++; return State(); }
	[JsonRpcMethod("design/add-element")] public DesignerSessionState AddElement(long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y) { EnsureVersion(baseVersion); if (!editor.AddElement(parentId, string.IsNullOrEmpty(item.TypeName) ? item.Name : item.TypeName)) throw new InvalidOperationException("MewUI element insertion was rejected."); version++; return State(); }
	[JsonRpcMethod("design/delete-elements")] public DesignerSessionState DeleteElements(long baseVersion, string[] elementIds) { EnsureVersion(baseVersion); foreach (var id in elementIds) if (!editor.Remove(id)) throw new InvalidOperationException("MewUI element deletion was rejected: " + id); version++; return State(); }
	[JsonRpcMethod("design/rename")] public DesignerSessionState Rename(long baseVersion, string elementId, string newName) => SetProperty(baseVersion, elementId, "$name", newName);
	[JsonRpcMethod("design/undo")] public DesignerSessionState Undo(long baseVersion) { EnsureVersion(baseVersion); if (!editor.Undo()) throw new InvalidOperationException("Nothing to undo."); version++; return State(); }
	[JsonRpcMethod("design/redo")] public DesignerSessionState Redo(long baseVersion) { EnsureVersion(baseVersion); if (!editor.Redo()) throw new InvalidOperationException("Nothing to redo."); version++; return State(); }
	[JsonRpcMethod("session/flush")] public DesignerEditSet Flush(long baseVersion) { EnsureVersion(baseVersion); return new DesignerEditSet { SessionId = sessionId, DocumentId = documentId, BaseVersion = version, Files = { new DesignerSourceFileSnapshot { FileName = fileName, Kind = "Designer", Text = editor.Text } } }; }
	DesignerSessionState State() { var root = editor.Roots.FirstOrDefault(); return new DesignerSessionState { SessionId = sessionId, DocumentId = documentId, Version = version, Accepted = string.IsNullOrEmpty(editor.Error), Error = editor.Error, RootType = root?.Type ?? "", ComponentCount = root == null ? 0 : Count(root), Tree = root == null ? null : Node(root) }; }
	static DesignerElementNode Node(MewUIElementNode n) => new() { Id = n.Id, Name = n.Name, Type = n.Type, Properties = n.Properties.Select(p => new DesignerPropertyInfo { Name = p.Key, DisplayName = p.Key, Value = p.Value, Category = "MewUI" }).Prepend(new DesignerPropertyInfo { Name = "$name", DisplayName = "Name", Value = n.Name, Category = "Identity" }).ToList(), Children = n.Children.Select(Node).ToList() };
	static int Count(MewUIElementNode n) => 1 + n.Children.Sum(Count); void EnsureSession(string candidate, string document) { if (candidate != sessionId) throw new InvalidOperationException("Stale designer session."); if (documentId.Length != 0 && document != documentId) throw new InvalidOperationException("Wrong document."); } void EnsureVersion(long candidate) { if (candidate != version) throw new InvalidOperationException($"Stale version {candidate}; current is {version}."); }
	[JsonRpcMethod("ping")] public object Ping() => new(); [JsonRpcMethod("shutdown")] public object Shutdown() { shutdown.Set(); return new(); } public void WaitForShutdown() => shutdown.Wait();
}
