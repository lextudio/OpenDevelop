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

	public GtkDesignerHostService(string expectedToken)
	{
		this.expectedToken = expectedToken;
		GtkPropertyMetadata.Preload();
	}

	[JsonRpcMethod("initialize")]
	public HostHandshake Initialize(string token, int protocolVersion, string sessionId)
	{
		DesignerHostHandshakeValidator.Validate(expectedToken, token, protocolVersion);
		documents.Initialize(sessionId);
		this.sessionId = sessionId;
		return new HostHandshake { ProtocolVersion = DesignerProtocol.Version, Runtime = "GTK 4 document model", ProcessId = Environment.ProcessId, SessionId = sessionId };
	}

	[JsonRpcMethod("session/open")]
	public DesignerSessionState Open(DesignerDocumentSnapshot snapshot) => Load(snapshot, create: true);
	[JsonRpcMethod("session/update")]
	public DesignerSessionState Update(DesignerDocumentSnapshot snapshot) => Load(snapshot, create: false);
	DesignerSessionState Load(DesignerDocumentSnapshot snapshot, bool create)
	{
		EnsureSession(snapshot.SessionId);
		var session = create
			? GetOrCreate(snapshot.DocumentId)
			: Get(snapshot.DocumentId);
		session.Version = snapshot.Version; session.FileName = snapshot.PrimaryFileName;
		session.Editor.Reset(snapshot.Files.FirstOrDefault()?.Text ?? "");
		if (GtkAdwaita.Requires(session.Editor.Text)) OnGtkThread(() => { GtkAdwaita.EnsureInitialized(); return true; });
		return State(session, true);
	}

	[JsonRpcMethod("design/set-property")]
	public DesignerSessionState SetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName, string value)
	{
		EnsureSession(sessionId);
		var session = Get(documentId); EnsureVersion(session, baseVersion);
		bool changed;
		if (propertyName == "$id") changed = session.Editor.Rename(elementId, value);
		else {
			// Type-checked against the GIR catalogue first, so invalid text never enters the document.
			var (node, parentClass) = Locate(session.Editor, elementId);
			var normalized = GtkPropertyMetadata.Validate(node, propertyName, value, parentClass);
			changed = propertyName.StartsWith(GtkPropertyMetadata.LayoutPrefix, StringComparison.Ordinal)
				? session.Editor.SetLayoutProperty(elementId, propertyName.Substring(GtkPropertyMetadata.LayoutPrefix.Length), normalized)
				: session.Editor.SetProperty(elementId, propertyName, normalized,
					translatable: node != null && GtkPropertyMetadata.IsTranslatable(node.ClassName, propertyName));
		}
		if (!changed) throw new InvalidOperationException("GTK property mutation was rejected.");
		session.Version++; return State(session, false);
	}

	/// <summary>The companion wiring source and typed handler signatures for the document's
	/// &lt;signal&gt; entries (see <see cref="GtkSignalWiring"/>). Needs the GIR catalogue.</summary>
	[JsonRpcMethod("gtk/signal-wiring")]
	public GtkSignalWiringResult SignalWiring(string sessionId, string documentId, string uiFileName, string? namespaceName, string className)
	{
		EnsureSession(sessionId);
		var session = Get(documentId);
		var catalog = GtkPropertyMetadata.Catalog ?? throw new InvalidOperationException(GtkPropertyMetadata.Diagnostic);
		var handlers = GtkSignalWiring.Handlers(session.Editor.Text, catalog);
		return new GtkSignalWiringResult {
			Source = GtkSignalWiring.GenerateCompanion(uiFileName, namespaceName, className, handlers),
			Handlers = handlers.Select(GtkSignalHandlerInfo.From).ToList()
		};
	}

	/// <summary>Removes the property so GTK's own default applies - the GIR catalogue is the
	/// defaults model IDesignHostPropertyReset asks for; no default value is ever written.</summary>
	[JsonRpcMethod("design/reset-property")]
	public DesignerSessionState ResetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName)
	{
		EnsureSession(sessionId);
		var session = Get(documentId); EnsureVersion(session, baseVersion);
		var reset = propertyName.StartsWith(GtkPropertyMetadata.LayoutPrefix, StringComparison.Ordinal)
			? session.Editor.SetLayoutProperty(elementId, propertyName.Substring(GtkPropertyMetadata.LayoutPrefix.Length), null)
			: session.Editor.SetProperty(elementId, propertyName, null);
		if (!reset) throw new InvalidOperationException("GTK property reset was rejected.");
		session.Version++; return State(session, false);
	}

	[JsonRpcMethod("design/add-element")]
	public DesignerSessionState AddElement(string sessionId, string documentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, double x, double y, DesignerDropTarget dropTarget)
	{
		EnsureSession(sessionId);
		var session = Get(documentId); EnsureVersion(session, baseVersion);
		// A drop carries its design position (a toolbox click sends -1, -1): resolve it with the same
		// planner the IDE drew its indicator from, against GTK's own measured bounds.
		// A dropTarget is the IDE's own resolution of this point (DesignerDropTarget, computed by
		// the same planner against the same snapshot), so prefer it: the client already had to
		// compute it to draw the insertion caret, and recomputing here risks the two disagreeing.
		// A toolbox click has no point and sends none, so the host resolves it itself.
		int? index = dropTarget?.Index; (int, int)? cell = null;
		if (dropTarget?.Cell is { } target) cell = (target.Column, target.Row);
		if (index == null && cell == null && x >= 0 && y >= 0 && session.Editor.Roots.FirstOrDefault() is { } first
			&& GtkDropPlanner.Plan(DropNode(session, first), x, y) is { } plan && plan.ContainerId == parentId) {
			index = plan.Index; cell = plan.Cell;
		}
		if (!session.Editor.Add(parentId, string.IsNullOrEmpty(item.TypeName) ? item.Name : item.TypeName, index, cell)) throw new InvalidOperationException("GTK element insertion was rejected.");
		session.Version++; return State(session, false);
	}

	[JsonRpcMethod("design/delete-elements")]
	public DesignerSessionState DeleteElements(string sessionId, string documentId, long baseVersion, string[] elementIds)
	{
		EnsureSession(sessionId);
		var session = Get(documentId); EnsureVersion(session, baseVersion);
		foreach (var id in elementIds) if (!session.Editor.Remove(id)) throw new InvalidOperationException("GTK element deletion was rejected: " + id);
		session.Version++; return State(session, false);
	}

	[JsonRpcMethod("design/rename")]
	public DesignerSessionState Rename(string sessionId, string documentId, long baseVersion, string elementId, string newName) => SetProperty(sessionId, documentId, baseVersion, elementId, "$id", newName);
	[JsonRpcMethod("design/set-event")]
	public DesignerSessionState SetEvent(string sessionId, string documentId, long baseVersion, string elementId, string eventName, string handlerName) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Editor.SetSignal(elementId, eventName, handlerName)) throw new InvalidOperationException("GTK signal mutation was rejected."); session.Version++; return State(session, false); }
	[JsonRpcMethod("design/reorder")]
	public DesignerSessionState Reorder(string sessionId, string documentId, long baseVersion, string elementId, int delta) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Editor.Reorder(elementId, delta)) throw new InvalidOperationException("GTK reorder was rejected."); session.Version++; return State(session, false); }
	[JsonRpcMethod("design/hit-test")]
	public DesignerHitTestResult HitTest(string sessionId, string documentId, long baseVersion, double x, double y) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); var hit = session.NativeBounds.Where(p => x >= p.Value.X && y >= p.Value.Y && x <= p.Value.X + p.Value.Width && y <= p.Value.Y + p.Value.Height).OrderBy(p => p.Value.Width * p.Value.Height).FirstOrDefault(); return string.IsNullOrEmpty(hit.Key) ? new DesignerHitTestResult() : new DesignerHitTestResult { Hit = true, ComponentName = hit.Key, Chain = { hit.Key } }; }
	[JsonRpcMethod("design/undo")]
	public DesignerSessionState Undo(string sessionId, string documentId, long baseVersion) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Editor.Undo()) throw new InvalidOperationException("Nothing to undo."); session.Version++; return State(session, false); }
	[JsonRpcMethod("design/redo")]
	public DesignerSessionState Redo(string sessionId, string documentId, long baseVersion) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); if (!session.Editor.Redo()) throw new InvalidOperationException("Nothing to redo."); session.Version++; return State(session, false); }

	[JsonRpcMethod("design/render")]
	public DesignerSessionState RenderDocument(string sessionId, string documentId, long baseVersion) { EnsureSession(sessionId); var session = Get(documentId); EnsureVersion(session, baseVersion); return State(session, true); }

	[JsonRpcMethod("session/flush")]
	public DesignerEditSet Flush(string sessionId, string documentId, long baseVersion)
	{
		EnsureSession(sessionId);
		var session = Get(documentId); EnsureVersion(session, baseVersion);
		return new DesignerEditSet { SessionId = sessionId, DocumentId = documentId, BaseVersion = session.Version,
			Files = { new DesignerSourceFileSnapshot { FileName = session.FileName, Kind = "Designer", Text = session.Editor.Text } } };
	}

	[JsonRpcMethod("session/close")]
	public object Close(string sessionId, string documentId)
	{
		EnsureSession(sessionId);
		documents.Remove(sessionId, documentId, session => OnGtkThread(() => { session.DisposeNative(); return true; }));
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
		var result = new DesignerSessionState { SessionId = sessionId, DocumentId = session.DocumentId, Version = session.Version, Accepted = string.IsNullOrEmpty(session.Editor.Error), Error = session.Editor.Error, RootType = tree.Type, ComponentCount = Count(tree), Tree = tree, Render = render, CanUndo = session.Editor.CanUndo, CanRedo = session.Editor.CanRedo };
		if (!string.IsNullOrEmpty(GtkPropertyMetadata.Diagnostic)) result.Diagnostics.Add(new DesignerDiagnostic { Severity = "Info", Message = GtkPropertyMetadata.Diagnostic });
		if (GtkAdwaita.Requires(session.Editor.Text) && GtkAdwaita.Diagnostic.Length > 0) result.Diagnostics.Add(new DesignerDiagnostic { Severity = "Warning", Message = GtkAdwaita.Diagnostic });
		foreach (var message in session.PreviewDiagnostics.Distinct()) result.Diagnostics.Add(new DesignerDiagnostic { Severity = "Warning", Message = message });
		// A GtkWindow is a Bin: the widget tree holds its content, while the title bar and border are
		// drawn by the window manager, so they cannot appear in the snapshot (Render paints
		// GetChild()). Say so, and let the client draw the chrome - otherwise a window is
		// indistinguishable from a bare content element on the design surface.
		if (session.NativeRoot is Gtk.Window) {
			result.RootIsWindow = true;
			if (string.IsNullOrEmpty(result.RootType))
				result.RootType = session.NativeRoot.GetType().Name;
		}
		if (!string.IsNullOrEmpty(session.RenderDiagnostic)) result.Diagnostics.Add(new DesignerDiagnostic { Severity = "Warning", Message = session.RenderDiagnostic });
		return result;
	}
	/// <summary>The document as the preview's GtkBuilder gets it: no &lt;signal&gt;s (handlers are C#,
	/// not native symbols) and, with the GIR catalogue, sanitised so a custom widget or an invalid
	/// property becomes a placeholder or is skipped instead of failing the whole document. The
	/// edited document itself is never changed.</summary>
	static string PreviewXml(DocumentSession session)
	{
		var document = XDocument.Parse(session.Editor.Text, LoadOptions.PreserveWhitespace);
		document.Descendants().Where(e => e.Name.LocalName == "signal").Remove();
		var xml = document.ToString(SaveOptions.DisableFormatting);
		var diagnostics = new List<string>();
		if (GtkPropertyMetadata.Catalog is { } catalog)
			xml = GtkPreviewSanitizer.Sanitize(xml, catalog, diagnostics, GtkPropertyMetadata.LayoutChildClass, UnavailableReason(session),
				string.IsNullOrEmpty(session.FileName) ? null : Path.GetDirectoryName(session.FileName), PreviewDirectory);
		session.PreviewDiagnostics = diagnostics;
		return xml;
	}

	/// <summary>Why a GIR class cannot be instantiated in this document's preview, or null. Adw
	/// classes need the document to opt in to libadwaita and the library to have loaded.</summary>
	static Func<GirClass, string?> UnavailableReason(DocumentSession session)
	{
		var requiresAdw = GtkAdwaita.Requires(session.Editor.Text);
		return c => GirCatalog.NamespaceOf(c) != "Adw" ? null
			: !requiresAdw ? "a libadwaita widget, but the document does not declare <requires lib=\"libadwaita\">."
			: !GtkAdwaita.Available ? "libadwaita is not available in the designer."
			: null;
	}

	/// <summary>gtk_builder_add_from_string, NOT gtk_builder_new_from_string: the latter treats any
	/// error as fatal (g_error) and aborts the whole host; the former reports a GError, which
	/// Gir.Core raises as an exception the caller turns into a diagnostic.</summary>
	static Gtk.Builder BuildFrom(string xml)
	{
		// From a file, not a string: GtkBuilder loads an image only from a path it resolves against
		// the file it builds from (see GtkPreviewSanitizer.PreviewPath). The file lives in the
		// host's own temp folder, never beside the user's .ui.
		Directory.CreateDirectory(PreviewDirectory);
		var file = Path.Combine(PreviewDirectory, "preview-" + Guid.NewGuid().ToString("N") + ".ui");
		File.WriteAllText(file, xml);
		var builder = Gtk.Builder.New();
		try { builder.AddFromFile(file); return builder; }
		catch { builder.Dispose(); throw; }
		finally { try { File.Delete(file); } catch { } }
	}

	/// <summary>This host's folder for preview .ui files (per process, so pooled hosts do not collide).</summary>
	static readonly string PreviewDirectory = Path.Combine(Path.GetTempPath(), "OpenDevelop-GtkPreview", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

	DesignerRenderFrame? Render(DocumentSession session, string? rootId)
	{
		session.RenderDiagnostic = "";
		if (string.IsNullOrEmpty(rootId) || rootId.StartsWith("$", StringComparison.Ordinal)) return null;
		// Which object is being rendered, and what the realized widget for it is, decides the frame size:
		// a child has no default size of its own and falls back to its natural size. Record it on every
		// call - including the cached path below - so a run can be read back rather than guessed at.
		session.RenderDiagnostic = "render root=" + rootId
			+ " class=" + (session.NativeRoot?.GetType().Name ?? "?")
			+ " size=" + session.NativeRoot?.GetWidth() + "x" + session.NativeRoot?.GetHeight()
			+ " v" + session.Version + "\n";
		try {
			var xml = PreviewXml(session);
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
				// An unmapped window never runs a size-allocate pass on its child, so the content keeps
				// its natural size while the frame reports the size that was asked for. The client then
				// stretches that small bitmap across the large frame it was promised, and a label-and-
				// button window comes out as an 800x600 frame holding an 800x60 strip of squashed text.
				// Allocate the paint target to the size the frame will claim, and settle the context, until
				// it agrees - otherwise the reported size and the rendered pixels disagree.
				if (!ReferenceEquals(paintTarget, root)) {
					for (var attempt = 0; attempt < 4; attempt++) {
						if (paintTarget.GetWidth() == width && paintTarget.GetHeight() == height) break;
						paintTarget.Allocate(width, height, -1, null);
						paintTarget.QueueDraw();
						DrainMainContext();
					}
					// Append rather than assign: the allocation note below must not displace the tree.
					if (Environment.GetEnvironmentVariable("OD_GTK_DUMP_TREE") == "1")
						session.RenderDiagnostic = DescribeWidgetTree(paintTarget, 0) + session.RenderDiagnostic;
					if (paintTarget.GetWidth() != width || paintTarget.GetHeight() != height)
						session.RenderDiagnostic += "GTK allocated the window content to "
							+ paintTarget.GetWidth() + "x" + paintTarget.GetHeight() + " rather than the requested "
							+ width + "x" + height + ", so the preview is smaller than the frame it is reported in.\n";
				}
				// ToNode, not FreeToNode: gtk_snapshot_free_to_node frees the GtkSnapshot, but its
				// managed wrapper keeps a toggle reference, and releasing that later (from the GC)
				// touched freed memory - a native 0xC0000005 in ToggleRegistration.RemoveToggleRef.
				// ToNode leaves the snapshot alive and the wrapper disposes it normally.
				Gsk.RenderNode? node;
				using (var snapshot = Gtk.Snapshot.New()) {
					if (!ReferenceEquals(paintTarget, root)) root.SnapshotChild(paintTarget, snapshot);
					else {
						using var paintable = Gtk.WidgetPaintable.New(paintTarget);
						paintable.Snapshot(snapshot, width, height);
					}
					node = snapshot.ToNode();
				}
				if (node == null) throw new InvalidOperationException("GTK produced an empty render node.");
				renderer ??= CreateRenderer();
				using var texture = renderer.RenderTexture(node, null);
				// Whatever GTK produced is what the canvas has to lay out, so report the texture's size and
				// not the size that was requested. A frame that claims more pixels than it carries is what
				// turned a too-small render into a stretched, distorted surface.
				width = Math.Max(1, texture.Width);
				height = Math.Max(1, texture.Height);
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

			/// <summary>Builds a .ui document the way a preview does and prints what GTK ended up allocating,
		/// without the IDE in the loop. The render path runs inside a child process behind RPC, so a size
		/// that disagrees with the declared default is otherwise only visible as a stretched preview;
		/// this reproduces it in one step and says which of measure, default size or allocate is wrong.
		/// Reached as: GtkDesigner.Host --probe &lt;file.ui&gt; [objectId]</summary>
	/// <summary>Lists the realized widget tree with the size each was actually allocated, which is
		/// what tells a parse problem (a control missing from the tree) apart from a layout one (the control
		/// is there but was given no space).</summary>
		static string DescribeWidgetTree(Gtk.Widget widget, int depth)
		{
			if (depth > 8) return "";
			var line = new System.Text.StringBuilder();
			line.Append(new string(' ', depth * 2)).Append(widget.GetType().Name)
				.Append(" size=").Append(widget.GetWidth()).Append('x').Append(widget.GetHeight()).Append('\n');
			// GTK4 dropped GtkContainer: children are reached through the widget's own first-child /
			// next-sibling chain.
			for (var child = widget.GetFirstChild(); child != null; child = child.GetNextSibling())
				line.Append(DescribeWidgetTree(child, depth + 1));
			return line.ToString();
		}

		static void DrainMainContext()
		{
			var context = GLib.MainContext.Default();
			for (var iteration = 0; iteration < 8 && context.Pending(); iteration++) context.Iteration(false);
		}
	}
	DesignerElementNode Node(DocumentSession session, GtkUiNode node, string? parentClass = null) { session.NativeBounds.TryGetValue(node.Id, out var bounds); return new() { Id = node.Id, Name = node.Id, Type = node.ClassName, X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height,
		Properties = GtkPropertyMetadata.PropertiesFor(node, parentClass).Prepend(new DesignerPropertyInfo { Name = "$id", DisplayName = "ID", Value = node.Id, Category = "Identity" }).ToList(),
		Events = GtkPropertyMetadata.SignalsFor(node.ClassName, SignalsFor).Select(name => new DesignerEventInfo { Name = name, Category = "GTK Signals", Handler = session.Editor.GetSignals(node.Id).GetValueOrDefault(name) ?? "" }).ToList(),
		Children = node.Children.Select(n => Node(session, n, node.ClassName)).ToList() }; }
	/// <summary>The drop planner's view of a node: measured bounds, orientation and grid cell.</summary>
	static GtkDropNode DropNode(DocumentSession session, GtkUiNode node)
	{
		session.NativeBounds.TryGetValue(node.Id, out var b);
		int Layout(string name, int fallback) => node.Layout != null && node.Layout.TryGetValue(name, out var v) && int.TryParse(v, out var i) ? i : fallback;
		return new GtkDropNode(node.Id, node.ClassName, b.X, b.Y, b.Width, b.Height,
			node.Children.Select(c => DropNode(session, c)).ToList(),
			node.Properties.TryGetValue("orientation", out var o) ? o : null,
			Layout("column", 0), Layout("row", 0), Layout("column-span", 1), Layout("row-span", 1));
	}

	/// <summary>The node with <paramref name="id"/> and its parent's class (null for a root).</summary>
	static (GtkUiNode? Node, string? ParentClass) Locate(GtkUiDocumentEditor editor, string id)
	{
		foreach (var root in editor.Roots) {
			if (root.Id == id) return (root, null);
			var stack = new Stack<GtkUiNode>(); stack.Push(root);
			while (stack.Count > 0) {
				var parent = stack.Pop();
				foreach (var child in parent.Children) {
					if (child.Id == id) return (child, parent.ClassName);
					stack.Push(child);
				}
			}
		}
		return (null, null);
	}
	static IEnumerable<string> SignalsFor(string type) => type switch {
		"GtkButton" => new[] { "clicked", "activate" },
		"GtkEntry" or "GtkPasswordEntry" => new[] { "activate", "changed" },
		"GtkCheckButton" => new[] { "toggled", "activate" },
		"GtkWindow" or "GtkApplicationWindow" => new[] { "close-request", "show", "hide" },
		_ => new[] { "show", "hide" }
	};
	void MeasureNativeBounds(DocumentSession session)
	{
		session.NativeBounds.Clear(); if (!string.IsNullOrEmpty(session.Editor.Error) || session.Editor.Roots.Count == 0) return;
		try {
			var xml = PreviewXml(session);
			using var builder = BuildFrom(xml); var rootId = session.Editor.Roots[0].Id; if (builder.GetObject(rootId) is not Gtk.Widget root) return;
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
	DocumentSession GetOrCreate(string documentId) => documents.GetOrAdd(sessionId, documentId, () => new DocumentSession(documentId));
	DocumentSession Get(string documentId) => documents.Get(sessionId, documentId);
	static void EnsureVersion(DocumentSession session, long candidate) { if (candidate != session.Version) throw new InvalidOperationException($"Stale version {candidate}; current is {session.Version}."); }
	[JsonRpcMethod("ping")] public object Ping() => new();
	[JsonRpcMethod("shutdown")] public object Shutdown()
	{
		documents.CloseAll(session => OnGtkThread(() => { session.DisposeNative(); return true; }));
		shutdown.Set(); gtkWorkAvailable.Set(); return new();
	}
	public void WaitForShutdown()
	{
		// Own the default main context for the host's whole life. Gir.Core releases a GObject
		// wrapper (from the GC finalizer thread too) through MainContext.Invoke, which runs the
		// callback INLINE on the calling thread whenever that thread can acquire the context.
		// This loop only held it during Iteration, so the finalizer thread usually could, and
		// called into GTK concurrently with this thread - a native 0xC0000005 in
		// ToggleRegistration.RemoveToggleRef. Owned here, those releases are queued to the
		// context and run on this thread in the Iteration below.
		var acquired = gtkContext.Acquire();
		try {
			while (!shutdown.IsSet) {
				while (gtkWork.TryDequeue(out var action)) action();
				while (gtkContext.Pending()) gtkContext.Iteration(false);
				gtkWorkAvailable.WaitOne(10);
			}
		} finally {
			if (acquired) gtkContext.Release();
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
		public List<string> PreviewDiagnostics = new();
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
			NativeBuilder = BuildFrom(xml);
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
