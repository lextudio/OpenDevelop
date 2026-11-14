using ICSharpCode.SharpDevelop.Designer.Remote;
using System.Xml;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost;

/// <summary>
/// One designer document served by one Surface process, expressed in the shared DDP shapes
/// (<c>session/open</c>, <c>session/update</c>, <c>design/export-png</c>, <c>session/close</c>).
/// A Surface renders and nothing else: it has no document model, no element ids and no write path, so
/// the parent's XAML source model stays the authority and this backend is a better renderer only
/// (doc/technotes/winui-designer-landscape.md, 7.1). Consequences, all visible in the returned state:
/// <list type="bullet">
/// <item><description><see cref="DesignerSessionState.Tree"/> is null (the surface reports no element tree yet).</description></item>
/// <item><description>Edit RPCs are refused as ordinary results (<see cref="Refuse"/>), never thrown, with the version unchanged.</description></item>
/// <item><description>A render error is a rejected state carrying a positioned diagnostic in the sent text's own coordinates.</description></item>
/// </list>
/// </summary>
public sealed class SurfaceDesignDocument : IAsyncDisposable
{
	readonly SurfaceConnection connection;
	readonly SurfaceProcessHost? owner;
	readonly Action? afterDispose;
	double viewportWidth = 1280, viewportHeight = 720, viewportScale = 1.0;
	string lastXaml = "";
	long frameSequence;
	long version = -1;
	byte[]? lastPng;
	DesignerSessionState? lastState;

	public SurfaceDesignDocument(SurfaceConnection connection) => this.connection = connection;

	/// <summary>
	/// <c>OD_WINUI_COMPILED=1</c> opts into compiled-page rendering: the surface instantiates the document's own page class
	/// (named by its <c>x:Class</c>) from the project assembly instead of parsing the markup, which is what makes generated
	/// <c>{x:Bind}</c> and code-behind run. It shows the project's LAST BUILD. Off by default: the text-authoritative
	/// renderer is the normal mode.
	/// </summary>
	static readonly bool CompiledPagesEnabled = Environment.GetEnvironmentVariable("OD_WINUI_COMPILED") == "1";

	static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

	/// <summary>The document's <c>x:Class</c> full name, when compiled-page rendering is on and the document names one.</summary>
	static string? CompiledPageTypeFor(string xaml)
	{
		if (!CompiledPagesEnabled || string.IsNullOrEmpty(xaml))
			return null;
		try {
			return (string?)XDocument.Parse(xaml).Root?.Attribute(XamlNamespace + "Class");
		} catch (XmlException) {
			return null;
		}
	}

	/// <summary>Points the connection at this document's compiled page (or back to markup).</summary>
	void ApplyCompiledPage(string xaml) => connection.CompiledPageType = CompiledPageTypeFor(xaml);

	/// <summary>
	/// Serves the document from <paramref name="owner"/>'s process, which is stopped when the document is disposed.
	/// <paramref name="afterDispose"/> then runs (for example to delete the document's private run directory).
	/// </summary>
	public SurfaceDesignDocument(SurfaceProcessHost owner, Action? afterDispose = null)
	{
		this.owner = owner;
		this.afterDispose = afterDispose;
		connection = owner.Connection;
	}

	/// <summary>The surface's stderr tail, when the document owns the process.</summary>
	public string ChildLog => owner?.Log ?? "";

	/// <summary>The Windows App SDK version the surface was built against (informational; "unknown" when absent).</summary>
	public string Wasdk => connection.Ready.Wasdk;

	/// <summary>Opens the document: the first render, which also fixes the surface's viewport.</summary>
	public async Task<DesignerSessionState> OpenAsync(DesignerDocumentRequest request, CancellationToken cancellationToken = default)
	{
		var snapshot = request.Snapshot;
		var viewport = request.Viewport ?? new DesignerViewport { Width = 1280, Height = 720 };
		viewportWidth = viewport.Width; viewportHeight = viewport.Height; viewportScale = viewport.Dpi;
		connection.DescribeFrames = BuildTree;
		ApplyCompiledPage(PrimaryText(snapshot));
		var result = await connection.LoadXamlAsync(
			PrimaryText(snapshot), viewport.Width, viewport.Height, viewport.Dpi, false, null, cancellationToken).ConfigureAwait(false);
		return await WithTreeAsync(Complete(snapshot, result), snapshot, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Re-renders a changed document at the viewport the surface already holds.</summary>
	public async Task<DesignerSessionState> UpdateAsync(DesignerDocumentRequest request, CancellationToken cancellationToken = default)
	{
		var snapshot = request.Snapshot;
		if (connection.IsNativeLive)
			return Rejected(snapshot.SessionId, snapshot.DocumentId, Math.Max(version, 0),
				"The document is in native-live mode; it is re-rendered when that mode is left.");
		if (version >= 0 && snapshot.Version < version)
			return Rejected(snapshot.SessionId, snapshot.DocumentId, version,
				$"Stale document version {snapshot.Version}; the surface is at {version}.");
		ApplyCompiledPage(PrimaryText(snapshot));
		var result = await connection.UpdateXamlAsync(PrimaryText(snapshot), false, null, cancellationToken).ConfigureAwait(false);
		return await WithTreeAsync(Complete(snapshot, result), snapshot, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// How long to wait for the surface's fence (see <see cref="SurfaceConnection.PickAtAsync"/>) before treating it as stuck. A healthy
	/// surface answers in tens of milliseconds, so this only bounds a hang; it is not a miss timer.
	/// </summary>
	public TimeSpan PickTimeout { get; set; } = TimeSpan.FromSeconds(10);

	/// <summary>
	/// The element at a design point, for canvas clicks. Only a <em>named</em> element is reported as a hit: the shell resolves a
	/// click to the source document by x:Name, and the surface's own tree path is not the shell's path scheme, so an unnamed element
	/// (or empty space) is a miss and the shell falls back to its source-model selection. A miss never faults the connection.
	/// </summary>
	public async Task<DesignerHitTestResult> HitTestAsync(double x, double y, CancellationToken cancellationToken = default)
	{
		if (connection.IsNativeLive)
			return new DesignerHitTestResult { Hit = false };   // the native window takes the clicks itself
		if (lastFrameTree != null) {
			// Answered from the described frame: no round trip at all.
			var at = DeepestAt(lastFrameTree, x, y);
			return at?.Name is { Length: > 0 } hitName
				? new DesignerHitTestResult { Hit = true, ComponentName = hitName, ComponentType = at.Type, Chain = new List<string> { hitName } }
				: new DesignerHitTestResult { Hit = false };
		}
		var pick = await connection.PickAtAsync(lastXaml, viewportWidth, viewportHeight, viewportScale, x, y, PickTimeout, cancellationToken).ConfigureAwait(false);
		if (pick?.Name is not { Length: > 0 } name)
			return new DesignerHitTestResult { Hit = false };
		return new DesignerHitTestResult {
			Hit = true,
			ComponentName = name,
			ComponentType = pick.ElementType,
			Chain = new List<string> { name },
		};
	}

	/// <summary>
	/// Whether <c>session/open</c> and <c>session/update</c> also return an element <see cref="DesignerSessionState.Tree"/> with bounds.
	/// The WinUI canvas resolves clicks, selection overlays and drags from that tree, and the surface has no message that lists one, so it
	/// is assembled with a native-mode excursion per accepted render (about half a second; set OD_SURFACE_TREE=0 in the child to turn it off).
	/// </summary>
	public bool BuildTree { get; set; } = true;

	/// <summary>The most elements asked about in one excursion; a larger page gets a tree of its first elements only.</summary>
	public int MaxTreeElements { get; set; } = 400;

	async Task<DesignerSessionState> WithTreeAsync(DesignerSessionState state, DesignerDocumentSnapshot snapshot, CancellationToken cancellationToken)
	{
		if (BuildTree && state.Accepted)
			state.Tree = lastFrameTree != null
				? TreeFromFrame(lastFrameTree)   // the surface described the frame it just rendered: no native-mode excursion
				: await BuildTreeAsync(PrimaryText(snapshot), cancellationToken).ConfigureAwait(false);
		return state;
	}

	/// <summary>The element tree the surface reported with the last accepted frame (a surface with <c>describe</c>), else null.</summary>
	IReadOnlyList<SurfaceTreeNode>? lastFrameTree;

	/// <summary>
	/// The shell's element tree from a described frame. Surface paths ("0", "0.1", "0.1.0") become the shell's child-index ids ("", "1",
	/// "1,0"); each node's parent is its path minus the last segment.
	/// </summary>
	static DesignerElementNode? TreeFromFrame(IReadOnlyList<SurfaceTreeNode> frameTree)
	{
		var byPath = new Dictionary<string, DesignerElementNode>(StringComparer.Ordinal);
		DesignerElementNode? root = null;
		foreach (var item in frameTree) {
			if (!item.Path.StartsWith("0", StringComparison.Ordinal))
				continue;
			string treePath = item.Path.Length <= 2 ? "" : item.Path.Substring(2).Replace('.', ',');
			var node = new DesignerElementNode {
				Id = treePath, Path = treePath, Name = item.Name, Type = item.Type,
				X = item.X, Y = item.Y, Width = item.Width, Height = item.Height, IsVisible = true, IsDesignable = true,
			};
			ApplyLayout(node, item.Layout);
			byPath[item.Path] = node;
			if (item.Path == "0") {
				root = node;
				continue;
			}
			int cut = item.Path.LastIndexOf('.');
			if (cut > 0 && byPath.TryGetValue(item.Path.Substring(0, cut), out var parent)) {
				node.LayoutMode = DesignerLayoutMode.InferFromContainerType(parent.Type);
				parent.Children.Add(node);
			}
		}
		return root;
	}

	/// <summary>
	/// Puts the measured layout facts on the shell's node: a Grid's tracks (<c>GridTracks</c>), a child's margin or Canvas position
	/// (<c>LayoutInsets</c>, the semantics the drop and snap code reads) and its z-index. A Grid with no declared tracks reports none.
	/// </summary>
	static void ApplyLayout(DesignerElementNode node, SurfaceTreeLayout? layout)
	{
		if (layout == null)
			return;
		if (layout.Rows != null || layout.Columns != null)
			node.GridTracks = new DesignerGridGuides {
				Accepted = true,
				RowTracks = (layout.Rows ?? Array.Empty<SurfaceTrack>()).Select(t => new DesignerGridTrackInfo { Offset = t.Offset, Size = t.Size }).ToList(),
				ColumnTracks = (layout.Columns ?? Array.Empty<SurfaceTrack>()).Select(t => new DesignerGridTrackInfo { Offset = t.Offset, Size = t.Size }).ToList(),
			};
		if (layout.Parent == "Canvas")
			node.LayoutInsets = new DesignerLayoutInsets { Kind = "CanvasPosition", Left = layout.CanvasLeft ?? 0, Top = layout.CanvasTop ?? 0 };
		else if (layout.Parent == "Grid")
			node.LayoutInsets = new DesignerLayoutInsets {
				Kind = "Margin", Left = layout.MarginLeft, Top = layout.MarginTop, Right = layout.MarginRight, Bottom = layout.MarginBottom,
			};
		if (layout.Parent != null)
			node.ZIndex = layout.ZIndex;
	}

	/// <summary>
	/// The runtime's own property list for the element at <paramref name="elementPath"/> (the shell's child-index path, "" for the page root,
	/// "0,1" for the root's second authored child): names, categories, current values, read-only flags and enum choices. Empty when the
	/// frame was not described or the page has changed since.
	/// </summary>
	public async Task<IReadOnlyList<DesignerPropertyInfo>> DescribePropertiesAsync(string elementPath, CancellationToken cancellationToken = default)
	{
		if (connection.IsNativeLive)
			return Array.Empty<DesignerPropertyInfo>();
		string surfacePath = string.IsNullOrEmpty(elementPath) ? "0" : "0." + elementPath.Replace(',', '.');
		var rows = await connection.DescribeElementAsync(surfacePath, cancellationToken).ConfigureAwait(false);
		if (rows == null)
			return Array.Empty<DesignerPropertyInfo>();
		return rows.Select(r => new DesignerPropertyInfo {
			Name = r.Name, DisplayName = r.Name, Category = r.Category, TypeName = r.TypeName, Value = r.Value,
			IsReadOnly = r.ReadOnly, IsEnum = r.Options != null && r.TypeName != "Boolean",
			AllowedValues = r.Options?.ToList() ?? new List<string>(),
			Kind = r.Options != null ? (r.TypeName == "Boolean" ? "Boolean" : "Enum") : r.TypeName switch {
				"Boolean" => "Boolean", "Double" or "Single" or "Int32" => "Number", "Thickness" => "Thickness", "Brush" => "Brush", _ => "String" },
		}).ToList();
	}

	/// <summary>The deepest described element containing the design point (frame DIPs), or null.</summary>
	static SurfaceTreeNode? DeepestAt(IReadOnlyList<SurfaceTreeNode> frameTree, double x, double y)
	{
		SurfaceTreeNode? best = null;
		foreach (var node in frameTree)
			if (node.Width > 0 && node.Height > 0 && x >= node.X && y >= node.Y && x < node.X + node.Width && y < node.Y + node.Height
				&& (best == null || node.Path.Length > best.Path.Length))
				best = node;
		return best;
	}

	/// <summary>
	/// The element tree of <paramref name="xaml"/> with each element's name, type and bounds in design space, or null when nothing resolved.
	///
	/// The structure comes from the document: each element that is a child, not a property element (<c>Grid.RowDefinitions</c>), gets the
	/// surface's own path ("0" for the root, "0.0" for its first child, ...) and is selected in one burst. The shell's node ids are the
	/// child-index paths the other WinUI hosts use ("" for the root, "0", "0,1"), and it maps a pick back to the source by node type and
	/// same-type index in tree order, which this structure matches. An element the surface cannot resolve is left out and its children are
	/// attached to its nearest resolved ancestor.
	/// </summary>
	async Task<DesignerElementNode?> BuildTreeAsync(string xaml, CancellationToken cancellationToken)
	{
		XDocument document;
		try {
			document = XDocument.Parse(xaml);
		} catch (XmlException) {
			return null;
		}
		if (document.Root == null)
			return null;

		var candidates = new List<(string SurfacePath, string TreePath, int Parent)>();
		void Walk(XElement element, string surfacePath, string treePath, int parent)
		{
			if (candidates.Count >= MaxTreeElements)
				return;
			int self = candidates.Count;
			candidates.Add((surfacePath, treePath, parent));
			int child = 0;
			foreach (var childElement in element.Elements()) {
				if (childElement.Name.LocalName.Contains('.'))
					continue;   // a property element (Grid.RowDefinitions, Button.Content): not a child of the visual tree
				Walk(childElement, surfacePath + "." + child, treePath.Length == 0 ? child.ToString() : treePath + "," + child, self);
				child++;
			}
		}
		Walk(document.Root, "0", "", -1);

		var picks = await connection.SelectPathsAsync(xaml, viewportWidth, viewportHeight, viewportScale,
			candidates.Select(c => c.SurfacePath).ToList(), PickTimeout, cancellationToken).ConfigureAwait(false);

		var nodes = new DesignerElementNode?[candidates.Count];
		for (int i = 0; i < candidates.Count; i++) {
			if (!picks.TryGetValue(candidates[i].SurfacePath, out var pick))
				continue;
			nodes[i] = new DesignerElementNode {
				Id = candidates[i].TreePath,
				Path = candidates[i].TreePath,
				Name = string.IsNullOrEmpty(pick.Name) ? null : pick.Name,
				Type = pick.ElementType,
				X = pick.X, Y = pick.Y, Width = pick.Width, Height = pick.Height,
				IsVisible = true,
				IsDesignable = true,
			};
		}
		if (nodes[0] is not { } root)
			return null;
		for (int i = 1; i < candidates.Count; i++) {
			if (nodes[i] is not { } node)
				continue;
			int parent = candidates[i].Parent;
			while (parent > 0 && nodes[parent] == null)
				parent = candidates[parent].Parent;
			var owner = nodes[Math.Max(parent, 0)] ?? root;
			node.LayoutMode = DesignerLayoutMode.InferFromContainerType(owner.Type);
			owner.Children.Add(node);
		}
		return root;
	}
	/// <summary>True while the document is shown in the surface's own window (see <see cref="EnterNativeLiveAsync"/>).</summary>
	public bool IsNativeLive => connection.IsNativeLive;

	/// <summary>
	/// Switches this document to a live native window: the surface hosts the page in a real window (handle in the result) that animates
	/// and takes input itself. Frames and picks stop until <see cref="ExitNativeLiveAsync"/>. When the surface dies instead, the result
	/// carries <see cref="NativeLiveResult.Fallback"/>, the last accepted snapshot, and a message for the recovery banner.
	/// </summary>
	public async Task<NativeLiveResult> EnterNativeLiveAsync(CancellationToken cancellationToken = default)
	{
		if (lastState == null)
			return new NativeLiveResult(null, null, "Open the document before switching to native mode.");
		try {
			var window = await connection.EnterNativeLiveAsync(lastXaml, viewportWidth, viewportHeight, viewportScale, null, cancellationToken).ConfigureAwait(false);
			return new NativeLiveResult(window, null, null);
		} catch (SurfaceConnectionFaultedException) {
			return new NativeLiveResult(null, lastState, "The renderer was lost while switching to native mode; showing the last snapshot.");
		} catch (SurfaceProtocolException ex) {
			return new NativeLiveResult(null, lastState, ex.Message);
		}
	}

	/// <summary>
	/// Back to snapshots. The document is re-rendered so the canvas shows the current source (it may have been edited meanwhile); when
	/// that cannot be done because the native window crashed, the last snapshot is returned with a message.
	/// </summary>
	public async Task<NativeLiveResult> ExitNativeLiveAsync(DesignerDocumentRequest? current, CancellationToken cancellationToken = default)
	{
		try {
			await connection.ExitNativeLiveAsync(cancellationToken).ConfigureAwait(false);
		} catch (SurfaceConnectionFaultedException) {
			return new NativeLiveResult(null, lastState, "The native window was lost; showing the last snapshot.");
		}
		if (current == null)
			return new NativeLiveResult(null, lastState, null);
		var state = await UpdateAsync(current, cancellationToken).ConfigureAwait(false);
		return new NativeLiveResult(null, state, null);
	}

	/// <summary>An edit request this backend cannot honour; a normal result, with the document version unchanged.</summary>
	public DesignerSessionState Refuse(string sessionId, string documentId, string operation, string? reason = null)
		=> Rejected(sessionId, documentId, Math.Max(version, 0),
			operation + " is not supported by the Surface renderer" + (reason == null ? "; edits are applied to the source document." : ": " + reason));

	/// <summary>Writes the last rendered frame to <paramref name="path"/> and returns the path.</summary>
	public string ExportPng(string path)
	{
		if (lastPng == null)
			throw new InvalidOperationException("No frame has been rendered yet.");
		File.WriteAllBytes(path, lastPng);
		return path;
	}

	DesignerSessionState Complete(DesignerDocumentSnapshot snapshot, SurfaceRenderResult result)
	{
		if (result.Error is { } error) {
			// The document did not change as far as the surface is concerned; keep the previous version and frame.
			var state = Rejected(snapshot.SessionId, snapshot.DocumentId, Math.Max(version, 0), error.Message);
			state.Diagnostics.Add(new DesignerDiagnostic {
				Severity = "Error",
				Message = error.Message,
				FileName = snapshot.PrimaryFileName,
				Line = error.Line ?? 0,
				Column = error.Column ?? 0,
			});
			state.Render = lastState?.Render;
			return state;
		}

		var frame = result.Frame!;
		lastPng = frame.Png;
		lastFrameTree = frame.Tree;
		lastXaml = PrimaryText(snapshot);
		version = snapshot.Version;
		var ok = new DesignerSessionState {
			SessionId = snapshot.SessionId,
			DocumentId = snapshot.DocumentId,
			Version = version,
			Accepted = true,
			RootType = RootElementName(PrimaryText(snapshot)),
			Render = new DesignerRenderFrame {
				Sequence = ++frameSequence,
				// Same basis as the Uno/Microsoft hosts: pixels in Width/Height, scale in Dpi.
				Width = frame.Width,
				Height = frame.Height,
				Dpi = frame.DipWidth > 0 ? (double)frame.Width / frame.DipWidth : 1.0,
				PngBase64 = Convert.ToBase64String(frame.Png),
			},
		};
		lastState = ok;
		return ok;
	}

	static DesignerSessionState Rejected(string sessionId, string documentId, long version, string error)
		=> new() { SessionId = sessionId, DocumentId = documentId, Version = version, Accepted = false, Error = error };

	static string PrimaryText(DesignerDocumentSnapshot snapshot)
	{
		var file = snapshot.Files.FirstOrDefault(item => item.FileName == snapshot.PrimaryFileName)
			?? snapshot.Files.FirstOrDefault(item => item.Kind == "Source")
			?? snapshot.Files.FirstOrDefault();
		return file?.Text ?? "";
	}

	/// <summary>The local name of the root element, or "" when the text is not well-formed XML.</summary>
	static string RootElementName(string xaml)
	{
		try {
			return System.Xml.Linq.XDocument.Parse(xaml).Root?.Name.LocalName ?? "";
		} catch (System.Xml.XmlException) {
			return "";
		}
	}

	public async ValueTask DisposeAsync()
	{
		try {
			await (owner != null ? owner.DisposeAsync() : connection.DisposeAsync()).ConfigureAwait(false);
		} finally {
			afterDispose?.Invoke();
		}
	}
}
