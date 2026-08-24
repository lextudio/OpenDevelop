using System.Security.Cryptography;
using System.Threading;
using System.Xml.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;

namespace ICSharpCode.GtkDesigner.Host;

sealed class GtkDesignerHostService : IDesignerChildService
{
	readonly string expectedToken;
	readonly ManualResetEventSlim shutdown = new(false);
	readonly DesignerDocumentRegistry<DocumentSession> documents = new();
	readonly GLib.MainContext gtkContext = GLib.MainContext.Default();
	readonly int gtkThreadId = Environment.CurrentManagedThreadId;
	readonly System.Collections.Concurrent.ConcurrentQueue<Action> gtkWork = new();
	readonly AutoResetEvent gtkWorkAvailable = new(false);
	string sessionId = "";

	public GtkDesignerHostService(string expectedToken) => this.expectedToken = expectedToken;

	[JsonRpcMethod("initialize")]
	public HostHandshake Initialize(string token, int protocolVersion, string sessionId)
	{
		if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedToken), Convert.FromHexString(token))) throw new UnauthorizedAccessException();
		if (protocolVersion != DesignerProtocol.Version) throw new NotSupportedException();
		documents.Initialize(sessionId);
		this.sessionId = sessionId;
		return new HostHandshake { ProtocolVersion = DesignerProtocol.Version, Runtime = "GTK 4 document model", ProcessId = Environment.ProcessId, SessionId = sessionId };
	}

	[JsonRpcMethod("session/open")]
	public DesignerSessionState Open(DesignerDocumentSnapshot snapshot) => Load(snapshot);
	[JsonRpcMethod("session/update")]
	public DesignerSessionState Update(DesignerDocumentSnapshot snapshot) => Load(snapshot);
	DesignerSessionState Load(DesignerDocumentSnapshot snapshot)
	{
		EnsureSession(snapshot.SessionId);
		var session = GetOrCreate(snapshot.DocumentId);
		session.Version = snapshot.Version; session.FileName = snapshot.PrimaryFileName;
		session.Editor.Reset(snapshot.Files.FirstOrDefault()?.Text ?? "");
		return State(session, true);
	}

	[JsonRpcMethod("design/set-property")]
	public DesignerSessionState SetProperty(string documentId, long baseVersion, string elementId, string propertyName, string value)
	{
		var session = Get(documentId); EnsureVersion(session, baseVersion);
		var changed = propertyName == "$id" ? session.Editor.Rename(elementId, value) : session.Editor.SetProperty(elementId, propertyName, value);
		if (!changed) throw new InvalidOperationException("GTK property mutation was rejected.");
		session.Version++; return State(session, false);
	}

	[JsonRpcMethod("design/add-element")]
	public DesignerSessionState AddElement(string documentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y)
	{
		var session = Get(documentId); EnsureVersion(session, baseVersion);
		if (!session.Editor.Add(parentId, string.IsNullOrEmpty(item.TypeName) ? item.Name : item.TypeName)) throw new InvalidOperationException("GTK element insertion was rejected.");
		session.Version++; return State(session, false);
	}

	[JsonRpcMethod("design/delete-elements")]
	public DesignerSessionState DeleteElements(string documentId, long baseVersion, string[] elementIds)
	{
		var session = Get(documentId); EnsureVersion(session, baseVersion);
		foreach (var id in elementIds) if (!session.Editor.Remove(id)) throw new InvalidOperationException("GTK element deletion was rejected: " + id);
		session.Version++; return State(session, false);
	}

	[JsonRpcMethod("design/rename")]
	public DesignerSessionState Rename(string documentId, long baseVersion, string elementId, string newName) => SetProperty(documentId, baseVersion, elementId, "$id", newName);
	[JsonRpcMethod("design/set-event")]
	public DesignerSessionState SetEvent(string documentId, long baseVersion, string elementId, string eventName, string handlerName) { var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Editor.SetSignal(elementId, eventName, handlerName)) throw new InvalidOperationException("GTK signal mutation was rejected."); session.Version++; return State(session, false); }
	[JsonRpcMethod("design/reorder")]
	public DesignerSessionState Reorder(string documentId, long baseVersion, string elementId, int delta) { var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Editor.Reorder(elementId, delta)) throw new InvalidOperationException("GTK reorder was rejected."); session.Version++; return State(session, false); }
	[JsonRpcMethod("design/hit-test")]
	public DesignerHitTestResult HitTest(string documentId, long baseVersion, double x, double y) { var session = Get(documentId); EnsureVersion(session, baseVersion); var hit = session.NativeBounds.Where(p => x >= p.Value.X && y >= p.Value.Y && x <= p.Value.X + p.Value.Width && y <= p.Value.Y + p.Value.Height).OrderBy(p => p.Value.Width * p.Value.Height).FirstOrDefault(); return string.IsNullOrEmpty(hit.Key) ? new DesignerHitTestResult() : new DesignerHitTestResult { Hit = true, ComponentName = hit.Key, Chain = { hit.Key } }; }
	[JsonRpcMethod("design/undo")]
	public DesignerSessionState Undo(string documentId, long baseVersion) { var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Editor.Undo()) throw new InvalidOperationException("Nothing to undo."); session.Version++; return State(session, false); }
	[JsonRpcMethod("design/redo")]
	public DesignerSessionState Redo(string documentId, long baseVersion) { var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Editor.Redo()) throw new InvalidOperationException("Nothing to redo."); session.Version++; return State(session, false); }

	[JsonRpcMethod("design/render")]
	public DesignerSessionState RenderDocument(string documentId, long baseVersion) { var session = Get(documentId); EnsureVersion(session, baseVersion); return State(session, true); }

	[JsonRpcMethod("session/flush")]
	public DesignerEditSet Flush(string documentId, long baseVersion)
	{
		var session = Get(documentId); EnsureVersion(session, baseVersion);
		return new DesignerEditSet { SessionId = sessionId, DocumentId = documentId, BaseVersion = session.Version,
			Files = { new DesignerSourceFileSnapshot { FileName = session.FileName, Kind = "Designer", Text = session.Editor.Text } } };
	}

	[JsonRpcMethod("session/close")]
	public object Close(string documentId)
	{
		documents.Remove(documentId, session => OnGtkThread(() => { session.DisposeNative(); return true; }));
		return new();
	}

	DesignerSessionState State(DocumentSession session, bool renderNative)
	{
		var render = renderNative ? OnGtkThread(() => {
			MeasureNativeBounds(session);
			return string.IsNullOrEmpty(session.Editor.Error) ? Render(session, session.Editor.Roots.FirstOrDefault()?.Id) : null;
		}) : session.CachedRender;
		var roots = session.Editor.Roots.Select(n => Node(session, n)).ToList();
		var tree = roots.Count == 1 ? roots[0] : new DesignerElementNode { Id = "$interface", Name = "interface", Type = "GtkInterface", Children = roots };
		var result = new DesignerSessionState { SessionId = sessionId, DocumentId = session.DocumentId, Version = session.Version, Accepted = string.IsNullOrEmpty(session.Editor.Error), Error = session.Editor.Error, RootType = tree.Type, ComponentCount = Count(tree), Tree = tree, Render = render };
		if (!string.IsNullOrEmpty(session.RenderDiagnostic)) result.Diagnostics.Add(new DesignerDiagnostic { Severity = "Warning", Message = session.RenderDiagnostic });
		return result;
	}
	DesignerRenderFrame? Render(DocumentSession session, string? rootId)
	{
		session.RenderDiagnostic = "";
		if (string.IsNullOrEmpty(rootId) || rootId.StartsWith("$", StringComparison.Ordinal)) return null;
		try {
			var document = XDocument.Parse(session.Editor.Text, LoadOptions.PreserveWhitespace);
			document.Descendants().Where(e => e.Name.LocalName == "signal").Remove();
			var xml = document.ToString(SaveOptions.DisableFormatting);
			var renderKey = rootId + ":" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(xml)));
			if (session.CachedRenderKey == renderKey && session.CachedRender != null)
				return new DesignerRenderFrame { Sequence = session.Version, Width = session.CachedRender.Width, Height = session.CachedRender.Height, PngBase64 = session.CachedRender.PngBase64 };
			var bytes = NativeGtkRenderer.Render(session, xml, rootId, out var width, out var height);
			var frame = new DesignerRenderFrame { Sequence = session.Version, Width = width, Height = height, PngBase64 = Convert.ToBase64String(bytes) };
			session.CachedRenderKey = renderKey; session.CachedRender = frame;
			return frame;
		} catch (Exception ex) { session.RenderDiagnostic = ex.Message; return null; }
	}
	static class NativeGtkRenderer
	{
		static readonly object Gate = new();
		static Gsk.CairoRenderer? renderer;

		public static byte[] Render(DocumentSession session, string xml, string rootId, out int width, out int height)
		{
			lock (Gate) {
				if (session.NativeVersion != session.Version || session.NativeRootId != rootId) session.LoadNative(xml, rootId);
				var root = session.NativeRoot ?? throw new InvalidOperationException($"GTK object '{rootId}' is not a widget.");
				root.Measure(Gtk.Orientation.Horizontal, -1, out _, out var naturalWidth, out _, out _);
				width = Math.Max(1, naturalWidth);
				root.Measure(Gtk.Orientation.Vertical, width, out _, out var naturalHeight, out _, out _);
				height = Math.Max(1, naturalHeight);
				if (root is Gtk.Window window) {
					window.GetDefaultSize(out var defaultWidth, out var defaultHeight);
					width = Math.Max(width, defaultWidth); height = Math.Max(height, defaultHeight);
				}
				root.SetSizeRequest(width, height);
				root.Realize();
				root.Allocate(width, height, -1, null);
				if (!root.GetMapped()) {
					if (root is Gtk.Window nativeWindow) nativeWindow.SetOpacity(0);
					root.SetVisible(true);
				}
				root.QueueDraw();
				DrainMainContext();
				var paintTarget = root is Gtk.Window mappedWindow ? mappedWindow.GetChild() ?? root : root;
				var snapshot = Gtk.Snapshot.New();
				if (!ReferenceEquals(paintTarget, root)) root.SnapshotChild(paintTarget, snapshot);
				else {
					using var paintable = Gtk.WidgetPaintable.New(paintTarget);
					paintable.Snapshot(snapshot, width, height);
				}
				var node = snapshot.FreeToNode() ?? throw new InvalidOperationException("GTK produced an empty render node.");
				renderer ??= CreateRenderer();
				using var texture = renderer.RenderTexture(node, null);
				node.Unref();
				var pngPath = Path.Combine(Path.GetTempPath(), "OpenDevelop-GtkPreview-" + Guid.NewGuid().ToString("N") + ".png");
				try {
					if (!texture.SaveToPng(pngPath)) throw new InvalidOperationException("GTK could not encode the preview texture.");
					return File.ReadAllBytes(pngPath);
				} finally { try { File.Delete(pngPath); } catch { } }
			}
		}

		static Gsk.CairoRenderer CreateRenderer()
		{
			var value = Gsk.CairoRenderer.New();
			value.Realize(null);
			return value;
		}

		static void DrainMainContext()
		{
			var context = GLib.MainContext.Default();
			for (var iteration = 0; iteration < 8 && context.Pending(); iteration++) context.Iteration(false);
		}
	}
	DesignerElementNode Node(DocumentSession session, GtkUiNode node) { session.NativeBounds.TryGetValue(node.Id, out var bounds); return new() { Id = node.Id, Name = node.Id, Type = node.ClassName, X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height,
		Properties = node.Properties.Select(p => new DesignerPropertyInfo { Name = p.Key, DisplayName = p.Key, Value = p.Value, Category = "GTK" }).Prepend(new DesignerPropertyInfo { Name = "$id", DisplayName = "ID", Value = node.Id, Category = "GTK" }).ToList(),
		Children = node.Children.Select(n => Node(session, n)).ToList() }; }
	void MeasureNativeBounds(DocumentSession session)
	{
		session.NativeBounds.Clear(); if (!string.IsNullOrEmpty(session.Editor.Error) || session.Editor.Roots.Count == 0) return;
		try {
			var document = XDocument.Parse(session.Editor.Text); document.Descendants().Where(e => e.Name.LocalName == "signal").Remove(); var xml = document.ToString(SaveOptions.DisableFormatting);
			using var builder = Gtk.Builder.NewFromString(xml, -1); var rootId = session.Editor.Roots[0].Id; if (builder.GetObject(rootId) is not Gtk.Widget root) return;
			root.Measure(Gtk.Orientation.Horizontal, -1, out var minWidth, out var naturalWidth, out _, out _); var width = Math.Max(1, naturalWidth);
			root.Measure(Gtk.Orientation.Vertical, width, out var minHeight, out var naturalHeight, out _, out _); var height = Math.Max(1, naturalHeight);
			if (root is Gtk.Window window) { window.GetDefaultSize(out var defaultWidth, out var defaultHeight); width = Math.Max(width, defaultWidth); height = Math.Max(height, defaultHeight); }
			root.Allocate(width, height, -1, null); session.NativeBounds[rootId] = (0, 0, width, height);
			foreach (var node in session.Editor.Roots.SelectMany(Flatten)) if (builder.GetObject(node.Id) is Gtk.Widget widget && widget.ComputeBounds(root, out var rect)) session.NativeBounds[node.Id] = (rect.GetX(), rect.GetY(), rect.GetWidth(), rect.GetHeight());
		} catch { session.NativeBounds.Clear(); }
	}
	static IEnumerable<GtkUiNode> Flatten(GtkUiNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
	static int Count(DesignerElementNode node) => 1 + node.Children.Sum(Count);
	T OnGtkThread<T>(Func<T> action)
	{
		if (Environment.CurrentManagedThreadId == gtkThreadId) return action();
		using var completed = new ManualResetEventSlim();
		T? result = default;
		Exception? failure = null;
		gtkWork.Enqueue(() => {
			try { result = action(); } catch (Exception ex) { failure = ex; } finally { completed.Set(); }
		});
		gtkWorkAvailable.Set();
		completed.Wait();
		if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
		return result!;
	}
	void EnsureSession(string candidate) => documents.ValidateSession(candidate);
	DocumentSession GetOrCreate(string documentId) => documents.GetOrAdd(documentId, () => new DocumentSession(documentId));
	DocumentSession Get(string documentId) => documents.Get(documentId);
	static void EnsureVersion(DocumentSession session, long candidate) { if (candidate != session.Version) throw new InvalidOperationException($"Stale version {candidate}; current is {session.Version}."); }
	[JsonRpcMethod("ping")] public object Ping() => new();
	[JsonRpcMethod("shutdown")] public object Shutdown()
	{
		documents.CloseAll(session => OnGtkThread(() => { session.DisposeNative(); return true; }));
		shutdown.Set(); gtkWorkAvailable.Set(); return new();
	}
	public void WaitForShutdown()
	{
		while (!shutdown.IsSet) {
			while (gtkWork.TryDequeue(out var action)) action();
			while (gtkContext.Pending()) gtkContext.Iteration(false);
			gtkWorkAvailable.WaitOne(10);
		}
	}
	public void OnParentDisconnected() { shutdown.Set(); gtkWorkAvailable.Set(); }
	sealed class DocumentSession
	{
		public DocumentSession(string documentId) => DocumentId = documentId;
		public string DocumentId { get; }
		public GtkUiDocumentEditor Editor { get; } = new();
		public Dictionary<string, (double X, double Y, double Width, double Height)> NativeBounds { get; } = new(StringComparer.Ordinal);
		public string RenderDiagnostic = "";
		public string CachedRenderKey = "";
		public DesignerRenderFrame? CachedRender;
		public Gtk.Builder? NativeBuilder;
		public Gtk.Widget? NativeRoot;
		public string NativeRootId = "";
		public long NativeVersion = -1;
		public string FileName = "";
		public long Version;
		public void LoadNative(string xml, string rootId)
		{
			DisposeNative();
			NativeBuilder = Gtk.Builder.NewFromString(xml, -1);
			NativeRoot = NativeBuilder.GetObject(rootId) as Gtk.Widget;
			NativeRootId = rootId;
			NativeVersion = Version;
		}
		public void DisposeNative()
		{
			if (NativeRoot is Gtk.Window window) { window.SetVisible(false); window.Destroy(); }
			else if (NativeRoot != null) { NativeRoot.SetVisible(false); NativeRoot.Unrealize(); }
			NativeRoot = null;
			NativeBuilder?.Dispose();
			NativeBuilder = null;
			NativeVersion = -1;
		}
	}
}
