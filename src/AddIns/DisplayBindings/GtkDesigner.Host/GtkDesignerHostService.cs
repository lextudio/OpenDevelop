using System.Security.Cryptography;
using System.Threading;
using System.Diagnostics;
using System.Xml.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;

namespace ICSharpCode.GtkDesigner.Host;

sealed class GtkDesignerHostService : IDesignerChildService
{
	readonly string expectedToken;
	readonly ManualResetEventSlim shutdown = new(false);
	readonly Dictionary<string, DocumentSession> documents = new(StringComparer.Ordinal);
	string sessionId = "";

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
		EnsureSession(snapshot.SessionId);
		var session = GetOrCreate(snapshot.DocumentId);
		session.Version = snapshot.Version; session.FileName = snapshot.PrimaryFileName;
		session.Editor.Reset(snapshot.Files.FirstOrDefault()?.Text ?? "");
		return State(session);
	}

	[JsonRpcMethod("design/set-property")]
	public DesignerSessionState SetProperty(string documentId, long baseVersion, string elementId, string propertyName, string value)
	{
		var session = Get(documentId); EnsureVersion(session, baseVersion);
		var changed = propertyName == "$id" ? session.Editor.Rename(elementId, value) : session.Editor.SetProperty(elementId, propertyName, value);
		if (!changed) throw new InvalidOperationException("GTK property mutation was rejected.");
		session.Version++; return State(session);
	}

	[JsonRpcMethod("design/add-element")]
	public DesignerSessionState AddElement(string documentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y)
	{
		var session = Get(documentId); EnsureVersion(session, baseVersion);
		if (!session.Editor.Add(parentId, string.IsNullOrEmpty(item.TypeName) ? item.Name : item.TypeName)) throw new InvalidOperationException("GTK element insertion was rejected.");
		session.Version++; return State(session);
	}

	[JsonRpcMethod("design/delete-elements")]
	public DesignerSessionState DeleteElements(string documentId, long baseVersion, string[] elementIds)
	{
		var session = Get(documentId); EnsureVersion(session, baseVersion);
		foreach (var id in elementIds) if (!session.Editor.Remove(id)) throw new InvalidOperationException("GTK element deletion was rejected: " + id);
		session.Version++; return State(session);
	}

	[JsonRpcMethod("design/rename")]
	public DesignerSessionState Rename(string documentId, long baseVersion, string elementId, string newName) => SetProperty(documentId, baseVersion, elementId, "$id", newName);
	[JsonRpcMethod("design/set-event")]
	public DesignerSessionState SetEvent(string documentId, long baseVersion, string elementId, string eventName, string handlerName) { var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Editor.SetSignal(elementId, eventName, handlerName)) throw new InvalidOperationException("GTK signal mutation was rejected."); session.Version++; return State(session); }
	[JsonRpcMethod("design/reorder")]
	public DesignerSessionState Reorder(string documentId, long baseVersion, string elementId, int delta) { var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Editor.Reorder(elementId, delta)) throw new InvalidOperationException("GTK reorder was rejected."); session.Version++; return State(session); }
	[JsonRpcMethod("design/hit-test")]
	public DesignerHitTestResult HitTest(string documentId, long baseVersion, double x, double y) { var session = Get(documentId); EnsureVersion(session, baseVersion); var hit = session.NativeBounds.Where(p => x >= p.Value.X && y >= p.Value.Y && x <= p.Value.X + p.Value.Width && y <= p.Value.Y + p.Value.Height).OrderBy(p => p.Value.Width * p.Value.Height).FirstOrDefault(); return string.IsNullOrEmpty(hit.Key) ? new DesignerHitTestResult() : new DesignerHitTestResult { Hit = true, ComponentName = hit.Key, Chain = { hit.Key } }; }
	[JsonRpcMethod("design/undo")]
	public DesignerSessionState Undo(string documentId, long baseVersion) { var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Editor.Undo()) throw new InvalidOperationException("Nothing to undo."); session.Version++; return State(session); }
	[JsonRpcMethod("design/redo")]
	public DesignerSessionState Redo(string documentId, long baseVersion) { var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Editor.Redo()) throw new InvalidOperationException("Nothing to redo."); session.Version++; return State(session); }

	[JsonRpcMethod("session/flush")]
	public DesignerEditSet Flush(string documentId, long baseVersion)
	{
		var session = Get(documentId); EnsureVersion(session, baseVersion);
		return new DesignerEditSet { SessionId = sessionId, DocumentId = documentId, BaseVersion = session.Version,
			Files = { new DesignerSourceFileSnapshot { FileName = session.FileName, Kind = "Designer", Text = session.Editor.Text } } };
	}

	[JsonRpcMethod("session/close")]
	public object Close(string documentId) { documents.Remove(documentId); return new(); }

	DesignerSessionState State(DocumentSession session)
	{
		MeasureNativeBounds(session);
		var render = string.IsNullOrEmpty(session.Editor.Error) ? Render(session, session.Editor.Roots.FirstOrDefault()?.Id) : null;
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
			session.NativeBounds.TryGetValue(rootId, out var rootBounds);
			var width = Math.Max(1, (int)Math.Ceiling(rootBounds.Width));
			var height = Math.Max(1, (int)Math.Ceiling(rootBounds.Height));
			var bytes = CreatePreviewPng(width, height, session.NativeBounds.Values);
			return new DesignerRenderFrame { Sequence = session.Version, Width = width, Height = height, PngBase64 = Convert.ToBase64String(bytes) };
		} catch (Exception ex) { session.RenderDiagnostic = ex.Message; return null; }
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
	static byte[] CreatePreviewPng(int width, int height, IEnumerable<(double X, double Y, double Width, double Height)> bounds)
	{
		var pixels = new byte[height * (1 + width * 4)];
		for (var y = 0; y < height; y++) {
			var row = y * (1 + width * 4);
			pixels[row] = 0;
			for (var x = 0; x < width; x++) {
				var offset = row + 1 + x * 4;
				pixels[offset] = 246; pixels[offset + 1] = 247; pixels[offset + 2] = 249; pixels[offset + 3] = 255;
			}
		}
		foreach (var b in bounds.OrderByDescending(b => b.Width * b.Height))
			DrawRect(pixels, width, height, (int)Math.Round(b.X), (int)Math.Round(b.Y), (int)Math.Round(b.Width), (int)Math.Round(b.Height));
		using var stream = new MemoryStream();
		stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
		WriteChunk(stream, "IHDR", BigEndian(width).Concat(BigEndian(height)).Concat(new byte[] { 8, 6, 0, 0, 0 }).ToArray());
		using var compressed = new MemoryStream();
		using (var z = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
			z.Write(pixels, 0, pixels.Length);
		WriteChunk(stream, "IDAT", compressed.ToArray());
		WriteChunk(stream, "IEND", Array.Empty<byte>());
		return stream.ToArray();
	}
	static void DrawRect(byte[] pixels, int imageWidth, int imageHeight, int x, int y, int width, int height)
	{
		if (width <= 0 || height <= 0) return;
		var left = Math.Clamp(x, 0, imageWidth - 1); var top = Math.Clamp(y, 0, imageHeight - 1);
		var right = Math.Clamp(x + width - 1, 0, imageWidth - 1); var bottom = Math.Clamp(y + height - 1, 0, imageHeight - 1);
		for (var px = left; px <= right; px++) { SetPixel(pixels, imageWidth, px, top, 210, 216, 226); SetPixel(pixels, imageWidth, px, bottom, 210, 216, 226); }
		for (var py = top; py <= bottom; py++) { SetPixel(pixels, imageWidth, left, py, 210, 216, 226); SetPixel(pixels, imageWidth, right, py, 210, 216, 226); }
	}
	static void SetPixel(byte[] pixels, int imageWidth, int x, int y, byte r, byte g, byte b)
	{
		var offset = y * (1 + imageWidth * 4) + 1 + x * 4;
		pixels[offset] = r; pixels[offset + 1] = g; pixels[offset + 2] = b; pixels[offset + 3] = 255;
	}
	static byte[] BigEndian(int value) => new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
	static void WriteChunk(Stream stream, string type, byte[] data)
	{
		var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
		stream.Write(BigEndian(data.Length)); stream.Write(typeBytes); stream.Write(data);
		var crcBytes = typeBytes.Concat(data).ToArray();
		stream.Write(BigEndian(unchecked((int)Crc32(crcBytes))));
	}
	static uint Crc32(byte[] data)
	{
		uint crc = 0xffffffff;
		foreach (var value in data) {
			crc ^= value;
			for (var i = 0; i < 8; i++) crc = (crc & 1) == 1 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
		}
		return ~crc;
	}
	static IEnumerable<GtkUiNode> Flatten(GtkUiNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
	static int Count(DesignerElementNode node) => 1 + node.Children.Sum(Count);
	void EnsureSession(string candidate) { if (candidate != sessionId) throw new InvalidOperationException("Stale designer session."); }
	DocumentSession GetOrCreate(string documentId) => documents.TryGetValue(documentId, out var session) ? session : documents[documentId] = new DocumentSession(documentId);
	DocumentSession Get(string documentId) => documents.TryGetValue(documentId, out var session) ? session : throw new InvalidOperationException("Unknown document.");
	static void EnsureVersion(DocumentSession session, long candidate) { if (candidate != session.Version) throw new InvalidOperationException($"Stale version {candidate}; current is {session.Version}."); }
	[JsonRpcMethod("ping")] public object Ping() => new();
	[JsonRpcMethod("shutdown")] public object Shutdown() { shutdown.Set(); return new(); }
	public void WaitForShutdown() => shutdown.Wait();
	sealed class DocumentSession
	{
		public DocumentSession(string documentId) => DocumentId = documentId;
		public string DocumentId { get; }
		public GtkUiDocumentEditor Editor { get; } = new();
		public Dictionary<string, (double X, double Y, double Width, double Height)> NativeBounds { get; } = new(StringComparer.Ordinal);
		public string RenderDiagnostic = "";
		public string FileName = "";
		public long Version;
	}
}
