using System.Security.Cryptography;
using System.Threading;
using ICSharpCode.SharpDevelop.Designer.Remote;
using LeXtudio.MewUI.Xaml;
using StreamJsonRpc;

namespace ICSharpCode.MewUIDesigner.Host;

sealed class MewUIDesignerHostService : IDesignerChildService
{
	readonly string expectedToken; readonly ManualResetEventSlim shutdown = new(false); readonly DesignerDocumentRegistry<DocumentSession> documents = new();
	string sessionId = "";
	public MewUIDesignerHostService(string expectedToken) => this.expectedToken = expectedToken;
	[JsonRpcMethod("initialize")] public HostHandshake Initialize(string token, int protocolVersion, string sessionId) { DesignerHostHandshakeValidator.Validate(expectedToken, token, protocolVersion); documents.Initialize(sessionId); this.sessionId = sessionId; return new HostHandshake { ProtocolVersion = DesignerProtocol.Version, Runtime = "MewUI MXAML model", ProcessId = Environment.ProcessId, SessionId = sessionId }; }
	[JsonRpcMethod("session/open")] public DesignerSessionState Open(DesignerDocumentRequest request) => Load(request.Snapshot, create: true); [JsonRpcMethod("session/update")] public DesignerSessionState Update(DesignerDocumentRequest request) => Load(request.Snapshot, create: false);
	DesignerSessionState Load(DesignerDocumentSnapshot snapshot, bool create) { EnsureSession(snapshot.SessionId); var session = create ? GetOrCreate(snapshot.DocumentId) : Get(snapshot.DocumentId); session.Version = snapshot.Version; var file = snapshot.Files.FirstOrDefault(f => f.Kind == "Designer") ?? snapshot.Files.FirstOrDefault(); session.FileName = file?.FileName ?? snapshot.DesignerFileName; session.Document.Reset(file?.Text ?? ""); session.UndoDepth = session.RedoDepth = 0; return State(session); }
	[JsonRpcMethod("design/set-property")] public DesignerSessionState SetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName, string value) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); var ok = propertyName == "$name" ? session.Document.Rename(elementId, value) : session.Document.SetProperty(elementId, propertyName, value); if (!ok) throw new InvalidOperationException("MewUI property mutation was rejected."); Mutated(session); return State(session); }
	[JsonRpcMethod("design/set-event")] public DesignerSessionState SetEvent(string sessionId, string documentId, long baseVersion, string elementId, string eventName, string handlerName) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Document.SetEvent(elementId, eventName, string.IsNullOrWhiteSpace(handlerName) ? null : handlerName)) throw new InvalidOperationException("MewUI event mutation was rejected."); Mutated(session); return State(session); }
	[JsonRpcMethod("design/add-element")] public DesignerSessionState AddElement(string sessionId, string documentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, DesignerDropTarget dropTarget) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Document.Add(parentId, string.IsNullOrEmpty(item.TypeName) ? item.Name : item.TypeName)) throw new InvalidOperationException("MewUI element insertion was rejected."); Mutated(session); return State(session); }
	[JsonRpcMethod("design/delete-elements")] public DesignerSessionState DeleteElements(string sessionId, string documentId, long baseVersion, string[] elementIds) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); foreach (var id in elementIds) if (!session.Document.Remove(id)) throw new InvalidOperationException("MewUI element deletion was rejected: " + id); Mutated(session); return State(session); }
	/// <summary>The named elements as MXAML snippets (read-only; the same contract as the WPF and GTK hosts' copy).</summary>
	[JsonRpcMethod("design/copy-elements")]
	public DesignerCopyResult CopyElements(string sessionId, string documentId, long baseVersion, string[] elementIds)
	{
		EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion);
		var snippets = session.Document.CopySnippets(elementIds ?? Array.Empty<string>());
		return snippets == null || snippets.Count == 0
			? new DesignerCopyResult { Error = "Only named, non-root MewUI elements can be copied." }
			: new DesignerCopyResult { Accepted = true, Snippets = snippets.ToList() };
	}
	/// <summary>Pastes MXAML snippets into the target or its nearest container, after <paramref name="afterId"/> when given; names are made unique; one undo step.</summary>
	[JsonRpcMethod("design/paste-elements")]
	public DesignerSessionState PasteElements(string sessionId, string documentId, long baseVersion, string targetId, string[] snippets, string? afterId = null)
	{
		EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion);
		if (!session.Document.Paste(targetId, snippets ?? Array.Empty<string>(), out var created, afterId))
			throw new InvalidOperationException("The clipboard is not pastable MewUI markup for this target.");
		Mutated(session);
		var state = State(session);
		state.CreatedElementIds = created;
		return state;
	}
	/// <summary>Wraps sibling elements in a new container (the WPF host's wrap contract); the wrapper is CreatedElementId.</summary>
	[JsonRpcMethod("design/wrap-elements")]
	public DesignerSessionState WrapElements(string sessionId, string documentId, long baseVersion, string[] elementIds, string containerType)
	{
		EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion);
		if (!session.Document.Wrap(elementIds ?? Array.Empty<string>(), containerType, out var created))
			throw new InvalidOperationException("MewUI wrap was rejected: the elements must be non-root siblings, and a single-child container takes one.");
		Mutated(session);
		var state = State(session);
		state.CreatedElementId = created;
		return state;
	}
	/// <summary>Replaces a container with its children in place (the WPF host's unwrap contract).</summary>
	[JsonRpcMethod("design/unwrap-element")]
	public DesignerSessionState UnwrapElement(string sessionId, string documentId, long baseVersion, string elementId)
	{
		EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion);
		if (!session.Document.Unwrap(elementId))
			throw new InvalidOperationException("MewUI unwrap was rejected: a non-root container with children is required, and a single-child host takes one.");
		Mutated(session);
		return State(session);
	}
	/// <summary>Replaces a ComboBox/ListBox's &lt;Item&gt; strings from the shared list editor.</summary>
	[JsonRpcMethod("design/set-items")] public DesignerSessionState SetItems(string sessionId, string documentId, long baseVersion, string elementId, DesignerCollectionItemEdit[] items) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Document.SetItems(elementId, (items ?? Array.Empty<DesignerCollectionItemEdit>()).Select(item => item.Text ?? "").ToList())) throw new InvalidOperationException("MewUI items edit was rejected: a ComboBox or ListBox is required."); Mutated(session); return State(session); }
	[JsonRpcMethod("design/rename")] public DesignerSessionState Rename(string sessionId, string documentId, long baseVersion, string elementId, string newName) => SetProperty(sessionId, documentId, baseVersion, elementId, "$name", newName);
	[JsonRpcMethod("design/reorder")] public DesignerSessionState Reorder(string sessionId, string documentId, long baseVersion, string elementId, int delta) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Document.Reorder(elementId, delta)) throw new InvalidOperationException("MewUI reorder was rejected."); Mutated(session); return State(session); }
	[JsonRpcMethod("design/set-bounds")] public DesignerSessionState SetBounds(string sessionId, string documentId, long baseVersion, string elementId, double x, double y, double width, double height) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Document.SetCanvasBounds(elementId, x, y, width, height)) throw new InvalidOperationException("MewUI bounds mutation was rejected: only direct Canvas children can be positioned or resized."); Mutated(session); return State(session); }
	[JsonRpcMethod("design/undo")] public DesignerSessionState Undo(string sessionId, string documentId, long baseVersion) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Document.Undo()) throw new InvalidOperationException("Nothing to undo."); session.UndoDepth--; session.RedoDepth++; session.Version++; return State(session); }
	[JsonRpcMethod("design/redo")] public DesignerSessionState Redo(string sessionId, string documentId, long baseVersion) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Document.Redo()) throw new InvalidOperationException("Nothing to undo."); session.RedoDepth--; session.UndoDepth++; session.Version++; return State(session); }
	[JsonRpcMethod("session/flush")] public DesignerEditSet Flush(string sessionId, string documentId, long baseVersion) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); return new DesignerEditSet { SessionId = this.sessionId, DocumentId = documentId, BaseVersion = session.Version, Files = { new DesignerSourceFileSnapshot { FileName = session.FileName, Kind = "Designer", Text = session.Document.ToXaml() } } }; }
	[JsonRpcMethod("session/close")] public object Close(string sessionId, string documentId) { EnsureSession(sessionId); documents.Remove(sessionId, documentId, _ => { }); return new(); }
	DesignerSessionState State(DocumentSession session) { var root = session.Document.Root; var state = new DesignerSessionState { SessionId = sessionId, DocumentId = session.DocumentId, Version = session.Version, Accepted = !session.Document.HasErrors && session.Document.LastParseSucceeded, Error = session.Document.Error, RootType = root?.Type ?? "", ComponentCount = root == null ? 0 : Count(root), Tree = root == null ? null : Node(root, "0"), CanUndo = session.UndoDepth > 0, CanRedo = session.RedoDepth > 0 }; Render(session, root, state); return state; }
	static void Render(DocumentSession session, MxamlObject? root, DesignerSessionState state)
	{
		session.Bounds.Clear();
		if (root == null || state.Tree == null) return;
#if MEWUI_RENDER
		var result = MewUIRenderer.TryRender(root, session.Document.ToXaml(), session.Version);
		state.Diagnostics.AddRange(result.Diagnostics.Select(m => new DesignerDiagnostic { Severity = "Warning", Message = m }));
		if (result.Frame.Width <= 0) return;
		state.Render = result.Frame;
		Place(state.Tree, "0", result.Bounds, session.Bounds);
#else
		state.Diagnostics.Add(new DesignerDiagnostic { Severity = "Info", Message = "MewUI preview rendering is only wired up for the macOS and Windows backends; this host shows the element tree without a frame." });
#endif
	}
	static void Place(DesignerElementNode node, string path, Dictionary<string, (double X, double Y, double Width, double Height)> rendered, Dictionary<string, (double X, double Y, double Width, double Height)> byId)
	{
		if (rendered.TryGetValue(path, out var b)) { node.X = b.X; node.Y = b.Y; node.Width = b.Width; node.Height = b.Height; byId[node.Id] = b; }
		for (var i = 0; i < node.Children.Count; i++) Place(node.Children[i], path + "," + i, rendered, byId);
	}
	[JsonRpcMethod("design/hit-test")]
	public DesignerHitTestResult HitTest(string sessionId, string documentId, long baseVersion, double x, double y) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); var hit = session.Bounds.Where(p => x >= p.Value.X && y >= p.Value.Y && x <= p.Value.X + p.Value.Width && y <= p.Value.Y + p.Value.Height).OrderBy(p => p.Value.Width * p.Value.Height).FirstOrDefault(); return string.IsNullOrEmpty(hit.Key) ? new DesignerHitTestResult() : new DesignerHitTestResult { Hit = true, ComponentName = hit.Key, Chain = { hit.Key } }; }
	static void Mutated(DocumentSession session) { session.UndoDepth++; session.RedoDepth = 0; session.Version++; }
	// An unnamed element gets a synthetic, path-based id ("#0,2,1") so siblings stay distinct on the
	// canvas; Name stays empty, and mutations addressed to such an id are rejected by the document.
	static DesignerElementNode Node(MxamlObject n, string path) => new() { Id = string.IsNullOrEmpty(n.Name) ? "#" + path : n.Name, Name = n.Name, Type = n.Type, Properties = n.Attributes.Where(a => !a.IsEvent).Select(a => Property(n.Type, a)).Prepend(new DesignerPropertyInfo { Name = "$name", DisplayName = "Name", Value = n.Name, Category = "Identity", ShouldSerialize = true }).Concat(ItemsRow(n)).ToList(), Events = MewUIControlCatalog.Events.Select(name => new DesignerEventInfo { Name = name, Category = "MewUI Events", Handler = n.Attributes.FirstOrDefault(a => a.IsEvent && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? "" }).ToList(), Children = n.Children.Select((c, i) => Node(c, path + "," + i)).ToList() };
	static DesignerPropertyInfo Property(string type, MxamlAttribute attribute)
	{
		var kind = MewUIControlCatalog.KindOf(type, attribute.Name);
		var info = new DesignerPropertyInfo { Name = attribute.Name, DisplayName = attribute.Name, Value = attribute.Value, Category = "MewUI", Kind = kind switch { MxamlPropertyKind.Boolean => "Boolean", MxamlPropertyKind.Double or MxamlPropertyKind.Int32 => "Number", MxamlPropertyKind.Enum => "Enum", _ => "String" }, ShouldSerialize = true };
		if (kind == MxamlPropertyKind.Enum) info.AllowedValues.AddRange(MewUIControlCatalog.EnumValuesOf(type, attribute.Name));
		return info;
	}
	static int Count(MxamlObject n) => 1 + n.Children.Sum(Count);
	void EnsureSession(string candidate) => documents.ValidateSession(candidate);
	DocumentSession GetOrCreate(string documentId) => documents.GetOrAdd(sessionId, documentId, () => new DocumentSession(documentId));
	DocumentSession Get(string documentId) => documents.Get(sessionId, documentId);
	static void EnsureVersion(DocumentSession session, long candidate) { if (candidate != session.Version) throw new InvalidOperationException($"Stale version {candidate}; current is {session.Version}."); }
	/// <summary>A ComboBox/ListBox's items as the shared ItemsCollection row (committed by design/set-items).</summary>
	static IEnumerable<DesignerPropertyInfo> ItemsRow(MxamlObject n)
	{
		if (!MewUIControlCatalog.HasStringItems(n.Type)) yield break;
		yield return new DesignerPropertyInfo { Name = "$items", DisplayName = "Items", Category = "Common", Kind = "Collection", EditorKind = "ItemsCollection",
			IsReadOnly = true, Value = "(" + n.Items.Count + (n.Items.Count == 1 ? " item)" : " items)"), CollectionItems = n.Items.ToList(), ShouldSerialize = n.Items.Count > 0 };
	}
	[JsonRpcMethod("ping")] public object Ping() => new(); [JsonRpcMethod("shutdown")] public object Shutdown() { documents.CloseAll(_ => { }); shutdown.Set(); return new(); } public void WaitForShutdown() => shutdown.Wait(); public void OnParentDisconnected() => shutdown.Set();
	sealed class DocumentSession { public DocumentSession(string documentId) => DocumentId = documentId; public string DocumentId { get; } public MxamlDocument Document { get; } = new(); public string FileName = ""; public long Version; public int UndoDepth; public int RedoDepth; public readonly Dictionary<string, (double X, double Y, double Width, double Height)> Bounds = new(); }
}
