using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ComponentModel.Design;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vb = Microsoft.CodeAnalysis.VisualBasic;
using VbSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using System.Reflection;
using System.Runtime.Loader;

namespace ICSharpCode.FormsDesigner.Host;

sealed class DesignerHostService : IDesignerChildService
{
	const int ProtocolVersion = DesignerProtocol.Version;
	readonly string expectedToken;
	readonly ManualResetEventSlim shutdown = new(false);
	string? sessionId;
	DesignerDocumentSnapshot? current;
	DesignSurface? designSurface;
	SnapshotDesignerLoader? snapshotLoader;
	ProjectAssemblyLoadContext? projectLoadContext;
	Assembly? projectAssembly;
	readonly List<Assembly> referencedAssemblies = new();
	Size? rootDesignSize;
	SizeF? rootAutoScaleDimensions;
#if !MICROSOFT_WINFORMS
	/// <summary>Design-time SHADOW for Visible, keyed by component name - the value the SOURCE says
	/// (and the Properties pad shows) while the live control stays on screen.
	///
	/// Real WinForms' ControlDesigner shadows Visible itself: setting it false records the value for
	/// runtime but leaves the control selectable on the design surface, which is what VS does.
	/// LibreWinForms' portable fork has no such shadowing, so without this the control genuinely
	/// disappeared from the surface the moment the property was set - reachable only from the
	/// Document Outline pad. Hence Libre-only: on the Microsoft backend the framework already does
	/// it, and duplicating it there would fight the real designer.</summary>
	readonly Dictionary<string, bool> shadowedVisible = new(StringComparer.Ordinal);
#endif
	long frameSequence;
	bool initialized;
	static volatile bool portableGpuReadbackUnavailable;
	static volatile bool portableGpuReadbackWarm;
	static readonly bool traceSessionOpen = String.Equals(Environment.GetEnvironmentVariable("OPENDEVELOP_DESIGNER_TRACE"), "1", StringComparison.Ordinal);

	public DesignerHostService(string expectedToken) => this.expectedToken = expectedToken;

	bool IsVisualBasic => current?.Language.Equals("VisualBasic", StringComparison.OrdinalIgnoreCase) == true
		|| current?.DesignerFileName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) == true
		|| current?.PrimaryFileName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) == true;

	bool IsValidIdentifier(string name) => IsVisualBasic
		? Vb.SyntaxFacts.IsValidIdentifier(name)
		: SyntaxFacts.IsValidIdentifier(name);

	[JsonRpcMethod("initialize")]
	public HostHandshake Initialize(string token, int protocolVersion, string sessionId)
	{
		DesignerHostHandshakeValidator.Validate(expectedToken, token, protocolVersion);
		initialized = true;
		this.sessionId = sessionId;
		return new HostHandshake { ProtocolVersion = ProtocolVersion, Runtime = RuntimeInformation.FrameworkDescription, ProcessId = Environment.ProcessId, SessionId = sessionId };
	}

	[JsonRpcMethod("session/open")]
	public DesignerSessionState Open(DesignerDocumentRequest request)
	{
		var snapshot = request.Snapshot;
		Trace("session/open received");
		EnsureInitialized();
		EnsureOwnSession(snapshot);
		Trace("session/open validated envelope");
		Validate(snapshot);
		Trace("session/open validated snapshot");
		CreateDesignSurface(snapshot);
		Trace("session/open created design surface");
		current = snapshot;
		return CurrentState(snapshot.Version);
	}

	static void Trace(string message)
	{
		if (traceSessionOpen)
			Console.Error.WriteLine($"FormsDesigner.Host: {message}");
	}

	[JsonRpcMethod("session/update")]
	public DesignerSessionState Update(DesignerDocumentRequest request)
	{
		var snapshot = request.Snapshot;
		EnsureInitialized();
		EnsureOwnSession(snapshot);
		Validate(snapshot);
		if (current is null)
			return Rejected(snapshot.Version, "No designer session is open.");
		if (snapshot.Version <= current.Version)
			return Rejected(snapshot.Version, $"Stale document baseVersion {snapshot.Version}; current baseVersion is {current.Version}.");
		CreateDesignSurface(snapshot);
		current = snapshot;
		return CurrentState(snapshot.Version);
	}

	[JsonRpcMethod("session/flush")]
	public DesignerEditSet Flush(string sessionId, string documentId, long baseVersion)
	{
		EnsureInitialized();
		EnsureOwnSession(sessionId, documentId);
		if (current is null || current.Version != baseVersion)
			throw new InvalidOperationException("Cannot flush a stale or unopened document baseVersion.");
		foreach (var file in current.Files.Where(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))) {
			if (IsVisualBasic) {
				var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
				file.Text = new MeQualifierRewriter().Visit(vbRoot)!.ToFullString();
			} else {
				var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
				file.Text = new ThisQualifierRewriter().Visit(root)!.ToFullString();
			}
		}
		return new DesignerEditSet { SessionId = sessionId, DocumentId = documentId, BaseVersion = baseVersion, Files = current.Files };
	}

	/// <summary>Pushes the parent's selection into the child's REAL ISelectionService and returns a
	/// freshly rendered state.
	///
	/// This is what activates the genuine design-time chrome instead of us redrawing it: the real
	/// designers are all selection-driven. ToolStripDesigner keeps its template node
	/// (`_editorNode`, a DesignerToolStripControlHost it already appended to ToolStrip.Items) at
	/// Visible=false until its strip is selected; ToolStripMenuItemDesigner, on selection, calls
	/// CreatetypeHereNode(), sets `MenuItem.DropDown.TopLevel = false` and `AutoClose = false`,
	/// then ShowDropDown() - so the expanded dropdown becomes a CHILD CONTROL of the form (not a
	/// floating window) and therefore lands in Form.DrawToBitmap's output, together with that
	/// level's own "Type Here" node. Until this RPC existed the child's selection service was
	/// never told anything, so none of that chrome ever appeared.</summary>
	[JsonRpcMethod("design/set-selection")]
	public DesignerSessionState SetSelection(string sessionId, string documentId, long baseVersion, string[] elementIds)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "select in");
		var host = GetHost();
		if (designSurface?.GetService(typeof(ISelectionService)) is ISelectionService selection) {
			var components = (elementIds ?? [])
				.Select(id => host.Container.Components[id])
				.Where(component => component != null)
				.ToArray();
			selection.SetSelectedComponents(components, SelectionTypes.Replace);
		}
		// The chrome the selection just triggered is created asynchronously by the designers:
		// ToolStripMenuItemDesigner's ShowDropDown()/PerformLayout() and the template node's
		// visibility change only settle once the pending messages are pumped. Rendering in the
		// same call without this captured the frame BEFORE the expanded dropdown and the "Type
		// Here" node had laid out, so their geometry was already reported correctly while their
		// pixels were missing from the bitmap.
		Application.DoEvents();
		(GetHost().RootComponent as Control)?.PerformLayout();
		Application.DoEvents();
		return CurrentState(baseVersion);
	}

	/// <summary>Switches a TabControl's real SelectedIndex - the mechanism a click on a tab
	/// HEADER drives client-side (RemoteFormsDesignerControl.TrySwitchTabAsync), since a header is
	/// not a component the generic hit-test can ever resolve to. Deliberately NOT routed through
	/// design/set-property: real VS's own tab-header click does not persist which tab was active
	/// at design time (no "tabControl1.SelectedIndex = 1;" line ever appears in Designer.cs from
	/// clicking around), and does not create an undo step either - it is pure view state, the same
	/// reasoning design/set-selection already follows for the selection itself. No transaction, no
	/// RewriteProperty; just moves the live property so the next render shows the right page.</summary>
	[JsonRpcMethod("design/select-tab")]
	public DesignerSessionState SelectTab(string sessionId, string documentId, long baseVersion, string elementId, int tabIndex)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "select a tab in");
		var host = GetHost();
		if (host.Container.Components[elementId] is TabControl tabs && tabIndex >= 0 && tabIndex < tabs.TabPages.Count) {
			tabs.SelectedIndex = tabIndex;
			// Same reasoning as SetSelection's own note: TabControl's real OnSelectedIndexChanged
			// (which flips the old/new TabPage's own Visible) is asynchronous relative to this
			// call - rendering in the same call without pumping captured the frame BEFORE the
			// swap had actually happened, so the reported SelectedIndex was already correct while
			// the painted pixels still showed the PREVIOUS page.
			Application.DoEvents();
			(GetHost().RootComponent as Control)?.PerformLayout();
			Application.DoEvents();
		}
		return CurrentState(baseVersion);
	}

	/// <summary>Hit-tests a point INSIDE one popup overlay's own local coordinate space (see
	/// DesignerSessionState.Popups) and, if it lands on an item, selects that item through the
	/// real ISelectionService - the same "let the real designer react" approach
	/// <see cref="SetSelection"/> uses. A popup's dropdown is not reachable from the root form's
	/// own Controls tree (it is parented into the designer's adorner window), so the ordinary
	/// <see cref="HitTest"/>/root-relative path cannot be reused here; the owner element id tells
	/// us which live ToolStripDropDown to test against instead of walking down from the root.</summary>
	[JsonRpcMethod("design/hit-test-popup")]
	public DesignerSessionState HitTestPopupAndSelect(string sessionId, string documentId, long baseVersion, string ownerElementId, int x, int y)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "select in");
		var host = GetHost();
		var rootControl = host.RootComponent as Control;
		var dropDown = rootControl == null ? null
			: ExpandedDropDowns(rootControl).FirstOrDefault(entry => entry.OwnerElementId == ownerElementId).DropDown
			// Not an owning ToolStripDropDownItem's submenu - ownerElementId may instead name a
			// tray-only ContextMenuStrip's own overlay directly (see SelectedContextMenuStripPopups).
			?? host.Container.Components[ownerElementId] as ToolStripDropDown;
		IComponent? hit = null;
		if (dropDown != null && designSurface?.GetService(typeof(ISelectionService)) is ISelectionService selection) {
			// ToolStripDropDown IS-A ToolStrip, so the existing root hit-test walk already knows
			// how to test its Items - it just needed a dropdown instead of the form to start from.
			hit = FindDeepest(dropDown, new Point(x, y), host.Container);
			if (hit != null)
				selection.SetSelectedComponents(new IComponent[] { hit }, SelectionTypes.Replace);
		}
		var state = CurrentState(baseVersion);
		state.PopupHitElementId = hit?.Site?.Name;
		return state;
	}

	[JsonRpcMethod("design/hit-test")]
	public DesignerHitTestResult HitTest(string sessionId, string documentId, long baseVersion, int x, int y)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "hit-test");
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost;
		var root = host?.RootComponent as Control;
		// x/y arrive in surface (rendered bitmap) space; Control/ToolStripItem bounds are
		// client-space, so strip the root form's non-client offset first.
		var offset = RootClientOffset(root);
		// On MICROSOFT_WINFORMS, root.Height (Form.Height) is the native outer window height and
		// already includes the non-client offset above. The portable host's root.Height is
		// client-only (LibreWinForms has no real non-client frame), so the simulated title bar's
		// height must be added on top of it to get the true painted bounds - otherwise a press in
		// the bottom PortableFormTitleBarHeight pixels of a portable Form's visible client area
		// (which FindDeepest legitimately found no child under) would be wrongly rejected here.
#if MICROSOFT_WINFORMS
		var maxY = root?.Height ?? 0;
#else
		var maxY = (root?.Height ?? 0) + offset.Y;
#endif
		var hit = root == null ? null
			: FindDeepest(root, new Point(x - offset.X, y - offset.Y), host?.Container)
				// A press on the caption/border falls outside the client area entirely; treat it
				// as selecting the form itself rather than clearing the selection.
				?? (x >= 0 && y >= 0 && x < root.Width && y < maxY ? root : null);
		return new DesignerHitTestResult {
			ComponentName = hit?.Site?.Name ?? "",
			ComponentType = hit?.GetType().FullName ?? ""
		};
	}

	/// <summary>True when a ListBox/ComboBox/CheckedListBox's Items are a plain string list the
	/// collection editor can own: no DataSource, and every item a string.</summary>
	static bool TryGetEditableItems(IComponent component, out System.Collections.IList? items)
	{
		items = component switch {
			ListBox { DataSource: null } list => list.Items,
			ComboBox { DataSource: null } combo => combo.Items,
			DomainUpDown upDown => upDown.Items,
			_ => null
		};
		return items != null && items.Cast<object?>().All(item => item is string);
	}

	/// <summary>Replaces a list control's string Items with the edited list (each entry names its
	/// previous index or -1) and rewrites the designer source to one Items.AddRange call (one
	/// Items.Add per item for a DomainUpDown, whose collection has no AddRange).</summary>
	[JsonRpcMethod("design/set-items")]
	public DesignerSessionState SetItems(string sessionId, string documentId, long baseVersion, string elementId, DesignerCollectionItemEdit[] items, string? propertyName = null)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost
			?? throw new InvalidOperationException("The designer surface is unavailable.");
		var component = host.Container.Components.Cast<IComponent>()
			.FirstOrDefault(item => String.Equals(item.Site?.Name, elementId, StringComparison.Ordinal))
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		if (component is TreeView treeView)
			return SetTreeNodes(host, treeView, elementId, baseVersion, items ?? Array.Empty<DesignerCollectionItemEdit>());
		if (component is DataGridView gridView)
			return SetDataGridViewColumns(gridView, elementId, baseVersion, items ?? Array.Empty<DesignerCollectionItemEdit>());
		if (component is TabControl tabControl)
			return SetTabPages(tabControl, elementId, baseVersion, items ?? Array.Empty<DesignerCollectionItemEdit>());
		if (component is ListView listView) {
			// Columns and Items are both list-editor rows of a ListView; a caller that names no
			// property (an older client) means Columns, the only one it could edit.
			return String.Equals(propertyName, "Items", StringComparison.Ordinal)
				? SetListViewItems(elementId, baseVersion, items ?? Array.Empty<DesignerCollectionItemEdit>())
				: SetListViewColumns(host, listView, elementId, baseVersion, items ?? Array.Empty<DesignerCollectionItemEdit>());
		}
		if (!TryGetEditableItems(component, out var collection) || collection == null)
			throw new InvalidOperationException($"{elementId}.Items is not an editable string list.");
		items ??= Array.Empty<DesignerCollectionItemEdit>();
		var sources = items.Where(entry => entry.SourceIndex >= 0).Select(entry => entry.SourceIndex).ToList();
		if (sources.Any(index => index >= collection.Count) || sources.Distinct().Count() != sources.Count)
			throw new ArgumentException("Invalid item source index.", nameof(items));
		var texts = items.Select(entry => entry.Text ?? "").ToArray();
		using (var transaction = host.CreateTransaction($"Edit {elementId}.Items")) {
			collection.Clear();
			foreach (var text in texts)
				collection.Add(text);
			transaction.Commit();
		}
		// DomainUpDownItemCollection has no AddRange; the designer writes one Add per item.
		RewriteItems(elementId, texts, addEach: component is DomainUpDown);
		return CurrentState(baseVersion);
	}

	/// <summary>Builds the .resx data entry (mimetype and value text) an ImageList holding the given images would store under its
	/// <c>ImageStream</c> key: a real ImageList is filled (each image scaled to the size, as WinForms does) and serialized with
	/// the same <c>ResXResourceWriter</c> the designer uses, so the entry is exactly what WinForms reads back. Nothing is written
	/// to disk: the IDE owns the file and applies the entry in its own document transaction.</summary>
	[JsonRpcMethod("design/build-image-list-resource")]
	public DesignerImageListResource BuildImageListResource(string sessionId, string documentId, DesignerImageListEntry[] images, int width, int height, string colorDepth)
	{
		try {
			images ??= [];
			if (images.Length == 0)
				return new DesignerImageListResource { Accepted = false, Error = "An image list stream needs at least one image." };
			if (width < 1 || width > 256 || height < 1 || height > 256)
				return new DesignerImageListResource { Accepted = false, Error = "An image list's image size must be between 1 and 256." };
			if (!Enum.TryParse<ColorDepth>(colorDepth, out var depth) || !Enum.IsDefined(depth))
				return new DesignerImageListResource { Accepted = false, Error = "Unknown color depth: " + colorDepth };
			using var imageList = new ImageList { ImageSize = new Size(width, height), ColorDepth = depth };
			// The sources must outlive the serialization: the portable ImageList draws from them lazily
			// (real WinForms copies on Add), so they are disposed after the entry is built, not per image.
			var sources = new List<(MemoryStream Stream, Bitmap Bitmap)>();
			try {
				foreach (var entry in images) {
					var stream = new MemoryStream(entry.Png ?? []);
					Bitmap bitmap;
					try { bitmap = new Bitmap(stream); } catch { stream.Dispose(); throw; }
					sources.Add((stream, bitmap));
					imageList.Images.Add(bitmap);
				}
				return SerializeImageList(imageList);
			} finally {
				foreach (var (stream, bitmap) in sources) { bitmap.Dispose(); stream.Dispose(); }
			}
		} catch (Exception exception) {
			return new DesignerImageListResource { Accepted = false, Error = exception.GetBaseException().Message };
		}
	}

	static DesignerImageListResource SerializeImageList(ImageList imageList)
	{
		var streamer = imageList.ImageStream ?? throw new InvalidOperationException("The image list produced no stream.");
		using var buffer = new MemoryStream();
		using (var writer = new System.Resources.ResXResourceWriter(buffer)) {
			writer.AddResource("imageList.ImageStream", streamer);
			writer.Generate();
		}
		// The writer closes its stream; ToArray still works on a closed MemoryStream.
		using var written = new MemoryStream(buffer.ToArray());
		var data = System.Xml.Linq.XDocument.Load(written).Root!.Elements("data")
			.First(element => (string?)element.Attribute("name") == "imageList.ImageStream");
		return new DesignerImageListResource {
			Accepted = true, Count = imageList.Images.Count,
			MimeType = (string?)data.Attribute("mimetype") ?? "", Value = data.Element("value")?.Value ?? ""
		};
	}

	/// <summary>Assigns a writable <c>string[]</c> property (TextBox.Lines) from the list editor, as one designer transaction and one
	/// <c>new string[] { ... }</c> assignment in InitializeComponent. An empty list is an empty
	/// array, which is what the WinForms editor writes.</summary>
	[JsonRpcMethod("design/set-string-array")]
	public DesignerSessionState SetStringArray(string sessionId, string documentId, long baseVersion, string elementId, string propertyName, string[] values)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost
			?? throw new InvalidOperationException("The designer surface is unavailable.");
		var component = host.Container.Components.Cast<IComponent>()
			.FirstOrDefault(item => String.Equals(item.Site?.Name, elementId, StringComparison.Ordinal))
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var property = TypeDescriptor.GetProperties(component)[propertyName ?? ""];
		if (property == null || property.PropertyType != typeof(string[]) || property.IsReadOnly)
			throw new InvalidOperationException($"{elementId}.{propertyName} is not a writable string array.");
		var array = (values ?? Array.Empty<string>()).Select(value => value ?? "").ToArray();
		using (var transaction = host.CreateTransaction($"Set {elementId}.{propertyName}")) {
			property.SetValue(component, array);
			transaction.Commit();
		}
		RewriteProperty(elementId, propertyName!, array, component == host.RootComponent);
		return CurrentState(baseVersion);
	}

	const string TreeIndent = "  ";

	/// <summary>A TreeView's nodes as indented lines (two spaces per level), when every node is a
	/// non-blank text node whose settings can be carried through an edit (see TreeNodeSettings; a blank line is
	/// dropped on edit, so a blank node would be lost) - the only shape the list
	/// editor and the <c>new TreeNode("text", children)</c> source form can round-trip.</summary>
	static bool TryGetPlainTreeLines(TreeNodeCollection nodes, out List<string> lines)
	{
		var result = new List<string>();
		bool Walk(TreeNodeCollection level, int depth)
		{
			foreach (TreeNode node in level) {
				// Name, images (including the state image), checked state, colours, font, tooltip, a string Tag and a
				// named component ContextMenuStrip are carried through an edit (TreeNodeSettings). An unsited menu
				// has no durable source reference, so it must keep the collection editor unavailable rather than be lost.
				if (node.Tag is not (null or string)
					|| node.ContextMenuStrip is IComponent menu && menu.Site?.Name is not { Length: > 0 }
					|| node.Text.Trim().Length == 0 || node.Text.StartsWith(" ", StringComparison.Ordinal))
					return false;
				result.Add(String.Concat(Enumerable.Repeat(TreeIndent, depth)) + node.Text);
				if (!Walk(node.Nodes, depth + 1))
					return false;
			}
			return true;
		}
		var plain = Walk(nodes, 0);
		lines = result;
		return plain;
	}

	/// <summary>Builds nodes from indented lines: a line's depth is its leading two-space count,
	/// clamped to one deeper than the previous line so a stray indent cannot skip a level.</summary>
	static List<TreeNode> BuildTreeNodes(IEnumerable<string> lines)
	{
		var roots = new List<TreeNode>();
		var path = new List<TreeNode>();
		foreach (var line in lines) {
			var depth = 0;
			while (line.Length >= (depth + 1) * TreeIndent.Length && line.AsSpan(depth * TreeIndent.Length, TreeIndent.Length).SequenceEqual(TreeIndent))
				depth++;
			depth = Math.Min(depth, path.Count);
			var node = new TreeNode(line.Substring(depth * TreeIndent.Length).TrimStart());
			if (depth == 0) roots.Add(node);
			else path[depth - 1].Nodes.Add(node);
			path.RemoveRange(depth, path.Count - depth);
			path.Add(node);
		}
		return roots;
	}

	DesignerSessionState SetTreeNodes(IDesignerHost host, TreeView treeView, string elementId, long baseVersion, DesignerCollectionItemEdit[] items)
	{
		if (!TryGetPlainTreeLines(treeView.Nodes, out _))
			throw new InvalidOperationException($"{elementId}.Nodes has nodes the list editor cannot represent.");
		// Kept entries keep their node's Name (Visual Studio names every node "NodeN"); the
		// SourceIndex is the node's position in the flattened, depth-first list.
		var flattened = new List<TreeNode>();
		void Flatten(TreeNodeCollection level) { foreach (TreeNode node in level) { flattened.Add(node); Flatten(node.Nodes); } }
		Flatten(treeView.Nodes);
		var sources = items.Where(entry => entry.SourceIndex >= 0).Select(entry => entry.SourceIndex).ToList();
		if (sources.Any(index => index >= flattened.Count) || sources.Distinct().Count() != sources.Count)
			throw new ArgumentException("Invalid node source index.", nameof(items));
		var kept = items.Where(entry => (entry.Text ?? "").Trim().Length > 0).ToList();
		var roots = BuildTreeNodes(kept.Select(entry => entry.Text ?? ""));
		var built = new List<TreeNode>();
		void FlattenBuilt(IEnumerable<TreeNode> level) { foreach (var node in level) { built.Add(node); FlattenBuilt(node.Nodes.Cast<TreeNode>()); } }
		FlattenBuilt(roots);
		for (var i = 0; i < built.Count; i++)
			if (kept[i].SourceIndex >= 0)
				CopyTreeNodeSettings(flattened[kept[i].SourceIndex], built[i]);
		if (!RemoveTreeNodeLocals(elementId, out var unsupported))
			throw new InvalidOperationException($"{elementId}.Nodes sets {unsupported} on a node, which the list editor cannot carry through; edit it in the source.");
		using (var transaction = host.CreateTransaction($"Edit {elementId}.Nodes")) {
			treeView.Nodes.Clear();
			treeView.Nodes.AddRange(roots.ToArray());
			transaction.Commit();
		}
		RewriteCollectionStatements(elementId, "Nodes", roots.Count == 0 ? null
			: IsVisualBasic ? $"Me.{elementId}.Nodes.AddRange(New System.Windows.Forms.TreeNode() {{{string.Join(", ", roots.Select(TreeNodeSourceVisualBasic))}}})"
			: $"this.{elementId}.Nodes.AddRange(new System.Windows.Forms.TreeNode[] {{ {string.Join(", ", roots.Select(TreeNodeSource))} }});");
		return CurrentState(baseVersion);
	}

	static readonly string[] CommonTypeSuggestions = [
		"System.Boolean", "System.Byte", "System.Char", "System.DateTime", "System.Decimal", "System.Double",
		"System.Guid", "System.Int16", "System.Int32", "System.Int64", "System.Object", "System.Single",
		"System.String", "System.TimeSpan", "System.Uri"];

	List<string>? typeSuggestions;

	/// <summary>Type names for a System.Type property's drop-down: the project assembly's public,
	/// non-generic, top-level types (what the type editor accepts) first, then common framework
	/// types. Cached per design surface; capped so a large project stays responsive.</summary>
	List<string> TypeSuggestions()
	{
		if (typeSuggestions != null)
			return typeSuggestions;
		var project = new List<string>();
		if (projectAssembly != null) {
			Type?[] exported;
			try { exported = projectAssembly.GetExportedTypes(); }
			catch (System.Reflection.ReflectionTypeLoadException e) { exported = e.Types; }
			catch { exported = []; }
			project.AddRange(exported.Where(type => type is { IsGenericType: false, IsNested: false, FullName: not null })
				.Select(type => type!.FullName!).OrderBy(name => name, StringComparer.Ordinal).Take(500));
		}
		return typeSuggestions = project.Concat(CommonTypeSuggestions.Where(name => !project.Contains(name))).ToList();
	}

	/// <summary>A TabControl's pages, when every one is a designer component (a named field in the
	/// source, as Visual Studio writes them) and the source adds them through
	/// <c>Controls.Add</c> - the shape whose add/remove/reorder this editor can express as component
	/// and statement edits. A <c>TabPages.Add</c> in the source is another spelling it would not
	/// replace, so it is left to the source.</summary>
	bool TryGetSitedPages(TabControl tabControl, out List<TabPage> pages)
	{
		pages = tabControl.TabPages.Cast<TabPage>().ToList();
		return pages.All(page => page.Site?.Name is { Length: > 0 })
			&& !SourceMentionsMember(tabControl.Site?.Name ?? "", "TabPages");
	}

	/// <summary>Whether InitializeComponent mentions <c>&lt;ownerId&gt;.&lt;member&gt;</c> anywhere.</summary>
	bool SourceMentionsMember(string ownerId, string member)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			return vbRoot.DescendantNodes().OfType<VbSyntax.MemberAccessExpressionSyntax>().Any(access =>
				access.Name.Identifier.ValueText == member && access.Expression != null && VbSimpleTargetName(access.Expression) == ownerId);
		}
		return CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(access =>
			access.Name.Identifier.ValueText == member && SimpleTargetName(access.Expression) == ownerId);
	}

	/// <summary>Applies the list editor to TabControl.TabPages (see <see cref="SetSitedComponentList"/>);
	/// a page that still holds controls cannot be dropped, its children would be left without a parent.</summary>
	DesignerSessionState SetTabPages(TabControl tabControl, string elementId, long baseVersion, DesignerCollectionItemEdit[] items)
	{
		if (!TryGetSitedPages(tabControl, out var existing))
			throw new InvalidOperationException($"{elementId}.TabPages has pages that are not designer components.");
		return SetSitedComponentList(elementId, baseVersion, items, existing.Select(page => page.Site!.Name!).ToList(),
			existing.Select(page => page.Text ?? "").ToList(),
			existing.Select(page => page.Controls.Count > 0 ? $"{page.Site!.Name} still holds controls; move or delete them before removing the page." : null).ToList(),
			typeof(TabPage), "tabPage", "Text", "Controls", "System.Windows.Forms.Control", "pages");
	}

	/// <summary>An ImageList's images with their keys and PNG bytes, for the IDE's image-list editor; false when an image cannot be read.</summary>
	static bool TryDescribeImageList(ImageList imageList, out List<DesignerImageListEntry> entries)
	{
		entries = [];
		try {
			var keys = imageList.Images.Keys.Cast<string>().ToList();
			for (var index = 0; index < imageList.Images.Count; index++) {
				using var stream = new MemoryStream();
				imageList.Images[index].Save(stream, System.Drawing.Imaging.ImageFormat.Png);
				entries.Add(new DesignerImageListEntry { Key = index < keys.Count ? keys[index] ?? "" : "", Png = stream.ToArray() });
			}
			return true;
		} catch (Exception) {
			entries = [];
			return false;
		}
	}

	/// <summary>A DataGridView's columns, when every one is a designer component, the grid is not data
	/// bound, and the source adds them through its own <c>Columns</c> collection.</summary>
	static bool TryGetSitedGridColumns(DataGridView grid, out List<DataGridViewColumn> columns)
	{
		columns = grid.Columns.Cast<DataGridViewColumn>().ToList();
		return grid.DataSource == null && columns.All(column => column.Site?.Name is { Length: > 0 });
	}

	/// <summary>Applies the list editor to DataGridView.Columns: each entry's text is the column's
	/// HeaderText (see <see cref="SetSitedComponentList"/>); a new column is a
	/// <c>DataGridViewTextBoxColumn</c> with its Name set to its field.</summary>
	DesignerSessionState SetDataGridViewColumns(DataGridView grid, string elementId, long baseVersion, DesignerCollectionItemEdit[] items)
	{
		if (!TryGetSitedGridColumns(grid, out var existing))
			throw new InvalidOperationException($"{elementId}.Columns has columns that are not designer components.");
		return SetSitedComponentList(elementId, baseVersion, items, existing.Select(column => column.Site!.Name!).ToList(),
			existing.Select(column => column.HeaderText ?? "").ToList(), existing.Select(_ => (string?)null).ToList(),
			typeof(DataGridViewTextBoxColumn), "dataGridViewTextBoxColumn", "HeaderText", "Columns", "System.Windows.Forms.DataGridViewColumn", "columns",
			nameProperty: "Name");
	}

	/// <summary>Applies a list editor to a collection of named designer components: kept entries
	/// (SourceIndex) keep their component and get its <paramref name="textProperty"/> updated; new
	/// entries add a <paramref name="namePrefix"/>N component of <paramref name="addedType"/> with its
	/// field; dropped ones lose their field and statements (unless <paramref name="removalRefusals"/>
	/// names a reason, which refuses the edit); the order is one <c>AddRange</c> on
	/// <paramref name="collectionMember"/>. Source-first like <see cref="SetListViewColumns"/>: the
	/// source is rewritten and the surface rebuilt from it, and a failure restores the original.</summary>
	DesignerSessionState SetSitedComponentList(string elementId, long baseVersion, DesignerCollectionItemEdit[] items,
		IReadOnlyList<string> existingNames, IReadOnlyList<string> existingTexts, IReadOnlyList<string?> removalRefusals,
		Type addedType, string namePrefix, string textProperty, string collectionMember, string collectionElementType, string noun,
		string? nameProperty = null)
	{
		var sources = items.Where(entry => entry.SourceIndex >= 0).Select(entry => entry.SourceIndex).ToList();
		if (items.Any(entry => entry.SourceIndex < -1) || sources.Any(index => index >= existingNames.Count) || sources.Distinct().Count() != sources.Count)
			throw new ArgumentException($"Invalid {noun} source index.", nameof(items));
		var removedNames = existingNames.Where((_, index) => !sources.Contains(index)).ToList();
		for (var index = 0; index < existingNames.Count; index++)
			if (!sources.Contains(index) && removalRefusals[index] is { } refusal)
				throw new InvalidOperationException(refusal);
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost
			?? throw new InvalidOperationException("The designer surface is unavailable.");
		var taken = new HashSet<string>(host.Container.Components.Cast<IComponent>().Select(component => component.Site?.Name ?? ""), StringComparer.Ordinal);
		var ordered = new List<string>();
		var added = new List<string>();
		var texts = new List<(string Name, string Text)>();
		foreach (var entry in items) {
			var text = entry.Text ?? "";
			if (entry.SourceIndex >= 0) {
				var name = existingNames[entry.SourceIndex];
				if (!String.Equals(existingTexts[entry.SourceIndex], text, StringComparison.Ordinal))
					texts.Add((name, text));
				ordered.Add(name);
			} else {
				var index = 1;
				while (taken.Contains(namePrefix + index))
					index++;
				var name = namePrefix + index;
				taken.Add(name);
				added.Add(name);
				texts.Add((name, text));
				ordered.Add(name);
			}
		}
		var designerFile = CurrentDesignerFile();
		var originalSource = designerFile.Text;
		try {
			foreach (var name in removedNames)
				RewriteDeletedComponent(name);
			foreach (var name in added) {
				RewriteAddedComponent(addedType, name);
				MoveCreationAfterOwner(name, elementId);
				if (nameProperty != null)
					RewriteProperty(name, nameProperty, name);
			}
			foreach (var (name, text) in texts)
				RewriteProperty(name, textProperty, text);
			RewriteCollectionStatements(elementId, collectionMember, ordered.Count == 0 ? null
				: IsVisualBasic ? $"Me.{elementId}.{collectionMember}.AddRange(New {collectionElementType}() {{{string.Join(", ", ordered.Select(name => "Me." + name))}}})"
				: $"this.{elementId}.{collectionMember}.AddRange(new {collectionElementType}[] {{ {string.Join(", ", ordered.Select(name => "this." + name))} }});");
			CreateDesignSurface(current!);
		} catch (Exception exception) {
			designerFile.Text = originalSource;
			try {
				CreateDesignSurface(current!);
			} catch (Exception recoveryException) {
				return Rejected(baseVersion, $"Editing the {noun} failed and the design surface could not be restored: " + recoveryException.Message);
			}
			return Rejected(baseVersion, $"Editing the {noun} failed: " + exception.Message);
		}
		return CurrentState(baseVersion);
	}

	/// <summary>The texts of a ListView's items as the designer SOURCE declares them, when every
	/// item is a plain text item: <c>new ListViewItem("text")</c>, inline or through one of Visual
	/// Studio's locals, added only by <c>Items.Add/AddRange</c> and never touched otherwise (no
	/// sub-items, image, check mark, group, tag or other assignment). Read from source rather than
	/// from the live ListView because the portable LibreWinForms ListView needs native list-view
	/// calls (USER32) just to enumerate its items, so only the source is the same authority on
	/// every backend; it is also what the edit rewrites.</summary>
	bool TryReadListViewItems(string ownerId, out List<string> texts)
	{
		texts = [];
		try {
			var file = CurrentDesignerFile();
			return IsVisualBasic ? TryReadListViewItemsVisualBasic(file.Text, ownerId, texts)
				: TryReadListViewItemsCSharp(file.Text, ownerId, texts);
		} catch (Exception) {
			texts.Clear();
			return false;
		}
	}

	static bool TryReadListViewItemsCSharp(string source, string ownerId, List<string> texts)
	{
		var method = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
			.FirstOrDefault(item => item.Identifier.ValueText == "InitializeComponent");
		if (method?.Body == null) return false;
		var locals = method.Body.Statements.OfType<LocalDeclarationStatementSyntax>().SelectMany(local => local.Declaration.Variables)
			.ToDictionary(variable => variable.Identifier.ValueText, variable => variable.Initializer?.Value as ObjectCreationExpressionSyntax, StringComparer.Ordinal);
		bool IsOwnerItems(ExpressionSyntax expression) => expression is MemberAccessExpressionSyntax access
			&& access.Name.Identifier.ValueText == "Items" && SimpleTargetName(access.Expression) == ownerId;
		bool Plain(ObjectCreationExpressionSyntax? creation, out string text)
		{
			text = "";
			if (creation == null || !creation.Type.ToString().EndsWith("ListViewItem", StringComparison.Ordinal)
				|| creation.Initializer != null || creation.ArgumentList == null)
				return false;
			var arguments = creation.ArgumentList.Arguments;
			// new ListViewItem("a"), new ListViewItem(new string[] { "a", "b" }) and Visual Studio's
			// new ListViewItem(new string[] { "a", "b" }, -1) (no image).
			if (arguments.Count is < 1 or > 2 || (arguments.Count == 2 && arguments[1].Expression.ToString().Trim() != "-1"))
				return false;
			var cells = new List<string>();
			if (arguments[0].Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) && arguments.Count == 1)
				cells.Add(literal.Token.ValueText);
			else {
				var elements = arguments[0].Expression switch {
					ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
					ImplicitArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
					_ => default(SeparatedSyntaxList<ExpressionSyntax>?)
				};
				if (elements is not { Count: > 0 }) return false;
				foreach (var cell in elements.Value) {
					if (cell is not LiteralExpressionSyntax cellLiteral || !cellLiteral.IsKind(SyntaxKind.StringLiteralExpression)) return false;
					cells.Add(cellLiteral.Token.ValueText);
				}
			}
			return TryJoinListViewCells(cells, out text);
		}
		foreach (var statement in method.Body.Statements) {
			if (!statement.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(IsOwnerItems))
				continue;
			if (statement is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax call } invocation }
				|| !IsOwnerItems(call.Expression) || invocation.ArgumentList.Arguments.Count != 1)
				return false;
			var argument = invocation.ArgumentList.Arguments[0].Expression;
			IEnumerable<ExpressionSyntax>? elements = call.Name.Identifier.ValueText switch {
				"Add" => [argument],
				"AddRange" => argument switch {
					ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
					ImplicitArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions,
					_ => null
				},
				_ => null
			};
			if (elements == null) return false;
			foreach (var element in elements) {
				var creation = element as ObjectCreationExpressionSyntax;
				if (element is IdentifierNameSyntax name) {
					if (!locals.TryGetValue(name.Identifier.ValueText, out creation)) return false;
					// A local may appear only in its declaration and in this owner's Add/AddRange: any other
					// statement (an assignment, SubItems.Add, another list) carries a setting that is not
					// representable here and would be left dangling when the local is removed.
					if (method.Body.Statements.Any(st => !(st is LocalDeclarationStatementSyntax declaration
								&& declaration.Declaration.Variables.Any(variable => variable.Identifier.ValueText == name.Identifier.ValueText))
							&& !st.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(IsOwnerItems)
							&& st.DescendantNodes().OfType<IdentifierNameSyntax>().Any(id => id.Identifier.ValueText == name.Identifier.ValueText)))
						return false;
				}
				if (!Plain(creation, out var text)) return false;
				texts.Add(text);
			}
		}
		return true;
	}

	static bool TryReadListViewItemsVisualBasic(string source, string ownerId, List<string> texts)
	{
		var method = ((VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(source).GetRoot()).DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
			.FirstOrDefault(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms && ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
		if (method == null) return false;
		var locals = new Dictionary<string, VbSyntax.ObjectCreationExpressionSyntax?>(StringComparer.Ordinal);
		foreach (var declarator in method.Statements.OfType<VbSyntax.LocalDeclarationStatementSyntax>().SelectMany(local => local.Declarators)) {
			var creation = (declarator.Initializer?.Value ?? (declarator.AsClause as VbSyntax.AsNewClauseSyntax)?.NewExpression) as VbSyntax.ObjectCreationExpressionSyntax;
			foreach (var name in declarator.Names)
				locals[name.Identifier.ValueText] = creation;
		}
		bool IsOwnerItems(VbSyntax.ExpressionSyntax? expression) => expression is VbSyntax.MemberAccessExpressionSyntax access
			&& access.Name.Identifier.ValueText == "Items" && access.Expression != null && VbSimpleTargetName(access.Expression) == ownerId;
		bool Plain(VbSyntax.ObjectCreationExpressionSyntax? creation, out string text)
		{
			text = "";
			if (creation == null || !creation.Type.ToString().EndsWith("ListViewItem", StringComparison.Ordinal)
				|| creation.Initializer != null || creation.ArgumentList == null)
				return false;
			var arguments = creation.ArgumentList.Arguments;
			if (arguments.Count is < 1 or > 2 || arguments.Any(argument => argument is not VbSyntax.SimpleArgumentSyntax)
				|| (arguments.Count == 2 && ((VbSyntax.SimpleArgumentSyntax)arguments[1]).Expression.ToString().Trim() != "-1"))
				return false;
			var first = ((VbSyntax.SimpleArgumentSyntax)arguments[0]).Expression;
			var cells = new List<string>();
			if (first is VbSyntax.LiteralExpressionSyntax literal && literal.IsKind(Vb.SyntaxKind.StringLiteralExpression) && arguments.Count == 1)
				cells.Add(literal.Token.ValueText);
			else {
				if (first is not VbSyntax.ArrayCreationExpressionSyntax { Initializer: { Initializers.Count: > 0 } initializer }) return false;
				foreach (var cell in initializer.Initializers) {
					if (cell is not VbSyntax.LiteralExpressionSyntax cellLiteral || !cellLiteral.IsKind(Vb.SyntaxKind.StringLiteralExpression)) return false;
					cells.Add(cellLiteral.Token.ValueText);
				}
			}
			return TryJoinListViewCells(cells, out text);
		}
		foreach (var statement in method.Statements) {
			if (!statement.DescendantNodesAndSelf().OfType<VbSyntax.MemberAccessExpressionSyntax>().Any(IsOwnerItems))
				continue;
			if (statement is not VbSyntax.ExpressionStatementSyntax { Expression: VbSyntax.InvocationExpressionSyntax { Expression: VbSyntax.MemberAccessExpressionSyntax call } invocation }
				|| !IsOwnerItems(call.Expression) || invocation.ArgumentList?.Arguments.Count != 1
				|| invocation.ArgumentList.Arguments[0] is not VbSyntax.SimpleArgumentSyntax { Expression: var argument })
				return false;
			IEnumerable<VbSyntax.ExpressionSyntax>? elements = call.Name.Identifier.ValueText switch {
				"Add" => [argument],
				"AddRange" => argument is VbSyntax.ArrayCreationExpressionSyntax { Initializer: { } initializer } ? initializer.Initializers : null,
				_ => null
			};
			if (elements == null) return false;
			foreach (var element in elements) {
				var creation = element as VbSyntax.ObjectCreationExpressionSyntax;
				if (element is VbSyntax.IdentifierNameSyntax name) {
					if (!locals.TryGetValue(name.Identifier.ValueText, out creation)) return false;
					if (method.Statements.Any(st => !(st is VbSyntax.LocalDeclarationStatementSyntax declaration
								&& declaration.Declarators.Any(declarator => declarator.Names.Any(declared => declared.Identifier.ValueText == name.Identifier.ValueText)))
							&& !st.DescendantNodesAndSelf().OfType<VbSyntax.MemberAccessExpressionSyntax>().Any(IsOwnerItems)
							&& st.DescendantNodes().OfType<VbSyntax.IdentifierNameSyntax>().Any(id => id.Identifier.ValueText == name.Identifier.ValueText)))
						return false;
				}
				if (!Plain(creation, out var text)) return false;
				texts.Add(text);
			}
		}
		return true;
	}

	const string ListViewCellSeparator = " | ";

	/// <summary>One list-editor line for an item's cell texts: "a | b". A '|' inside a cell, or
	/// padding around one of several cells, would not survive being typed back, so such an item is
	/// not plain (it stays read-only, as every non-plain item does).</summary>
	static bool TryJoinListViewCells(List<string> cells, out string line)
	{
		line = "";
		if (cells.Any(cell => cell.Contains('|', StringComparison.Ordinal) || cell.Contains('\n', StringComparison.Ordinal)
				|| cell.Contains('\r', StringComparison.Ordinal) || (cells.Count > 1 && cell != cell.Trim())))
			return false;
		line = string.Join(ListViewCellSeparator, cells);
		return true;
	}

	/// <summary>The cell texts of an edited line (the inverse of <see cref="TryJoinListViewCells"/>): a line
	/// without '|' is one untouched cell, otherwise each cell is trimmed.</summary>
	static string[] SplitListViewCells(string line)
		=> line.Contains('|', StringComparison.Ordinal) ? line.Split('|').Select(cell => cell.Trim()).ToArray() : [line];

	static string ListViewItemSource(string line, bool visualBasic)
	{
		var cells = SplitListViewCells(line);
		return visualBasic
			? cells.Length == 1 ? $"New System.Windows.Forms.ListViewItem({SerializeValueVisualBasic(cells[0])})"
				: $"New System.Windows.Forms.ListViewItem(New String() {{{string.Join(", ", cells.Select(SerializeValueVisualBasic))}}}, -1)"
			: cells.Length == 1 ? $"new System.Windows.Forms.ListViewItem({SerializeValue(cells[0])})"
				: $"new System.Windows.Forms.ListViewItem(new string[] {{ {string.Join(", ", cells.Select(SerializeValue))} }}, -1)";
	}

	static readonly HashSet<string> CarriedListViewItemSettings = new(StringComparer.Ordinal);

	/// <summary>Applies the list editor to ListView.Items: the items become one
	/// <c>Items.AddRange(new ListViewItem[] { new ListViewItem("text"), ... })</c>, and Visual
	/// Studio's <c>ListViewItem listViewItemN</c> locals and their statements are removed. Refused,
	/// source untouched, when a local sets anything but Text (that value would be lost).
	/// Source-first like <see cref="SetListViewColumns"/>: a live, handle-backed ListView's items
	/// go through native list-view messages LibreWinForms lacks on macOS, so the surface is
	/// rebuilt from the rewritten source and a failure restores the original.</summary>
	DesignerSessionState SetListViewItems(string elementId, long baseVersion, DesignerCollectionItemEdit[] items)
	{
		if (!TryReadListViewItems(elementId, out var existing))
			throw new InvalidOperationException($"{elementId}.Items has items the list editor cannot represent.");
		var sources = items.Where(entry => entry.SourceIndex >= 0).Select(entry => entry.SourceIndex).ToList();
		if (items.Any(entry => entry.SourceIndex < -1) || sources.Any(index => index >= existing.Count) || sources.Distinct().Count() != sources.Count)
			throw new ArgumentException("Invalid item source index.", nameof(items));
		var texts = items.Select(entry => entry.Text ?? "").ToList();
		var designerFile = CurrentDesignerFile();
		var originalSource = designerFile.Text;
		if (!RemoveItemLocals(elementId, "Items", "ListViewItem", CarriedListViewItemSettings, out var unsupported))
			throw new InvalidOperationException($"{elementId}.Items sets {unsupported} on an item, which the list editor cannot carry through; edit it in the source.");
		try {
			RewriteCollectionStatements(elementId, "Items", texts.Count == 0 ? null
				: IsVisualBasic ? $"Me.{elementId}.Items.AddRange(New System.Windows.Forms.ListViewItem() {{{string.Join(", ", texts.Select(text => ListViewItemSource(text, true)))}}})"
				: $"this.{elementId}.Items.AddRange(new System.Windows.Forms.ListViewItem[] {{ {string.Join(", ", texts.Select(text => ListViewItemSource(text, false)))} }});");
			CreateDesignSurface(current!);
		} catch (Exception exception) {
			designerFile.Text = originalSource;
			try {
				CreateDesignSurface(current!);
			} catch (Exception recoveryException) {
				return Rejected(baseVersion, "Editing the items failed and the design surface could not be restored: " + recoveryException.Message);
			}
			return Rejected(baseVersion, "Editing the items failed: " + exception.Message);
		}
		return CurrentState(baseVersion);
	}

	/// <summary>A ListView's columns, when every one is a designer component (a named field in
	/// the source, as Visual Studio writes them) - the shape whose add/remove/reorder this editor can
	/// express as component and statement edits.</summary>
	static bool TryGetSitedColumns(ListView listView, out List<ColumnHeader> columns)
	{
		columns = listView.Columns.Cast<ColumnHeader>().ToList();
		return columns.All(column => column.Site?.Name is { Length: > 0 });
	}

	/// <summary>Applies the list editor to ListView.Columns: kept entries (SourceIndex) keep their
	/// ColumnHeader component and get its Text updated; new entries add a "columnHeaderN" component
	/// with its field; dropped columns lose their field and statements; the order is one
	/// <c>Columns.AddRange</c>. Source-first: the designer source is rewritten and the surface
	/// rebuilt from it, because changing a live, handle-backed ListView's columns calls native
	/// list-view messages that LibreWinForms does not provide on macOS (and a rebuild is the same
	/// authority the delete path recovers from). A failure restores the original source.</summary>
	DesignerSessionState SetListViewColumns(IDesignerHost host, ListView listView, string elementId, long baseVersion, DesignerCollectionItemEdit[] items)
	{
		if (!TryGetSitedColumns(listView, out var existing))
			throw new InvalidOperationException($"{elementId}.Columns has columns that are not designer components.");
		var sources = items.Where(entry => entry.SourceIndex >= 0).Select(entry => entry.SourceIndex).ToList();
		if (sources.Any(index => index >= existing.Count) || sources.Distinct().Count() != sources.Count)
			throw new ArgumentException("Invalid column source index.", nameof(items));
		var existingNames = existing.Select(column => column.Site!.Name!).ToList();
		var removedNames = existingNames.Where((_, index) => !sources.Contains(index)).ToList();
		var taken = new HashSet<string>(host.Container.Components.Cast<IComponent>().Select(component => component.Site?.Name ?? ""), StringComparer.Ordinal);
		var ordered = new List<string>();
		var added = new List<string>();
		var texts = new List<(string Name, string Text)>();
		foreach (var entry in items) {
			var text = entry.Text ?? "";
			if (entry.SourceIndex >= 0) {
				var name = existingNames[entry.SourceIndex];
				if (!String.Equals(existing[entry.SourceIndex].Text, text, StringComparison.Ordinal))
					texts.Add((name, text));
				ordered.Add(name);
			} else {
				var index = 1;
				while (taken.Contains("columnHeader" + index))
					index++;
				var name = "columnHeader" + index;
				taken.Add(name);
				added.Add(name);
				texts.Add((name, text));
				ordered.Add(name);
			}
		}
		var designerFile = CurrentDesignerFile();
		var originalSource = designerFile.Text;
		try {
			foreach (var name in removedNames)
				RewriteDeletedComponent(name);
			foreach (var name in added) {
				RewriteAddedComponent(typeof(ColumnHeader), name);
				MoveCreationAfterOwner(name, elementId);
			}
			foreach (var (name, text) in texts)
				RewriteProperty(name, "Text", text);
			RewriteCollectionStatements(elementId, "Columns", ordered.Count == 0 ? null
				: IsVisualBasic ? $"Me.{elementId}.Columns.AddRange(New System.Windows.Forms.ColumnHeader() {{{string.Join(", ", ordered.Select(name => "Me." + name))}}})"
				: $"this.{elementId}.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {{ {string.Join(", ", ordered.Select(name => "this." + name))} }});");
			CreateDesignSurface(current!);
		} catch (Exception exception) {
			designerFile.Text = originalSource;
			try {
				CreateDesignSurface(current!);
			} catch (Exception recoveryException) {
				return Rejected(baseVersion, "Editing the columns failed and the design surface could not be restored: " + recoveryException.Message);
			}
			return Rejected(baseVersion, "Editing the columns failed: " + exception.Message);
		}
		return CurrentState(baseVersion);
	}

	/// <summary>Moves "<paramref name="name"/> = new ..." to just after the owner's own creation
	/// statement (RewriteAddedComponent puts it first). A ColumnHeader created before its ListView
	/// leaves LibreWinForms unable to dispose the surface later ("The container cannot be disposed
	/// at design time"); Visual Studio writes columns after the ListView as well.</summary>
	void MoveCreationAfterOwner(string name, string ownerId)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			var vbMethod = vbRoot.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().First(item =>
				item.BlockStatement is VbSyntax.MethodStatementSyntax ms && ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
			var vbStatements = vbMethod.Statements.ToList();
			int VbCreation(string target) => vbStatements.FindIndex(st => st is VbSyntax.AssignmentStatementSyntax assignment
				&& NormalizeTarget(assignment.Left.ToString()) == target && assignment.Right is VbSyntax.ObjectCreationExpressionSyntax);
			var vbOwn = VbCreation(name);
			if (vbOwn < 0 || VbCreation(ownerId) < 0) return;
			var vbMoved = vbStatements[vbOwn];
			vbStatements.RemoveAt(vbOwn);
			vbStatements.Insert(VbCreation(ownerId) + 1, vbMoved);
			file.Text = vbRoot.ReplaceNode(vbMethod, vbMethod.WithStatements(Vb.SyntaxFactory.List(vbStatements))).NormalizeWhitespace().ToFullString();
			return;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First(item => item.Identifier.ValueText == "InitializeComponent");
		var statements = method.Body!.Statements.ToList();
		int Creation(string target) => statements.FindIndex(st => st is ExpressionStatementSyntax {
			Expression: AssignmentExpressionSyntax { Right: ObjectCreationExpressionSyntax } assignment }
			&& NormalizeTarget(assignment.Left.ToString()) == target);
		var own = Creation(name);
		if (own < 0 || Creation(ownerId) < 0) return;
		var moved = statements[own];
		statements.RemoveAt(own);
		statements.Insert(Creation(ownerId) + 1, moved);
		file.Text = root.ReplaceNode(method, method.WithBody(method.Body.WithStatements(SyntaxFactory.List(statements)))).NormalizeWhitespace().ToFullString();
	}

	/// <summary>The settings a kept node carries through a list edit, in source order, with
	/// their non-default values (the list itself edits only text, nesting and order).</summary>
	static IEnumerable<(string Name, object Value)> TreeNodeSettings(TreeNode node)
	{
		if (!String.IsNullOrEmpty(node.Name)) yield return ("Name", node.Name);
		if (node.Checked) yield return ("Checked", true);
		if (!String.IsNullOrEmpty(node.ImageKey)) yield return ("ImageKey", node.ImageKey);
		else if (node.ImageIndex >= 0) yield return ("ImageIndex", node.ImageIndex);
		if (!String.IsNullOrEmpty(node.SelectedImageKey)) yield return ("SelectedImageKey", node.SelectedImageKey);
		else if (node.SelectedImageIndex >= 0) yield return ("SelectedImageIndex", node.SelectedImageIndex);
		if (!String.IsNullOrEmpty(node.StateImageKey)) yield return ("StateImageKey", node.StateImageKey);
		else if (node.StateImageIndex >= 0) yield return ("StateImageIndex", node.StateImageIndex);
		if (!String.IsNullOrEmpty(node.ToolTipText)) yield return ("ToolTipText", node.ToolTipText);
		if (!node.ForeColor.IsEmpty) yield return ("ForeColor", node.ForeColor);
		if (!node.BackColor.IsEmpty) yield return ("BackColor", node.BackColor);
		if (node.NodeFont != null) yield return ("NodeFont", node.NodeFont);
		if (node.Tag is string tag) yield return ("Tag", tag);
		if (node.ContextMenuStrip is IComponent menu && menu.Site?.Name is { Length: > 0 }) yield return ("ContextMenuStrip", menu);
	}

	static void CopyTreeNodeSettings(TreeNode source, TreeNode target)
	{
		foreach (var (name, value) in TreeNodeSettings(source))
			TypeDescriptor.GetProperties(target)[name]?.SetValue(target, value);
	}

	static string TreeNodeInitializer(TreeNode node)
	{
		var settings = TreeNodeSettings(node).Select(setting => $"{setting.Name} = {SerializeValue(setting.Value)}").ToList();
		return settings.Count == 0 ? "" : " { " + string.Join(", ", settings) + " }";
	}

	static string TreeNodeInitializerVisualBasic(TreeNode node)
	{
		var settings = TreeNodeSettings(node).Select(setting => $".{setting.Name} = {SerializeValueVisualBasic(setting.Value)}").ToList();
		return settings.Count == 0 ? "" : " With {" + string.Join(", ", settings) + "}";
	}

	static string TreeNodeSource(TreeNode node) => (node.Nodes.Count == 0
		? $"new System.Windows.Forms.TreeNode({SerializeValue(node.Text)})"
		: $"new System.Windows.Forms.TreeNode({SerializeValue(node.Text)}, new System.Windows.Forms.TreeNode[] {{ {string.Join(", ", node.Nodes.Cast<TreeNode>().Select(TreeNodeSource))} }})")
		+ TreeNodeInitializer(node);

	static string TreeNodeSourceVisualBasic(TreeNode node) => (node.Nodes.Count == 0
		? $"New System.Windows.Forms.TreeNode({SerializeValueVisualBasic(node.Text)})"
		: $"New System.Windows.Forms.TreeNode({SerializeValueVisualBasic(node.Text)}, New System.Windows.Forms.TreeNode() {{{string.Join(", ", node.Nodes.Cast<TreeNode>().Select(TreeNodeSourceVisualBasic))}}})")
		+ TreeNodeInitializerVisualBasic(node);

	/// <summary>Removes Visual Studio's TreeNode locals from InitializeComponent - each
	/// <c>TreeNode treeNodeN = new TreeNode(...)</c> declaration and every statement on it
	/// (<c>treeNodeN.Name = ...</c>) - before the tree is rewritten as one nested AddRange, so no
	/// dead node code is left behind. Only locals reachable from <paramref name="treeId"/>'s own
	/// <c>Nodes.Add/AddRange</c> (directly, or nested in another reachable node's initializer) are
	/// removed: another TreeView's nodes in the same InitializeComponent are left untouched.</summary>
	static readonly HashSet<string> CarriedTreeNodeSettings = new(StringComparer.Ordinal) {
		"Name", "Text", "Checked", "ImageIndex", "SelectedImageIndex", "StateImageIndex", "ImageKey", "SelectedImageKey", "StateImageKey",
		"ToolTipText", "ForeColor", "BackColor", "NodeFont", "Tag", "ContextMenuStrip" };

	/// <returns>False, changing nothing, when a reachable local has an assignment the edit cannot
	/// carry through (<paramref name="unsupported"/> names it): removing it would lose the value.</returns>
	bool RemoveTreeNodeLocals(string treeId, out string unsupported)
		=> RemoveItemLocals(treeId, "Nodes", "TreeNode", CarriedTreeNodeSettings, out unsupported);

	/// <summary>Generalised <see cref="RemoveTreeNodeLocals"/>: the <paramref name="typeName"/> locals
	/// reachable from <paramref name="ownerId"/>'s own <paramref name="member"/>.Add/AddRange, refusing
	/// when one carries a setting outside <paramref name="carried"/>.</summary>
	bool RemoveItemLocals(string treeId, string member, string typeName, HashSet<string> carried, out string unsupported)
	{
		unsupported = "";
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			var vbMethod = vbRoot.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().First(item =>
				item.BlockStatement is VbSyntax.MethodStatementSyntax ms && ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
			var vbNames = vbMethod.Statements.OfType<VbSyntax.LocalDeclarationStatementSyntax>()
				.SelectMany(local => local.Declarators)
				.Where(d => d.Names.Count == 1 && ((d.AsClause as VbSyntax.SimpleAsClauseSyntax)?.Type.ToString()
					?? (d.AsClause as VbSyntax.AsNewClauseSyntax)?.NewExpression.ToString() ?? "").Contains(typeName, StringComparison.Ordinal))
				.Select(d => d.Names[0].Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
			var vbInitializers = vbMethod.Statements.OfType<VbSyntax.LocalDeclarationStatementSyntax>().SelectMany(local => local.Declarators)
				.Where(d => d.Names.Count == 1 && vbNames.Contains(d.Names[0].Identifier.ValueText))
				.ToDictionary(d => d.Names[0].Identifier.ValueText, d => (SyntaxNode?)d.Initializer?.Value ?? (d.AsClause as VbSyntax.AsNewClauseSyntax)?.NewExpression, StringComparer.Ordinal);
			var vbRoots = vbMethod.Statements.OfType<VbSyntax.ExpressionStatementSyntax>()
				.Where(st => st.Expression is VbSyntax.InvocationExpressionSyntax { Expression: VbSyntax.MemberAccessExpressionSyntax access }
					&& access.Name.Identifier.ValueText is "Add" or "AddRange"
					&& access.Expression is VbSyntax.MemberAccessExpressionSyntax owner && owner.Name.Identifier.ValueText == member
					&& owner.Expression != null && VbSimpleTargetName(owner.Expression) == treeId)
				.Cast<SyntaxNode>();
			vbNames = ReachableLocals(vbRoots, vbNames, vbInitializers);
			var vbOther = vbMethod.Statements.OfType<VbSyntax.AssignmentStatementSyntax>()
				.Select(st => st.Left as VbSyntax.MemberAccessExpressionSyntax)
				.FirstOrDefault(left => left is { Expression: VbSyntax.IdentifierNameSyntax owner } && vbNames.Contains(owner.Identifier.ValueText)
					&& !carried.Contains(left.Name.Identifier.ValueText));
			if (vbOther != null) { unsupported = vbOther.Name.Identifier.ValueText; return false; }
			var vbInitialized = vbRoots.Concat(vbInitializers.Where(pair => vbNames.Contains(pair.Key)).Select(pair => pair.Value!).Where(node => node != null))
				.SelectMany(node => node.DescendantNodesAndSelf().OfType<VbSyntax.ObjectCreationExpressionSyntax>())
				.Where(creation => creation.Type.ToString().EndsWith(typeName, StringComparison.Ordinal))
				.SelectMany(creation => (creation.Initializer as VbSyntax.ObjectMemberInitializerSyntax)?.Initializers ?? default)
				.Select(field => (field as VbSyntax.NamedFieldInitializerSyntax)?.Name.Identifier.ValueText ?? field.ToString())
				.FirstOrDefault(name => !carried.Contains(name));
			if (vbInitialized != null) { unsupported = vbInitialized; return false; }
			var vbKept = vbMethod.Statements.Where(st => !(st is VbSyntax.LocalDeclarationStatementSyntax l
					&& l.Declarators.All(d => d.Names.All(n => vbNames.Contains(n.Identifier.ValueText))))
				&& !(st is VbSyntax.AssignmentStatementSyntax a && a.Left is VbSyntax.MemberAccessExpressionSyntax { Expression: VbSyntax.IdentifierNameSyntax owner }
					&& vbNames.Contains(owner.Identifier.ValueText))).ToList();
			file.Text = vbRoot.ReplaceNode(vbMethod, vbMethod.WithStatements(Vb.SyntaxFactory.List(vbKept))).NormalizeWhitespace().ToFullString();
			return true;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First(item => item.Identifier.ValueText == "InitializeComponent");
		var names = method.Body!.Statements.OfType<LocalDeclarationStatementSyntax>()
			.Where(local => local.Declaration.Type.ToString().EndsWith(typeName, StringComparison.Ordinal)
				|| local.Declaration.Variables.All(v => v.Initializer?.Value is ObjectCreationExpressionSyntax c && c.Type.ToString().EndsWith(typeName, StringComparison.Ordinal)))
			.SelectMany(local => local.Declaration.Variables).Select(v => v.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
		var initializers = method.Body.Statements.OfType<LocalDeclarationStatementSyntax>().SelectMany(local => local.Declaration.Variables)
			.Where(v => names.Contains(v.Identifier.ValueText))
			.ToDictionary(v => v.Identifier.ValueText, v => (SyntaxNode?)v.Initializer?.Value, StringComparer.Ordinal);
		var roots = method.Body.Statements.OfType<ExpressionStatementSyntax>()
			.Where(st => st.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
				&& access.Name.Identifier.ValueText is "Add" or "AddRange"
				&& access.Expression is MemberAccessExpressionSyntax owner && owner.Name.Identifier.ValueText == member
				&& SimpleTargetName(owner.Expression) == treeId)
			.Cast<SyntaxNode>();
		names = ReachableLocals(roots, names, initializers);
		var other = method.Body.Statements.OfType<ExpressionStatementSyntax>()
			.Select(st => (st.Expression as AssignmentExpressionSyntax)?.Left as MemberAccessExpressionSyntax)
			.FirstOrDefault(left => left is { Expression: IdentifierNameSyntax owner } && names.Contains(owner.Identifier.ValueText)
				&& !carried.Contains(left.Name.Identifier.ValueText));
		if (other != null) { unsupported = other.Name.Identifier.ValueText; return false; }
		var initialized = roots.Concat(initializers.Where(pair => names.Contains(pair.Key)).Select(pair => pair.Value!).Where(node => node != null))
			.SelectMany(node => node.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>())
			.Where(creation => creation.Type.ToString().EndsWith(typeName, StringComparison.Ordinal) && creation.Initializer != null)
			.SelectMany(creation => creation.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>())
			.Select(assignment => (assignment.Left as IdentifierNameSyntax)?.Identifier.ValueText ?? assignment.Left.ToString())
			.FirstOrDefault(name => !carried.Contains(name));
		if (initialized != null) { unsupported = initialized; return false; }
		var keptStatements = method.Body.Statements.Where(st => !(st is LocalDeclarationStatementSyntax l
				&& l.Declaration.Variables.All(v => names.Contains(v.Identifier.ValueText)))
			&& !(st is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax owner } } }
				&& names.Contains(owner.Identifier.ValueText))).ToList();
		file.Text = root.ReplaceNode(method, method.WithBody(method.Body.WithStatements(SyntaxFactory.List(keptStatements)))).NormalizeWhitespace().ToFullString();
		return true;
	}

	/// <summary>The TreeNode locals named in <paramref name="roots"/>, plus those nested in a
	/// reachable local's own initializer (a parent node's <c>new TreeNode("x", new[] { child })</c>).</summary>
	static HashSet<string> ReachableLocals(IEnumerable<SyntaxNode> roots, HashSet<string> candidates, Dictionary<string, SyntaxNode?> initializers)
	{
		var reachable = new HashSet<string>(StringComparer.Ordinal);
		var pending = new Stack<SyntaxNode>(roots);
		while (pending.Count > 0) {
			var node = pending.Pop();
			foreach (var token in node.DescendantTokens()) {
				var name = token.ValueText;
				if (candidates.Contains(name) && reachable.Add(name) && initializers.TryGetValue(name, out var initializer) && initializer != null)
					pending.Push(initializer);
			}
		}
		return reachable;
	}

	/// <summary>Replaces every "&lt;id&gt;.&lt;member&gt;.Add/AddRange(...)" statement in
	/// InitializeComponent with <paramref name="statement"/> (none when null), at the first replaced
	/// statement's position, else after the control's own last statement.</summary>
	void RewriteCollectionStatements(string elementId, string member, string? statement)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			var vbMethod = vbRoot.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().First(item =>
				item.BlockStatement is VbSyntax.MethodStatementSyntax ms && ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
			var vbStatements = vbMethod.Statements.ToList();
			bool IsVbCall(VbSyntax.StatementSyntax s) => s is VbSyntax.ExpressionStatementSyntax {
				Expression: VbSyntax.InvocationExpressionSyntax { Expression: VbSyntax.MemberAccessExpressionSyntax access } }
				&& access.Name.Identifier.ValueText is "Add" or "AddRange"
				&& access.Expression is VbSyntax.MemberAccessExpressionSyntax owner && owner.Name.Identifier.ValueText == member
				&& owner.Expression != null && VbSimpleTargetName(owner.Expression) == elementId;
			var vbAt = vbStatements.FindIndex(IsVbCall);
			if (vbAt < 0) {
				var last = vbStatements.FindLastIndex(s => s.DescendantNodes().OfType<VbSyntax.MemberAccessExpressionSyntax>()
					.Any(access => access.Expression != null && VbSimpleTargetName(access.Expression) == elementId));
				vbAt = last >= 0 ? last + 1 : vbStatements.Count;
			}
			var vbKept = vbStatements.Where(s => !IsVbCall(s)).ToList();
			vbAt -= vbStatements.Take(vbAt).Count(IsVbCall);
			if (statement != null)
				vbKept.Insert(vbAt, Vb.SyntaxFactory.ParseExecutableStatement(statement));
			file.Text = vbRoot.ReplaceNode(vbMethod, vbMethod.WithStatements(Vb.SyntaxFactory.List(vbKept))).NormalizeWhitespace().ToFullString();
			return;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First(item => item.Identifier.ValueText == "InitializeComponent");
		var statements = method.Body!.Statements.ToList();
		bool IsCall(StatementSyntax s) => s is ExpressionStatementSyntax {
			Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } }
			&& access.Name.Identifier.ValueText is "Add" or "AddRange"
			&& access.Expression is MemberAccessExpressionSyntax owner && owner.Name.Identifier.ValueText == member
			&& SimpleTargetName(owner.Expression) == elementId;
		var at = statements.FindIndex(IsCall);
		if (at < 0) {
			var last = statements.FindLastIndex(s => s.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
				.Any(access => SimpleTargetName(access.Expression) == elementId));
			at = last >= 0 ? last + 1 : statements.Count;
		}
		var kept = statements.Where(s => !IsCall(s)).ToList();
		at -= statements.Take(at).Count(IsCall);
		if (statement != null)
			kept.Insert(at, SyntaxFactory.ParseStatement(statement));
		file.Text = root.ReplaceNode(method, method.WithBody(method.Body.WithStatements(SyntaxFactory.List(kept)))).NormalizeWhitespace().ToFullString();
	}

	/// <summary>Replaces every "&lt;id&gt;.Items.Add(...)"/"AddRange(...)" statement in
	/// InitializeComponent with one AddRange of <paramref name="texts"/> (none when empty), at the
	/// first replaced statement's position, else after the control's last own statement. With
	/// <paramref name="addEach"/> it writes one Add per item instead (a collection without AddRange).</summary>
	void RewriteItems(string elementId, string[] texts, bool addEach)
	{
		if (IsVisualBasic) { RewriteItemsVisualBasic(elementId, texts, addEach); return; }
		var file = CurrentDesignerFile();
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.First(item => item.Identifier.ValueText == "InitializeComponent");
		var statements = method.Body!.Statements.ToList();
		bool IsItemsCall(StatementSyntax statement) => statement is ExpressionStatementSyntax {
			Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } }
			&& access.Name.Identifier.ValueText is "Add" or "AddRange" && IsTargetCollectionAccess(access.Expression, elementId, "");
		var insertAt = statements.FindIndex(IsItemsCall);
		if (insertAt < 0) {
			// No Items call yet: after the control's own last statement, else at the end.
			var last = statements.FindLastIndex(statement => statement.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
				.Any(access => SimpleTargetName(access.Expression) == elementId));
			insertAt = last >= 0 ? last + 1 : statements.Count;
		}
		var kept = statements.Where(statement => !IsItemsCall(statement)).ToList();
		insertAt -= statements.Take(insertAt).Count(IsItemsCall);
		if (addEach)
			kept.InsertRange(insertAt, texts.Select(text => SyntaxFactory.ParseStatement($"this.{elementId}.Items.Add({SerializeValue(text)});")));
		else if (texts.Length > 0) {
			var literals = string.Join(", ", texts.Select(text => SerializeValue(text).ToString()));
			kept.Insert(insertAt, SyntaxFactory.ParseStatement($"this.{elementId}.Items.AddRange(new object[] {{ {literals} }});"));
		}
		file.Text = root.ReplaceNode(method, method.WithBody(method.Body.WithStatements(SyntaxFactory.List(kept))))
			.NormalizeWhitespace().ToFullString();
	}

	void RewriteItemsVisualBasic(string elementId, string[] texts, bool addEach)
	{
		var file = CurrentDesignerFile();
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
		var method = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().First(item =>
			item.BlockStatement is VbSyntax.MethodStatementSyntax ms && ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
			&& ms.Identifier.ValueText == "InitializeComponent");
		var statements = method.Statements.ToList();
		bool IsItemsCall(VbSyntax.StatementSyntax statement) => statement is VbSyntax.ExpressionStatementSyntax {
			Expression: VbSyntax.InvocationExpressionSyntax { Expression: VbSyntax.MemberAccessExpressionSyntax access } }
			&& access.Name.Identifier.ValueText is "Add" or "AddRange" && IsVbTargetCollectionAccess(access.Expression, elementId, "");
		var insertAt = statements.FindIndex(IsItemsCall);
		if (insertAt < 0) {
			var last = statements.FindLastIndex(statement => statement.DescendantNodes().OfType<VbSyntax.MemberAccessExpressionSyntax>()
				.Any(access => access.Expression != null && VbSimpleTargetName(access.Expression) == elementId));
			insertAt = last >= 0 ? last + 1 : statements.Count;
		}
		var kept = statements.Where(statement => !IsItemsCall(statement)).ToList();
		insertAt -= statements.Take(insertAt).Count(IsItemsCall);
		if (addEach)
			kept.InsertRange(insertAt, texts.Select(text => Vb.SyntaxFactory.ParseExecutableStatement($"Me.{elementId}.Items.Add({SerializeValueVisualBasic(text)})")));
		else if (texts.Length > 0) {
			var literals = string.Join(", ", texts.Select(text => SerializeValueVisualBasic(text).ToString()));
			kept.Insert(insertAt, Vb.SyntaxFactory.ParseExecutableStatement($"Me.{elementId}.Items.AddRange(New Object() {{{literals}}})"));
		}
		file.Text = root.ReplaceNode(method, method.WithStatements(Vb.SyntaxFactory.List(kept)))
			.NormalizeWhitespace().ToFullString();
	}

	/// <summary>Sets one authored TableLayoutPanel row/column style. The mutation is accepted only
	/// when InitializeComponent proves the style collection: exactly one plain
	/// <c>&lt;id&gt;.RowStyles/ColumnStyles.Add(new RowStyle/ColumnStyle(...))</c> statement per live
	/// style. WinForms creates default styles at runtime that have no source, so a live style alone is
	/// never treated as authored. The source statement is rewritten in place, after which the live
	/// style is changed in one designer transaction.</summary>
	[JsonRpcMethod("design/set-table-layout-track")]
	public DesignerSessionState SetTableLayoutTrack(string sessionId, string documentId, long baseVersion, string elementId, bool isRow, int index, string sizeType, double value)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		if (!Enum.TryParse<SizeType>(sizeType, ignoreCase: false, out var type) || !Enum.IsDefined(type))
			throw new ArgumentException("Unknown SizeType: " + sizeType, nameof(sizeType));
		var (host, table) = FindTableLayoutPanel(elementId);
		ApplyTableLayoutStyles(host, table, elementId, isRow, new[] { (index, type, value) });
		return CurrentState(baseVersion);
	}

	/// <summary>Returns both authored style lists for the single Edit Rows and Columns dialog.
	/// Proving them here lets the client avoid presenting an editor whose OK action cannot safely
	/// rewrite Designer.cs.</summary>
	[JsonRpcMethod("design/get-table-layout-tracks")]
	public DesignerTableLayoutTracks GetTableLayoutTracks(string sessionId, string documentId, long baseVersion, string elementId)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "inspect");
		try {
			var (_, table) = FindTableLayoutPanel(elementId);
			var file = CurrentDesignerFile();
			ProveTableLayoutTrackSource(file.Text, elementId, true, table.RowStyles.Count, table.RowCount);
			ProveTableLayoutTrackSource(file.Text, elementId, false, table.ColumnStyles.Count, table.ColumnCount);
			return new DesignerTableLayoutTracks {
				Accepted = true,
				Rows = table.RowStyles.Cast<RowStyle>().Select((style, index) => new DesignerTableLayoutTrackEdit { SourceIndex = index, SizeType = style.SizeType.ToString(), Value = StyleLength(style) }).ToList(),
				Columns = table.ColumnStyles.Cast<ColumnStyle>().Select((style, index) => new DesignerTableLayoutTrackEdit { SourceIndex = index, SizeType = style.SizeType.ToString(), Value = StyleLength(style) }).ToList()
			};
		} catch (Exception exception) {
			return new DesignerTableLayoutTracks { Error = exception.Message };
		}
	}

	/// <summary>Applies both lists from Edit Rows and Columns as one undoable source/live mutation.
	/// Structural changes remain available through the explicit Insert/Remove commands; this dialog
	/// deliberately edits the authored definitions it displayed, so rows and columns cannot drift
	/// through two independently committed RPC calls.</summary>
	[JsonRpcMethod("design/apply-table-layout-tracks")]
	public DesignerSessionState ApplyTableLayoutTracks(string sessionId, string documentId, long baseVersion, string elementId,
		IReadOnlyList<DesignerTableLayoutTrackEdit> rows, IReadOnlyList<DesignerTableLayoutTrackEdit> columns)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		var (host, table) = FindTableLayoutPanel(elementId);
		if (rows == null || columns == null || rows.Count != table.RowStyles.Count || columns.Count != table.ColumnStyles.Count)
			throw new ArgumentException("The track list changed; reopen Edit Rows and Columns.");
		var rowEdits = ValidateTableLayoutTrackEdits(rows, table.RowStyles.Count);
		var columnEdits = ValidateTableLayoutTrackEdits(columns, table.ColumnStyles.Count);
		var file = CurrentDesignerFile();
		var rowText = IsVisualBasic
			? RewriteTableLayoutStylesVisualBasic(file.Text, elementId, "RowStyles", "RowStyle", table.RowStyles.Count, rowEdits)
			: RewriteTableLayoutStyles(file.Text, elementId, "RowStyles", "RowStyle", table.RowStyles.Count, rowEdits);
		var newText = IsVisualBasic
			? RewriteTableLayoutStylesVisualBasic(rowText, elementId, "ColumnStyles", "ColumnStyle", table.ColumnStyles.Count, columnEdits)
			: RewriteTableLayoutStyles(rowText, elementId, "ColumnStyles", "ColumnStyle", table.ColumnStyles.Count, columnEdits);
		using (var transaction = host.CreateTransaction($"Edit rows and columns of {elementId}")) {
			ApplyLiveTableLayoutStyles(table.RowStyles, rowEdits);
			ApplyLiveTableLayoutStyles(table.ColumnStyles, columnEdits);
			transaction.Commit();
		}
		file.Text = newText;
		return CurrentState(baseVersion);
	}

	static List<(int Index, SizeType Type, double Value)> ValidateTableLayoutTrackEdits(IReadOnlyList<DesignerTableLayoutTrackEdit> edits, int count)
	{
		var result = new List<(int, SizeType, double)>();
		for (var index = 0; index < edits.Count; index++) {
			var edit = edits[index] ?? throw new ArgumentException("A track is missing.");
			if (edit.SourceIndex != index || !Enum.TryParse<SizeType>(edit.SizeType, false, out var type) || !Enum.IsDefined(type)
				|| (type != SizeType.AutoSize && (!Double.IsFinite(edit.Value) || edit.Value < 0 || edit.Value > Single.MaxValue)))
				throw new ArgumentException("The track list contains an invalid style.");
			result.Add((index, type, edit.Value));
		}
		if (result.Count != count) throw new ArgumentException("The track list changed.");
		return result;
	}

	static void ApplyLiveTableLayoutStyles(TableLayoutStyleCollection styles, IReadOnlyList<(int Index, SizeType Type, double Value)> edits)
	{
		foreach (var (index, type, value) in edits) {
			var style = styles[index]; style.SizeType = type;
			if (type != SizeType.AutoSize) {
				if (style is RowStyle row) row.Height = (float)value;
				else ((ColumnStyle)style).Width = (float)value;
			}
		}
	}

	/// <summary>A divider drag: track <paramref name="index"/> should become
	/// <paramref name="size"/> pixels. Between two Percent tracks the pair's percentages are
	/// redistributed in proportion to their measured sizes, keeping their sum (the in-process
	/// TableLayoutPanel designer's behaviour), so the table stays proportional; otherwise the track
	/// becomes Absolute. Both styles change in one source rewrite and one transaction.</summary>
	[JsonRpcMethod("design/resize-table-layout-track")]
	public DesignerSessionState ResizeTableLayoutTrack(string sessionId, string documentId, long baseVersion, string elementId, bool isRow, int index, double size)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		if (!Double.IsFinite(size) || size < 0)
			throw new ArgumentOutOfRangeException(nameof(size));
		var (host, table) = FindTableLayoutPanel(elementId);
		var styles = isRow ? (TableLayoutStyleCollection)table.RowStyles : table.ColumnStyles;
		var measured = isRow ? table.GetRowHeights() : table.GetColumnWidths();
		if (index < 0 || index + 1 >= styles.Count || index + 1 >= measured.Length)
			throw new ArgumentOutOfRangeException(nameof(index));
		var first = styles[index];
		var second = styles[index + 1];
		if (first.SizeType == SizeType.Percent && second.SizeType == SizeType.Percent) {
			var pair = (double)measured[index] + measured[index + 1];
			var total = (double)StyleLength(first) + StyleLength(second);
			if (pair <= 0 || total <= 0)
				throw new InvalidOperationException("The Percent tracks have no measurable extent to redistribute.");
			var percent = Math.Round(total * Math.Min(size, pair) / pair, 2);
			ApplyTableLayoutStyles(host, table, elementId, isRow, new[] {
				(index, SizeType.Percent, percent), (index + 1, SizeType.Percent, Math.Round(total - percent, 2))
			});
		} else {
			ApplyTableLayoutStyles(host, table, elementId, isRow, new[] { (index, SizeType.Absolute, Math.Round(size)) });
		}
		return CurrentState(baseVersion);
	}

	/// <summary>Appends a row/column to a TableLayoutPanel: its Count grows by one and a style
	/// copying the last track's SizeType and length is added after the last style statement. Like
	/// the style edits, it requires a provable authored style list plus exactly one
	/// <c>&lt;id&gt;.RowCount/ColumnCount = &lt;integer literal&gt;</c> assignment matching the live
	/// count. Appending never moves an existing child's cell.</summary>
	[JsonRpcMethod("design/add-table-layout-track")]
	public DesignerSessionState AddTableLayoutTrack(string sessionId, string documentId, long baseVersion, string elementId, bool isRow)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		var (host, table) = FindTableLayoutPanel(elementId);
		var styles = isRow ? (TableLayoutStyleCollection)table.RowStyles : table.ColumnStyles;
		var count = isRow ? table.RowCount : table.ColumnCount;
		if (styles.Count == 0)
			throw new InvalidOperationException($"{elementId} has no authored style to copy for a new track.");
		var last = styles[styles.Count - 1];
		var (type, value) = (last.SizeType, (double)StyleLength(last));
		var file = CurrentDesignerFile();
		var proof = ProveTableLayoutTrackSource(file.Text, elementId, isRow, styles.Count, count);
		var root = proof.Root.TrackNodes(proof.Styles[^1], proof.CountLiteral);
		root = root.ReplaceNode(root.GetCurrentNode(proof.CountLiteral)!, CountLiteral(count + 1).WithTriviaFrom(proof.CountLiteral));
		var current = root.GetCurrentNode(proof.Styles[^1])!;
		root = root.ReplaceNode(current, new[] { current, StyleStatement(elementId, isRow, type, value).WithTriviaFrom(proof.Styles[^1]) });
		using (var transaction = host.CreateTransaction($"Add {(isRow ? "row" : "column")} to {elementId}")) {
			if (isRow) {
				table.RowCount = count + 1;
				table.RowStyles.Add(new RowStyle(type, (float)value));
			} else {
				table.ColumnCount = count + 1;
				table.ColumnStyles.Add(new ColumnStyle(type, (float)value));
			}
			transaction.Commit();
		}
		file.Text = root.ToFullString();
		return CurrentState(baseVersion);
	}

	/// <summary>Removes a TableLayoutPanel's last row/column. Accepted only on the same proof as
	/// <see cref="AddTableLayoutTrack"/>, while at least two tracks exist and no child control's cell
	/// (including its span) reaches the removed track, so no control is deleted or moved.</summary>
	[JsonRpcMethod("design/remove-table-layout-track")]
	public DesignerSessionState RemoveTableLayoutTrack(string sessionId, string documentId, long baseVersion, string elementId, bool isRow)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		var (host, table) = FindTableLayoutPanel(elementId);
		var styles = isRow ? (TableLayoutStyleCollection)table.RowStyles : table.ColumnStyles;
		var count = isRow ? table.RowCount : table.ColumnCount;
		if (count < 2)
			throw new InvalidOperationException($"{elementId} must keep at least one {(isRow ? "row" : "column")}.");
		foreach (Control child in table.Controls) {
			var position = table.GetPositionFromControl(child);
			var start = isRow ? position.Row : position.Column;
			var span = isRow ? table.GetRowSpan(child) : table.GetColumnSpan(child);
			// A child without a resolved cell may flow into the last track, so it is not proof of
			// a free track.
			if (start < 0 || start + Math.Max(1, span) - 1 >= count - 1)
				throw new InvalidOperationException($"{child.Site?.Name ?? child.Name} may occupy the last {(isRow ? "row" : "column")} of {elementId}.");
		}
		var file = CurrentDesignerFile();
		var proof = ProveTableLayoutTrackSource(file.Text, elementId, isRow, styles.Count, count);
		var root = proof.Root.TrackNodes(proof.Styles[^1], proof.CountLiteral);
		root = root.ReplaceNode(root.GetCurrentNode(proof.CountLiteral)!, CountLiteral(count - 1).WithTriviaFrom(proof.CountLiteral));
		root = root.RemoveNode(root.GetCurrentNode(proof.Styles[^1])!, SyntaxRemoveOptions.KeepNoTrivia)!;
		using (var transaction = host.CreateTransaction($"Remove {(isRow ? "row" : "column")} from {elementId}")) {
			styles.RemoveAt(styles.Count - 1);
			if (isRow)
				table.RowCount = count - 1;
			else
				table.ColumnCount = count - 1;
			transaction.Commit();
		}
		file.Text = root.ToFullString();
		return CurrentState(baseVersion);
	}

	/// <summary>Inserts a row/column before track <paramref name="index"/>, copying that track's
	/// style, as the in-process TableLayoutPanel designer's Insert did: children at or after the
	/// track move one cell on, and a child spanning across it grows by one. Besides the track-list
	/// proof, every child's placement must be provable (see <see cref="ProveTableLayoutChildren"/>),
	/// so the cell and span literals can be rewritten exactly.</summary>
	[JsonRpcMethod("design/insert-table-layout-track")]
	public DesignerSessionState InsertTableLayoutTrack(string sessionId, string documentId, long baseVersion, string elementId, bool isRow, int index)
		=> EditTableLayoutTrackAt(sessionId, documentId, baseVersion, elementId, isRow, index, insert: true);

	/// <summary>Removes the empty row/column <paramref name="index"/>: refused while any child's
	/// cell or span covers it; children after it move one cell back.</summary>
	[JsonRpcMethod("design/remove-table-layout-track-at")]
	public DesignerSessionState RemoveTableLayoutTrackAt(string sessionId, string documentId, long baseVersion, string elementId, bool isRow, int index)
		=> EditTableLayoutTrackAt(sessionId, documentId, baseVersion, elementId, isRow, index, insert: false);

	DesignerSessionState EditTableLayoutTrackAt(string sessionId, string documentId, long baseVersion, string elementId, bool isRow, int index, bool insert)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		var (host, table) = FindTableLayoutPanel(elementId);
		var styles = isRow ? (TableLayoutStyleCollection)table.RowStyles : table.ColumnStyles;
		var count = isRow ? table.RowCount : table.ColumnCount;
		if (index < 0 || index >= count)
			throw new ArgumentOutOfRangeException(nameof(index));
		if (!insert && count < 2)
			throw new InvalidOperationException($"{elementId} must keep at least one {(isRow ? "row" : "column")}.");
		var file = CurrentDesignerFile();
		var proof = ProveTableLayoutTrackSource(file.Text, elementId, isRow, styles.Count, count);
		var children = ProveTableLayoutChildren(proof.Root, table, elementId);
		// The new cell and span of each child on this axis; unchanged children are left out.
		var moves = new List<(TableLayoutChild Child, int Start, int Span)>();
		foreach (var child in children) {
			var start = isRow ? child.Row : child.Column;
			var span = isRow ? child.RowSpan : child.ColumnSpan;
			if (insert) {
				if (start >= index)
					moves.Add((child, start + 1, span));
				else if (start + span - 1 >= index)
					moves.Add((child, start, span + 1));
			} else {
				if (start <= index && start + span - 1 >= index)
					throw new InvalidOperationException($"{child.Name} occupies {(isRow ? "row" : "column")} {index} of {elementId}.");
				if (start > index)
					moves.Add((child, start - 1, span));
			}
		}
		var copied = styles[index];
		var (type, value) = (copied.SizeType, (double)StyleLength(copied));

		var tracked = new List<SyntaxNode> { proof.Styles[index], proof.CountLiteral };
		foreach (var (child, _, _) in moves) {
			tracked.Add(isRow ? child.RowLiteral : child.ColumnLiteral);
			if ((isRow ? child.RowSpanLiteral : child.ColumnSpanLiteral) is { } spanLiteral)
				tracked.Add(spanLiteral);
		}
		var root = proof.Root.TrackNodes(tracked);
		root = root.ReplaceNode(root.GetCurrentNode(proof.CountLiteral)!, CountLiteral(insert ? count + 1 : count - 1).WithTriviaFrom(proof.CountLiteral));
		foreach (var (child, start, span) in moves) {
			var startLiteral = isRow ? child.RowLiteral : child.ColumnLiteral;
			root = root.ReplaceNode(root.GetCurrentNode(startLiteral)!, CountLiteral(start).WithTriviaFrom(startLiteral));
			if (span != (isRow ? child.RowSpan : child.ColumnSpan)) {
				// A span can only grow across the insertion, so the child spans two or more
				// tracks already and its span statement was proven to exist.
				var spanLiteral = (isRow ? child.RowSpanLiteral : child.ColumnSpanLiteral)!;
				root = root.ReplaceNode(root.GetCurrentNode(spanLiteral)!, CountLiteral(span).WithTriviaFrom(spanLiteral));
			}
		}
		var styleStatement = root.GetCurrentNode(proof.Styles[index])!;
		root = insert
			? root.ReplaceNode(styleStatement, new[] { StyleStatement(elementId, isRow, type, value).WithTriviaFrom(proof.Styles[index]), styleStatement })
			: root.RemoveNode(styleStatement, SyntaxRemoveOptions.KeepNoTrivia)!;

		using (var transaction = host.CreateTransaction($"{(insert ? "Insert" : "Remove")} {(isRow ? "row" : "column")} {index} of {elementId}")) {
			if (insert) {
				if (isRow) {
					table.RowCount = count + 1;
					table.RowStyles.Insert(index, new RowStyle(type, (float)value));
				} else {
					table.ColumnCount = count + 1;
					table.ColumnStyles.Insert(index, new ColumnStyle(type, (float)value));
				}
			}
			foreach (var (child, start, span) in moves) {
				table.SetCellPosition(child.Control, isRow
					? new TableLayoutPanelCellPosition(child.Column, start)
					: new TableLayoutPanelCellPosition(start, child.Row));
				if (isRow)
					table.SetRowSpan(child.Control, span);
				else
					table.SetColumnSpan(child.Control, span);
			}
			if (!insert) {
				styles.RemoveAt(index);
				if (isRow)
					table.RowCount = count - 1;
				else
					table.ColumnCount = count - 1;
			}
			transaction.Commit();
		}
		file.Text = root.ToFullString();
		return CurrentState(baseVersion);
	}

	sealed record TableLayoutChild(Control Control, string Name, int Column, int Row, int ColumnSpan, int RowSpan,
		SyntaxNode ColumnLiteral, SyntaxNode RowLiteral, SyntaxNode? ColumnSpanLiteral, SyntaxNode? RowSpanLiteral);

	static readonly HashSet<string> TableLayoutPlacementMembers = new(StringComparer.Ordinal) {
		"Controls", "SetColumn", "SetRow", "SetCellPosition", "SetColumnSpan", "SetRowSpan"
	};

	/// <summary>Proves where every child of the table is placed: InitializeComponent places each
	/// live child with exactly one <c>&lt;id&gt;.Controls.Add(child, column, row)</c> of integer
	/// literals, optionally sets its span with one <c>SetColumnSpan/SetRowSpan(child, n)</c>
	/// literal, and contains no other placement statement for the table (SetColumn, SetRow,
	/// SetCellPosition, AddRange, an unpositioned Add...). The literals must equal the live cell
	/// positions and spans, so rewriting them moves exactly the control the user sees.</summary>
	List<TableLayoutChild> ProveTableLayoutChildren(SyntaxNode root, TableLayoutPanel table, string elementId)
	{
		var unprovable = new InvalidOperationException($"The placement of {elementId}'s controls cannot be edited safely.");
		var statements = IsVisualBasic
			? root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().First(item =>
				item.BlockStatement is VbSyntax.MethodStatementSyntax ms && ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent").Statements.Cast<SyntaxNode>().ToList()
			: root.DescendantNodes().OfType<MethodDeclarationSyntax>()
				.First(item => item.Identifier.ValueText == "InitializeComponent" && item.Body != null).Body!.Statements.Cast<SyntaxNode>().ToList();
		var adds = new Dictionary<string, (SyntaxNode Column, SyntaxNode Row)>(StringComparer.Ordinal);
		var columnSpans = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
		var rowSpans = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
		foreach (var statement in statements) {
			if (!MentionsTablePlacement(statement, elementId))
				continue;
			if (TableCall(statement, elementId) is not { } call)
				throw unprovable;
			if (call is { Collection: "Controls", Method: "Add", Arguments: [var child, var column, var row] }
				&& NodeTargetName(child) is { } addName && IntLiteral(column) != null && IntLiteral(row) != null
				&& adds.TryAdd(addName, (column, row)))
				continue;
			if (call is { Collection: null, Method: "SetColumnSpan" or "SetRowSpan", Arguments: [var spanned, var span] }
				&& NodeTargetName(spanned) is { } spanName && IntLiteral(span) is > 0
				&& (call.Method == "SetColumnSpan" ? columnSpans : rowSpans).TryAdd(spanName, span))
				continue;
			throw unprovable;
		}
		var result = new List<TableLayoutChild>();
		foreach (Control control in table.Controls) {
			var name = control.Site?.Name;
			if (name == null || !adds.TryGetValue(name, out var cell))
				throw unprovable;
			var position = table.GetCellPosition(control);
			var columnSpan = columnSpans.TryGetValue(name, out var columnSpanLiteral) ? IntLiteral(columnSpanLiteral)!.Value : 1;
			var rowSpan = rowSpans.TryGetValue(name, out var rowSpanLiteral) ? IntLiteral(rowSpanLiteral)!.Value : 1;
			if (position.Column != IntLiteral(cell.Column) || position.Row != IntLiteral(cell.Row)
				|| table.GetColumnSpan(control) != columnSpan || table.GetRowSpan(control) != rowSpan)
				throw unprovable;
			result.Add(new TableLayoutChild(control, name, position.Column, position.Row, columnSpan, rowSpan,
				cell.Column, cell.Row, columnSpanLiteral, rowSpanLiteral));
		}
		if (adds.Count != result.Count || columnSpans.Keys.Concat(rowSpans.Keys).Any(name => !adds.ContainsKey(name)))
			throw unprovable;
		return result;
	}

	bool MentionsTablePlacement(SyntaxNode statement, string elementId) => IsVisualBasic
		? statement.DescendantNodes().OfType<VbSyntax.MemberAccessExpressionSyntax>().Any(access => access.Expression != null
			&& TableLayoutPlacementMembers.Contains(access.Name.Identifier.ValueText) && VbSimpleTargetName(access.Expression) == elementId)
		: statement.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(access =>
			TableLayoutPlacementMembers.Contains(access.Name.Identifier.ValueText) && SimpleTargetName(access.Expression) == elementId);

	/// <summary>A statement that is exactly one call on the table: <c>&lt;id&gt;.Method(args)</c>
	/// (Collection null) or <c>&lt;id&gt;.Controls.Method(args)</c>.</summary>
	(string Method, string? Collection, IReadOnlyList<SyntaxNode> Arguments)? TableCall(SyntaxNode statement, string elementId)
	{
		if (IsVisualBasic) {
			if (statement is not VbSyntax.ExpressionStatementSyntax { Expression: VbSyntax.InvocationExpressionSyntax { Expression: VbSyntax.MemberAccessExpressionSyntax method } invocation }
				|| method.Expression == null || invocation.ArgumentList == null
				|| invocation.ArgumentList.Arguments.Any(argument => argument is not VbSyntax.SimpleArgumentSyntax { IsNamed: false }))
				return null;
			var arguments = invocation.ArgumentList.Arguments.Cast<VbSyntax.SimpleArgumentSyntax>().Select(argument => (SyntaxNode)argument.Expression).ToList();
			if (VbSimpleTargetName(method.Expression) == elementId && method.Expression is not VbSyntax.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Controls" })
				return (method.Name.Identifier.ValueText, null, arguments);
			if (method.Expression is VbSyntax.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Controls", Expression: { } owner } && VbSimpleTargetName(owner) == elementId)
				return (method.Name.Identifier.ValueText, "Controls", arguments);
			return null;
		}
		if (statement is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call }
			|| call.ArgumentList.Arguments.Any(argument => argument.NameColon != null || !argument.RefKindKeyword.IsKind(SyntaxKind.None)))
			return null;
		var values = call.ArgumentList.Arguments.Select(argument => (SyntaxNode)argument.Expression).ToList();
		if (SimpleTargetName(access.Expression) == elementId && access.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Controls" })
			return (access.Name.Identifier.ValueText, null, values);
		if (access.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Controls" } controls && SimpleTargetName(controls.Expression) == elementId)
			return (access.Name.Identifier.ValueText, "Controls", values);
		return null;
	}

	static string? NodeTargetName(SyntaxNode node) => node switch {
		ExpressionSyntax expression => SimpleTargetName(expression),
		VbSyntax.ExpressionSyntax expression => VbSimpleTargetName(expression),
		_ => null
	};

	static int? IntLiteral(SyntaxNode node) => node switch {
		LiteralExpressionSyntax { Token.Value: int value } => value,
		VbSyntax.LiteralExpressionSyntax { Token.Value: int value } => value,
		_ => null
	};

	SyntaxNode CountLiteral(int value) => IsVisualBasic
		? Vb.SyntaxFactory.ParseExpression(value.ToString(CultureInfo.InvariantCulture))
		: SyntaxFactory.ParseExpression(value.ToString(CultureInfo.InvariantCulture));

	SyntaxNode StyleStatement(string elementId, bool isRow, SizeType type, double value)
	{
		var member = isRow ? "RowStyles" : "ColumnStyles";
		var styleType = isRow ? "RowStyle" : "ColumnStyle";
		return IsVisualBasic
			? Vb.SyntaxFactory.ParseExecutableStatement($"Me.{elementId}.{member}.Add(New System.Windows.Forms.{styleType}({StyleArguments(type, value)}))")
			: SyntaxFactory.ParseStatement($"this.{elementId}.{member}.Add(new System.Windows.Forms.{styleType}({StyleArguments(type, value)}));");
	}

	/// <summary>Proves that InitializeComponent authors the whole track definition: one plain style
	/// Add per live style (at least one) and exactly one integer-literal Count assignment equal to the
	/// live count, with as many styles as tracks. Returns the parsed root, the style statements in
	/// order and the Count literal.</summary>
	(SyntaxNode Root, List<SyntaxNode> Styles, SyntaxNode CountLiteral) ProveTableLayoutTrackSource(string text, string elementId, bool isRow, int liveStyles, int liveCount)
	{
		var member = isRow ? "RowStyles" : "ColumnStyles";
		var styleType = isRow ? "RowStyle" : "ColumnStyle";
		var countMember = isRow ? "RowCount" : "ColumnCount";
		var unprovable = new InvalidOperationException($"{elementId}'s {(isRow ? "rows" : "columns")} are not an authored track list that can be edited safely.");
		if (liveStyles != liveCount || liveStyles == 0)
			throw unprovable;
		SyntaxNode root;
		List<SyntaxNode> styles;
		SyntaxNode? literal = null;
		if (IsVisualBasic) {
			root = Vb.VisualBasicSyntaxTree.ParseText(text).GetRoot();
			var method = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().FirstOrDefault(item =>
				item.BlockStatement is VbSyntax.MethodStatementSyntax ms && ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent") ?? throw unprovable;
			bool Mentions(VbSyntax.StatementSyntax statement, string name) => statement.DescendantNodes().OfType<VbSyntax.MemberAccessExpressionSyntax>()
				.Any(access => access.Name.Identifier.ValueText == name && access.Expression != null && VbSimpleTargetName(access.Expression) == elementId);
			var styleStatements = method.Statements.Where(statement => Mentions(statement, member)).ToList();
			if (!styleStatements.All(statement => IsVbPlainStyleAdd(statement, elementId, member, styleType)))
				throw unprovable;
			styles = styleStatements.Cast<SyntaxNode>().ToList();
			var countStatements = method.Statements.Where(statement => Mentions(statement, countMember)).ToList();
			if (countStatements is [VbSyntax.AssignmentStatementSyntax {
				Left: VbSyntax.MemberAccessExpressionSyntax { Expression: not null } left,
				Right: VbSyntax.LiteralExpressionSyntax { Token.Value: int value } right } assignment]
				&& assignment.IsKind(Vb.SyntaxKind.SimpleAssignmentStatement)
				&& left.Name.Identifier.ValueText == countMember && VbSimpleTargetName(left.Expression!) == elementId && value == liveCount)
				literal = right;
		} else {
			root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
			var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
				.FirstOrDefault(item => item.Identifier.ValueText == "InitializeComponent" && item.Body != null) ?? throw unprovable;
			bool Mentions(StatementSyntax statement, string name) => statement.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
				.Any(access => access.Name.Identifier.ValueText == name && SimpleTargetName(access.Expression) == elementId);
			var styleStatements = method.Body!.Statements.Where(statement => Mentions(statement, member)).ToList();
			if (!styleStatements.All(statement => IsPlainStyleAdd(statement, elementId, member, styleType)))
				throw unprovable;
			styles = styleStatements.Cast<SyntaxNode>().ToList();
			var countStatements = method.Body.Statements.Where(statement => Mentions(statement, countMember)).ToList();
			if (countStatements is [ExpressionStatementSyntax {
				Expression: AssignmentExpressionSyntax {
					Left: MemberAccessExpressionSyntax left,
					Right: LiteralExpressionSyntax { Token.Value: int value } right } assignment }]
				&& assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
				&& left.Name.Identifier.ValueText == countMember && SimpleTargetName(left.Expression) == elementId && value == liveCount)
				literal = right;
		}
		if (literal == null || styles.Count != liveStyles)
			throw unprovable;
		return (root, styles, literal);
	}

	/// <summary>Changes one row/column's SizeType, choosing its length as the TableLayoutPanel
	/// "Edit Rows and Columns" user would: Absolute takes the track's current measured size, so
	/// the layout does not jump; Percent takes the average of the other Percent tracks (100 when
	/// there are none), so it joins them as an equal share; AutoSize has no length. Choosing the
	/// SizeType it already has is refused rather than recorded as an empty edit.</summary>
	[JsonRpcMethod("design/set-table-layout-track-unit")]
	public DesignerSessionState SetTableLayoutTrackUnit(string sessionId, string documentId, long baseVersion, string elementId, bool isRow, int index, string sizeType)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		if (!Enum.TryParse<SizeType>(sizeType, ignoreCase: false, out var type) || !Enum.IsDefined(type))
			throw new ArgumentException("Unknown SizeType: " + sizeType, nameof(sizeType));
		var (host, table) = FindTableLayoutPanel(elementId);
		var styles = isRow ? (TableLayoutStyleCollection)table.RowStyles : table.ColumnStyles;
		var measured = isRow ? table.GetRowHeights() : table.GetColumnWidths();
		if (index < 0 || index >= styles.Count || index >= measured.Length)
			throw new ArgumentOutOfRangeException(nameof(index));
		if (styles[index].SizeType == type)
			throw new InvalidOperationException($"{(isRow ? "Row" : "Column")} {index} of {elementId} is already {type}.");
		var others = styles.Cast<TableLayoutStyle>().Where((style, position) => position != index && style.SizeType == SizeType.Percent)
			.Select(style => (double)StyleLength(style)).ToList();
		var value = type switch {
			SizeType.Absolute => (double)measured[index],
			SizeType.Percent => others.Count == 0 ? 100 : Math.Round(others.Average(), 2),
			_ => 0
		};
		ApplyTableLayoutStyles(host, table, elementId, isRow, new[] { (index, type, value) });
		return CurrentState(baseVersion);
	}

	static float StyleLength(TableLayoutStyle style) => style is RowStyle row ? row.Height : ((ColumnStyle)style).Width;

	(IDesignerHost Host, TableLayoutPanel Table) FindTableLayoutPanel(string elementId)
	{
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost
			?? throw new InvalidOperationException("The designer surface is unavailable.");
		if (host.Container.Components[elementId] is not TableLayoutPanel table)
			throw new ArgumentException("TableLayoutPanel not found: " + elementId, nameof(elementId));
		return (host, table);
	}

	/// <summary>Sets authored TableLayoutPanel row/column styles. Accepted only when
	/// InitializeComponent proves the style collection: exactly one plain
	/// <c>&lt;id&gt;.RowStyles/ColumnStyles.Add(new RowStyle/ColumnStyle(...))</c> statement per live
	/// style. WinForms creates default styles at runtime that have no source, so a live style alone is
	/// never treated as authored. Everything is validated and proven before anything changes; the
	/// source statements are rewritten in place, then the live styles change in one transaction.</summary>
	void ApplyTableLayoutStyles(IDesignerHost host, TableLayoutPanel table, string elementId, bool isRow, IReadOnlyList<(int Index, SizeType Type, double Value)> edits)
	{
		var styles = isRow ? (TableLayoutStyleCollection)table.RowStyles : table.ColumnStyles;
		foreach (var (index, type, value) in edits) {
			if (index < 0 || index >= styles.Count)
				throw new ArgumentOutOfRangeException(nameof(index));
			if (type != SizeType.AutoSize && (!Double.IsFinite(value) || value < 0 || value > Single.MaxValue))
				throw new ArgumentOutOfRangeException(nameof(value));
		}
		var member = isRow ? "RowStyles" : "ColumnStyles";
		var styleType = isRow ? "RowStyle" : "ColumnStyle";

		var file = CurrentDesignerFile();
		var newText = IsVisualBasic
			? RewriteTableLayoutStylesVisualBasic(file.Text, elementId, member, styleType, styles.Count, edits)
			: RewriteTableLayoutStyles(file.Text, elementId, member, styleType, styles.Count, edits);

		using (var transaction = host.CreateTransaction($"Edit {elementId}.{member}")) {
			foreach (var (index, type, value) in edits) {
				var style = styles[index];
				style.SizeType = type;
				if (type != SizeType.AutoSize) {
					if (style is RowStyle row)
						row.Height = (float)value;
					else
						((ColumnStyle)style).Width = (float)value;
				}
			}
			transaction.Commit();
		}
		file.Text = newText;
	}

	static string StyleArguments(SizeType type, double value) => type == SizeType.AutoSize
		? "System.Windows.Forms.SizeType.AutoSize"
		: $"System.Windows.Forms.SizeType.{type}, {((float)value).ToString("R", CultureInfo.InvariantCulture)}F";

	static string RewriteTableLayoutStyles(string text, string elementId, string member, string styleType, int liveCount, IReadOnlyList<(int Index, SizeType Type, double Value)> edits)
	{
		var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.FirstOrDefault(item => item.Identifier.ValueText == "InitializeComponent" && item.Body != null)
			?? throw new InvalidOperationException("InitializeComponent was not found.");
		// Any statement mentioning the collection must be a provable plain Add, otherwise the
		// source/live index mapping is unknown (Clear, Insert, RemoveAt, indexers, loops...).
		var mentions = method.Body!.Statements.Where(statement => statement.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
			.Any(access => access.Name.Identifier.ValueText == member && SimpleTargetName(access.Expression) == elementId)).ToList();
		if (mentions.Count != liveCount || !mentions.All(statement => IsPlainStyleAdd(statement, elementId, member, styleType)))
			throw new InvalidOperationException($"{elementId}.{member} is not an authored style list that can be edited safely.");
		var replacements = edits.ToDictionary(edit => (SyntaxNode)mentions[edit.Index], edit => (SyntaxNode)SyntaxFactory.ParseStatement(
			$"this.{elementId}.{member}.Add(new System.Windows.Forms.{styleType}({StyleArguments(edit.Type, edit.Value)}));")
			.WithTriviaFrom(mentions[edit.Index]));
		return root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]).ToFullString();
	}

	static string RewriteTableLayoutStylesVisualBasic(string text, string elementId, string member, string styleType, int liveCount, IReadOnlyList<(int Index, SizeType Type, double Value)> edits)
	{
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(text).GetRoot();
		var method = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().FirstOrDefault(item =>
			item.BlockStatement is VbSyntax.MethodStatementSyntax ms && ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
			&& ms.Identifier.ValueText == "InitializeComponent")
			?? throw new InvalidOperationException("InitializeComponent was not found.");
		var mentions = method.Statements.Where(statement => statement.DescendantNodes().OfType<VbSyntax.MemberAccessExpressionSyntax>()
			.Any(access => access.Name.Identifier.ValueText == member && access.Expression != null && VbSimpleTargetName(access.Expression) == elementId)).ToList();
		if (mentions.Count != liveCount || !mentions.All(statement => IsVbPlainStyleAdd(statement, elementId, member, styleType)))
			throw new InvalidOperationException($"{elementId}.{member} is not an authored style list that can be edited safely.");
		var replacements = edits.ToDictionary(edit => (SyntaxNode)mentions[edit.Index], edit => (SyntaxNode)Vb.SyntaxFactory.ParseExecutableStatement(
			$"Me.{elementId}.{member}.Add(New System.Windows.Forms.{styleType}({StyleArguments(edit.Type, edit.Value)}))")
			.WithTriviaFrom(mentions[edit.Index]));
		return root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]).ToFullString();
	}

	static bool IsVbPlainStyleAdd(VbSyntax.StatementSyntax statement, string elementId, string member, string styleType)
	{
		if (statement is not VbSyntax.ExpressionStatementSyntax {
			Expression: VbSyntax.InvocationExpressionSyntax {
				Expression: VbSyntax.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add" } add,
				ArgumentList.Arguments: [VbSyntax.SimpleArgumentSyntax { IsNamed: false, Expression: VbSyntax.ObjectCreationExpressionSyntax creation }] } }
			|| add.Expression is not VbSyntax.MemberAccessExpressionSyntax collection || collection.Expression == null
			|| collection.Name.Identifier.ValueText != member || VbSimpleTargetName(collection.Expression) != elementId
			|| creation.Initializer != null)
			return false;
		var typeName = creation.Type switch {
			VbSyntax.QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
			VbSyntax.IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
			_ => null
		};
		return typeName == styleType && (creation.ArgumentList?.Arguments.Count ?? 0) <= 2;
	}

	static bool IsPlainStyleAdd(StatementSyntax statement, string elementId, string member, string styleType)
	{
		if (statement is not ExpressionStatementSyntax {
			Expression: InvocationExpressionSyntax {
				Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add" } add,
				ArgumentList.Arguments: [{ Expression: ObjectCreationExpressionSyntax creation }] } }
			|| add.Expression is not MemberAccessExpressionSyntax collection
			|| collection.Name.Identifier.ValueText != member || SimpleTargetName(collection.Expression) != elementId
			|| creation.Initializer != null)
			return false;
		var typeName = creation.Type switch {
			QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
			IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
			_ => null
		};
		return typeName == styleType && (creation.ArgumentList?.Arguments.Count ?? 0) <= 2;
	}

	[JsonRpcMethod("design/set-property")]
	public DesignerSessionState SetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName, string value)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost
			?? throw new InvalidOperationException("The designer surface is unavailable.");
		var component = host.Container.Components.Cast<IComponent>()
			.FirstOrDefault(item => String.Equals(item.Site?.Name, elementId, StringComparison.Ordinal))
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var isRootComponent = component == host.RootComponent;
		var property = TypeDescriptor.GetProperties(component)[propertyName]
			?? throw new ArgumentException("Property not found: " + propertyName, nameof(propertyName));
		// Keep the image mutation boundary aligned with DescribeProperties: an image is exposed as
		// an opaque Unsupported value even though the underlying WinForms descriptor is writable.
		// It must never accept the display token as a scalar edit.
		if (property.IsReadOnly || IsResourceImageProperty(property.PropertyType))
			throw new InvalidOperationException($"Property {elementId}.{propertyName} is read-only.");
		var referenceCandidates = ComponentReferenceCandidates(host, component, property);
		// A System.Type is typed as its full name and resolved like the designer source's own type
		// references (framework, then the project assembly); empty clears it. Generic and nested
		// types are refused: their source spelling would not resolve back when the file reloads.
		var converted = property.PropertyType == typeof(Type)
			? String.IsNullOrWhiteSpace(value) ? null
				: snapshotLoader?.ResolveType(value.Trim()) is { IsGenericType: false, IsNested: false } resolvedType ? resolvedType
				: throw new ArgumentException($"Unknown type: {value}", nameof(value))
			: referenceCandidates == null ? ConvertPropertyValue(property, value)
			: String.IsNullOrEmpty(value) ? null
			: referenceCandidates.FirstOrDefault(candidate => String.Equals(candidate.Site?.Name, value, StringComparison.Ordinal))
				?? throw new ArgumentException($"Unknown component reference: {value}", nameof(value));
		if (component == host.RootComponent && propertyName == "AutoScaleDimensions" && converted is SizeF scale)
			rootAutoScaleDimensions = scale;
		// Validate source serialization before mutating the live component. A
		// failed complex-property serializer must not split live and source state.
		_ = SerializeValue(converted);
#if !MICROSOFT_WINFORMS
		// Shadow Visible instead of applying it: record what the source will say, keep the control
		// on the surface. See shadowedVisible for why this is Libre-only.
		if (IsShadowedVisible(component, propertyName) && converted is bool visible) {
			shadowedVisible[elementId] = visible;
			RewriteProperty(elementId, propertyName, converted, isRootComponent);
			return CurrentState(baseVersion);
		}
#endif
		using (var transaction = host.CreateTransaction($"Set {elementId}.{propertyName}")) {
			property.SetValue(component, converted);
			transaction.Commit();
		}
		snapshotLoader?.ClearResourceOrigin(component, propertyName);
		RewriteProperty(elementId, propertyName, converted, isRootComponent);
		return CurrentState(baseVersion);
	}

#if !MICROSOFT_WINFORMS
	/// <summary>Whether this property assignment should be shadowed rather than applied.
	///
	/// A TabPage is excluded deliberately: its Visible is driven by the TabControl itself (that is
	/// precisely what makes an unselected page's children report invisible), so shadowing it would
	/// leave every page showing at once. The design root is excluded because hiding it is not a
	/// meaningful design-time edit and the surface would have nothing to render.</summary>
	bool IsShadowedVisible(IComponent component, string propertyName)
		=> propertyName == "Visible"
			&& component is Control control
			&& control is not TabPage
			&& !ReferenceEquals(component, GetHost().RootComponent);
#endif

	static object ConvertPropertyValue(PropertyDescriptor property, string value)
	{
		if (property.PropertyType == typeof(Padding)) {
			var parts = value.Split(',').Select(item => Int32.Parse(item.Trim(), CultureInfo.InvariantCulture)).ToArray();
			if (parts.Length == 1) return new Padding(parts[0]);
			if (parts.Length == 4) return new Padding(parts[0], parts[1], parts[2], parts[3]);
			throw new FormatException("Padding requires one or four comma-separated integers.");
		}
		if (property.PropertyType == typeof(Font)) {
			var parts = value.Split(',').Select(item => item.Trim()).ToArray();
			if (parts.Length < 2) throw new FormatException("Font requires 'family, size[, style]'.");
			var style = parts.Length > 2 ? Enum.Parse<FontStyle>(parts[2], true) : FontStyle.Regular;
			return new Font(parts[0], Single.Parse(parts[1], CultureInfo.InvariantCulture), style);
		}
		if (property.PropertyType == typeof(SizeF)) {
			var parts = value.Split(',').Select(item => Single.Parse(item.Trim(), CultureInfo.InvariantCulture)).ToArray();
			if (parts.Length != 2) throw new FormatException("SizeF requires two comma-separated numbers.");
			return new SizeF(parts[0], parts[1]);
		}
		return property.Converter.ConvertFromInvariantString(value)
			?? throw new InvalidOperationException($"Cannot convert '{value}' to {property.PropertyType.FullName}.");
	}

	static List<IComponent>? ComponentReferenceCandidates(IDesignerHost host, IComponent owner, PropertyDescriptor property,
		Dictionary<Type, List<IComponent>>? candidatesByType = null)
	{
		if (property.PropertyType == typeof(object))
			return null;
		if (candidatesByType?.TryGetValue(property.PropertyType, out var cached) == true)
			return cached.Where(candidate => candidate != owner).ToList() is { Count: > 0 } eligible ? eligible : null;
		var candidates = host.Container.Components.Cast<IComponent>()
			.Where(candidate => candidate != host.RootComponent && property.PropertyType.IsAssignableFrom(candidate.GetType())
				&& !String.IsNullOrEmpty(candidate.Site?.Name)).ToList();
		candidatesByType?.Add(property.PropertyType, candidates);
		candidates = candidates.Where(candidate => candidate != owner).ToList();
		return candidates.Count == 0 ? null : candidates;
	}

	[JsonRpcMethod("design/reset-property")]
	public DesignerSessionState ResetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "reset property on");
		var host = GetHost();
		var component = host.Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var property = TypeDescriptor.GetProperties(component)[propertyName]
			?? throw new ArgumentException("Property not found: " + propertyName, nameof(propertyName));
		// LibreWinForms currently returns false from CanResetValue for some properties
		// (for example Enabled) even after they have been explicitly serialized.
		if (property.IsReadOnly || (!property.CanResetValue(component) && !property.ShouldSerializeValue(component)))
			throw new InvalidOperationException($"Property {elementId}.{propertyName} cannot be reset.");
		using (var transaction = host.CreateTransaction($"Reset {elementId}.{propertyName}")) {
			property.ResetValue(component);
			// Some LibreWinForms descriptors expose DefaultValueAttribute but their
			// ResetValue implementation is currently a no-op.
			if (property.ShouldSerializeValue(component)
				&& property.Attributes[typeof(DefaultValueAttribute)] is DefaultValueAttribute defaultValue)
				property.SetValue(component, defaultValue.Value);
			if (property.ShouldSerializeValue(component) && TryGetDefaultPropertyValue(component, property, out var freshDefault))
				property.SetValue(component, freshDefault);
			transaction.Commit();
		}
		snapshotLoader?.ClearResourceOrigin(component, propertyName);
		RewriteResetProperty(elementId, propertyName, component == host.RootComponent);
		return CurrentState(baseVersion);
	}

	static bool TryGetDefaultPropertyValue(IComponent component, PropertyDescriptor property, out object? value)
	{
		value = null;
		object? fresh = null;
		try {
			fresh = Activator.CreateInstance(component.GetType());
			if (fresh == null) return false;
			var freshProperty = TypeDescriptor.GetProperties(fresh)[property.Name];
			if (freshProperty == null) return false;
			value = freshProperty.GetValue(fresh);
			return true;
		} catch {
			return false;
		} finally {
			(fresh as IDisposable)?.Dispose();
		}
	}

	[JsonRpcMethod("design/rename")]
	public DesignerSessionState RenameComponent(string sessionId, string documentId, long baseVersion, string elementId, string newName)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "rename");
		if (!IsValidIdentifier(newName))
			throw new ArgumentException("A valid component name is required.", nameof(newName));
		var host = GetHost();
		var component = host.Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		if (component == host.RootComponent) throw new InvalidOperationException("The root component cannot be renamed here.");
		if (host.Container.Components[newName] != null) throw new ArgumentException("A component with that name already exists: " + newName, nameof(newName));
		RewriteComponentName(elementId, newName);
		// Control.Name and ToolStripItem.Name are both real, separately-serialized design-time
		// properties (a "foo.Name = "oldName";" statement, distinct from the field/identifier
		// RewriteComponentName above already renamed) - not just Control's. Missing the
		// ToolStripItem case left its Name property statement holding the stale OLD name as a
		// string literal after every identifier reference had already moved to the new one.
		if (component is Control or ToolStripItem) RewriteProperty(newName, "Name", newName);
		CreateDesignSurface(current!);
		return CurrentState(baseVersion);
	}

	[JsonRpcMethod("design/set-event")]
	public DesignerSessionState SetEvent(string sessionId, string documentId, long baseVersion, string elementId, string eventName, string handlerName)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		if (!String.IsNullOrEmpty(handlerName) && !IsValidIdentifier(handlerName))
			throw new ArgumentException("A valid event handler name is required.", nameof(handlerName));
		var component = GetHost().Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var descriptor = TypeDescriptor.GetEvents(component)[eventName]
			?? throw new ArgumentException("Event not found: " + eventName, nameof(eventName));
		RewriteEvent(elementId, descriptor, handlerName);
		return CurrentState(baseVersion);
	}

	[JsonRpcMethod("design/activate-default-event")]
	public DesignerSessionState ActivateDefaultEvent(string sessionId, string documentId, long baseVersion, string elementId)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "activate the default event on");
		var component = GetHost().Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var attribute = TypeDescriptor.GetAttributes(component)[typeof(DefaultEventAttribute)] as DefaultEventAttribute;
		var eventName = attribute?.Name;
		// LibreWinForms does not yet expose the framework DefaultEventAttribute on
		// several standard controls. Preserve the established WinForms behavior.
		if (String.IsNullOrEmpty(eventName))
			eventName = component is Form ? "Load" : "Click";
		var descriptor = String.IsNullOrEmpty(eventName) ? null : TypeDescriptor.GetEvents(component)[eventName];
		if (descriptor == null)
			throw new InvalidOperationException($"Component {elementId} has no default event.");
		var existing = DescribeEvents(component, BuildEventHandlerIndex()).FirstOrDefault(item => item.Name == descriptor.Name)?.Handler;
		RewriteEvent(elementId, descriptor, String.IsNullOrEmpty(existing) ? elementId + "_" + descriptor.Name : existing);
		return CurrentState(baseVersion);
	}

	[JsonRpcMethod("design/add-element")]
	public DesignerSessionState AddControl(string sessionId, string documentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, string proposedName, int x, int y, DesignerDropTarget dropTarget)
	{
		var elementId = proposedName;
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		if (!IsValidIdentifier(elementId))
			throw new ArgumentException("A valid component name is required.", nameof(elementId));
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost
			?? throw new InvalidOperationException("The designer surface is unavailable.");
		if (host.Container.Components[elementId] != null)
			throw new ArgumentException("A component with that name already exists: " + elementId, nameof(elementId));
		if (ResolveTrayComponentType(item?.TypeName) is { } componentType) {
			// A non-visual component (Timer, ToolTip, ImageList...) has no parent or bounds: it
			// joins the component tray, as the in-process designer's toolbox drop did.
			using (var transaction = host.CreateTransaction("Add " + elementId)) {
				host.CreateComponent(componentType, elementId);
				transaction.Commit();
			}
			RewriteAddedComponent(componentType, elementId);
			return CurrentState(baseVersion);
		}
		var parent = host.Container.Components[parentId] as Control
			?? throw new ArgumentException("Parent control not found: " + parentId, nameof(parentId));
		var type = ResolveControlType(item?.TypeName);
		Control control;
		using (var transaction = host.CreateTransaction("Add " + elementId)) {
			control = (Control)host.CreateComponent(type, elementId);
			if (control.Width <= 0 || control.Height <= 0)
				control.Size = type == typeof(NumericUpDown) ? new Size(120, 20) : new Size(75, 23);
			control.Location = new Point(x, y);
			parent.Controls.Add(control);
			transaction.Commit();
		}
		RewriteAddedControl(parentId, type, elementId, x, y, control.Width, control.Height);
		return CurrentState(baseVersion);
	}

	[JsonRpcMethod("design/set-bounds")]
	public DesignerSessionState SetBounds(string sessionId, string documentId, long baseVersion, string elementId, int x, int y, int width, int height)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Control bounds must be positive.");
		var host = GetHost();
		var control = host.Container.Components[elementId] as Control
			?? throw new ArgumentException("Control not found: " + elementId, nameof(elementId));
		if (control == host.RootComponent) {
#if !MICROSOFT_WINFORMS
			// Incoming root bounds are OUTER bounds (that is what gets reported and what the
			// selection/resize handle track). RewriteRootSize persists them into the designer
			// file's Size/ClientSize slot, which ReadRootDesignSize reads back as the portable
			// host's DESIGN size - so the simulated title bar has to come back off here, or every
			// resize drag would add another PortableFormTitleBarHeight to the form.
			if (control is Form)
				height = Math.Max(1, height - PortableFormTitleBarHeight);
#endif
			RewriteRootSize(width, height);
			CreateDesignSurface(current!);
			return CurrentState(baseVersion);
		}
		using (var transaction = host.CreateTransaction("Set bounds " + elementId)) {
			control.Bounds = new Rectangle(x, y, width, height);
			transaction.Commit();
		}
		ClearResourceOriginsForBounds(control);
		RewriteBounds(elementId, x, y, width, height);
		return CurrentState(baseVersion);
	}

	[JsonRpcMethod("design/delete-elements")]
	public DesignerSessionState DeleteElements(string sessionId, string documentId, long baseVersion, string[] elementIds)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		var host = GetHost();
		if (elementIds == null || elementIds.Length == 0)
			return Rejected(baseVersion, "Select at least one component to delete.");
		if (elementIds.Any(String.IsNullOrWhiteSpace))
			return Rejected(baseVersion, "Every component selected for deletion must have an identifier.");

		// Resolve and validate every name before the first DestroyComponent call. In particular,
		// do not let a later missing/root component turn an otherwise ordinary multi-selection
		// delete into a partially committed source edit.
		var selected = new Dictionary<string, IComponent>(StringComparer.Ordinal);
		foreach (var elementId in elementIds.Distinct(StringComparer.Ordinal)) {
			var component = host.Container.Components[elementId];
			if (component == null)
				return Rejected(baseVersion, "Component not found: " + elementId);
			if (component == host.RootComponent)
				return Rejected(baseVersion, "The root component cannot be deleted.");
			selected[elementId] = component;
		}
		if (selected.Count == 0)
			return Rejected(baseVersion, "Select at least one component to delete.");

		// A selected child is already destroyed with a selected parent. Only destroy the outer
		// roots, but collect their complete live descendant set before doing so: every declaration
		// and collection attachment for those descendants must leave the designer source too.
		var roots = selected.Values.Where(component => !HasSelectedAncestor(component, selected)).ToArray();
		var deletedElementIds = host.Container.Components.Cast<IComponent>()
			.Where(component => roots.Any(root => ReferenceEquals(component, root) || HasAncestor(component, root)))
			.Select(component => component.Site?.Name)
			.Where(name => !String.IsNullOrEmpty(name))
			.OrderByDescending(name => ComponentDepth(selected.TryGetValue(name!, out var component) ? component : host.Container.Components[name!]))
			.ToArray();
		var designerFile = CurrentDesignerFile();
		var originalSource = designerFile.Text;
		try {
			using (var transaction = host.CreateTransaction("Delete " + String.Join(", ", roots.Select(component => component.Site?.Name)))) {
				foreach (var component in roots)
					host.DestroyComponent(component);
				transaction.Commit();
			}
			foreach (var elementId in deletedElementIds)
				RewriteDeletedComponent(elementId!);
			return CurrentState(baseVersion);
		} catch (Exception exception) {
			// DestroyComponent transactions are not proven to restore all nested Control and
			// ToolStrip state on every backend. Restore the source first, then rebuild from that
			// authority rather than returning a rejection backed by a partially deleted live tree.
			designerFile.Text = originalSource;
			try {
				CreateDesignSurface(current!);
			} catch (Exception recoveryException) {
				return Rejected(baseVersion, "Delete failed and the design surface could not be restored: " + recoveryException.Message);
			}
			return Rejected(baseVersion, "Delete failed: " + exception.Message);
		}
	}

	static bool HasSelectedAncestor(IComponent component, Dictionary<string, IComponent> selected)
	{
		for (var parent = ComponentParent(component); parent != null; parent = ComponentParent(parent)) {
			if (parent.Site?.Name is { Length: > 0 } name && selected.ContainsKey(name))
				return true;
		}
		return false;
	}

	static bool HasAncestor(IComponent component, IComponent ancestor)
	{
		for (var parent = ComponentParent(component); parent != null; parent = ComponentParent(parent)) {
			if (ReferenceEquals(parent, ancestor)) return true;
		}
		return false;
	}

	static int ComponentDepth(IComponent? component)
	{
		var depth = 0;
		for (var parent = component == null ? null : ComponentParent(component); parent != null; parent = ComponentParent(parent))
			depth++;
		return depth;
	}

	static IComponent? ComponentParent(IComponent component) => component switch {
		Control control => control.Parent,
		ToolStripItem item when item.OwnerItem != null => item.OwnerItem,
		ToolStripItem item when item.Owner != null => item.Owner,
		_ => null
	};

	[JsonRpcMethod("design/set-z-order")]
	public DesignerSessionState SetZOrder(string sessionId, string documentId, long baseVersion, string elementId, bool bringToFront)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "change z-order for");
		var host = GetHost();
		var control = host.Container.Components[elementId] as Control
			?? throw new ArgumentException("Control not found: " + elementId, nameof(elementId));
		if (control == host.RootComponent || control.Parent == null)
			throw new InvalidOperationException("The root component z-order cannot be changed.");
		using (var transaction = host.CreateTransaction((bringToFront ? "Bring to front " : "Send to back ") + elementId)) {
			control.Parent.Controls.SetChildIndex(control, bringToFront ? 0 : control.Parent.Controls.Count - 1);
			transaction.Commit();
		}
		RewriteZOrder(control.Parent.Site?.Name ?? "", elementId, bringToFront ? 0 : control.Parent.Controls.Count - 1);
		return CurrentState(baseVersion);
	}

	[JsonRpcMethod("design/apply-layout")]
	public DesignerSessionState ApplyLayout(string sessionId, string documentId, long baseVersion, string operation, string[] elementIds, int deltaX, int deltaY)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "apply layout to");
		var host = GetHost();
		var controls = elementIds.Distinct(StringComparer.Ordinal)
			.Select(name => host.Container.Components[name] as Control
				?? throw new ArgumentException("Control not found: " + name, nameof(elementIds)))
			.Where(control => control != host.RootComponent).ToArray();
		if (controls.Length == 0) return CurrentState(baseVersion);
		if (controls.Select(control => control.Parent).Distinct().Count() != 1)
			throw new InvalidOperationException("Layout commands require controls with the same parent.");
		var primary = controls[0];
		using (var transaction = host.CreateTransaction("Remote layout " + operation)) {
				switch (operation) {
				case "move": foreach (var item in controls) item.Location = new Point(Math.Max(0, item.Left + deltaX), Math.Max(0, item.Top + deltaY)); break;
				case "align-left": foreach (var item in controls.Skip(1)) item.Left = primary.Left; break;
				case "align-right": foreach (var item in controls.Skip(1)) item.Left = primary.Right - item.Width; break;
				case "align-top": foreach (var item in controls.Skip(1)) item.Top = primary.Top; break;
				case "align-bottom": foreach (var item in controls.Skip(1)) item.Top = primary.Bottom - item.Height; break;
				case "align-horizontal-centers": foreach (var item in controls.Skip(1)) item.Left = primary.Left + (primary.Width - item.Width) / 2; break;
				case "align-vertical-centers": foreach (var item in controls.Skip(1)) item.Top = primary.Top + (primary.Height - item.Height) / 2; break;
				case "same-size": foreach (var item in controls.Skip(1)) item.Size = primary.Size; break;
				case "same-width": foreach (var item in controls.Skip(1)) item.Width = primary.Width; break;
				case "same-height": foreach (var item in controls.Skip(1)) item.Height = primary.Height; break;
				case "center-horizontal": foreach (var item in controls) item.Left = Math.Max(0, (item.Parent!.ClientSize.Width - item.Width) / 2); break;
				case "center-vertical": foreach (var item in controls) item.Top = Math.Max(0, (item.Parent!.ClientSize.Height - item.Height) / 2); break;
				case "snap-grid": foreach (var item in controls) item.Bounds = new Rectangle(Snap(item.Left), Snap(item.Top), Math.Max(8, Snap(item.Width)), Math.Max(8, Snap(item.Height))); break;
				case "horizontal-space-equal": SpaceEqually(controls, true); break;
				case "vertical-space-equal": SpaceEqually(controls, false); break;
				case "horizontal-space-increase": AdjustSpacing(controls, true, 8, false); break;
				case "horizontal-space-decrease": AdjustSpacing(controls, true, -8, false); break;
				case "horizontal-space-concatenate": AdjustSpacing(controls, true, 0, true); break;
				case "vertical-space-increase": AdjustSpacing(controls, false, 8, false); break;
				case "vertical-space-decrease": AdjustSpacing(controls, false, -8, false); break;
				case "vertical-space-concatenate": AdjustSpacing(controls, false, 0, true); break;
				default: throw new NotSupportedException("Unsupported remote layout operation: " + operation);
			}
			transaction.Commit();
		}
		foreach (var control in controls)
			ClearResourceOriginsForBounds(control);
		foreach (var control in controls)
			RewriteBounds(control.Site!.Name!, control.Left, control.Top, control.Width, control.Height);
		return CurrentState(baseVersion);
	}

	/// <summary>A bounds mutation writes the same source properties that localized forms can
	/// obtain through <c>ApplyResources</c>. The live value is now authoritative, so it must no
	/// longer be advertised as resource-backed until a fresh snapshot proves that again.</summary>
	void ClearResourceOriginsForBounds(Control control)
	{
		snapshotLoader?.ClearResourceOrigin(control, "Location");
		snapshotLoader?.ClearResourceOrigin(control, "Size");
		snapshotLoader?.ClearResourceOrigin(control, "Bounds");
		snapshotLoader?.ClearResourceOrigin(control, "ClientSize");
	}

	/// <summary>Generic smart-tag listing: works for any component with registered
	/// DesignerActionLists (VS calls this the "smart tag" - the chevron button at a selected
	/// component's top-right). LibreWinForms's portable fork has no
	/// System.ComponentModel.Design.DesignerActionService support, so this whole feature is
	/// Microsoft-backend only.</summary>
	[JsonRpcMethod("design/list-smart-tag-actions")]
	public DesignerSmartTagActions ListSmartTagActions(string sessionId, string documentId, long baseVersion, string elementId)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "list smart tag actions for");
		var host = GetHost();
		var component = host.Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var lists = GetActionLists(host, component);
		var items = new List<DesignerSmartTagActionInfo>();
		if (lists != null) {
			for (var listIndex = 0; listIndex < lists.Count; listIndex++) {
				var list = lists[listIndex];
				var sorted = list.GetSortedActionItems();
				for (var itemIndex = 0; itemIndex < sorted.Count; itemIndex++)
					items.Add(DescribeSmartTagItem(list, sorted[itemIndex], listIndex, itemIndex, elementId));
			}
		}
		return new DesignerSmartTagActions { Accepted = true, Items = items };
	}

	/// <summary>Smart-tag lists come from the component's own <c>ComponentDesigner.ActionLists</c>
	/// (e.g. <c>ToolStripDesigner</c> overrides it to return <c>ToolStripActionList</c>) rather
	/// than <c>DesignerActionService.GetComponentActions</c>: the latter only returns anything for
	/// a component whose designer registered with the service during its own
	/// <c>Initialize()</c>, which requires <c>DesignerActionService</c> to already be present in
	/// the container at that moment - something only the VS shell's own designer loader sets up,
	/// not the bare <see cref="DesignSurface"/> this host constructs. Reading <c>ActionLists</c>
	/// directly needs no such service.</summary>
	static DesignerActionListCollection? GetActionLists(IDesignerHost host, IComponent component)
		=> (host.GetDesigner(component) as ComponentDesigner)?.ActionLists;

	static DesignerSmartTagActionInfo DescribeSmartTagItem(DesignerActionList list, DesignerActionItem item, int listIndex, int itemIndex, string ownerElementId)
	{
		var info = new DesignerSmartTagActionInfo {
			ListIndex = listIndex,
			ItemIndex = itemIndex,
			DisplayName = item.DisplayName ?? "",
			Description = item.Description ?? "",
			Category = item.Category ?? "",
		};
		switch (item) {
		case DesignerActionMethodItem methodItem:
			info.Kind = "Method";
			info.MemberName = methodItem.MemberName ?? "";
			break;
		case DesignerActionPropertyItem propertyItem: {
			info.Kind = "Property";
			info.MemberName = propertyItem.MemberName ?? "";
			info.PropertyOwnerElementId = ownerElementId;
			var property = TypeDescriptor.GetProperties(list)[propertyItem.MemberName];
			if (property != null) {
				info.TypeName = property.PropertyType.FullName ?? property.PropertyType.Name;
				info.IsEnum = property.PropertyType.IsEnum;
				if (info.IsEnum) info.AllowedValues.AddRange(Enum.GetNames(property.PropertyType));
				else if (property.PropertyType == typeof(bool)) info.AllowedValues.AddRange(["True", "False"]);
				try {
					var value = property.GetValue(list);
					info.Value = value == null ? "" : (property.Converter.ConvertToInvariantString(value) ?? "");
				} catch { info.Value = ""; }
			}
			break;
		}
		// DesignerActionHeaderItem derives from DesignerActionTextItem - check it first.
		case DesignerActionHeaderItem: info.Kind = "Header"; break;
		case DesignerActionTextItem: info.Kind = "Text"; break;
		default: info.Kind = "Text"; break;
		}
		return info;
	}

	/// <summary>Invokes a <c>DesignerActionMethodItem</c> found by re-fetching the same smart
	/// tag list (never cached between calls - the live <c>DesignerActionList</c> is not a
	/// serializable DDP object). Some such methods (e.g. <c>TabControlActionList</c>'s "Add Tab"/
	/// "Remove Tab") mutate the component's runtime child collection directly via
	/// <c>host.CreateComponent</c>/<c>host.DestroyComponent</c> rather than through a property this
	/// host already knows how to serialize - so any <see cref="IComponent"/> the invoke itself adds
	/// or removes (observed via <see cref="IComponentChangeService"/>, the same mechanism the real
	/// designer's own undo engine uses to notice this) is synced to source afterward using the same
	/// <see cref="RewriteAddedControl"/>/<see cref="RewriteDeletedComponent"/> helpers the explicit
	/// add/delete RPCs use. Other methods (e.g. ToolStripActionList's "Insert Standard Items") rely
	/// on a <c>BehaviorService</c> this headless host never registers and so are themselves no-ops
	/// here - nothing to sync in that case.</summary>
	[JsonRpcMethod("design/invoke-smart-tag-method")]
	public DesignerSessionState InvokeSmartTagMethod(string sessionId, string documentId, long baseVersion, string elementId, int listIndex, int itemIndex)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "invoke smart tag method for");
		var host = GetHost();
		var component = host.Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var lists = GetActionLists(host, component);
		if (lists == null || listIndex < 0 || listIndex >= lists.Count)
			throw new ArgumentException("Smart tag action list not found.", nameof(listIndex));
		var sorted = lists[listIndex].GetSortedActionItems();
		if (itemIndex < 0 || itemIndex >= sorted.Count || sorted[itemIndex] is not DesignerActionMethodItem methodItem)
			throw new ArgumentException("Smart tag method item not found.", nameof(itemIndex));
		InvokeAndSyncComponentChanges(host, "Invoke " + methodItem.DisplayName, methodItem.Invoke);
		return CurrentState(baseVersion);
	}

	/// <summary>Shared by <see cref="InvokeSmartTagMethod"/> and <see cref="InvokeVerb"/>: runs
	/// <paramref name="invoke"/> inside a designer transaction, observes every
	/// <see cref="IComponent"/> it adds or removes via <see cref="IComponentChangeService"/> (the
	/// same mechanism the real designer's own undo engine uses to notice this), and syncs each one
	/// to designer source using the same <see cref="RewriteAddedControl"/>/
	/// <see cref="RewriteDeletedComponent"/> helpers the explicit add/delete RPCs use. A method or
	/// verb that only mutates properties the host already knows how to serialize (or that no-ops
	/// headlessly for lack of a BehaviorService, like ToolStripActionList's "Insert Standard
	/// Items") simply has nothing to sync here.</summary>
	void InvokeAndSyncComponentChanges(IDesignerHost host, string transactionName, Action invoke)
	{
		var changeService = host.GetService(typeof(IComponentChangeService)) as IComponentChangeService;
		var added = new List<IComponent>();
		var removed = new List<string>();
		ComponentEventHandler onAdded = (s, e) => { if (e.Component != null) added.Add(e.Component); };
		ComponentEventHandler onRemoved = (s, e) => { if (e.Component?.Site?.Name is { } name) removed.Add(name); };
		if (changeService != null) { changeService.ComponentAdded += onAdded; changeService.ComponentRemoved += onRemoved; }
		try {
			using (var transaction = host.CreateTransaction(transactionName)) {
				invoke();
				transaction.Commit();
			}
		} finally {
			if (changeService != null) { changeService.ComponentAdded -= onAdded; changeService.ComponentRemoved -= onRemoved; }
		}
		foreach (var name in removed)
			RewriteDeletedComponent(name);
		foreach (var newComponent in added) {
			if (newComponent is not Control control || control.Site?.Name is not { } newElementId || newElementId.Length == 0) continue;
			var parentId = control.Parent?.Site?.Name;
			if (String.IsNullOrEmpty(parentId)) continue;
			RewriteAddedControl(parentId, control.GetType(), newElementId, control.Left, control.Top, control.Width, control.Height);
			if (!String.IsNullOrEmpty(control.Text))
				RewriteProperty(newElementId, "Text", control.Text);
		}
	}

	/// <summary>Generic designer-verb listing (VS's own right-click context menu for a selected
	/// component - <c>TabControlDesigner</c>'s "Add Tab"/"Remove Tab" are exposed this way, NOT as
	/// smart-tag actions: <c>ComponentDesigner.Verbs</c>, distinct from <c>ActionLists</c>).
	/// LibreWinForms has no <c>System.ComponentModel.Design.DesignerVerb</c> support wired through
	/// its designer host, so - like the smart-tag RPCs - this is Microsoft-backend only.</summary>
	[JsonRpcMethod("design/list-verbs")]
	public DesignerVerbs ListVerbs(string sessionId, string documentId, long baseVersion, string elementId)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "list verbs for");
		var host = GetHost();
		var component = host.Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var verbs = (host.GetDesigner(component) as ComponentDesigner)?.Verbs;
		var items = new List<DesignerVerbInfo>();
		if (verbs != null) {
			for (var index = 0; index < verbs.Count; index++) {
				var verb = verbs[index];
				items.Add(new DesignerVerbInfo {
					Index = index,
					Text = verb.Text ?? "",
					Description = verb.Description ?? "",
					Enabled = verb.Enabled,
					Visible = verb.Visible,
				});
			}
		}
		return new DesignerVerbs { Accepted = true, Items = items };
	}

	/// <summary>Invokes a <c>DesignerVerb</c> found by re-fetching the same verb collection (never
	/// cached between calls, same reasoning as <see cref="ListSmartTagActions"/>'s own doc
	/// comment), then syncs any component it added/removed to source - see
	/// <see cref="InvokeAndSyncComponentChanges"/>.</summary>
	[JsonRpcMethod("design/invoke-verb")]
	public DesignerSessionState InvokeVerb(string sessionId, string documentId, long baseVersion, string elementId, int verbIndex)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "invoke verb for");
		var host = GetHost();
		var component = host.Container.Components[elementId]
			?? throw new ArgumentException("Component not found: " + elementId, nameof(elementId));
		var verbs = (host.GetDesigner(component) as ComponentDesigner)?.Verbs;
		if (verbs == null || verbIndex < 0 || verbIndex >= verbs.Count)
			throw new ArgumentException("Verb not found.", nameof(verbIndex));
		var verb = verbs[verbIndex];
		InvokeAndSyncComponentChanges(host, "Invoke " + verb.Text, () => verb.Invoke());
		return CurrentState(baseVersion);
	}

	/// <summary>Runs a <see cref="CommandID"/> through the real designer's own
	/// <see cref="IMenuCommandService"/>, exactly as an in-process designer's menu item would.</summary>
	/// <remarks>
	/// This is the generic escape hatch for the whole <see cref="StandardCommands"/> surface -
	/// AlignLeft, SizeToGrid, BringToFront, LockControls, ShowTabOrder and everything else a
	/// component's designer registers. Without it each of those has to be re-implemented as its own
	/// RPC on the parent side, because the parent has no local IDesignerHost to invoke them on
	/// (FormsDesignerViewContent.Host returns null by design - the real host lives here).
	///
	/// Commands are routed by their GUID+ID rather than by name so that nothing needs to enumerate
	/// or whitelist them: a CommandID the parent has never heard of still reaches the designer that
	/// registered it. The invoke is wrapped in InvokeAndSyncComponentChanges for the same reason
	/// verbs are - a command may add or remove components (Add Tab does), and those edits have to
	/// reach the source file, not just the live design surface.
	///
	/// **Scope, and why the per-command RPCs elsewhere in this file stay.** A self-hosted
	/// DesignSurface has no IMenuCommandService at all unless something registers one, and even
	/// then the registry starts empty: the class that fills it with the StandardCommands set
	/// (align, size-to-grid, z-order, lock, tab order) is
	/// `System.Windows.Forms.Design.ControlCommandSet`, which is **internal** - only Visual Studio's
	/// own designer package constructs it. So those commands can never route through here, and
	/// TryExecuteRemoteLayout's per-command branches on the parent side are a necessity rather than
	/// duplicated effort. What this RPC does reach is anything a designer registers for itself in
	/// Initialize, third-party ControlDesigners included.
	///
	/// **Do not "fix" this by registering a MenuCommandService here.** That was tried: adding
	/// `commandServices.AddService(typeof(IMenuCommandService), new MenuCommandService(designSurface))`
	/// in CreateDesignSurface makes this service resolve, but it breaks renaming a component with
	/// `The container cannot be disposed at design time.` (proven by
	/// ChildHost_Rename_UpdatesNamePropertyLiteralForToolStripItem, which passes without the
	/// registration and fails with it). The StandardCommands still would not work, because of the
	/// internal ControlCommandSet above - so the registration buys nothing and costs a working
	/// feature.
	/// </remarks>
	[JsonRpcMethod("design/invoke-menu-command")]
	public DesignerSessionState InvokeMenuCommand(string sessionId, string documentId, long baseVersion, string commandGuid, int commandId)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "invoke menu command for");
		if (!Guid.TryParse(commandGuid, out var guid))
			throw new ArgumentException("Not a CommandID GUID: " + commandGuid, nameof(commandGuid));
		var host = GetHost();
		var menuCommands = host.GetService(typeof(IMenuCommandService)) as IMenuCommandService
			?? throw new NotSupportedException("The design surface exposes no IMenuCommandService.");
		var command = new CommandID(guid, commandId);
		// Report an unroutable command instead of silently doing nothing: GlobalInvoke returns false
		// when no designer handled it, which is the difference between "this designer does not
		// support that" and "it ran and had no visible effect".
		var handled = false;
		InvokeAndSyncComponentChanges(host, "Invoke " + command,
			() => handled = menuCommands.GlobalInvoke(command));
		if (!handled)
			throw new NotSupportedException("No designer handled the command " + command + ".");
		return CurrentState(baseVersion);
	}

	/// <summary>The ToolStrip/StatusStrip/MenuStrip "insert new item" chevron: creates a real
	/// sited ToolStripItem via <c>host.CreateComponent</c> (so it is indistinguishable from a
	/// hand-authored item, unlike the unsited scaffolding <c>BuildElementTree</c> already filters
	/// out) and appends it to the strip's own Items, or to a submenu's DropDownItems when
	/// <paramref name="parentItemId"/> names a drop-down item.</summary>
	[JsonRpcMethod("design/add-toolstrip-item")]
	public DesignerSessionState AddToolStripItem(string sessionId, string documentId, long baseVersion, string elementId, string itemTypeName, string parentItemId, string newItemId)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		if (!IsValidIdentifier(newItemId))
			throw new ArgumentException("A valid component name is required.", nameof(newItemId));
		var host = GetHost();
		if (host.Container.Components[newItemId] != null)
			throw new ArgumentException("A component with that name already exists: " + newItemId, nameof(newItemId));
		var strip = host.Container.Components[elementId] as ToolStrip
			?? throw new ArgumentException("ToolStrip not found: " + elementId, nameof(elementId));
		var parentDropDown = String.IsNullOrEmpty(parentItemId) ? null
			: host.Container.Components[parentItemId] as ToolStripDropDownItem
				?? throw new ArgumentException("Parent menu item not found: " + parentItemId, nameof(parentItemId));
		var type = ResolveToolStripItemType(itemTypeName);
		ToolStripItem item;
		using (var transaction = host.CreateTransaction("Add " + newItemId)) {
			item = (ToolStripItem)host.CreateComponent(type, newItemId);
			var items = parentDropDown != null ? parentDropDown.DropDownItems : strip.Items;
			items.Add(item);
			transaction.Commit();
		}
		RewriteAddedToolStripItem(elementId, parentItemId, type, newItemId);
		return CurrentState(baseVersion);
	}

	static Type ResolveToolStripItemType(string name) => ResolveToolStripItemTypeShortName(ShortTypeName(name));

	static string ShortTypeName(string name)
	{
		var lastDot = name.LastIndexOf('.');
		return lastDot < 0 ? name : name.Substring(lastDot + 1);
	}

	static Type ResolveToolStripItemTypeShortName(string name) => name switch {
		"Button" or "ToolStripButton" => typeof(ToolStripButton),
		"Label" or "ToolStripLabel" => typeof(ToolStripLabel),
		"SplitButton" or "ToolStripSplitButton" => typeof(ToolStripSplitButton),
		"DropDownButton" or "ToolStripDropDownButton" => typeof(ToolStripDropDownButton),
		"Separator" or "ToolStripSeparator" => typeof(ToolStripSeparator),
		"ComboBox" or "ToolStripComboBox" => typeof(ToolStripComboBox),
		"TextBox" or "ToolStripTextBox" => typeof(ToolStripTextBox),
		"ProgressBar" or "ToolStripProgressBar" => typeof(ToolStripProgressBar),
		"StatusLabel" or "ToolStripStatusLabel" => typeof(ToolStripStatusLabel),
		"MenuItem" or "ToolStripMenuItem" => typeof(ToolStripMenuItem),
		_ => throw new NotSupportedException("Unsupported ToolStripItem type: " + name)
	};

	void RewriteAddedToolStripItem(string stripId, string parentItemId, Type type, string elementId)
	{
		if (IsVisualBasic) { RewriteAddedToolStripItemVisualBasic(stripId, parentItemId, type, elementId); return; }
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.First(item => item.Identifier.ValueText == "InitializeComponent");
		var className = method.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText;
		var collectionExpression = String.IsNullOrEmpty(parentItemId)
			? $"this.{stripId}.Items" : $"this.{parentItemId}.DropDownItems";
		var statements = new[] {
			SyntaxFactory.ParseStatement($"this.{elementId} = new {type.FullName}();\n"),
			SyntaxFactory.ParseStatement($"{collectionExpression}.Add(this.{elementId});\n")
		};
		var updatedMethod = method.WithBody(method.Body!.AddStatements(statements));
		root = root.ReplaceNode(method, updatedMethod);
		var declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First(item => item.Identifier.ValueText == className);
		var field = (FieldDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration($"private {type.FullName} {elementId};\n")!;
		root = root.ReplaceNode(declaration, declaration.AddMembers(field));
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	void RewriteAddedToolStripItemVisualBasic(string stripId, string parentItemId, Type type, string elementId)
	{
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
		var method = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
			.First(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms
				&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
		var className = method.Ancestors().OfType<VbSyntax.ClassBlockSyntax>().First().BlockStatement.Identifier.ValueText;
		var collectionExpression = String.IsNullOrEmpty(parentItemId)
			? $"Me.{stripId}.Items" : $"Me.{parentItemId}.DropDownItems";
		var statements = new[] {
			Vb.SyntaxFactory.ParseExecutableStatement($"Me.{elementId} = New {type.FullName}()"),
			Vb.SyntaxFactory.ParseExecutableStatement($"{collectionExpression}.Add(Me.{elementId})")
		};
		var updatedMethod = method.WithStatements(method.Statements.AddRange(statements));
		root = root.ReplaceNode(method, updatedMethod);
		var declaration = root.DescendantNodes().OfType<VbSyntax.ClassBlockSyntax>().First(item => item.BlockStatement.Identifier.ValueText == className);
		var field = ParseMemberField($"Friend WithEvents {elementId} As {type.FullName}");
		root = root.ReplaceNode(declaration, declaration.WithMembers(declaration.Members.Add(field)));
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	/// <summary>Drag-to-reorder for a ToolStrip/StatusStrip/MenuStrip item: moves it to
	/// <paramref name="targetIndex"/> within whatever collection it is CURRENTLY in (the strip's
	/// own Items, or a submenu's DropDownItems - resolved from the item's own Owner/OwnerItem, the
	/// same way <see cref="ExpandedDropDowns"/>/<see cref="BelongsTo"/> do; this RPC never moves an
	/// item to a DIFFERENT collection than the one it started in). Gated to Microsoft only, like
	/// AddToolStripItem: unlike that RPC this needs no DesignerActionService/CreateComponent
	/// machinery, but LibreWinForms' ToolStripItem does not expose Owner/OwnerItem at all.</summary>
	[JsonRpcMethod("design/reorder-toolstrip-item")]
	public DesignerSessionState ReorderToolStripItem(string sessionId, string documentId, long baseVersion, string elementId, int targetIndex)
	{
		EnsureCurrentVersion(sessionId, documentId, baseVersion, "edit");
		var host = GetHost();
		var item = host.Container.Components[elementId] as ToolStripItem
			?? throw new ArgumentException("ToolStripItem not found: " + elementId, nameof(elementId));
		var ownerItem = item.OwnerItem as ToolStripDropDownItem;
		var collection = ownerItem != null ? ownerItem.DropDownItems : item.Owner?.Items
			?? throw new ArgumentException("Item has no owning collection: " + elementId, nameof(elementId));
		using (var transaction = host.CreateTransaction("Reorder " + elementId)) {
			collection.Remove(item);
			var clampedIndex = Math.Clamp(targetIndex, 0, collection.Count);
			collection.Insert(clampedIndex, item);
			transaction.Commit();
		}
		var stripId = (ownerItem != null ? (ownerItem.Owner ?? item.Owner) : item.Owner)?.Site?.Name ?? "";
		RewriteReorderedToolStripItems(stripId, ownerItem?.Site?.Name ?? "", collection);
		return CurrentState(baseVersion);
	}

	/// <summary>Reorders the designer source's own record of a strip's items to match the LIVE
	/// collection's new order, after <see cref="ReorderToolStripItem"/> has already reordered the
	/// real ToolStripItemCollection. Handles both shapes existing fixtures/tests use: a single
	/// "collection.AddRange(new T[] { a, b, c })" call (its array elements are reordered in place)
	/// and a sequence of separate "collection.Add(x)" statements (the STATEMENTS are reordered in
	/// place, at the positions the original ones occupied) - anything else (an item that was only
	/// ever added at runtime, never textually) is left untouched, since there is nothing to move.</summary>
	void RewriteReorderedToolStripItems(string stripId, string parentItemId, ToolStripItemCollection collection)
	{
		if (String.IsNullOrEmpty(stripId)) return;
		var orderedNames = collection.Cast<ToolStripItem>().Select(i => i.Site?.Name ?? "").Where(n => n.Length > 0).ToList();
		if (orderedNames.Count < 2) return;
		if (IsVisualBasic) { RewriteReorderedToolStripItemsVisualBasic(stripId, parentItemId, orderedNames); return; }
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.FirstOrDefault(item => item.Identifier.ValueText == "InitializeComponent");
		if (method?.Body == null) return;

		// Deliberately NOT a string match against a hardcoded "this.{stripId}.Items"/"Me.{...}":
		// design/session-flush's ThisQualifierRewriter persistently drops the "this."/"Me."
		// qualifier from current.Files' own text (not just its own returned copy), so a second
		// reorder call after a Flush would silently find nothing if this matched by exact text.
		var addRange = method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(invocation =>
			invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AddRange" } access
			&& IsTargetCollectionAccess(access.Expression, stripId, parentItemId));
		if (addRange?.ArgumentList.Arguments is [{ Expression: ArrayCreationExpressionSyntax { Initializer: { } initializer } }]) {
			var byName = initializer.Expressions.ToDictionary(SimpleTargetName, expression => expression);
			var reordered = orderedNames.Where(byName.ContainsKey).Select(name => byName[name]);
			var newArray = ((ArrayCreationExpressionSyntax)addRange.ArgumentList.Arguments[0].Expression)
				.WithInitializer(initializer.WithExpressions(SyntaxFactory.SeparatedList(reordered)));
			root = root.ReplaceNode(addRange.ArgumentList.Arguments[0].Expression, newArray);
			file.Text = root.NormalizeWhitespace().ToFullString();
			return;
		}

		var addStatements = method.Body.Statements.OfType<ExpressionStatementSyntax>().Where(statement =>
			statement.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add" } access, ArgumentList.Arguments: [var argument] }
			&& IsTargetCollectionAccess(access.Expression, stripId, parentItemId)).ToList();
		if (addStatements.Count < 2) return;
		var statementsByName = addStatements.ToDictionary(
			statement => SimpleTargetName(((InvocationExpressionSyntax)statement.Expression).ArgumentList.Arguments[0].Expression), statement => statement);
		var reorderedStatements = orderedNames.Where(statementsByName.ContainsKey).Select(name => statementsByName[name]).ToList();
		var bodyStatements = method.Body.Statements.ToList();
		var positions = addStatements.Select(statement => bodyStatements.IndexOf(statement)).ToList();
		for (var i = 0; i < positions.Count; i++)
			bodyStatements[positions[i]] = reorderedStatements[i];
		var updatedMethod = method.WithBody(method.Body.WithStatements(SyntaxFactory.List(bodyStatements)));
		root = root.ReplaceNode(method, updatedMethod);
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	/// <summary>Whether expression is "&lt;owner&gt;.Items"/"&lt;owner&gt;.DropDownItems" naming
	/// exactly the strip/parent this reorder targets - regardless of a "this."/"Me." qualifier (or
	/// none at all) on owner, since that qualifier is not stable across a Flush (see the caller's
	/// own note).</summary>
	static bool IsTargetCollectionAccess(ExpressionSyntax expression, string stripId, string parentItemId)
	{
		if (expression is not MemberAccessExpressionSyntax access) return false;
		var wantMember = String.IsNullOrEmpty(parentItemId) ? "Items" : "DropDownItems";
		var wantOwner = String.IsNullOrEmpty(parentItemId) ? stripId : parentItemId;
		return access.Name.Identifier.ValueText == wantMember && SimpleTargetName(access.Expression) == wantOwner;
	}

	void RewriteReorderedToolStripItemsVisualBasic(string stripId, string parentItemId, List<string> orderedNames)
	{
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
		var method = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().FirstOrDefault(item =>
			item.BlockStatement is VbSyntax.MethodStatementSyntax ms && ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
			&& ms.Identifier.ValueText == "InitializeComponent");
		if (method == null) return;

		var addStatements = method.Statements.OfType<VbSyntax.ExpressionStatementSyntax>().Where(statement =>
			statement.Expression is VbSyntax.InvocationExpressionSyntax { Expression: VbSyntax.MemberAccessExpressionSyntax access, ArgumentList.Arguments.Count: 1 }
			&& access.Name.Identifier.ValueText == "Add" && IsVbTargetCollectionAccess(access.Expression, stripId, parentItemId)).ToList();
		if (addStatements.Count < 2) return;
		var statementsByName = addStatements.ToDictionary(statement => VbSimpleTargetName(
			((VbSyntax.InvocationExpressionSyntax)statement.Expression).ArgumentList.Arguments[0].GetExpression()), statement => statement);
		var reorderedStatements = orderedNames.Where(statementsByName.ContainsKey).Select(name => statementsByName[name]).ToList();
		var bodyStatements = method.Statements.ToList();
		var positions = addStatements.Select(statement => bodyStatements.IndexOf(statement)).ToList();
		for (var i = 0; i < positions.Count; i++)
			bodyStatements[positions[i]] = reorderedStatements[i];
		var updatedMethod = method.WithStatements(Vb.SyntaxFactory.List(bodyStatements));
		root = root.ReplaceNode(method, updatedMethod);
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	static bool IsVbTargetCollectionAccess(VbSyntax.ExpressionSyntax expression, string stripId, string parentItemId)
	{
		if (expression is not VbSyntax.MemberAccessExpressionSyntax access) return false;
		var wantMember = String.IsNullOrEmpty(parentItemId) ? "Items" : "DropDownItems";
		var wantOwner = String.IsNullOrEmpty(parentItemId) ? stripId : parentItemId;
		return access.Name.Identifier.ValueText == wantMember && VbSimpleTargetName(access.Expression) == wantOwner;
	}

	static int Snap(int value) => (int)Math.Round(value / 8d, MidpointRounding.AwayFromZero) * 8;

	static void SpaceEqually(Control[] controls, bool horizontal)
	{
		if (controls.Length < 3) return;
		var ordered = horizontal ? controls.OrderBy(item => item.Left).ToArray() : controls.OrderBy(item => item.Top).ToArray();
		var first = horizontal ? ordered[0].Left : ordered[0].Top;
		var lastEdge = horizontal ? ordered[^1].Right : ordered[^1].Bottom;
		var occupied = ordered.Sum(item => horizontal ? item.Width : item.Height);
		var gap = (lastEdge - first - occupied) / (ordered.Length - 1);
		var cursor = first;
		foreach (var item in ordered) {
			if (horizontal) item.Left = cursor; else item.Top = cursor;
			cursor += (horizontal ? item.Width : item.Height) + gap;
		}
	}

	static void AdjustSpacing(Control[] controls, bool horizontal, int delta, bool concatenate)
	{
		if (controls.Length < 2) return;
		var ordered = horizontal ? controls.OrderBy(item => item.Left).ToArray() : controls.OrderBy(item => item.Top).ToArray();
		var cursor = horizontal ? ordered[0].Right : ordered[0].Bottom;
		for (var index = 1; index < ordered.Length; index++) {
			var previousEdge = horizontal ? ordered[index - 1].Right : ordered[index - 1].Bottom;
			var currentStart = horizontal ? ordered[index].Left : ordered[index].Top;
			var gap = concatenate ? 0 : Math.Max(0, currentStart - previousEdge + delta);
			if (horizontal) ordered[index].Left = cursor + gap; else ordered[index].Top = cursor + gap;
			cursor = horizontal ? ordered[index].Right : ordered[index].Bottom;
		}
	}

	static IComponent? FindDeepest(Control control, Point point, IContainer? container = null)
	{
		for (var index = control.Controls.Count - 1; index >= 0; index--) {
			var child = control.Controls[index];
			if (!child.Visible || !child.Bounds.Contains(point)) continue;
			// Same container-membership filtering the ToolStrip.Items walk below already applies,
			// extended to plain child Controls: a ToolStripTemplateNode's in-place editor
			// (ToolStripTemplateNode.EnterInSituEdit's TextBox, hosted via a
			// DesignerToolStripControlHost) becomes a REAL child Control of the ToolStrip/dropdown
			// it lives in, but it is never sited in the designer's own IContainer - it is UI, not
			// a component. Without this check, a click on it hit-tested as that raw TextBox and
			// got passed to ISelectionService.SetSelectedComponents, which real WinForms then
			// treated as "selection left" this dropdown's ownership and closed it - reported as
			// "clicking Type Here makes the popup disappear".
			if (container != null && !container.Components.Cast<IComponent>().Contains(child))
				continue;
			return FindDeepest(child, new Point(point.X - child.Left, point.Y - child.Top), container);
		}
		if (!control.ClientRectangle.Contains(point))
			return null;
		// A click on a real ToolStrip/MenuStrip/StatusStrip should select the item under the
		// pointer directly, the same way clicking a plain Control does - point is already in
		// this control's own client space by the time we get here, which is the same space
		// ToolStripItem.Bounds live in (both relative to the owning ToolStrip).
		if (control is ToolStrip toolStrip) {
			for (var index = toolStrip.Items.Count - 1; index >= 0; index--) {
				var item = toolStrip.Items[index];
				if (item.Visible && item.Bounds.Contains(point)
					&& (container == null || container.Components.Cast<IComponent>().Contains(item)))
					return item;
			}
		}
		return control;
	}

	[JsonRpcMethod("shutdown")]
	public void Shutdown() => shutdown.Set();
	[JsonRpcMethod("ping")]
	public void Ping() { }
	[JsonRpcMethod("diagnostics/delay")]
	public async Task Delay(int milliseconds) => await Task.Delay(milliseconds);
	public void WaitForShutdown() => shutdown.Wait();
	public void OnParentDisconnected() => shutdown.Set();

	void DisposeDesignSurface()
	{
		if (designSurface?.GetService(typeof(IMenuCommandService)) is DesignerMenuCommandService commands)
			commands.DisposingSurface = true;
		designSurface?.Dispose();
		snapshotLoader = null;
	}

	sealed class DesignerMenuCommandService(IServiceProvider provider) : MenuCommandService(provider)
	{
		public bool DisposingSurface { get; set; }
		public override void RemoveCommand(MenuCommand command)
		{
			// The headless tray is initialized before our command service exists. Its
			// Arrange/Lineup/LargeIcons commands are consequently absent. During teardown
			// there is nothing to unregister for them; preserve normal validation otherwise.
			if (DisposingSurface && command is null) return;
			base.RemoveCommand(command);
		}
	}

	internal void Close()
	{
		DisposeDesignSurface();
		designSurface = null;
		projectLoadContext?.Unload();
		projectLoadContext = null;
		projectAssembly = null;
		referencedAssemblies.Clear();
		current = null;
	}

	void EnsureInitialized()
	{
		if (!initialized) throw new UnauthorizedAccessException("The designer host has not completed its handshake.");
	}

	void EnsureOwnSession(DesignerDocumentSnapshot snapshot) => EnsureOwnSession(snapshot.SessionId, snapshot.DocumentId);

	void EnsureOwnSession(string requestSessionId, string requestDocumentId)
	{
		if (requestSessionId != sessionId)
			throw new UnauthorizedAccessException("The request's session id does not match this designer host.");
		if (current != null && requestDocumentId != current.DocumentId)
			throw new InvalidOperationException("The request's document id does not match the open document.");
	}

	void EnsureCurrentVersion(string sessionId, string documentId, long baseVersion, string operation)
	{
		EnsureInitialized();
		EnsureOwnSession(sessionId, documentId);
		if (current == null || current.Version != baseVersion)
			throw new InvalidOperationException($"Cannot {operation} a stale or unopened document baseVersion.");
	}

	IDesignerHost GetHost() => designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost
		?? throw new InvalidOperationException("The designer surface is unavailable.");

	void RewriteProperty(string elementId, string propertyName, object? value, bool isRootComponent = false)
	{
		if (IsVisualBasic) { RewritePropertyVisualBasic(elementId, propertyName, value, isRootComponent); return; }
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var target = PropertyTarget(elementId, propertyName, isRootComponent);
		var assignment = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.FirstOrDefault(item => NormalizeTarget(item.Left.ToString()) == target);
		var expression = SerializeValue(value);
		if (assignment != null) {
			file.Text = root.ReplaceNode(assignment.Right, expression.WithTriviaFrom(assignment.Right)).ToFullString();
			return;
		}
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.First(item => item.Identifier.ValueText == "InitializeComponent");
		var statement = SyntaxFactory.ExpressionStatement(
			SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
				SyntaxFactory.ParseExpression("this." + target), expression));
		file.Text = root.ReplaceNode(method, method.WithBody(method.Body!.AddStatements(statement)))
			.NormalizeWhitespace().ToFullString();
	}

	void RewritePropertyVisualBasic(string elementId, string propertyName, object? value, bool isRootComponent)
	{
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
		var target = PropertyTarget(elementId, propertyName, isRootComponent);
		var assignment = root.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>()
			.FirstOrDefault(item => NormalizeTarget(item.Left.ToString()) == target);
		var expression = SerializeValueVisualBasic(value);
		if (assignment != null) {
			file.Text = root.ReplaceNode(assignment.Right, expression.WithTriviaFrom(assignment.Right)).ToFullString();
			return;
		}
		var method = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
			.First(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms
				&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
		var statement = Vb.SyntaxFactory.ParseExecutableStatement($"Me.{target} = {expression}");
		file.Text = root.ReplaceNode(method, method.WithStatements(method.Statements.Add(statement)))
			.NormalizeWhitespace().ToFullString();
	}

	void RewriteComponentName(string oldName, string newName)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			var vbTokens = vbRoot.DescendantTokens().Where(token => token.IsKind(Vb.SyntaxKind.IdentifierToken)
				&& token.ValueText == oldName && (token.Parent!.AncestorsAndSelf().OfType<VbSyntax.MethodBlockSyntax>()
					.Any(method => method.BlockStatement is VbSyntax.MethodStatementSyntax ms
					&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
					&& ms.Identifier.ValueText == "InitializeComponent")
					|| token.Parent.AncestorsAndSelf().OfType<VbSyntax.FieldDeclarationSyntax>().Any())).ToArray();
			vbRoot = vbRoot.ReplaceTokens(vbTokens, (token, _) => Vb.SyntaxFactory.Identifier(token.LeadingTrivia, newName, token.TrailingTrivia));
			file.Text = vbRoot.NormalizeWhitespace().ToFullString();
			return;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var tokens = root.DescendantTokens().Where(token => token.IsKind(SyntaxKind.IdentifierToken)
			&& token.ValueText == oldName && (token.Parent!.AncestorsAndSelf().OfType<MethodDeclarationSyntax>()
				.Any(method => method.Identifier.ValueText == "InitializeComponent")
				|| token.Parent.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().Any())).ToArray();
		root = root.ReplaceTokens(tokens, (token, _) => SyntaxFactory.Identifier(token.LeadingTrivia, newName, token.TrailingTrivia));
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	void RewriteResetProperty(string elementId, string propertyName, bool isRootComponent)
	{
		var file = CurrentDesignerFile();
		var target = PropertyTarget(elementId, propertyName, isRootComponent);
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			var vbStatements = vbRoot.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>()
				.Where(statement => NormalizeTarget(statement.Left.ToString()) == target).ToArray();
			file.Text = vbRoot.RemoveNodes(vbStatements, SyntaxRemoveOptions.KeepNoTrivia)!.NormalizeWhitespace().ToFullString();
			return;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var statements = root.DescendantNodes().OfType<ExpressionStatementSyntax>().Where(statement =>
			statement.Expression is AssignmentExpressionSyntax assignment
			&& assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
			&& NormalizeTarget(assignment.Left.ToString()) == target).ToArray();
		file.Text = root.RemoveNodes(statements, SyntaxRemoveOptions.KeepNoTrivia)!.NormalizeWhitespace().ToFullString();
	}

	static string NormalizeTarget(string target) => target.StartsWith("this.", StringComparison.Ordinal) ? target[5..]
		: target.StartsWith("Me.", StringComparison.Ordinal) ? target[3..] : target;

	static string PropertyTarget(string elementId, string propertyName, bool isRootComponent)
		=> isRootComponent ? propertyName : elementId + "." + propertyName;

	/// <summary>A type's fully qualified source spelling. Generic and nested types are refused by
	/// the type editor, so they need no source form here.</summary>
	static string TypeSourceName(Type type)
		=> type.IsGenericType ? throw new NotSupportedException("Generic types are not supported: " + type.FullName)
		: (type.FullName ?? type.Name).Replace('+', '.');

	static ExpressionSyntax SerializeValue(object? value) => value switch {
		null => SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression),
		IComponent component when component.Site?.Name is { Length: > 0 } name => SyntaxFactory.ParseExpression("this." + name),
		string text => SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(text)),
		Type type => SyntaxFactory.ParseExpression("typeof(" + TypeSourceName(type) + ")"),
		string[] texts => SyntaxFactory.ParseExpression("new string[] { " + string.Join(", ", texts.Select(text => SerializeValue(text).ToString())) + " }"),
		bool boolean => SyntaxFactory.LiteralExpression(boolean ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
		char character => SyntaxFactory.LiteralExpression(SyntaxKind.CharacterLiteralExpression, SyntaxFactory.Literal(character)),
		DateTime dateTime => SyntaxFactory.ParseExpression($"new System.DateTime({dateTime.Ticks}L)"),
		byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
			=> SyntaxFactory.ParseExpression(Convert.ToString(value, CultureInfo.InvariantCulture)!),
		Enum enumeration => SyntaxFactory.ParseExpression($"({enumeration.GetType().FullName ?? enumeration.GetType().Name}){Convert.ToInt64(enumeration, CultureInfo.InvariantCulture)}"),
		Point point => SyntaxFactory.ParseExpression($"new System.Drawing.Point({point.X}, {point.Y})"),
		Size size => SyntaxFactory.ParseExpression($"new System.Drawing.Size({size.Width}, {size.Height})"),
		SizeF size => SyntaxFactory.ParseExpression($"new System.Drawing.SizeF({size.Width.ToString(CultureInfo.InvariantCulture)}F, {size.Height.ToString(CultureInfo.InvariantCulture)}F)"),
		Rectangle rectangle => SyntaxFactory.ParseExpression($"new System.Drawing.Rectangle({rectangle.X}, {rectangle.Y}, {rectangle.Width}, {rectangle.Height})"),
		RectangleF rectangle => SyntaxFactory.ParseExpression($"new System.Drawing.RectangleF({rectangle.X.ToString(CultureInfo.InvariantCulture)}F, {rectangle.Y.ToString(CultureInfo.InvariantCulture)}F, {rectangle.Width.ToString(CultureInfo.InvariantCulture)}F, {rectangle.Height.ToString(CultureInfo.InvariantCulture)}F)"),
		Padding padding => SyntaxFactory.ParseExpression($"new System.Windows.Forms.Padding({padding.Left}, {padding.Top}, {padding.Right}, {padding.Bottom})"),
		Font font => SyntaxFactory.ParseExpression($"new System.Drawing.Font({SyntaxFactory.Literal(font.Name)}, {font.Size.ToString(CultureInfo.InvariantCulture)}F, (System.Drawing.FontStyle){Convert.ToInt32(font.Style, CultureInfo.InvariantCulture)})"),
		Color color => SyntaxFactory.ParseExpression($"System.Drawing.Color.FromArgb({color.A}, {color.R}, {color.G}, {color.B})"),
		_ => throw new NotSupportedException($"Serializing property values of type {value.GetType().FullName} is not supported yet.")
	};

	/// <summary>VB expression form: literals use True/False/Nothing and the character/float
	/// suffixes ("c, !, R), casts are CType, and struct values use New.</summary>
	static VbSyntax.ExpressionSyntax SerializeValueVisualBasic(object? value) => value switch {
		null => Vb.SyntaxFactory.NothingLiteralExpression(Vb.SyntaxFactory.Token(Vb.SyntaxKind.NothingKeyword)),
		IComponent component when component.Site?.Name is { Length: > 0 } name => Vb.SyntaxFactory.ParseExpression("Me." + name),
		string text => Vb.SyntaxFactory.StringLiteralExpression(Vb.SyntaxFactory.Literal(text)),
		Type type => Vb.SyntaxFactory.ParseExpression("GetType(" + TypeSourceName(type) + ")"),
		string[] texts => Vb.SyntaxFactory.ParseExpression("New String() {" + string.Join(", ", texts.Select(text => SerializeValueVisualBasic(text).ToString())) + "}"),
		bool boolean => boolean
			? Vb.SyntaxFactory.TrueLiteralExpression(Vb.SyntaxFactory.Token(Vb.SyntaxKind.TrueKeyword))
			: Vb.SyntaxFactory.FalseLiteralExpression(Vb.SyntaxFactory.Token(Vb.SyntaxKind.FalseKeyword)),
		char character => Vb.SyntaxFactory.CharacterLiteralExpression(Vb.SyntaxFactory.Literal(character)),
		DateTime dateTime => Vb.SyntaxFactory.ParseExpression($"New System.DateTime({dateTime.Ticks}L)"),
		int integer => Vb.SyntaxFactory.ParseExpression(integer.ToString(CultureInfo.InvariantCulture)),
		short or ushort or long or ulong or byte or sbyte or uint
			=> Vb.SyntaxFactory.ParseExpression(Convert.ToString(value, CultureInfo.InvariantCulture)!),
		float single => Vb.SyntaxFactory.ParseExpression(single.ToString(CultureInfo.InvariantCulture) + "!"),
		double number => Vb.SyntaxFactory.ParseExpression(number.ToString(CultureInfo.InvariantCulture) + "R"),
		decimal money => Vb.SyntaxFactory.ParseExpression(money.ToString(CultureInfo.InvariantCulture) + "D"),
		Enum enumeration => Vb.SyntaxFactory.ParseExpression($"CType({Convert.ToInt64(enumeration, CultureInfo.InvariantCulture)}, {enumeration.GetType().FullName ?? enumeration.GetType().Name})"),
		Point point => Vb.SyntaxFactory.ParseExpression($"New System.Drawing.Point({point.X}, {point.Y})"),
		Size size => Vb.SyntaxFactory.ParseExpression($"New System.Drawing.Size({size.Width}, {size.Height})"),
		SizeF size => Vb.SyntaxFactory.ParseExpression($"New System.Drawing.SizeF({size.Width.ToString(CultureInfo.InvariantCulture)}!, {size.Height.ToString(CultureInfo.InvariantCulture)}!)"),
		Rectangle rectangle => Vb.SyntaxFactory.ParseExpression($"New System.Drawing.Rectangle({rectangle.X}, {rectangle.Y}, {rectangle.Width}, {rectangle.Height})"),
		RectangleF rectangle => Vb.SyntaxFactory.ParseExpression($"New System.Drawing.RectangleF({rectangle.X.ToString(CultureInfo.InvariantCulture)}!, {rectangle.Y.ToString(CultureInfo.InvariantCulture)}!, {rectangle.Width.ToString(CultureInfo.InvariantCulture)}!, {rectangle.Height.ToString(CultureInfo.InvariantCulture)}!)"),
		Padding padding => Vb.SyntaxFactory.ParseExpression($"New System.Windows.Forms.Padding({padding.Left}, {padding.Top}, {padding.Right}, {padding.Bottom})"),
		Font font => Vb.SyntaxFactory.ParseExpression($"New System.Drawing.Font(\"{font.Name}\", {font.Size.ToString(CultureInfo.InvariantCulture)}!, CType({Convert.ToInt32(font.Style, CultureInfo.InvariantCulture)}, System.Drawing.FontStyle))"),
		Color color => Vb.SyntaxFactory.ParseExpression($"System.Drawing.Color.FromArgb({color.A}, {color.R}, {color.G}, {color.B})"),
		_ => throw new NotSupportedException($"Serializing property values of type {value.GetType().FullName} is not supported yet.")
	};

	void RewriteEvent(string elementId, EventDescriptor descriptor, string handlerName)
	{
		if (IsVisualBasic) { RewriteEventVisualBasic(elementId, descriptor, handlerName); return; }
		var designerFile = CurrentDesignerFile();
		var root = CSharpSyntaxTree.ParseText(designerFile.Text).GetCompilationUnitRoot();
		var target = elementId + "." + descriptor.Name;
		var isRootComponent = elementId == GetHost().RootComponent?.Site?.Name;
		var existing = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.FirstOrDefault(item => item.IsKind(SyntaxKind.AddAssignmentExpression)
				&& EventTargetMatches(NormalizeTarget(item.Left.ToString()), target, descriptor.Name, isRootComponent));
		if (String.IsNullOrEmpty(handlerName)) {
			if (existing?.Parent is StatementSyntax statement)
				designerFile.Text = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!.NormalizeWhitespace().ToFullString();
			return;
		}
		var handlerExpression = SyntaxFactory.ParseExpression(handlerName);
		if (existing != null)
			root = root.ReplaceNode(existing.Right, handlerExpression.WithTriviaFrom(existing.Right));
		else {
			var initialize = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
				.First(item => item.Identifier.ValueText == "InitializeComponent");
			var sourceTarget = isRootComponent ? "this." + descriptor.Name : "this." + target;
			var statement = SyntaxFactory.ParseStatement($"{sourceTarget} += {handlerName};\n");
			root = root.ReplaceNode(initialize, initialize.WithBody(initialize.Body!.AddStatements(statement)));
		}
		designerFile.Text = root.NormalizeWhitespace().ToFullString();

		var primaryFile = current!.Files.FirstOrDefault(item => item.Kind.Equals("Source", StringComparison.OrdinalIgnoreCase));
		if (primaryFile == null) return;
		var primaryRoot = CSharpSyntaxTree.ParseText(primaryFile.Text).GetCompilationUnitRoot();
		if (primaryRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(item => item.Identifier.ValueText == handlerName)) return;
		var declaration = primaryRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
		if (declaration == null) return;
		var invoke = descriptor.EventType?.GetMethod("Invoke");
		var parameters = invoke?.GetParameters() ?? [];
		var parameterList = String.Join(", ", parameters.Select((parameter, index) =>
			(parameter.ParameterType.FullName ?? parameter.ParameterType.Name) + " " + (parameter.Name ?? "arg" + index)));
		var method = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(
			$"private void {handlerName}({parameterList}) {{\n}}\n")!;
		primaryFile.Text = primaryRoot.ReplaceNode(declaration, declaration.AddMembers(method)).NormalizeWhitespace().ToFullString();
	}

	void RewriteEventVisualBasic(string elementId, EventDescriptor descriptor, string handlerName)
	{
		var designerFile = CurrentDesignerFile();
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(designerFile.Text).GetRoot();
		var target = elementId + "." + descriptor.Name;
		var isRootComponent = elementId == GetHost().RootComponent?.Site?.Name;
		var existing = root.DescendantNodes().OfType<VbSyntax.AddRemoveHandlerStatementSyntax>()
			.FirstOrDefault(item => item.IsKind(Vb.SyntaxKind.AddHandlerStatement)
				&& EventTargetMatches(NormalizeTarget(item.EventExpression.ToString()), target, descriptor.Name, isRootComponent));
		if (String.IsNullOrEmpty(handlerName)) {
			if (existing != null)
				designerFile.Text = root.RemoveNode(existing, SyntaxRemoveOptions.KeepNoTrivia)!.NormalizeWhitespace().ToFullString();
			return;
		}
		if (existing != null) {
			var handlerExpression = Vb.SyntaxFactory.AddressOfExpression(
				Vb.SyntaxFactory.ParseExpression("Me." + handlerName));
			designerFile.Text = root.ReplaceNode(existing.DelegateExpression, handlerExpression.WithTriviaFrom(existing.DelegateExpression))
				.NormalizeWhitespace().ToFullString();
		} else {
			var initialize = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
				.First(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms
				&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
			var sourceTarget = isRootComponent ? "Me." + descriptor.Name : "Me." + target;
			var statement = Vb.SyntaxFactory.ParseExecutableStatement($"AddHandler {sourceTarget}, AddressOf Me.{handlerName}");
			designerFile.Text = root.ReplaceNode(initialize, initialize.WithStatements(initialize.Statements.Add(statement)))
				.NormalizeWhitespace().ToFullString();
		}

		var primaryFile = current!.Files.FirstOrDefault(item => item.Kind.Equals("Source", StringComparison.OrdinalIgnoreCase));
		if (primaryFile == null) return;
		var primaryRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(primaryFile.Text).GetRoot();
		if (primaryRoot.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().Any(item =>
			item.BlockStatement is VbSyntax.MethodStatementSyntax ms
			&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
			&& ms.Identifier.ValueText == handlerName)) return;
		var declaration = primaryRoot.DescendantNodes().OfType<VbSyntax.ClassBlockSyntax>().FirstOrDefault();
		if (declaration == null) return;
		var invoke = descriptor.EventType?.GetMethod("Invoke");
		var parameters = invoke?.GetParameters() ?? [];
		// VB's NormalizeWhitespace round-trips "name As Type" parameter lists cleanly, while a
		// bare "Type name" list gets "System . Object" spacing artifacts.
		var parameterList = String.Join(", ", parameters.Select((parameter, index) =>
			(parameter.Name ?? "arg" + index) + " As " + (parameter.ParameterType.FullName ?? parameter.ParameterType.Name)));
		var member = ParseMemberMethod($"Private Sub {handlerName}({parameterList})\nEnd Sub");
		primaryFile.Text = primaryRoot.ReplaceNode(declaration, declaration.WithMembers(declaration.Members.Add(member)))
			.NormalizeWhitespace().ToFullString();
	}

	/// <summary>Parses a single VB member declaration through a dummy partial class; the
	/// VisualBasic SyntaxFactory has no ParseMemberDeclaration equivalent.</summary>
	static VbSyntax.MethodBlockSyntax ParseMemberMethod(string text)
	{
		var unit = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText("Partial Class Dummy\n" + text + "\nEnd Class").GetRoot();
		return unit.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>().First();
	}

	readonly Dictionary<string, string> typeIconCache = new(StringComparer.Ordinal);

	/// <summary>Real WinForms toolbox icon for a CLR type - the same embedded-resource lookup
	/// (<c>System.Drawing.ToolboxBitmapAttribute.GetImageFromResource</c>) real Visual Studio's
	/// Toolbox/smart-tag/insert-item UI relies on, walking up the type hierarchy when a type has
	/// no explicit [ToolboxBitmap]. Works for both Control and ToolStripItem types (unlike
	/// ResolveControlType, which requires Control assignability), so it can serve the smart-tag
	/// popup's own type rows and the ToolStrip insert-item dropdown's item types alike. Cached
	/// per type name since the embedded resource never changes within a session.</summary>
	[JsonRpcMethod("design/get-type-icon")]
	public DesignerTypeIconResult GetTypeIcon(string typeName)
	{
		if (typeIconCache.TryGetValue(typeName, out var cached))
			return new DesignerTypeIconResult { Accepted = true, PngBase64 = cached };
		try {
			var type = ResolveTypeForIcon(typeName)
				?? throw new NotSupportedException("Unknown type: " + typeName);
			using var image = System.Drawing.ToolboxBitmapAttribute.GetImageFromResource(type, null, false);
			var base64 = "";
			if (image != null) {
				using var stream = new MemoryStream();
				image.Save(stream, ImageFormat.Png);
				base64 = Convert.ToBase64String(stream.ToArray());
			}
			typeIconCache[typeName] = base64;
			return new DesignerTypeIconResult { Accepted = true, PngBase64 = base64 };
		} catch (Exception exception) {
			// A missing/unsupported icon is not a client-visible error - the smart-tag/insert-item
			// popups fall back to their own placeholder glyph when PngBase64 comes back empty.
			// Only genuinely unexpected failures (bad type name) are reported as Accepted=false.
			return new DesignerTypeIconResult { Accepted = false, Error = exception.Message };
		}
	}

	/// <summary>Like <see cref="ResolveControlType"/> but for icon lookup only: no Control-
	/// assignability requirement, since ToolStripItem (StatusStrip/MenuStrip items) is not a
	/// Control but still has a real toolbox icon.</summary>
	Type? ResolveTypeForIcon(string name)
	{
		var fullName = name.Contains('.') ? name : "System.Windows.Forms." + name;
		return projectAssembly?.GetType(fullName, false)
			?? referencedAssemblies.Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(candidate => candidate != null)
			?? typeof(Control).Assembly.GetType(fullName, false)
			?? AppDomain.CurrentDomain.GetAssemblies().Select(item => item.GetType(fullName, false)).FirstOrDefault(item => item != null);
	}

	Type ResolveControlType(string name)
	{
		var fullName = name.Contains('.') ? name : "System.Windows.Forms." + name;
		var type = projectAssembly?.GetType(fullName, false) ?? referencedAssemblies.Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(candidate => candidate != null) ?? typeof(Control).Assembly.GetType(fullName, false)
			?? AppDomain.CurrentDomain.GetAssemblies().Select(item => item.GetType(fullName, false)).FirstOrDefault(item => item != null);
		if (type == null || !typeof(Control).IsAssignableFrom(type) || type.IsAbstract)
			throw new NotSupportedException("Unsupported WinForms control type: " + name);
		return type;
	}

	/// <summary>A creatable non-Control component type that belongs in the tray, or null (the name
	/// is then resolved as a control, which reports an unsupported type). Like
	/// <see cref="IsTrayComponent"/>, it requires <c>[DesignTimeVisible(true)]</c>: a ToolStripItem,
	/// ColumnHeader or DataGridViewColumn is a component too, but it only exists inside its owner.</summary>
	Type? ResolveTrayComponentType(string? name)
	{
		if (String.IsNullOrEmpty(name))
			return null;
		var fullName = name.Contains('.') ? name : "System.Windows.Forms." + name;
		var type = projectAssembly?.GetType(fullName, false) ?? referencedAssemblies.Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(candidate => candidate != null)
			?? typeof(Control).Assembly.GetType(fullName, false) ?? Type.GetType(fullName, false)
			?? AppDomain.CurrentDomain.GetAssemblies().Select(item => item.GetType(fullName, false)).FirstOrDefault(item => item != null);
		return type != null && typeof(IComponent).IsAssignableFrom(type) && !typeof(Control).IsAssignableFrom(type)
			&& !type.IsAbstract && TypeDescriptor.GetAttributes(type).Contains(DesignTimeVisibleAttribute.Yes) && (type.GetConstructor(Type.EmptyTypes) != null || type.GetConstructor(new[] { typeof(IContainer) }) != null)
			? type : null;
	}

	/// <summary>Writes a tray component the way the WinForms designer does: a component with an
	/// <c>(IContainer)</c> constructor is created into <c>components</c>, which is declared and
	/// initialized first if the form has none yet.</summary>
	void RewriteAddedComponent(Type type, string elementId)
	{
		var usesContainer = type.GetConstructor(new[] { typeof(IContainer) }) != null;
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			var vbMethod = vbRoot.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
				.First(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms
					&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
					&& ms.Identifier.ValueText == "InitializeComponent");
			var vbClassName = vbMethod.Ancestors().OfType<VbSyntax.ClassBlockSyntax>().First().BlockStatement.Identifier.ValueText;
			var vbStatements = vbMethod.Statements;
			var vbInsertAt = 0;
			if (usesContainer) {
				var vbInit = vbStatements.OfType<VbSyntax.AssignmentStatementSyntax>().FirstOrDefault(item => NormalizeTarget(item.Left.ToString()) == "components");
				if (vbInit == null)
					vbStatements = vbStatements.Insert(0, Vb.SyntaxFactory.ParseExecutableStatement("Me.components = New System.ComponentModel.Container()"));
				vbInsertAt = vbInit == null ? 1 : vbStatements.IndexOf(vbInit) + 1;
			}
			vbStatements = vbStatements.Insert(vbInsertAt, Vb.SyntaxFactory.ParseExecutableStatement(
				$"Me.{elementId} = New {type.FullName}({(usesContainer ? "Me.components" : "")})"));
			vbRoot = vbRoot.ReplaceNode(vbMethod, vbMethod.WithStatements(vbStatements));
			var vbClass = vbRoot.DescendantNodes().OfType<VbSyntax.ClassBlockSyntax>().First(item => item.BlockStatement.Identifier.ValueText == vbClassName);
			var vbMembers = vbClass.Members;
			if (usesContainer && !vbMembers.OfType<VbSyntax.FieldDeclarationSyntax>().Any(field => field.Declarators.Any(d => d.Names.Any(n => n.Identifier.ValueText == "components"))))
				vbMembers = vbMembers.Add(ParseMemberField("Private components As System.ComponentModel.IContainer"));
			vbRoot = vbRoot.ReplaceNode(vbClass, vbClass.WithMembers(vbMembers.Add(ParseMemberField($"Friend WithEvents {elementId} As {type.FullName}"))));
			file.Text = vbRoot.NormalizeWhitespace().ToFullString();
			return;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First(item => item.Identifier.ValueText == "InitializeComponent");
		var className = method.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText;
		var statements = method.Body!.Statements;
		var insertAt = 0;
		if (usesContainer) {
			var init = statements.OfType<ExpressionStatementSyntax>().FirstOrDefault(item =>
				item.Expression is AssignmentExpressionSyntax assignment && NormalizeTarget(assignment.Left.ToString()) == "components");
			if (init == null)
				statements = statements.Insert(0, SyntaxFactory.ParseStatement("this.components = new System.ComponentModel.Container();\n"));
			insertAt = init == null ? 1 : statements.IndexOf(init) + 1;
		}
		statements = statements.Insert(insertAt, SyntaxFactory.ParseStatement(
			$"this.{elementId} = new {type.FullName}({(usesContainer ? "this.components" : "")});\n"));
		root = root.ReplaceNode(method, method.WithBody(method.Body.WithStatements(statements)));
		var declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First(item => item.Identifier.ValueText == className);
		var members = declaration.Members;
		if (usesContainer && !members.OfType<FieldDeclarationSyntax>().Any(field => field.Declaration.Variables.Any(v => v.Identifier.ValueText == "components")))
			members = members.Add((FieldDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration("private System.ComponentModel.IContainer components = null;\n")!);
		members = members.Add((FieldDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration($"private {type.FullName} {elementId};\n")!);
		root = root.ReplaceNode(declaration, declaration.WithMembers(members));
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	void RewriteAddedControl(string parentId, Type type, string elementId, int x, int y, int width, int height)
	{
		if (IsVisualBasic) { RewriteAddedControlVisualBasic(parentId, type, elementId, x, y, width, height); return; }
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.First(item => item.Identifier.ValueText == "InitializeComponent");
		var className = method.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText;
		var parentExpression = parentId == className
			? "this" : "this." + parentId;
		var statements = new[] {
			SyntaxFactory.ParseStatement($"this.{elementId} = new {type.FullName}();\n"),
			SyntaxFactory.ParseStatement($"this.{elementId}.Location = new System.Drawing.Point({x}, {y});\n"),
			SyntaxFactory.ParseStatement($"this.{elementId}.Size = new System.Drawing.Size({width}, {height});\n"),
			SyntaxFactory.ParseStatement($"{parentExpression}.Controls.Add(this.{elementId});\n")
		};
		var updatedMethod = method.WithBody(method.Body!.AddStatements(statements));
		root = root.ReplaceNode(method, updatedMethod);
		var declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First(item => item.Identifier.ValueText == className);
		var field = (FieldDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration($"private {type.FullName} {elementId};\n")!;
		root = root.ReplaceNode(declaration, declaration.AddMembers(field));
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	void RewriteAddedControlVisualBasic(string parentId, Type type, string elementId, int x, int y, int width, int height)
	{
		var file = current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
			?? current.Files.First();
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
		var method = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
			.First(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms
				&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
		var className = method.Ancestors().OfType<VbSyntax.ClassBlockSyntax>().First().BlockStatement.Identifier.ValueText;
		var parentExpression = parentId == className ? "Me" : "Me." + parentId;
		var statements = new[] {
			Vb.SyntaxFactory.ParseExecutableStatement($"Me.{elementId} = New {type.FullName}()"),
			Vb.SyntaxFactory.ParseExecutableStatement($"Me.{elementId}.Location = New System.Drawing.Point({x}, {y})"),
			Vb.SyntaxFactory.ParseExecutableStatement($"Me.{elementId}.Size = New System.Drawing.Size({width}, {height})"),
			Vb.SyntaxFactory.ParseExecutableStatement($"{parentExpression}.Controls.Add(Me.{elementId})")
		};
		var updatedMethod = method.WithStatements(method.Statements.AddRange(statements));
		root = root.ReplaceNode(method, updatedMethod);
		var declaration = root.DescendantNodes().OfType<VbSyntax.ClassBlockSyntax>().First(item => item.BlockStatement.Identifier.ValueText == className);
		var field = ParseMemberField($"Friend WithEvents {elementId} As {type.FullName}");
		root = root.ReplaceNode(declaration, declaration.WithMembers(declaration.Members.Add(field)));
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	/// <summary>Parses a single VB field declaration through a dummy partial class.</summary>
	static VbSyntax.FieldDeclarationSyntax ParseMemberField(string text)
	{
		var unit = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText("Partial Class Dummy\n" + text + "\nEnd Class").GetRoot();
		return unit.DescendantNodes().OfType<VbSyntax.FieldDeclarationSyntax>().First();
	}

	void RewriteBounds(string elementId, int x, int y, int width, int height)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			vbRoot = ReplaceRequiredAssignmentVisualBasic(vbRoot, elementId + ".Location",
				Vb.SyntaxFactory.ParseExpression($"New System.Drawing.Point({x}, {y})"));
			vbRoot = ReplaceRequiredAssignmentVisualBasic(vbRoot, elementId + ".Size",
				Vb.SyntaxFactory.ParseExpression($"New System.Drawing.Size({width}, {height})"));
			file.Text = vbRoot.ToFullString();
			return;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		root = ReplaceRequiredAssignment(root, elementId + ".Location",
			SyntaxFactory.ParseExpression($"new System.Drawing.Point({x}, {y})"));
		root = ReplaceRequiredAssignment(root, elementId + ".Size",
			SyntaxFactory.ParseExpression($"new System.Drawing.Size({width}, {height})"));
		file.Text = root.ToFullString();
	}

	void RewriteRootSize(int width, int height)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			var vbAssignment = vbRoot.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>()
				.FirstOrDefault(item => NormalizeTarget(item.Left.ToString()) is "ClientSize" or "Size");
			var vbValue = Vb.SyntaxFactory.ParseExpression($"New System.Drawing.Size({width}, {height})");
			if (vbAssignment != null) {
				// The selection outline and rendered bitmap use the outer Form bounds.  Persist
				// those exact bounds to Form.Size rather than ClientSize; otherwise each drag
				// adds the caption/border dimensions again when the host reloads the form.
				var vbReplacement = Vb.SyntaxFactory.AssignmentStatement(Vb.SyntaxKind.SimpleAssignmentStatement,
					Vb.SyntaxFactory.ParseExpression("Me.Size"), Vb.SyntaxFactory.Token(Vb.SyntaxKind.EqualsToken), vbValue).WithTriviaFrom(vbAssignment);
				file.Text = vbRoot.ReplaceNode(vbAssignment, vbReplacement).ToFullString();
				return;
			}
			var vbInitialize = vbRoot.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
				.First(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms
				&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
			file.Text = vbRoot.ReplaceNode(vbInitialize, vbInitialize.WithStatements(vbInitialize.Statements.Add(
				Vb.SyntaxFactory.ParseExecutableStatement($"Me.ClientSize = New System.Drawing.Size({width}, {height})"))))
				.NormalizeWhitespace().ToFullString();
			return;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var assignment = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.FirstOrDefault(item => NormalizeTarget(item.Left.ToString()) is "ClientSize" or "Size");
		var value = SyntaxFactory.ParseExpression($"new System.Drawing.Size({width}, {height})");
		if (assignment != null) {
			// The selection outline and rendered bitmap use the outer Form bounds. Persist
			// those exact bounds to Form.Size rather than ClientSize; otherwise each drag
			// adds the caption/border dimensions again when the host reloads the form.
			var replacement = SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
				SyntaxFactory.ParseExpression("this.Size"), value).WithTriviaFrom(assignment);
			file.Text = root.ReplaceNode(assignment, replacement).ToFullString();
			return;
		}
		var initialize = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.First(item => item.Identifier.ValueText == "InitializeComponent");
		file.Text = root.ReplaceNode(initialize, initialize.WithBody(initialize.Body!.AddStatements(
			SyntaxFactory.ParseStatement($"this.ClientSize = new System.Drawing.Size({width}, {height});\n"))))
			.NormalizeWhitespace().ToFullString();
	}

	/// <summary>The bare identifier a "this.foo"/"Me.foo" member-access expression (or a plain
	/// "foo" identifier) ultimately names - matches how AddToolStripItem's own generated statements
	/// (and every existing designer-source fixture) reference a sited component.</summary>
	static string SimpleTargetName(ExpressionSyntax expression) => expression switch {
		MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
		IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
		_ => ""
	};

	static string VbSimpleTargetName(VbSyntax.ExpressionSyntax expression) => expression switch {
		VbSyntax.MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
		VbSyntax.IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
		_ => ""
	};

	void RewriteDeletedComponent(string elementId)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
			// Shrink a shared AddRange array in place first - see the C# branch's own note on why.
			foreach (var arrayCreation in vbRoot.DescendantNodes().OfType<VbSyntax.ArrayCreationExpressionSyntax>().ToArray()) {
				if (arrayCreation.Initializer is not { } initializer) continue;
				var match = initializer.Initializers.FirstOrDefault(expression => VbSimpleTargetName(expression) == elementId);
				if (match == null || initializer.Initializers.Count <= 1) continue;
				var shrunk = initializer.WithInitializers(initializer.Initializers.Remove(match));
				vbRoot = vbRoot.ReplaceNode(initializer, shrunk);
			}
			// MethodBlockSyntax (InitializeComponent's own included) IS a StatementSyntax in VB's
			// model too - see the C# branch's own comment on why that must be excluded here. So are
			// the enclosing Class/Module and Namespace blocks: the class mentions the component in
			// its own field, so without excluding them the whole class was removed.
			var vbStatements = vbRoot.DescendantNodes().OfType<VbSyntax.StatementSyntax>()
				.Where(statement => statement is not (VbSyntax.MethodBlockBaseSyntax or VbSyntax.TypeBlockSyntax or VbSyntax.NamespaceBlockSyntax)
					&& statement.DescendantNodesAndSelf().OfType<VbSyntax.IdentifierNameSyntax>()
						.Any(identifier => identifier.Identifier.ValueText == elementId)).ToArray();
			vbRoot = vbRoot.RemoveNodes(vbStatements, SyntaxRemoveOptions.KeepNoTrivia)!;
			var vbFields = vbRoot.DescendantNodes().OfType<VbSyntax.FieldDeclarationSyntax>()
				.Where(field => field.Declarators.SelectMany(declarator => declarator.Names)
					.Any(name => name.Identifier.ValueText == elementId)).ToArray();
			vbRoot = vbRoot.RemoveNodes(vbFields, SyntaxRemoveOptions.KeepNoTrivia)!;
			file.Text = vbRoot.NormalizeWhitespace().ToFullString();
			return;
		}
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		// A deleted item that is one of SEVERAL elements in a shared "collection.AddRange(new
		// T[] { a, b, c })" call (see RewriteReorderedToolStripItems' own note on this shape) must
		// only lose ITS OWN array element - not the whole statement, which would silently drop
		// every OTHER sibling in that same AddRange from the designer source too. Shrinking the
		// array here, before the generic statement-removal pass below, means that pass no longer
		// sees this identifier in the (now-shorter) array and leaves the statement alone.
		foreach (var arrayCreation in root.DescendantNodes().OfType<ArrayCreationExpressionSyntax>().ToArray()) {
			if (arrayCreation.Initializer is not { } initializer) continue;
			var match = initializer.Expressions.FirstOrDefault(expression => SimpleTargetName(expression) == elementId);
			if (match == null || initializer.Expressions.Count <= 1) continue;
			var shrunk = initializer.WithExpressions(initializer.Expressions.Remove(match));
			root = root.ReplaceNode(initializer, shrunk);
		}
		// BlockSyntax (a `{ ... }` body, InitializeComponent's own included) IS a StatementSyntax
		// in Roslyn's model, so without excluding it here it always ends up in this list too -
		// InitializeComponent's body mentions elementId somewhere by construction - and
		// RemoveNodes drops an ANCESTOR before its own now-redundant descendants, silently wiping
		// the WHOLE METHOD BODY (every other component's statements included) instead of just the
		// deleted component's own few statements.
		var statements = root.DescendantNodes().OfType<StatementSyntax>()
			.Where(statement => statement is not BlockSyntax
				&& statement.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
					.Any(identifier => identifier.Identifier.ValueText == elementId)).ToArray();
		root = root.RemoveNodes(statements, SyntaxRemoveOptions.KeepNoTrivia)!;
		var variables = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
			.Where(variable => variable.Identifier.ValueText == elementId).ToArray();
		foreach (var variable in variables) {
			if (variable.Parent?.Parent is FieldDeclarationSyntax field)
				root = root.RemoveNode(field, SyntaxRemoveOptions.KeepNoTrivia)!;
		}
		file.Text = root.NormalizeWhitespace().ToFullString();
	}

	void RewriteZOrder(string parentId, string elementId, int childIndex)
	{
		var file = CurrentDesignerFile();
		if (IsVisualBasic) { RewriteZOrderVisualBasic(parentId, elementId, childIndex); return; }
		var root = CSharpSyntaxTree.ParseText(file.Text).GetCompilationUnitRoot();
		var initialize = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.First(item => item.Identifier.ValueText == "InitializeComponent");
		var oldStatements = initialize.Body!.Statements.Where(statement => {
			var invocation = statement.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault();
			return invocation?.Expression is MemberAccessExpressionSyntax member
				&& member.Name.Identifier.ValueText == "SetChildIndex"
				&& NormalizeTarget(invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() ?? "") == elementId;
		}).ToArray();
		var body = initialize.Body.RemoveNodes(oldStatements, SyntaxRemoveOptions.KeepNoTrivia)!;
		var parentExpression = String.IsNullOrEmpty(parentId) || parentId == initialize.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText
			? "this" : "this." + parentId;
		body = body.AddStatements(SyntaxFactory.ParseStatement($"{parentExpression}.Controls.SetChildIndex(this.{elementId}, {childIndex});\n"));
		file.Text = root.ReplaceNode(initialize, initialize.WithBody(body)).NormalizeWhitespace().ToFullString();
	}

	void RewriteZOrderVisualBasic(string parentId, string elementId, int childIndex)
	{
		var file = CurrentDesignerFile();
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(file.Text).GetRoot();
		var initialize = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
			.First(item => item.BlockStatement is VbSyntax.MethodStatementSyntax ms
				&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent");
		var oldStatements = initialize.Statements.Where(statement => {
			var invocation = statement.DescendantNodes().OfType<VbSyntax.InvocationExpressionSyntax>().FirstOrDefault();
			return invocation?.Expression is VbSyntax.MemberAccessExpressionSyntax member
				&& member.Name.Identifier.ValueText == "SetChildIndex"
				&& NormalizeTarget(invocation.ArgumentList.Arguments.FirstOrDefault() is VbSyntax.SimpleArgumentSyntax first ? first.Expression.ToString() : "") == elementId;
		}).ToArray();
		var body = initialize.WithStatements(Vb.SyntaxFactory.List(initialize.Statements.Where(statement => !oldStatements.Contains(statement))));
		var parentExpression = String.IsNullOrEmpty(parentId) || parentId == initialize.Ancestors().OfType<VbSyntax.ClassBlockSyntax>().First().BlockStatement.Identifier.ValueText
			? "Me" : "Me." + parentId;
		body = body.WithStatements(body.Statements.Add(Vb.SyntaxFactory.ParseExecutableStatement(
			$"{parentExpression}.Controls.SetChildIndex(Me.{elementId}, {childIndex})")));
		file.Text = root.ReplaceNode(initialize, body).NormalizeWhitespace().ToFullString();
	}

	DesignerSourceFileSnapshot CurrentDesignerFile() => current!.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))
		?? current.Files.First();

	static VbSyntax.CompilationUnitSyntax ReplaceRequiredAssignmentVisualBasic(VbSyntax.CompilationUnitSyntax root, string target, VbSyntax.ExpressionSyntax value)
	{
		var assignment = root.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>()
			.FirstOrDefault(item => NormalizeTarget(item.Left.ToString()) == target)
			?? throw new NotSupportedException("The existing designer source has no assignment for " + target + ".");
		return root.ReplaceNode(assignment.Right, value.WithTriviaFrom(assignment.Right));
	}

	static CompilationUnitSyntax ReplaceRequiredAssignment(CompilationUnitSyntax root, string target, ExpressionSyntax value)
	{
		var assignment = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.FirstOrDefault(item => NormalizeTarget(item.Left.ToString()) == target)
			?? throw new NotSupportedException("The existing designer source has no assignment for " + target + ".");
		return root.ReplaceNode(assignment.Right, value.WithTriviaFrom(assignment.Right));
	}

	static void Validate(DesignerDocumentSnapshot snapshot)
	{
		if (snapshot.Version < 0) throw new ArgumentOutOfRangeException(nameof(snapshot.Version));
		if (String.IsNullOrWhiteSpace(snapshot.PrimaryFileName)) throw new ArgumentException("A primary file is required.");
		if (snapshot.Files.Count == 0) throw new ArgumentException("At least one source file is required.");
		if (snapshot.Files.Count > 256) throw new ArgumentException("A designer snapshot may contain at most 256 files.");
		long payloadSize = 0;
		foreach (var file in snapshot.Files) {
			if (file.FileName.Length > 4096) throw new ArgumentException("A designer snapshot file name is too long.");
			payloadSize += file.Text.Length * sizeof(char);
			payloadSize += file.Base64.Length * 3L / 4L;
			if (payloadSize > 16 * 1024 * 1024)
				throw new ArgumentException("The designer snapshot exceeds the 16 MiB payload limit.");
		}
	}

	/// <summary>Normalize a project output path to the managed assembly: <c>OutputAssemblyFullPath</c>
	/// can point at the apphost (an extensionless executable on Unix, or a ".exe" native shim on
	/// Windows), whose bytes are not valid IL. The managed assembly always sits next to it as
	/// "<c>name</c>.dll".</summary>
	static string ResolveManagedAssemblyPath(string path)
	{
		var full = Path.GetFullPath(path);
		if (full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
			return full;
		var dll = Path.ChangeExtension(full, ".dll");
		return File.Exists(dll) ? dll : full;
	}

	void CreateDesignSurface(DesignerDocumentSnapshot snapshot)
	{
		Trace("CreateDesignSurface disposing previous surface");
		typeSuggestions = null;
		DisposeDesignSurface();
		projectLoadContext?.Unload();
		projectLoadContext = null;
		projectAssembly = null;
		referencedAssemblies.Clear();
		rootDesignSize = ReadRootDesignSize(snapshot);
		rootAutoScaleDimensions = ReadRootAutoScaleDimensions(snapshot);
		var projectReferencePaths = snapshot.ReferencedAssemblyPaths
			.Where(path => File.Exists(path) && !string.Equals(path, snapshot.ProjectAssemblyPath, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		var referencePaths = projectReferencePaths.Concat(snapshot.ToolboxAssemblyPaths)
			.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		if (!String.IsNullOrWhiteSpace(snapshot.ProjectAssemblyPath) && File.Exists(snapshot.ProjectAssemblyPath)) {
			var managedAssemblyPath = ResolveManagedAssemblyPath(snapshot.ProjectAssemblyPath);
			if (File.Exists(managedAssemblyPath)) {
				projectLoadContext = new ProjectAssemblyLoadContext(managedAssemblyPath, snapshot.ToolboxAssemblyPaths);
				projectAssembly = projectLoadContext.LoadFromAssemblyPath(managedAssemblyPath);
			}
		}
		if (projectLoadContext == null && referencePaths.Length > 0)
			projectLoadContext = new ProjectAssemblyLoadContext(Path.GetFullPath(referencePaths[0]), snapshot.ToolboxAssemblyPaths);
		if (projectLoadContext != null) {
			int loaded = 0;
			// A LibreWinForms project's output carries its own copies of the framework the host itself
			// runs on (System.Windows.Forms, LibreWinForms.Platform, ...), and the IDE lists them as
			// references. LoadFromAssemblyPath bypasses ProjectAssemblyLoadContext.Load's sharing, so a
			// second System.Windows.Forms landed in referencedAssemblies; ResolveControlType searches it
			// before the host's own, and a toolbox ComboBox or Label then came from that copy, whose
			// LibrePlatform was never registered ("No LibreWinForms platform backend is registered").
			// A reference the host has already loaded is the host's copy.
			var hostAssemblies = AssemblyLoadContext.Default.Assemblies
				.GroupBy(assembly => assembly.GetName().Name ?? "", StringComparer.OrdinalIgnoreCase)
				.ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
			foreach (var path in referencePaths) {
				try {
					var fullPath = Path.GetFullPath(path);
					referencedAssemblies.Add(hostAssemblies.TryGetValue(AssemblyName.GetAssemblyName(fullPath).Name ?? "", out var shared)
						? shared
						: projectLoadContext.LoadFromAssemblyPath(fullPath));
					loaded++;
				} catch { }
			}
		}
		Trace("CreateDesignSurface constructing DesignSurface");
		designSurface = new DesignSurface();
		Trace("CreateDesignSurface beginning loader");
		snapshotLoader = new SnapshotDesignerLoader(snapshot, ResolveProjectType);
		designSurface.BeginLoad(snapshotLoader);
		Trace("CreateDesignSurface loader completed");
		if (!designSurface.IsLoaded) {
			var errors = designSurface.LoadErrors?.Cast<object>().Select(item => item?.ToString()).Where(item => !String.IsNullOrEmpty(item));
			var errStr = String.Join(" | ", errors ?? []);
			throw new InvalidOperationException("The child design surface failed to load: " + errStr);
		}
		// host.CreateComponent(type) WITHOUT an explicit name (every RPC in this file that adds a
		// component - AddControl, AddToolStripItem - passes one explicitly instead, so this gap
		// went unnoticed until verbs/smart-tag methods like TabControlActionList's "Add Tab" call
		// CreateComponent themselves with no name of their own) needs an INameCreationService to
		// auto-generate one; without it the new component comes back completely unnamed, and every
		// later RPC/rewrite that identifies components by name silently fails for it.
		if (designSurface.GetService(typeof(IDesignerHost)) is IServiceContainer surfaceServices
			&& surfaceServices.GetService(typeof(INameCreationService)) == null)
			surfaceServices.AddService(typeof(INameCreationService), new DefaultNameCreationService());
		if (designSurface.View is Control view) {
			Trace("CreateDesignSurface creating view control");
			view.CreateControl();
			view.PerformLayout();
			Trace("CreateDesignSurface laid out view control");
		}
#if MICROSOFT_WINFORMS
		TryInstallStandardCommandSet(designSurface);
#endif
#if !MICROSOFT_WINFORMS
		AdoptVisibleShadowsFromLoadedSource();
		HidePortableNativeWindows();
#endif
	}

#if !MICROSOFT_WINFORMS
	/// <summary>
	/// The design root is constructed as an ordinary top-level Form and only made a child by its
	/// designer afterwards, and the portable (ProGPU) backend keeps the native window that first
	/// handle created - so off Windows every loaded document put a real "Form1" window, with its own
	/// menu bar, beside the IDE. The design host only ever needs rendered bitmaps, so its unowned
	/// top-level windows stay hidden (owned popups - open menu dropdowns - are left alone).
	/// Reflection because the backend exposes no headless switch: <c>LibrePlatform.Current.Windows</c>
	/// is the ProGPU <c>SilkWindowService</c>, which tracks its windows in <c>_windows</c>.
	/// </summary>
	static void HidePortableNativeWindows()
	{
		MakeBackgroundApplication();
		try {
			var platform = Type.GetType("LibreWinForms.Platform.LibrePlatform, System.Windows.Forms")
				?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("LibreWinForms.Platform.LibrePlatform")).FirstOrDefault(t => t != null);
			var windowService = (platform?.GetProperty("Current")?.GetValue(null))?.GetType().GetProperty("Windows")?.GetValue(platform!.GetProperty("Current")!.GetValue(null));
			if (windowService?.GetType().GetField("_windows", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(windowService) is not System.Collections.IEnumerable windows)
				return;
			foreach (var window in windows.Cast<object>().ToArray()) {
				var type = window.GetType();
				// Only unowned top-level windows: a menu dropdown is an owned popup window, and
				// hiding it closes the dropdown the designer is holding open for editing.
				var owner = type.GetProperty("Owner")?.GetValue(window);
				if (owner != null && !owner.Equals(Activator.CreateInstance(owner.GetType())))
					continue;
				if (type.GetProperty("Visible")?.GetValue(window) is true)
					type.GetMethod("Hide", Type.EmptyTypes)?.Invoke(window, null);
			}
		} catch (Exception exception) {
			Trace("HidePortableNativeWindows failed: " + exception.Message);
		}
	}

	/// <summary>macOS: the windowing backend makes the process a regular (Dock icon + menu bar)
	/// application when it creates a window. The design host is a background service of the IDE:
	/// NSApplicationActivationPolicyAccessory keeps it out of the Dock and the app switcher.</summary>
	static void MakeBackgroundApplication()
	{
		if (!OperatingSystem.IsMacOS())
			return;
		try {
			var app = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
			if (app != IntPtr.Zero)
				objc_msgSend_long(app, sel_registerName("setActivationPolicy:"), 1 /* Accessory */);
		} catch (Exception exception) {
			Trace("MakeBackgroundApplication failed: " + exception.Message);
		}
	}

	[System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib")]
	static extern IntPtr objc_getClass(string name);
	[System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib")]
	static extern IntPtr sel_registerName(string name);
	[System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
	[System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern bool objc_msgSend_long(IntPtr receiver, IntPtr selector, long value);
#endif

#if MICROSOFT_WINFORMS
	/// <summary>Keeps the installed command set alive for the lifetime of the surface: CommandSet
	/// subscribes to selection/host events and would otherwise be collectible while its commands
	/// remain registered.</summary>
	IDisposable installedCommandSet;

	/// <summary>Stands in for the part of Visual Studio's designer package that supplies the
	/// WinForms <c>CommandSet</c>, so the whole StandardCommands surface (align, size-to-grid,
	/// z-order, lock, tab order, Cut/Copy/Paste/Delete/SelectAll) is implemented by WinForms itself
	/// rather than reimplemented per command on the parent side.</summary>
	/// <remarks>
	/// A self-hosted DesignSurface provides none of this: `ControlCommandSet` registers every
	/// standard command into IMenuCommandService from its constructor, but nothing constructs it and
	/// the service does not exist either. The three pieces it needs are all obtainable -
	/// `EventHandlerService` and `MenuCommandService` are public types, and the ISite is the root
	/// component's own - so only `ControlCommandSet` itself needs reflection, being internal.
	///
	/// Entirely best-effort: this reaches into an internal type, so a runtime update can remove or
	/// rename it. Failure must leave the surface exactly as it was, with the parent's own per-command
	/// RPCs still handling everything - which is why every step is guarded and the whole thing is
	/// wrapped rather than allowed to fault CreateDesignSurface.
	/// </remarks>
	void TryInstallStandardCommandSet(DesignSurface designSurface)
	{
		try {
			if (designSurface.GetService(typeof(IDesignerHost)) is not IDesignerHost host
				|| designSurface.GetService(typeof(IDesignerHost)) is not IServiceContainer services)
				return;
			if (host.RootComponent?.Site is not ISite site)
				return;
			// Resolved by name rather than typeof: in the real WinForms IEventHandlerService is
			// internal (the LibreWinForms fork happens to make it public), and the service has to be
			// registered under that exact interface type because CommandSet looks it up by it.
			var designAssembly = typeof(MenuCommandService).Assembly;
			var eventHandlerServiceType = designAssembly.GetType("System.Windows.Forms.Design.EventHandlerService", throwOnError: false);
			var eventHandlerInterface = designAssembly.GetType("System.Windows.Forms.Design.IEventHandlerService", throwOnError: false);
			var commandSetType = designAssembly.GetType("System.Windows.Forms.Design.ControlCommandSet", throwOnError: false);
			if (eventHandlerServiceType == null || eventHandlerInterface == null || commandSetType == null) {
				Trace("TryInstallStandardCommandSet: this runtime does not expose the WinForms command set types");
				return;
			}
			// A hard requirement: CommandSet's constructor asks for it with GetRequiredService and
			// throws without it.
			if (services.GetService(eventHandlerInterface) == null)
				services.AddService(eventHandlerInterface,
					Activator.CreateInstance(eventHandlerServiceType, host.RootComponent as Control));
			if (services.GetService(typeof(IMenuCommandService)) == null)
				services.AddService(typeof(IMenuCommandService), new DesignerMenuCommandService(designSurface));
			installedCommandSet = Activator.CreateInstance(commandSetType,
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null, new object[] { site }, null) as IDisposable;
			Trace("TryInstallStandardCommandSet installed=" + (installedCommandSet != null));
		} catch (Exception exception) {
			// Never let this take the designer down - the parent's per-command RPCs remain the
			// fallback, exactly as before this existed.
			Trace("TryInstallStandardCommandSet failed: " + exception.Message);
		}
	}
#endif

#if !MICROSOFT_WINFORMS
	/// <summary>Takes over Visible for controls that arrived hidden from the loaded source, so they
	/// are shown (and stay selectable) on the surface while the source keeps saying false - the
	/// other half of the shadowing SetProperty does, without which reopening a document would drop
	/// straight back to "the control is gone".
	///
	/// `control.Visible` is an exact signal here BECAUSE LibreWinForms' getter does not fold the
	/// parent chain (unlike real WinForms' GetVisibleCore): it reports only this control's own
	/// flag, so false means this control itself was assigned false - not that some ancestor was.
	/// That is the same fork difference that made IsEffectivelyVisible walk parents explicitly.
	///
	/// TabPages are skipped: their Visible is the TabControl's own business (an unselected page IS
	/// legitimately hidden), and "restoring" them would show every page at once.</summary>
	void AdoptVisibleShadowsFromLoadedSource()
	{
		shadowedVisible.Clear();
		try {
			var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost;
			if (host?.Container == null) return;
			foreach (var control in host.Container.Components.Cast<IComponent>().OfType<Control>()) {
				if (control is TabPage || ReferenceEquals(control, host.RootComponent)) continue;
				if (control.Site?.Name is not { Length: > 0 } name || control.Visible) continue;
				shadowedVisible[name] = false;
				control.Visible = true;
			}
		} catch {
			// A fork that cannot report or set Visible must not take the whole session down; the
			// worst case is the pre-shadowing behaviour for this document.
		}
	}
#endif

	static string VbArgumentText(VbSyntax.ObjectCreationExpressionSyntax creation, int index)
		=> ((VbSyntax.SimpleArgumentSyntax)creation.ArgumentList!.Arguments[index]).Expression.ToString();

	static bool SnapshotIsVisualBasic(DesignerDocumentSnapshot snapshot) => snapshot.Language.Equals("VisualBasic", StringComparison.OrdinalIgnoreCase)
		|| snapshot.DesignerFileName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)
		|| snapshot.PrimaryFileName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase);

	static Size? ReadRootDesignSize(DesignerDocumentSnapshot snapshot)
	{
		var source = snapshot.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))?.Text;
		if (String.IsNullOrEmpty(source)) return null;
		if (SnapshotIsVisualBasic(snapshot)) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(source).GetRoot();
			var vbCreation = vbRoot.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>()
				.Where(item => NormalizeTarget(item.Left.ToString()) is "Size" or "ClientSize")
				.Select(item => item.Right).OfType<VbSyntax.ObjectCreationExpressionSyntax>().FirstOrDefault();
			if (vbCreation?.ArgumentList?.Arguments.Count != 2
				|| !Int32.TryParse(VbArgumentText(vbCreation, 0), out var vbWidth)
				|| !Int32.TryParse(VbArgumentText(vbCreation, 1), out var vbHeight)) return null;
			return new Size(vbWidth, vbHeight);
		}
		var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
		var creation = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.Where(item => NormalizeTarget(item.Left.ToString()) is "Size" or "ClientSize")
			.Select(item => item.Right).OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
		if (creation?.ArgumentList?.Arguments.Count != 2
			|| !Int32.TryParse(creation.ArgumentList.Arguments[0].Expression.ToString(), out var width)
			|| !Int32.TryParse(creation.ArgumentList.Arguments[1].Expression.ToString(), out var height)) return null;
		return new Size(width, height);
	}

	static SizeF? ReadRootAutoScaleDimensions(DesignerDocumentSnapshot snapshot)
	{
		var source = snapshot.Files.FirstOrDefault(item => item.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))?.Text;
		if (String.IsNullOrEmpty(source)) return null;
		if (SnapshotIsVisualBasic(snapshot)) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(source).GetRoot();
			var vbCreation = vbRoot.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>()
				.Where(item => NormalizeTarget(item.Left.ToString()) == "AutoScaleDimensions")
				.Select(item => item.Right).OfType<VbSyntax.ObjectCreationExpressionSyntax>().FirstOrDefault();
			if (vbCreation?.ArgumentList?.Arguments.Count != 2
				|| !Single.TryParse(VbArgumentText(vbCreation, 0).TrimEnd('!', 'F', 'f'), NumberStyles.Float, CultureInfo.InvariantCulture, out var vbWidth)
				|| !Single.TryParse(VbArgumentText(vbCreation, 1).TrimEnd('!', 'F', 'f'), NumberStyles.Float, CultureInfo.InvariantCulture, out var vbHeight)) return null;
			return new SizeF(vbWidth, vbHeight);
		}
		var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
		var creation = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.Where(item => NormalizeTarget(item.Left.ToString()) == "AutoScaleDimensions")
			.Select(item => item.Right).OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
		if (creation?.ArgumentList?.Arguments.Count != 2
			|| !Single.TryParse(creation.ArgumentList.Arguments[0].Expression.ToString().TrimEnd('F', 'f'), NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
			|| !Single.TryParse(creation.ArgumentList.Arguments[1].Expression.ToString().TrimEnd('F', 'f'), NumberStyles.Float, CultureInfo.InvariantCulture, out var height)) return null;
		return new SizeF(width, height);
	}

	Type? ResolveProjectType(string name) => projectAssembly?.GetType(name, false);

	DesignerSessionState CurrentState(long baseVersion)
	{
		Trace("CurrentState resolving design host");
		var host = designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost;
		var rootControl = host?.RootComponent as Control;
		Trace("CurrentState rendering frame");
		var render = Render(rootControl, rootDesignSize);
		Trace("CurrentState building element tree");
		var tree = rootControl == null ? null : BuildElementTree(rootControl, "", host?.Container, host: host);
		Trace("CurrentState describing components");
		// Parse the designer file and index every "target.Property" assignment ONCE per
		// CurrentState call, not once per component/property. DescribeProperties used to
		// re-parse the whole designer file and re-walk its entire syntax tree for EVERY
		// browsable property of EVERY component - fine for the small sample fixtures this
		// was tested against, but O(components x properties x fileSize) blows past the 30s
		// session/open timeout on a real-world form with ~200 components (e.g. JexusManager's
		// MainForm.cs), which is exactly why that hung: the trace showed every earlier stage
		// completing and the process dying silently inside this very step.
		var assignedTargets = BuildAssignedTargetIndex();
		var eventHandlers = BuildEventHandlerIndex();
		var referenceCandidatesByType = new Dictionary<Type, List<IComponent>>();
		var components = host?.Container?.Components.Cast<IComponent>().Select(component => {
			var properties = DescribeProperties(component, assignedTargets,
				component == host.RootComponent, referenceCandidatesByType);
			if (component == host.RootComponent && rootAutoScaleDimensions.HasValue) {
				var scale = properties.FirstOrDefault(item => item.Name == "AutoScaleDimensions");
				if (scale != null)
					scale.Value = $"{rootAutoScaleDimensions.Value.Width.ToString(CultureInfo.InvariantCulture)}, {rootAutoScaleDimensions.Value.Height.ToString(CultureInfo.InvariantCulture)}";
			}
			return new DesignerComponentInfo {
			Name = component.Site?.Name ?? "",
			Type = component.GetType().FullName ?? component.GetType().Name,
			Parent = component is Control control ? control.Parent?.Site?.Name ?? ""
				: component is ToolStripItem toolStripItem ? ToolStripItemParentName(toolStripItem) : "",
			Text = component is Control textControl ? textControl.Text ?? ""
				: component is ToolStripItem textItem ? textItem.Text ?? "" : "",
			AccessibleName = PropertyText(component, "AccessibleName") is { Length: > 0 } accessibleName
				? accessibleName : component is Control namedControl && !String.IsNullOrEmpty(namedControl.Text)
					? namedControl.Text : component.Site?.Name ?? "",
			AccessibleDescription = PropertyText(component, "AccessibleDescription"),
			AccessibleRole = PropertyText(component, "AccessibleRole") is { Length: > 0 } accessibleRole
				&& accessibleRole != "Default"
				? accessibleRole : component.GetType().Name,
			X = component is Control boundsControl ? boundsControl.Left
				: component is ToolStripItem boundsItem ? boundsItem.Bounds.X : 0,
			Y = component is Control boundsControl2 ? boundsControl2.Top
				: component is ToolStripItem boundsItem2 ? boundsItem2.Bounds.Y : 0,
			SurfaceX = component is Control surfaceControl ? SurfaceLocation(surfaceControl).X
				: component is ToolStripItem surfaceItem && surfaceItem.Owner != null
					? SurfaceLocation(surfaceItem.Owner).X + surfaceItem.Bounds.X : 0,
			SurfaceY = component is Control surfaceControl2 ? SurfaceLocation(surfaceControl2).Y
				: component is ToolStripItem surfaceItem2 && surfaceItem2.Owner != null
					? SurfaceLocation(surfaceItem2.Owner).Y + surfaceItem2.Bounds.Y : 0,
			Width = component == host.RootComponent && rootDesignSize.HasValue
#if MICROSOFT_WINFORMS
				? (component as Control)?.Width ?? rootDesignSize.Value.Width
#else
				? rootDesignSize.Value.Width
#endif
				: component is Control sizeControl ? sizeControl.Width
				: component is ToolStripItem sizeItem ? sizeItem.Bounds.Width : 0,
			Height = component == host.RootComponent && rootDesignSize.HasValue
#if MICROSOFT_WINFORMS
				? (component as Control)?.Height ?? rootDesignSize.Value.Height
#else
				// The Microsoft branch above reports the root's OUTER height (Form.Height already
				// covers the native caption/border). The portable host has no non-client frame, so
				// the simulated title bar Render() reserves and paints has to be added here too -
				// otherwise the root's selection rectangle stops at the client area and no longer
				// covers the frame that was actually rendered.
				? rootDesignSize.Value.Height + (component is Form ? PortableFormTitleBarHeight : 0)
#endif
				: component is Control sizeControl2 ? sizeControl2.Height
				: component is ToolStripItem sizeItem2 ? sizeItem2.Bounds.Height : 0,
			IsTrayComponent = IsTrayComponent(component),
			IsVisible = IsEffectivelyVisible(component),
			IsControl = component is Control,
			IsDropDownItem = component is ToolStripItem { OwnerItem: not null },
			ItemInsertionBounds = component is ToolStrip insertionStrip ? FindTemplateNodeBounds(insertionStrip) : null,
			ItemInsertionStyle = ItemInsertionStyle(component),
			NewItemTypeNames = NewItemTypeNames(component),
			Properties = properties,
			Events = DescribeEvents(component, eventHandlers),
			TabHeaderBounds = FindTabHeaderBounds(component)
			};
		}).ToList() ?? [];
		Trace("CurrentState described components");
		var diagnostics = new List<DesignerDiagnostic>();
		if (portableGpuReadbackUnavailable)
			diagnostics.Add(new DesignerDiagnostic { Severity = "Warning", Message = "GPU frame readback is unavailable; showing the bounded software fallback frame." });
		return new DesignerSessionState {
			SessionId = current?.SessionId ?? sessionId ?? "",
			DocumentId = current?.DocumentId ?? "",
			Version = baseVersion,
			Accepted = true,
			RootType = host?.RootComponent?.GetType().FullName ?? "",
			ComponentCount = host?.Container?.Components.Count ?? 0,
			Render = render,
			Diagnostics = diagnostics,
			Tree = tree,
			Components = components,
			TrayComponents = components.Where(component => component.IsTrayComponent)
				.Select(component => new DesignerTrayItem { Id = component.Name, Name = component.Name, Type = component.Type }).ToList(),
			Popups = rootControl == null ? [] : CapturePopupFrames(rootControl)
		};
	}

	/// <summary>How this strip lets the user add items, mirroring the branch in
	/// ToolStripTemplateNode.SetupNewEditNode: a MenuStrip (like any dropdown) gets the editable
	/// "Type Here" cell built by SetUpMenuTemplateNode, while ToolStrip/StatusStrip and
	/// ContextMenuStrip get the split button built by SetUpToolTemplateNode. Anything that is not
	/// a strip gets None.</summary>
	static string ItemInsertionStyle(IComponent component)
	{
		// A ToolStripDropDownItem (a menu item with its own submenu, like "File") is not itself a
		// MenuStrip/ToolStripDropDown, but ToolStripDesignerUtils.GetToolStripFromComponent
		// resolves ITS "ToolStrip" as item.DropDown - so it gets the same TypeHere style as the
		// dropdown it owns, for the popup overlay this item's own expanded submenu needs.
		if (component is MenuStrip or ToolStripDropDown or ToolStripDropDownItem)
			return DesignerItemInsertionStyles.TypeHere;
		if (component is ToolStrip)
			return DesignerItemInsertionStyles.SplitButton;
		return DesignerItemInsertionStyles.None;
	}

	/// <summary>The item types this strip's template node offers, in
	/// ToolStripDesignerUtils.GetStandardItemTypes' own order - its FIRST entry is what
	/// CommitTextToDesigner falls back to when the user types a name without picking a type.
	/// Reproduced here (rather than reflected out of the internal ToolStripDesignerUtils) so both
	/// backends and the client agree on one list.</summary>
	static List<string> NewItemTypeNames(IComponent component)
	{
		const string ns = "System.Windows.Forms.";
		if (component is MenuStrip)
			return [ns + "ToolStripMenuItem", ns + "ToolStripComboBox", ns + "ToolStripTextBox"];
		// ToolStripDropDownItem: same list as ToolStripDropDown, for the reason ItemInsertionStyle
		// above documents - GetToolStripFromComponent resolves this item's own "ToolStrip" as its
		// DropDown, which is a ToolStripDropDownMenu (this list), not a plain ToolStripDropDown.
		if (component is ToolStripDropDown or ToolStripDropDownItem)
			return [ns + "ToolStripMenuItem", ns + "ToolStripComboBox", ns + "ToolStripSeparator", ns + "ToolStripTextBox"];
		if (component is StatusStrip)
			return [ns + "ToolStripStatusLabel", ns + "ToolStripProgressBar", ns + "ToolStripDropDownButton", ns + "ToolStripSplitButton"];
		if (component is ToolStrip)
			return [ns + "ToolStripButton", ns + "ToolStripLabel", ns + "ToolStripSplitButton",
				ns + "ToolStripDropDownButton", ns + "ToolStripSeparator", ns + "ToolStripComboBox",
				ns + "ToolStripTextBox", ns + "ToolStripProgressBar"];
		return [];
	}

	/// <summary>Whether a component gets an entry in the component tray. Ports the rule from
	/// System.Windows.Forms.Design.DocumentDesigner.OnComponentAdded, whose own comment reads
	/// "If the component is a toolstrip or a top level form, we should add to the tray":
	/// <code>
	/// bool addControl = designer is ToolStripDesigner
	///     || designer is not ControlDesigner cd
	///     || (cd.Control is Form form &amp;&amp; form.TopLevel);
	/// if (!addControl || !attributes.Contains(DesignTimeVisibleAttribute.Yes)) return;
	/// </code>
	/// The ToolStripDesigner clause is why every MenuStrip/ToolStrip/StatusStrip (and
	/// BindingNavigator, whose designer derives from ToolStripDesigner) appears in the tray IN
	/// ADDITION to being laid out on the surface, while ToolStripContainer - a ControlDesigner
	/// that is not a ToolStripDesigner - does not. The "not a ControlDesigner" clause covers the
	/// non-visual components (Timer/ImageList/ToolTip/dialogs) and the Controls whose designer is
	/// a plain ComponentDesigner (ContextMenuStrip, PrintPreviewDialog).
	///
	/// NOTE this is deliberately NOT ComponentTray.CanCreateComponentFromTool: that predicate
	/// answers a different question (may a toolbox item be created by dropping it ONTO the tray)
	/// and excludes the strips, which is how this started out wrong.</summary>
	/// <summary>Whether this component is actually on screen in the frame being rendered - see
	/// DesignerComponentInfo.IsVisible's own doc comment for why the client cannot draw overlays or
	/// hit-test locally without it.
	///
	/// The parent chain is walked EXPLICITLY rather than trusting <c>Control.Visible</c>'s getter to
	/// fold it in. Real WinForms does fold it (GetVisibleCore recurses to the root), but
	/// LibreWinForms' portable fork does not - its getter reports only the control's own flag, so
	/// relying on it left every child of a hidden container reporting visible on that backend, i.e.
	/// phantom overlays for controls that are not rendered. Walking here makes the answer correct on
	/// both hosts and stops this depending on a WinForms implementation detail.</summary>
	bool IsEffectivelyVisible(IComponent component)
	{
		try {
			if (component is not Control control) return true;
			var root = GetHost().RootComponent as Control;
			// If the offscreen root form itself reports invisible, the flag carries no information
			// at all - EVERY component would come back false and the client would stop drawing all
			// of its overlays. Report true in that case, i.e. keep the pre-flag behaviour.
			if (root != null && !root.Visible) return true;
			// Up to but NOT including the root, whose own visibility was just handled: a design
			// surface's root form is not a normal window and its flag says nothing about children.
			for (var current = control; current != null && current != root; current = current.Parent) {
				if (!current.Visible) return false;
				// A TabPage that is not its TabControl's selected page is off screen even when the
				// Visible FLAG says otherwise: real WinForms clears it, but LibreWinForms' portable
				// TabControl never sets it, so on that backend the unselected page and everything
				// on it claimed to be visible - i.e. the phantom-overlay bug was still live there
				// for tab controls specifically, even after the parent-chain fix. Asking the
				// TabControl which page is selected is true on both forks and needs no flag.
				if (current is TabPage page && current.Parent is TabControl tabs) {
					var index = tabs.TabPages.IndexOf(page);
					if (index >= 0 && index != tabs.SelectedIndex) return false;
				}
			}
			return true;
		} catch {
			return true;
		}
	}

	bool IsTrayComponent(IComponent component)
	{
		try {
			// The root component is the design surface itself, never a tray entry. Real WinForms
			// gets this from its TopLevel check (a hosted root form has TopLevel=false), which
			// does not hold for this out-of-process host's own root form.
			if (component == (designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost)?.RootComponent)
				return false;
			if (!TypeDescriptor.GetAttributes(component).Contains(DesignTimeVisibleAttribute.Yes))
				return false;
			var designerType = DeclaredDesignerType(component.GetType());
			if (IsToolStripDesigner(designerType)) return true;
			if (!IsControlDesigner(designerType)) return true;
			return component is Form { TopLevel: true };
		} catch {
			return false;
		}
	}

	/// <summary>The type named by the component type's DesignerAttribute registered against the
	/// IDesigner base type, or NULL when it declares none / it cannot be loaded - the same lookup
	/// ComponentTray.GetDesignerType performs.</summary>
	static Type? DeclaredDesignerType(Type componentType)
	{
		foreach (var attribute in TypeDescriptor.GetAttributes(componentType).OfType<DesignerAttribute>()) {
			if (Type.GetType(attribute.DesignerBaseTypeName, false) == typeof(IDesigner))
				return Type.GetType(attribute.DesignerTypeName, false);
		}
		return null;
	}

	static bool IsControlDesigner(Type? designerType)
		=> designerType != null && typeof(System.Windows.Forms.Design.ControlDesigner).IsAssignableFrom(designerType);

	/// <summary>The equivalent of DocumentDesigner's <c>designer is ToolStripDesigner</c> test.
	/// Matched by name across the base chain because ToolStripDesigner is INTERNAL to
	/// System.Windows.Forms.Design and cannot be referenced as a type - walking the chain still
	/// catches the derived designers that must behave the same way (BindingNavigatorDesigner).</summary>
	static bool IsToolStripDesigner(Type? designerType)
	{
		for (var type = designerType; type != null; type = type.BaseType) {
			if (type.FullName == "System.Windows.Forms.Design.ToolStripDesigner")
				return true;
		}
		return false;
	}

	static string PropertyText(IComponent component, string propertyName)
	{
		try {
			var property = TypeDescriptor.GetProperties(component)[propertyName];
			var value = property?.GetValue(component);
			return value == null ? "" : property!.Converter.ConvertToInvariantString(value) ?? "";
		} catch { return ""; }
	}

	/// <summary>Builds the element tree for the Document Outline pad (the protocol's
	/// <c>Tree</c> shape), mirroring the flat <c>Components</c> list's control hierarchy.
	/// A ToolStrip/MenuStrip/StatusStrip's real children are ToolStripItems living in its
	/// Items collection - Controls is empty (or holds only internal implementation controls
	/// like the overflow button), so walking only Controls silently drops every menu item,
	/// toolbar button and status label from the outline.
	/// <paramref name="container"/> is the design host's own component container (null when no
	/// host is active, e.g. before a document is open) - both Controls and Items must be filtered
	/// to components it actually contains. Once an IDesignerHost/ToolStripDesigner is attached
	/// (true for every open document, not just an edge case), .NET's own ToolStrip design-time
	/// support injects live in-place-edit scaffolding - System.Windows.Forms.Design.
	/// ToolStripTemplateNode's TransparentToolStrip overlay control, ItemTypeToolStripMenuItem
	/// "choose item type" entries, DesignerToolStripControlHost - directly into the SAME runtime
	/// Controls/Items collections as the real, user-added items. Unlike the flat Components list
	/// below (built from host.Container.Components, which never contains this scaffolding because
	/// it was never sited through it), a naive Controls/Items walk cannot tell them apart from
	/// genuine children by type or position - only container membership distinguishes them.</summary>
	static DesignerElementNode BuildElementTree(Control control, string path, IContainer? container,
		string layoutMode = DesignerLayoutMode.Unknown, IDesignerHost? host = null)
	{
		var controls = control.Controls.Cast<Control>()
			.Where(child => container == null || container.Components.Cast<IComponent>().Contains(child))
			.ToList();
		var children = controls.Select((child, index) => {
			var node = BuildElementTree(child, ChildPath(path, index), container,
				DesignerLayoutMode.InferFromContainerType(control.GetType().Name), host);
			// WinForms paints the FIRST control in Controls on top (BringToFront moves it to index
			// 0), the opposite of the tree-order convention the shared hit test assumes, so state the
			// measured front-to-back order as a z-index: the first control gets the highest value.
			node.ZIndex = controls.Count - 1 - index;
			return node;
		});
		// The portable LibreWinForms ToolStripItem does not expose Bounds/Owner/OwnerItem or
		// ToolStripDropDownItem at all, so this walk only exists for the real Microsoft backend.
		if (control is ToolStrip toolStrip) {
			var offset = control.Controls.Count;
			children = children.Concat(toolStrip.Items.Cast<ToolStripItem>()
				.Where(item => container == null || container.Components.Cast<IComponent>().Contains(item))
				.Select((item, index) => BuildToolStripItemNode(item, ChildPath(path, offset + index), container,
					DesignerLayoutMode.InferFromContainerType(control.GetType().Name))));
		}
		return new DesignerElementNode {
			Id = control.Site?.Name ?? control.GetType().Name,
			Name = control.Site?.Name,
			Type = control.GetType().FullName ?? control.GetType().Name,
			X = control.Left,
			Y = control.Top,
			Width = control.Width,
			Height = control.Height,
			Path = path,
			IsDesignable = true,
			LayoutMode = layoutMode,
			GridTracks = BuildTableLayoutTracks(control),
			BaselineOffset = MeasureBaseline(control, host),
			Children = children.ToList()
		};
	}

	/// <summary>The text baseline the control's own designer offers as a snap line, local to the
	/// control's top edge; null when its designer states none. Asking the designer rather than
	/// computing from font metrics keeps this identical to the guides Visual Studio's designer
	/// draws, including per-control rules (a Label's text origin, a TextBox's border). A designer
	/// that cannot answer yet - its control not created - leaves the node without one.</summary>
	static double? MeasureBaseline(Control control, IDesignerHost? host)
	{
		if (host?.GetDesigner(control) is not System.Windows.Forms.Design.ControlDesigner designer)
			return null;
		try {
			foreach (System.Windows.Forms.Design.Behavior.SnapLine line in designer.SnapLines) {
				if (line.SnapLineType == System.Windows.Forms.Design.Behavior.SnapLineType.Baseline)
					return line.Offset;
			}
		} catch (Exception) {
			// A third-party designer's SnapLines may throw; a missing baseline is the safe answer.
		}
		return null;
	}

	/// <summary>Publishes the live TableLayoutPanel geometry rather than reconstructing it from
	/// source. Percent and Auto tracks can resolve differently once minimum sizes, spans and the
	/// actual container bounds participate in layout, so the canvas must start from WinForms'
	/// measured arrays. WinForms creates default Absolute styles at runtime even when Designer.cs
	/// authored no style at all, so this measured-only slice deliberately leaves Unit/Value empty;
	/// a future source mutation must establish authored style syntax independently.</summary>
	static DesignerGridGuides? BuildTableLayoutTracks(Control control)
	{
		if (control is not TableLayoutPanel table)
			return null;
		var rows = table.GetRowHeights();
		var columns = table.GetColumnWidths();
		var result = new DesignerGridGuides { Accepted = true };
		var offset = 0d;
		for (var index = 0; index < rows.Length; index++) {
			result.RowTracks.Add(new DesignerGridTrackInfo {
				Offset = offset, Size = rows[index]
			});
			offset += rows[index];
		}
		offset = 0;
		for (var index = 0; index < columns.Length; index++) {
			result.ColumnTracks.Add(new DesignerGridTrackInfo {
				Offset = offset, Size = columns[index]
			});
			offset += columns[index];
		}
		return result;
	}

	static string ChildPath(string path, int index) =>
		path.Length == 0 ? index.ToString(CultureInfo.InvariantCulture) : path + "," + index.ToString(CultureInfo.InvariantCulture);

	/// <summary>A ToolStripDropDownItem's own children (a menu item's submenu) live in
	/// DropDownItems, not Items - only ToolStrip/MenuStrip/StatusStrip themselves use Items.
	/// Same container-membership filtering as BuildElementTree: a submenu can carry the same
	/// unsited ItemTypeToolStripMenuItem/DesignerToolStripControlHost design-time scaffolding.</summary>
	static DesignerElementNode BuildToolStripItemNode(ToolStripItem item, string path, IContainer? container,
		string layoutMode)
	{
		var children = item is ToolStripDropDownItem dropDown
			? dropDown.DropDownItems.Cast<ToolStripItem>()
				.Where(child => container == null || container.Components.Cast<IComponent>().Contains(child))
				.Select((child, index) => BuildToolStripItemNode(child, ChildPath(path, index), container,
					DesignerLayoutMode.InferFromContainerType(item.GetType().Name)))
				.ToList()
			: [];
		return new DesignerElementNode {
			Id = item.Site?.Name ?? item.GetType().Name,
			Name = item.Site?.Name,
			Type = item.GetType().FullName ?? item.GetType().Name,
			X = item.Bounds.X,
			Y = item.Bounds.Y,
			Width = item.Bounds.Width,
			Height = item.Bounds.Height,
			Path = path,
			IsDesignable = true,
			LayoutMode = layoutMode,
			Children = children
		};
	}

	/// <summary>Parses the current designer file once and indexes every "target.Event" (or bare
	/// root-shorthand "Event") handler assignment it contains, so <see cref="DescribeEvents"/>
	/// can do an O(1) lookup per event instead of re-parsing the file and re-walking its whole
	/// syntax tree for EVERY browsable event of EVERY component - the same
	/// O(components x events x fileSize) blowup <see cref="BuildAssignedTargetIndex"/> fixes for
	/// properties, just worse here because the old code didn't even hoist the parse out of the
	/// per-event closure.</summary>
	Dictionary<string, string> BuildEventHandlerIndex()
	{
		var handlers = new Dictionary<string, string>(StringComparer.Ordinal);
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(CurrentDesignerFile().Text).GetRoot();
			foreach (var statement in vbRoot.DescendantNodes().OfType<VbSyntax.AddRemoveHandlerStatementSyntax>()) {
				if (!statement.IsKind(Vb.SyntaxKind.AddHandlerStatement)) continue;
				var key = NormalizeTarget(statement.EventExpression.ToString());
				if (handlers.ContainsKey(key)) continue;
				var handler = statement.DelegateExpression.ToString();
				if (handler.StartsWith("AddressOf Me.", StringComparison.Ordinal)) handler = handler["AddressOf ".Length..];
				if (handler.StartsWith("Me.", StringComparison.Ordinal)) handler = handler[3..];
				handlers[key] = handler;
			}
		} else {
			var root = CSharpSyntaxTree.ParseText(CurrentDesignerFile().Text).GetCompilationUnitRoot();
			foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>()) {
				if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression)) continue;
				var key = NormalizeTarget(assignment.Left.ToString());
				if (handlers.ContainsKey(key)) continue;
				var handler = assignment.Right.ToString();
				if (handler.StartsWith("this.", StringComparison.Ordinal)) handler = handler[5..];
				handlers[key] = handler;
			}
		}
		return handlers;
	}

	List<DesignerEventInfo> DescribeEvents(IComponent component, Dictionary<string, string> handlerIndex)
	{
		// The root form is conventionally emitted as "this.Load += ..." rather
		// than "Form1.Load += ...".  A component's site name is still Form1,
		// so accept the root shorthand as well as the normal component target.
		var isRootComponent = ReferenceEquals(component, GetHost().RootComponent);
		return TypeDescriptor.GetEvents(component).Cast<EventDescriptor>().Where(item => item.IsBrowsable).Select(item => {
			var target = (component.Site?.Name ?? "") + "." + item.Name;
			var handler = handlerIndex.TryGetValue(target, out var direct) ? direct
				: isRootComponent && handlerIndex.TryGetValue(item.Name, out var bare) ? bare : "";
			return new DesignerEventInfo { Name = item.Name, Category = item.Category ?? "Action", HandlerTypeName = item.EventType?.FullName ?? item.EventType?.Name ?? "", Handler = handler };
		}).ToList();
	}

	static bool EventTargetMatches(string normalizedTarget, string componentTarget, string eventName, bool isRootComponent)
		=> normalizedTarget == componentTarget || (isRootComponent && normalizedTarget == eventName);

	/// <summary>Parses the current designer file once and indexes every "target.Property"
	/// assignment it contains, so <see cref="DescribeProperties"/> can do an O(1) lookup per
	/// property instead of re-parsing the file and re-walking its whole syntax tree for each
	/// one. See the call site in <see cref="CurrentState"/> for why this matters.</summary>
	HashSet<string> BuildAssignedTargetIndex()
	{
		var targets = new HashSet<string>(StringComparer.Ordinal);
		if (IsVisualBasic) {
			var vbRoot = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(CurrentDesignerFile().Text).GetRoot();
			foreach (var assignment in vbRoot.DescendantNodes().OfType<VbSyntax.AssignmentStatementSyntax>())
				targets.Add(NormalizeTarget(assignment.Left.ToString()));
		} else {
			var root = CSharpSyntaxTree.ParseText(CurrentDesignerFile().Text).GetCompilationUnitRoot();
			foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
				if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
					targets.Add(NormalizeTarget(assignment.Left.ToString()));
		}
		return targets;
	}

	List<DesignerPropertyInfo> DescribeProperties(IComponent component, HashSet<string> assignedTargets, bool isRootComponent,
		Dictionary<Type, List<IComponent>> referenceCandidatesByType)
	{
		var result = new List<DesignerPropertyInfo>();
		var elementId = component.Site?.Name ?? "";
		foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(component)) {
			if (!property.IsBrowsable || property.Name is "Site" or "Container" or "Parent") continue;
			var assignedInSource = assignedTargets.Contains(PropertyTarget(elementId, property.Name, isRootComponent));
			var isImageProperty = IsResourceImageProperty(property.PropertyType);
			var referenceCandidates = ComponentReferenceCandidates(GetHost(), component, property, referenceCandidatesByType);
			object? value;
			string serialized;
			try {
				value = property.GetValue(component);
#if !MICROSOFT_WINFORMS
				// A shadowed Visible reads back as what the SOURCE says, not the live control's
				// on-surface true - otherwise the Properties pad would show the user's own
				// Visible=false edit snapping straight back to True. See shadowedVisible.
				if (property.Name == "Visible" && shadowedVisible.TryGetValue(elementId, out var shadowed))
					value = shadowed;
#endif
				if (value == null) serialized = isImageProperty && assignedInSource ? "[binary]" : "";
				else if (referenceCandidates != null) serialized = (value as IComponent)?.Site?.Name ?? "";
				// Portable resource loading can materialize an image entry as a byte-backed
				// object rather than a concrete System.Drawing.Image. The property contract is
				// still Image, so expose it as an opaque binary DDP value instead of leaking an
				// implementation-specific converter string to the Properties pad.
				else if (value is Image || isImageProperty) serialized = "[binary]";
				else if (value is Type typeValue) serialized = typeValue.FullName ?? typeValue.Name;
				else if (value is Padding padding) serialized = $"{padding.Left}, {padding.Top}, {padding.Right}, {padding.Bottom}";
				else if (value is Font font) serialized = $"{font.Name}, {font.Size.ToString(CultureInfo.InvariantCulture)}, {font.Style}";
				else if (value is SizeF size) serialized = $"{size.Width.ToString(CultureInfo.InvariantCulture)}, {size.Height.ToString(CultureInfo.InvariantCulture)}";
				else if (property.Converter.CanConvertTo(typeof(string))) serialized = property.Converter.ConvertToInvariantString(value) ?? "";
				else serialized = "[binary]";
			} catch { continue; }
			var info = new DesignerPropertyInfo {
				Name = property.Name,
				DisplayName = property.DisplayName ?? property.Name,
				Description = property.Description ?? "",
				Category = property.Category ?? "Misc",
				TypeName = property.PropertyType.FullName ?? property.PropertyType.Name,
				Value = serialized,
				IsNull = value == null && !(isImageProperty && assignedInSource),
				IsReadOnly = property.IsReadOnly || (referenceCandidates == null && !property.Converter.CanConvertFrom(typeof(string))
					&& property.PropertyType != typeof(Padding) && property.PropertyType != typeof(Font)
					&& property.PropertyType != typeof(SizeF) && property.PropertyType != typeof(Type)),
				// The source assignment is authoritative. Some LibreWinForms
				// descriptors keep returning true after ResetValue.
				ShouldSerialize = assignedInSource,
				IsEnum = property.PropertyType.IsEnum,
				Kind = PropertyKind(property.PropertyType)
			};
			if (snapshotLoader?.TryGetResourceOrigin(component, property.Name, out var resourceFileName, out var resourceKey) == true) {
				info.ResourceFileName = resourceFileName;
				info.ResourceKey = resourceKey;
				// This capability grants no child write access. The IDE must still own any
				// resource transaction, after preserving source/resource atomicity and undo.
				if (isImageProperty)
					info.EditorKind = "ResourceImage";
			}
			result.Add(info);
			if (property.PropertyType.IsEnum)
				result[^1].AllowedValues.AddRange(Enum.GetNames(property.PropertyType));
			if (property.Name == "Items" && TryGetEditableItems(component, out var editableItems) && editableItems != null) {
				var texts = editableItems.Cast<string>().ToList();
				result[^1].Kind = "Collection";
				result[^1].EditorKind = "ItemsCollection";
				result[^1].IsReadOnly = true;
				result[^1].Value = "(" + texts.Count + (texts.Count == 1 ? " item)" : " items)");
				result[^1].CollectionItems = texts;
				result[^1].ShouldSerialize = texts.Count > 0;
			}
			if (property.PropertyType == typeof(Type) && !property.IsReadOnly)
				result[^1].SuggestedValues = TypeSuggestions();
			if (property.Name == "Columns" && component is ListView columnOwner && TryGetSitedColumns(columnOwner, out var columns)) {
				// Each entry is a ColumnHeader component's Text; Width and the rest stay editable on
				// the column component itself.
				result[^1].Kind = "Collection";
				result[^1].EditorKind = "ItemsCollection";
				result[^1].IsReadOnly = true;
				result[^1].Value = "(" + columns.Count + (columns.Count == 1 ? " column)" : " columns)");
				result[^1].CollectionItems = columns.Select(column => column.Text ?? "").ToList();
				result[^1].ShouldSerialize = columns.Count > 0;
			}
			if (property.Name == "Images" && component is ImageList imageListOwner && TryDescribeImageList(imageListOwner, out var imageEntries)) {
				result[^1].Kind = "Collection";
				result[^1].EditorKind = "ImageListImages";
				result[^1].IsReadOnly = true;
				result[^1].Value = "(" + imageEntries.Count + (imageEntries.Count == 1 ? " image)" : " images)");
				result[^1].ImageListImages = imageEntries;
				result[^1].ImageListSize = imageListOwner.ImageSize.Width + "," + imageListOwner.ImageSize.Height;
				result[^1].ImageListColorDepth = imageListOwner.ColorDepth.ToString();
				result[^1].ShouldSerialize = imageEntries.Count > 0;
				// The stream's origin in the form's .resx, when the source proves one; the IDE owns any rewrite of it.
				if (snapshotLoader?.TryGetResourceOrigin(imageListOwner, "ImageStream", out var streamFile, out var streamKey) == true) {
					result[^1].ResourceFileName = streamFile;
					result[^1].ResourceKey = streamKey;
				}
			}
			if (property.Name == "Columns" && component is DataGridView gridOwner && TryGetSitedGridColumns(gridOwner, out var gridColumns)
				&& !SourceMentionsMember(gridOwner.Site?.Name ?? "", "Rows")) {
				result[^1].Kind = "Collection";
				result[^1].EditorKind = "ItemsCollection";
				result[^1].IsReadOnly = true;
				result[^1].Value = "(" + gridColumns.Count + (gridColumns.Count == 1 ? " column)" : " columns)");
				result[^1].CollectionItems = gridColumns.Select(column => column.HeaderText ?? "").ToList();
				result[^1].ShouldSerialize = gridColumns.Count > 0;
			}
			if (property.Name == "TabPages" && component is TabControl pagesOwner && TryGetSitedPages(pagesOwner, out var sitedPages)) {
				result[^1].Kind = "Collection";
				result[^1].EditorKind = "ItemsCollection";
				result[^1].IsReadOnly = true;
				result[^1].Value = "(" + sitedPages.Count + (sitedPages.Count == 1 ? " page)" : " pages)");
				result[^1].CollectionItems = sitedPages.Select(page => page.Text ?? "").ToList();
				result[^1].ShouldSerialize = sitedPages.Count > 0;
			}
			if (property.Name == "Items" && component is ListView itemsOwner && TryReadListViewItems(itemsOwner.Site?.Name ?? "", out var listViewTexts)) {
				result[^1].Kind = "Collection";
				result[^1].EditorKind = "ItemsCollection";
				result[^1].IsReadOnly = true;
				result[^1].Value = "(" + listViewTexts.Count + (listViewTexts.Count == 1 ? " item)" : " items)");
				result[^1].CollectionItems = listViewTexts;
				result[^1].ShouldSerialize = listViewTexts.Count > 0;
			}
			if (property.Name == "Nodes" && component is TreeView treeView && TryGetPlainTreeLines(treeView.Nodes, out var treeLines)) {
				// The same list editor as Items: a node's depth is its leading two-space indent.
				result[^1].Kind = "Collection";
				result[^1].EditorKind = "ItemsCollection";
				result[^1].IsReadOnly = true;
				var count = treeLines.Count;
				result[^1].Value = "(" + count + (count == 1 ? " node)" : " nodes)");
				result[^1].CollectionItems = treeLines;
				result[^1].ShouldSerialize = count > 0;
			}
			if (property.PropertyType == typeof(string[]) && !property.IsReadOnly) {
				// TextBox.Lines and the like: the same list editor as Items, committed through
				// design/set-string-array as a "new string[] { ... }" assignment.
				string[] lines;
				try { lines = property.GetValue(component) as string[] ?? Array.Empty<string>(); }
				catch { lines = Array.Empty<string>(); }
				result[^1].Kind = "Collection";
				result[^1].EditorKind = "ItemsCollection";
				result[^1].IsReadOnly = true;
				result[^1].Value = "(" + lines.Length + (lines.Length == 1 ? " line)" : " lines)");
				result[^1].CollectionItems = lines.ToList();
			}
			if (referenceCandidates != null) {
				result[^1].EditorKind = "ComponentReference";
				result[^1].AllowedValues.Add("");
				result[^1].AllowedValues.AddRange(referenceCandidates.Select(candidate => candidate.Site!.Name!));
			}
		}
		// RichTextBox.Rtf is intentionally non-browsable in WinForms, but it is the control's
		// native, lossless persistence format. Expose it only through the dedicated editor;
		// SetProperty still validates and serializes the real descriptor.
		if (String.Equals(component.GetType().FullName, "System.Windows.Forms.RichTextBox", StringComparison.Ordinal)
			&& TypeDescriptor.GetProperties(component)["Rtf"] is { } rtf)
			result.Add(new DesignerPropertyInfo {
				Name = "Rtf", DisplayName = "Formatted Text", Category = "Appearance",
				TypeName = typeof(string).FullName!, Kind = "String", Value = rtf.GetValue(component) as string ?? "",
				// Rtf has no ResetValue/ShouldSerialize contract. Do not advertise a grid
				// reset that the host cannot carry out; the dedicated editor owns writes.
				EditorKind = "RichText", IsReadOnly = false, ShouldSerialize = false
			});
		return result;
	}

	static string PropertyKind(Type type) =>
		type == typeof(bool) ? "Boolean"
		: type.IsEnum ? "Enum"
		: type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
			|| type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
			|| type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? "Number"
		: type == typeof(Color) ? "Color"
		: type == typeof(Padding) ? "Thickness"
		: type == typeof(Point) ? "Point"
		: type == typeof(Size) || type == typeof(SizeF) ? "Size"
		: type == typeof(Rectangle) || type == typeof(RectangleF) ? "Rect"
		: type == typeof(Uri) ? "Uri"
		: IsResourceImageProperty(type) ? "Unsupported"
		: "String";

	static bool IsResourceImageProperty(Type type)
		=> typeof(Image).IsAssignableFrom(type) || type == typeof(Icon);

	/// <summary>Position relative to the ROOT design component, which is the coordinate space the
	/// render frame and hit-testing both use.
	///
	/// Anchoring on the root explicitly rather than on "the first parentless control" matters
	/// because the two runtimes parent the root differently: under LibreWinForms the root Form has
	/// no parent, so the old walk stopped there by accident, but the Microsoft WinForms design
	/// surface hosts the root inside its own frame control and positions it at an offset (15,15).
	/// The old loop therefore folded that frame offset into every component, putting the reported
	/// coordinates 15px off from the very bitmap and hit-test results they are meant to line up
	/// with.</summary>
	/// <summary>A ToolStripItem's logical parent for the Properties Pad / flat component list:
	/// a submenu item's parent is the ToolStripDropDownItem that owns its dropdown (OwnerItem);
	/// a top-level item's parent is the ToolStrip/MenuStrip/StatusStrip itself (Owner).</summary>
	static string ToolStripItemParentName(ToolStripItem item) =>
		item.OwnerItem?.Site?.Name ?? item.Owner?.Site?.Name ?? "";

	Point SurfaceLocation(Control control)
	{
		var root = (designSurface?.GetService(typeof(IDesignerHost)) as IDesignerHost)?.RootComponent as Control;
		if (control == root) return Point.Empty;
		var offset = RootClientOffset(root);
#if MICROSOFT_WINFORMS
		// Measure against the root's SCREEN origin rather than by summing Locations up the parent
		// chain. The chain walk cannot describe an expanded menu dropdown: the designer parents
		// those into its own adorner window, so the loop below never reaches the root and sums
		// unrelated ancestor offsets - which is exactly why the dropdown items' outlines and name
		// labels landed away from the dropdown the designer had actually drawn. Screen-relative
		// measurement is also the basis PaintExpandedDropDowns composites with, so the reported
		// geometry and the painted pixels agree by construction.
		// Context menus are floating editors anchored at the canvas origin. Use the same
		// translation for their items and nested dropdowns so adorners and hit tests agree.
		if (control is ContextMenuStrip)
			return Point.Empty;
		if (control is ToolStripDropDown nested && nested.OwnerItem is { Owner: { } owner }) {
			var contextOwner = owner;
			for (var depth = 0; depth < 32 && contextOwner is ToolStripDropDown parentDropDown; depth++) {
				if (parentDropDown is ContextMenuStrip) {
					var nestedOrigin = control.PointToScreen(Point.Empty);
					var menuOrigin = parentDropDown.PointToScreen(Point.Empty);
					return new Point(nestedOrigin.X - menuOrigin.X, nestedOrigin.Y - menuOrigin.Y);
				}
				if (parentDropDown.OwnerItem?.Owner is not { } nextOwner) break;
				contextOwner = nextOwner;
			}
		}
		if (root != null && root.IsHandleCreated && control.IsHandleCreated) {
			try {
				var origin = control.PointToScreen(Point.Empty);
				var rootOrigin = root.PointToScreen(Point.Empty);
				return new Point(origin.X - rootOrigin.X + offset.X, origin.Y - rootOrigin.Y + offset.Y);
			} catch {
				// Fall through to the Location walk below.
			}
		}
#endif
		var point = control.Location;
		for (var parent = control.Parent; parent != null && parent != root; parent = parent.Parent)
			point.Offset(parent.Location);
		point.Offset(offset.X, offset.Y);
		return point;
	}

	/// <summary>For a TabControl: each tab HEADER's own rect (TabControl.GetTabRect(i), one per
	/// TabPages[i] in order), in the same absolute surface basis SurfaceX/Y use - see
	/// DesignerComponentInfo.TabHeaderBounds's own doc comment on why the client needs this (a tab
	/// header is not a component of its own; nothing else reports its geometry). Empty for
	/// anything that is not a TabControl, or if GetTabRect throws (an unlaid-out/headless control
	/// can decline to report a rect - the same defensive posture CapturePopupFrames takes).</summary>
	List<DesignerRectangle> FindTabHeaderBounds(IComponent component)
	{
		var result = new List<DesignerRectangle>();
		if (component is not TabControl tabs) return result;
		try {
			var origin = SurfaceLocation(tabs);
			for (var i = 0; i < tabs.TabCount; i++) {
				var rect = tabs.GetTabRect(i);
				result.Add(new DesignerRectangle { X = origin.X + rect.X, Y = origin.Y + rect.Y, Width = rect.Width, Height = rect.Height });
			}
		} catch (Exception exception) {
			Trace("FindTabHeaderBounds failed: " + exception.Message);
		}
		// LibreWinForms does not implement TabControl.TabCount/GetTabRect, so tab-header hit-
		// testing (and therefore click-to-switch-tab) is Microsoft-backend only for now - the
		// client falls back to its existing generic hit-test when this list is empty.
		return result;
	}

	/// <summary>Captures every menu dropdown the designer currently has expanded as ITS OWN
	/// bitmap/frame, rather than compositing them into the root frame.
	///
	/// Needed because ToolStripMenuItemDesigner keeps a selected menu item's dropdown open
	/// (TopLevel=false, AutoClose=false, ShowDropDown()), and it is parented into the designer's
	/// own adorner window rather than the form, so Form.DrawToBitmap never sees it - the items'
	/// geometry was already reported correctly while their pixels were missing. Reporting each as
	/// an independent DesignerPopupFrame (rather than baking it into the shared bitmap) is what
	/// lets the client host it as its own WPF overlay: pointer/keyboard input aimed at the popup
	/// then hit-tests and drags against just that surface, never needing to reverse through the
	/// root form's own coordinate space or fight the root frame's own adorners for z-order.</summary>
	List<DesignerPopupFrame> CapturePopupFrames(Control root)
	{
		var frames = new List<DesignerPopupFrame>();
		try {
			// A ContextMenuStrip's own ToolStripDropDownDesigner.InitializeDropDown shows it
			// UNCONDITIONALLY as soon as the component exists (not gated on selection - see
			// SelectedContextMenuStripPopups), via a synthetic owner item ExpandedDropDowns'
			// existing MenuStrip-oriented walk happens to discover it through - reported under
			// that synthetic item's own (wrong, internal) element id, not the real strip's name.
			// Every ContextMenuStrip reference is therefore excluded here and re-added, correctly
			// named and selection-gated, by SelectedContextMenuStripPopups below.
			var fromExpanded = ExpandedDropDowns(root).Where(entry => entry.DropDown is not ContextMenuStrip).ToList();
			var fromContextMenus = SelectedContextMenuStripPopups().ToList();
			foreach (var (ownerId, dropDown) in fromExpanded.Concat(fromContextMenus)) {
				if (dropDown.Width <= 0 || dropDown.Height <= 0)
					continue;
				// SurfaceLocation, not a second copy of the offset math: this must be the same
				// basis DesignerComponentInfo.SurfaceX/Y use, or the overlay lands in the wrong
				// spot relative to everything else the client draws.
				var origin = SurfaceLocation(dropDown);
				using var bitmap = new Bitmap(dropDown.Width, dropDown.Height);
				dropDown.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
				using var stream = new MemoryStream();
				bitmap.Save(stream, ImageFormat.Png);
				frames.Add(new DesignerPopupFrame {
					OwnerElementId = ownerId,
					X = origin.X,
					Y = origin.Y,
					TypeHereBounds = FindTemplateNodeBounds(dropDown),
					Render = new DesignerRenderFrame {
						Sequence = Interlocked.Increment(ref frameSequence),
						Width = bitmap.Width,
						Height = bitmap.Height,
						Dpi = 1,
						PngBase64 = Convert.ToBase64String(stream.ToArray())
					}
				});
			}
		} catch (Exception exception) {
			// A popup that cannot be captured must not cost us the whole frame.
			Trace("CapturePopupFrames failed: " + exception.Message);
		}
		return frames;
	}

	/// <summary>The real "Type Here" template node's own bounds within a dropdown, or null when
	/// it has none. Matched by type name (both "DesignerToolStripControlHost", the wrapper
	/// ToolStripTemplateNode.EnterInSituEdit swaps in) rather than by reference, since
	/// System.Windows.Forms.Design's template-node types are all internal and cannot be named
	/// directly from this assembly - the same constraint <see cref="IsToolStripDesigner"/> already
	/// works around for the designer TYPES themselves.</summary>
	static DesignerRectangle? FindTemplateNodeBounds(ToolStrip dropDown)
	{
		foreach (ToolStripItem item in dropDown.Items) {
			// Popup visibility is temporarily suppressed during root bitmap capture;
			// its template still belongs to the separately rendered popup frame.
			if (dropDown is not ToolStripDropDown && !item.Visible) continue;
			if (item.GetType().Name is not ("DesignerToolStripControlHost" or "ToolStripControlHost"))
				continue;
			return new DesignerRectangle { X = item.Bounds.X, Y = item.Bounds.Y, Width = item.Bounds.Width, Height = item.Bounds.Height };
		}
		return null;
	}

	/// <summary>Every dropdown currently held open by the designer, outermost first so a client
	/// that z-orders by list position still stacks nested submenus correctly. Paired with the
	/// element id of the ToolStripDropDownItem that owns it (empty for a strip's own
	/// ContextMenuStrip), which the client uses to keep the same overlay across frames.</summary>
	static IEnumerable<(string OwnerElementId, ToolStripDropDown DropDown)> ExpandedDropDowns(Control root)
	{
		var strips = root.Controls.Cast<Control>().OfType<ToolStrip>().ToList();
		var pending = new Queue<ToolStrip>(strips);
		while (pending.Count > 0) {
			var strip = pending.Dequeue();
			foreach (var item in strip.Items.Cast<ToolStripItem>().OfType<ToolStripDropDownItem>()) {
				if (item.DropDown is not { Visible: true } dropDown)
					continue;
				yield return (item.Site?.Name ?? "", dropDown);
				pending.Enqueue(dropDown);
			}
		}
	}

	/// <summary>ContextMenuStrip is never parented into <c>root.Controls</c> - it lives only in the
	/// tray - so <see cref="ExpandedDropDowns"/> never finds it. Unlike a MenuStrip submenu, the
	/// real ContextMenuStripDesigner (ToolStripDropDownDesigner.InitializeDropDown) shows it
	/// unconditionally as soon as the component exists, not gated on selection - so reusing that
	/// designer's own Visible flag would make every ContextMenuStrip permanently overlay the
	/// surface. OpenDevelop deliberately narrows this to "shown only while its tray icon, or one
	/// of its own items, is selected", matching the "default hidden, select the tray icon to
	/// edit" UX rather than VS's always-on behaviour. Keeping it visible for an item selection is
	/// necessary to edit its text, properties, and nested submenus in the floating surface.</summary>
	IEnumerable<(string OwnerElementId, ToolStripDropDown DropDown)> SelectedContextMenuStripPopups()
	{
		var host = GetHost();
		if (designSurface?.GetService(typeof(ISelectionService)) is not ISelectionService selection)
			yield break;
		var selected = selection.GetSelectedComponents().Cast<IComponent>().ToHashSet();
		foreach (var strip in host.Container.Components.Cast<IComponent>().OfType<ContextMenuStrip>()) {
			if (strip.Width <= 0 || strip.Height <= 0)
				continue;
			var owns = selected.Contains(strip)
				|| selected.OfType<ToolStripItem>().Any(item => BelongsTo(item, strip));
			if (owns)
				yield return (strip.Site?.Name ?? "", strip);
		}
	}

	/// <summary>Whether an item, or a nested submenu item, belongs to this exact
	/// ContextMenuStrip. The walk stops at the first owning strip so it does not escape through
	/// the designer's internal synthetic owner item.</summary>
	static bool BelongsTo(ToolStripItem item, ContextMenuStrip strip)
	{
		ToolStripItem? current = item;
		var guard = 0;
		while (current != null && guard++ < 32) {
			if (current.Owner == strip)
				return true;
			current = (current.Owner as ToolStripDropDown)?.OwnerItem;
		}
		return false;
	}

	/// <summary>Clips designer-only surrogate menu strips out of the form bitmap without
	/// closing dropdowns. Toggling Visible fires Closed and destroys native editing nodes.</summary>
	List<(Control Control, Region? Region)> ClipContextMenuDesignerHelpers(Control root)
	{
		var restore = new List<(Control, Region?)>();
		// Context menus themselves live in the designer's adorner window, not root.Controls.
		// Their selection-gated popup frames are captured separately; don't hide/close them.
		var menus = GetHost().Container.Components.Cast<IComponent>().OfType<ContextMenuStrip>().ToArray();
		foreach (var strip in root.Controls.Cast<Control>().OfType<ToolStrip>().ToArray()) {
			if (strip.Visible && strip.Items.Cast<ToolStripItem>().OfType<ToolStripDropDownItem>()
				.Any(item => menus.Contains(item.DropDown))) {
				if (GetHost().Container.Components.Cast<IComponent>().Contains(strip)) continue;
				restore.Add((strip, strip.Region?.Clone()));
				strip.Region = new Region(Rectangle.Empty);
			}
		}
		return restore;
	}


	/// <summary>Height of the simulated title bar <see cref="PaintFormChrome"/> overlays on a
	/// portable-host Form render, since LibreWinForms's Form.Size is client-only (no real HWND,
	/// so no native non-client frame to leave room for). Shared with <see cref="RootClientOffset"/>
	/// and <see cref="Render"/> so the reported hit-test/selection geometry always matches the
	/// extra space actually reserved and painted.</summary>
	const int PortableFormTitleBarHeight = 30;

	/// <summary>How far the painted bitmap's origin sits outside the root form's client area:
	/// native Form.DrawToBitmap paints the outer window (border + caption) while every child
	/// Location is client-space. Surface (bitmap) coordinates therefore differ from client
	/// coordinates by this much, in both directions - <see cref="SurfaceLocation"/> adds it when
	/// reporting bounds, and <see cref="HitTest"/> must subtract it before comparing an incoming
	/// surface point against client-space Control/ToolStripItem bounds. On the portable host this
	/// is the simulated title bar's height instead of a native border (see PortableFormTitleBarHeight).</summary>
	static Point RootClientOffset(Control? root)
	{
#if MICROSOFT_WINFORMS
		if (root is Form form) {
			var border = Math.Max(0, (form.Width - form.ClientSize.Width) / 2);
			return new Point(border, Math.Max(border, form.Height - form.ClientSize.Height - border));
		}
#else
		if (root is Form)
			return new Point(0, PortableFormTitleBarHeight);
#endif
		return Point.Empty;
	}

	DesignerRenderFrame? Render(Control? root, Size? designSize)
	{
		if (root == null) return null;
		Trace("Render sizing root");
		if (root.Width <= 0 || root.Height <= 0) root.Size = new Size(300, 200);
		Trace("Render creating root control");
		root.CreateControl();
#if !MICROSOFT_WINFORMS
		HidePortableNativeWindows();
#endif
		Trace("Render laying out root control");
		root.PerformLayout();
		var renderSize = designSize ?? root.Size;
#if MICROSOFT_WINFORMS
		// Do not crop the non-client frame that DrawToBitmap actually paints. Its dimensions
		// must match the root selection rectangle and the child SurfaceLocation offsets above.
		if (root is Form)
			renderSize = root.Size;
#else
		// The portable Form has no native non-client frame to grow the bitmap for, so reserve the
		// extra room ourselves - matching the RootClientOffset used for hit-testing/selection.
		if (root is Form)
			renderSize = new Size(renderSize.Width, renderSize.Height + PortableFormTitleBarHeight);
#endif
		Trace("Render creating bitmap");
		var bitmap = new Bitmap(Math.Max(1, renderSize.Width), Math.Max(1, renderSize.Height));
		Trace("Render creating graphics");
		using (var graphics = Graphics.FromImage(bitmap)) {
#if MICROSOFT_WINFORMS
			// The Microsoft child uses the native WinForms paint pipeline. The portable host keeps
			// its software painter below because it has no HWND/GDI implementation to capture.
			var hiddenContextMenus = ClipContextMenuDesignerHelpers(root);
			try {
				root.DrawToBitmap(bitmap, new Rectangle(Point.Empty, renderSize));
			} finally {
				foreach (var (control, region) in hiddenContextMenus)
					control.Region = region;
			}
			// DesignSurface Forms lack a real HWND so DrawToBitmap never paints the non-client
			// frame.  Overlay a simulated title bar so the designer surface visually identifies
			// the root component as a windowed form.
			if (root is Form formForChrome)
				PaintFormChrome(formForChrome, graphics, new Rectangle(Point.Empty, renderSize));
#else
			Trace("Render painting portable frame");
			if (designSize.HasValue) {
				var titleBarHeight = root is Form ? PortableFormTitleBarHeight : 0;
				var clientBounds = new Rectangle(0, titleBarHeight, renderSize.Width, Math.Max(0, renderSize.Height - titleBarHeight));
				PaintStandardControl(root, graphics, clientBounds);
				foreach (Control child in root.Controls) {
					var state = graphics.Save();
					graphics.TranslateTransform(child.Left, child.Top + titleBarHeight);
					PaintControl(child, graphics);
					graphics.Restore(state);
				}
				// LibreWinForms Forms have no real HWND, so nothing paints the title bar the way
				// DrawToBitmap's native non-client frame does for MICROSOFT_WINFORMS above - overlay
				// the same simulated chrome so the root component still visually reads as a form.
				if (root is Form formForChrome)
					PaintFormChrome(formForChrome, graphics, new Rectangle(Point.Empty, renderSize));
			} else PaintControl(root, graphics);
#endif
		}
		Trace("Render encoding PNG");
#if MICROSOFT_WINFORMS
		using var stream = new MemoryStream();
		bitmap.Save(stream, ImageFormat.Png);
		// Width/Height must be read before Dispose() - a disposed Bitmap's GDI+ handle is invalid,
		// and querying either property throws ArgumentException("Parameter is not valid.") instead
		// of returning the size it had a moment ago.
		var bitmapWidth = bitmap.Width;
		var bitmapHeight = bitmap.Height;
		bitmap.Dispose();
		Trace("Render encoded PNG");
		return new DesignerRenderFrame {
			Sequence = Interlocked.Increment(ref frameSequence),
			Width = bitmapWidth,
			Height = bitmapHeight,
			// The portable renderer paints in WinForms logical pixels. ProGPU's
			// Bitmap does not expose a device resolution on macOS.
			Dpi = 1,
			PngBase64 = Convert.ToBase64String(stream.ToArray())
		};
#else
		if (portableGpuReadbackUnavailable) {
			bitmap.Dispose();
			return FallbackFrame(root, renderSize);
		}
		var encoding = Task.Run(() => {
			try {
				using var stream = new MemoryStream();
				bitmap.Save(stream, ImageFormat.Png);
				return Convert.ToBase64String(stream.ToArray());
			} finally {
				bitmap.Dispose();
			}
		});
		// The first readback of a process is slow, not blocked: it brings up the GPU device, and took 6-8 s
		// on Windows arm64 (measured on the pumping thread and off it alike), against well under a second
		// once warm. A 2 s budget for that one turned a working renderer into the outline-only fallback for
		// the rest of the session. Give it its own budget - still inside the client's 30 s open - and if a
		// timed-out readback does finish, treat it as slow rather than broken so later frames use the GPU.
		var budget = portableGpuReadbackWarm ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(12);
		if (!encoding.Wait(budget)) {
			portableGpuReadbackUnavailable = true;
			_ = encoding.ContinueWith(task => {
				if (task.Status == TaskStatus.RanToCompletion) {
					portableGpuReadbackWarm = true;
					portableGpuReadbackUnavailable = false;
				}
				_ = task.Exception;
			}, TaskScheduler.Default);
			return FallbackFrame(root, renderSize);
		}
		portableGpuReadbackWarm = true;
		Trace("Render encoded PNG");
		return new DesignerRenderFrame {
			Sequence = Interlocked.Increment(ref frameSequence), Width = renderSize.Width, Height = renderSize.Height, Dpi = 1,
			PngBase64 = encoding.GetAwaiter().GetResult()
		};
#endif
	}

	DesignerRenderFrame FallbackFrame(Control root, Size size)
	{
		var pixels = new byte[checked(size.Width * size.Height * 4)];
		for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = SystemColors.Control.B; pixels[i + 1] = SystemColors.Control.G; pixels[i + 2] = SystemColors.Control.R; pixels[i + 3] = 255; }
		PaintFallback(root, 0, 0, pixels, size.Width, size.Height);
		return new DesignerRenderFrame { Sequence = Interlocked.Increment(ref frameSequence), Width = size.Width, Height = size.Height, Dpi = 1, Data = DesignerFrameCodec.EncodeDeflateBase64(pixels) };
	}

	static void PaintFallback(Control control, int offsetX, int offsetY, byte[] pixels, int width, int height)
	{
		var x = offsetX + control.Left; var y = offsetY + control.Top;
		var right = Math.Min(width, x + Math.Max(1, control.Width)); var bottom = Math.Min(height, y + Math.Max(1, control.Height));
		for (var yy = Math.Max(0, y); yy < bottom; yy++) for (var xx = Math.Max(0, x); xx < right; xx++) {
			var i = (yy * width + xx) * 4; var border = xx == x || yy == y || xx == right - 1 || yy == bottom - 1;
			pixels[i] = border ? (byte)96 : control.BackColor.B; pixels[i + 1] = border ? (byte)96 : control.BackColor.G; pixels[i + 2] = border ? (byte)96 : control.BackColor.R; pixels[i + 3] = 255;
		}
		foreach (Control child in control.Controls) PaintFallback(child, x, y, pixels, width, height);
	}

	static void PaintControl(Control control, Graphics graphics)
	{
		var bounds = new Rectangle(Point.Empty, control.Size);
#if MICROSOFT_WINFORMS
		PaintStandardControl(control, graphics, bounds);
#else
		// The canonical LibreWinForms package provides the standard Control surface, but its
		// paint-source extension is deliberately not part of the published reference contract.
		// Keep the designer host consumable from the local package feed by using the same stable
		// fallback renderer as ordinary controls.
		PaintStandardControl(control, graphics, bounds);
		foreach (Control child in control.Controls) {
			var state = graphics.Save();
			graphics.TranslateTransform(child.Left, child.Top);
			PaintControl(child, graphics);
			graphics.Restore(state);
		}
#endif
	}

	static void PaintStandardControl(Control control, Graphics graphics, Rectangle bounds)
	{
		var back = control.BackColor.IsEmpty ? SystemColors.Control : control.BackColor;
		var fore = control.ForeColor.IsEmpty ? SystemColors.ControlText : control.ForeColor;
		using var background = new SolidBrush(back);
		using var foreground = new SolidBrush(fore);
		using var border = new Pen(SystemColors.ControlDark, 1);
		using var light = new Pen(SystemColors.ControlLightLight, 1);
		graphics.FillRectangle(background, bounds);
		var font = control.Font ?? SystemFonts.DefaultFont;

		if (control is Button) {
			graphics.FillRectangle(new SolidBrush(SystemColors.Control), bounds);
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			graphics.DrawLine(light, 1, 1, Math.Max(1, bounds.Width - 2), 1);
			DrawCenteredText(graphics, control.Text, font, foreground, bounds);
		} else if (control is TextBoxBase) {
			graphics.FillRectangle(Brushes.White, bounds);
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			graphics.DrawString(control.Text ?? "", font, foreground, new PointF(3, 3));
		} else if (control is CheckBox or RadioButton) {
			var mark = new Rectangle(1, Math.Max(1, (bounds.Height - 13) / 2), 12, 12);
			if (control is RadioButton) graphics.DrawEllipse(border, mark);
			else graphics.DrawRectangle(border, mark);
			graphics.DrawString(control.Text ?? "", font, foreground, new PointF(17, Math.Max(1, (bounds.Height - font.Height) / 2f)));
		} else if (control is ComboBox or NumericUpDown) {
			graphics.FillRectangle(Brushes.White, bounds);
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			var buttonWidth = Math.Min(18, bounds.Width);
			graphics.FillRectangle(new SolidBrush(SystemColors.Control), bounds.Width - buttonWidth, 1, buttonWidth - 1, Math.Max(0, bounds.Height - 2));
			graphics.DrawLine(border, bounds.Width - buttonWidth, 1, bounds.Width - buttonWidth, bounds.Height - 2);
			graphics.DrawString(control.Text ?? "", font, foreground, new PointF(3, 3));
		} else if (control is GroupBox) {
			var top = Math.Max(6, font.Height / 2);
			graphics.DrawRectangle(border, 0, top, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - top - 1));
			graphics.FillRectangle(background, 8, 0, Math.Min(bounds.Width - 10, graphics.MeasureString(control.Text ?? "", font).Width + 4), font.Height + 2);
			graphics.DrawString(control.Text ?? "", font, foreground, new PointF(10, 0));
		} else if (control is TabControl tabs) {
			graphics.FillRectangle(Brushes.White, 1, 22, Math.Max(0, bounds.Width - 2), Math.Max(0, bounds.Height - 23));
			graphics.DrawRectangle(border, 0, 21, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 22));
			var left = 2;
			foreach (TabPage page in tabs.TabPages) {
				var width = Math.Max(45, (int)graphics.MeasureString(page.Text ?? "", font).Width + 14);
				graphics.FillRectangle(page == tabs.SelectedTab ? Brushes.White : background, left, 1, width, 21);
				graphics.DrawRectangle(border, left, 1, width, 21);
				graphics.DrawString(page.Text ?? "", font, foreground, new PointF(left + 7, 4));
				left += width + 1;
			}
		} else if (control is TreeView) {
			graphics.FillRectangle(Brushes.White, bounds);
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			DrawPlaceholderRows(graphics, font, foreground, border, bounds, tree: true);
		} else if (control is ListView) {
			graphics.FillRectangle(Brushes.White, bounds);
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			DrawPlaceholderRows(graphics, font, foreground, border, bounds, tree: false);
		} else if (control is DataGridView) {
			graphics.FillRectangle(Brushes.White, bounds);
			graphics.FillRectangle(background, 1, 1, Math.Max(0, bounds.Width - 2), Math.Min(23, bounds.Height - 2));
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			for (var x = 36; x < bounds.Width; x += 80) graphics.DrawLine(border, x, 1, x, bounds.Height - 2);
			for (var y = 24; y < bounds.Height; y += 22) graphics.DrawLine(border, 1, y, bounds.Width - 2, y);
			graphics.DrawString("DataGridView", font, foreground, new PointF(42, 4));
		} else if (control is MenuStrip or ToolStrip) {
			graphics.FillRectangle(new SolidBrush(SystemColors.Menu), bounds);
			graphics.DrawLine(border, 0, Math.Max(0, bounds.Height - 1), bounds.Width, Math.Max(0, bounds.Height - 1));
			graphics.DrawString(control is MenuStrip ? "File    Edit    View" : "New   Open   Save", font, foreground, new PointF(6, Math.Max(1, (bounds.Height - font.Height) / 2f)));
		} else if (control is Panel) {
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
		} else if (control is ListBox) {
			graphics.FillRectangle(Brushes.White, bounds);
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
		} else if (control is ProgressBar progress) {
			graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
			var range = Math.Max(1, progress.Maximum - progress.Minimum);
			var fill = Math.Max(0, (bounds.Width - 4) * (progress.Value - progress.Minimum) / range);
			graphics.FillRectangle(new SolidBrush(SystemColors.Highlight), 2, 2, fill, Math.Max(0, bounds.Height - 4));
		} else if (control is Label) {
			graphics.DrawString(control.Text ?? "", font, foreground, new PointF(0, Math.Max(0, (bounds.Height - font.Height) / 2f)));
		} else if (control is Form) {
			// The Form's Text is shown in the simulated title bar (PaintFormChrome), not restated
			// here as body text.
		} else if (!String.IsNullOrEmpty(control.Text)) {
			graphics.DrawString(control.Text, font, foreground, new PointF(3, 3));
		}
	}

	static void DrawPlaceholderRows(Graphics graphics, Font font, Brush foreground, Pen border, Rectangle bounds, bool tree)
	{
		for (var row = 0; row < 4; row++) {
			var y = 5 + row * 20;
			if (y + font.Height >= bounds.Height) break;
			var indent = tree ? 8 + row * 8 : 8;
			if (tree) {
				graphics.DrawRectangle(border, indent, y + 3, 8, 8);
				graphics.DrawLine(border, indent + 2, y + 7, indent + 6, y + 7);
			}
			graphics.DrawString(tree ? "Node " + (row + 1) : "List item " + (row + 1), font, foreground,
				new PointF(indent + (tree ? 14 : 0), y));
		}
	}

	static void DrawCenteredText(Graphics graphics, string? text, Font font, Brush brush, Rectangle bounds)
	{
		if (String.IsNullOrEmpty(text)) return;
		var size = graphics.MeasureString(text, font);
		graphics.DrawString(text, font, brush,
			new PointF(Math.Max(2, (bounds.Width - size.Width) / 2), Math.Max(1, (bounds.Height - size.Height) / 2)));
	}

	static void PaintFormChrome(Form form, Graphics graphics, Rectangle bounds)
	{
		if (bounds.Width <= 0 || bounds.Height <= 0) return;
		const int titleHeight = 30;
		const int btnW = 12;
		const int btnH = 12;
		const int btnPad = 4;

		using var titleBg = new SolidBrush(SystemColors.ActiveCaption);
		using var titleText = new SolidBrush(SystemColors.ActiveCaptionText);
		using var captionBorder = new Pen(SystemColors.ControlDark, 1);
		using var btnFace = new SolidBrush(SystemColors.Control);
		using var btnHighlight = new Pen(SystemColors.ControlLightLight, 1);
		using var btnShadow = new Pen(SystemColors.ControlDark, 1);
		using var closeRed = new SolidBrush(Color.FromArgb(196, 43, 28));
		using var titleFont = new Font(SystemFonts.DefaultFont.FontFamily,
			Math.Max(7, SystemFonts.DefaultFont.Size), FontStyle.Bold);

		graphics.FillRectangle(titleBg, 0, 0, bounds.Width, Math.Min(titleHeight, bounds.Height));
		graphics.DrawLine(captionBorder, 0, Math.Min(titleHeight, bounds.Height) - 1, bounds.Width - 1, Math.Min(titleHeight, bounds.Height) - 1);

		if (!string.IsNullOrEmpty(form.Text)) {
			var textRect = new RectangleF(6, 0, Math.Max(0, bounds.Width - (btnW + btnPad) * 3 - 16), titleHeight);
			var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
			graphics.DrawString(form.Text, titleFont, titleText, textRect, fmt);
		}

		var btnY = Math.Max(2, (titleHeight - btnH) / 2);
		var btnX = bounds.Width - (btnW + btnPad);

		graphics.FillRectangle(closeRed, btnX, btnY, btnW, btnH);
		graphics.DrawRectangle(captionBorder, btnX, btnY, btnW, btnH);
		var cx = btnX + btnW / 2;
		var cy = btnY + btnH / 2;
		graphics.DrawLine(Pens.White, cx - 3, cy - 3, cx + 3, cy + 3);
		graphics.DrawLine(Pens.White, cx + 3, cy - 3, cx - 3, cy + 3);

		btnX -= btnW + btnPad;
		graphics.FillRectangle(btnFace, btnX, btnY, btnW, btnH);
		graphics.DrawRectangle(captionBorder, btnX, btnY, btnW, btnH);
		graphics.DrawRectangle(btnHighlight, btnX + 1, btnY + 1, Math.Max(0, btnW - 3), Math.Max(0, btnH - 3));
		graphics.DrawLine(btnShadow, btnX + 3, btnY + btnH - 3, btnX + btnW - 3, btnY + btnH - 3);
		graphics.DrawLine(btnShadow, btnX + btnW - 3, btnY + 3, btnX + btnW - 3, btnY + btnH - 3);

		// Minimize: a single horizontal dash ("_"), not a "+" - real Windows chrome never draws
		// the vertical stroke here (that would read as a "restore"/maximize-adjacent glyph
		// instead), only the maximize button (drawn above) gets an outlined square.
		btnX -= btnW + btnPad;
		graphics.FillRectangle(btnFace, btnX, btnY, btnW, btnH);
		graphics.DrawRectangle(captionBorder, btnX, btnY, btnW, btnH);
		graphics.DrawLine(btnShadow, btnX + 3, btnY + btnH - 4, btnX + btnW - 3, btnY + btnH - 4);
	}

	static DesignerSessionState Accepted(long baseVersion) => new() { Version = baseVersion, Accepted = true };
	DesignerSessionState Rejected(long baseVersion, string error) => new() { SessionId = current?.SessionId ?? sessionId ?? "", DocumentId = current?.DocumentId ?? "", Version = baseVersion, Error = error };
}

sealed class ThisQualifierRewriter : CSharpSyntaxRewriter
{
	public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
	{
		if (node.Expression is ThisExpressionSyntax)
			return node.Name.WithTriviaFrom(node);
		return base.VisitMemberAccessExpression(node);
	}
}

sealed class MeQualifierRewriter : Vb.VisualBasicSyntaxRewriter
{
	public override SyntaxNode? VisitMemberAccessExpression(VbSyntax.MemberAccessExpressionSyntax node)
	{
		if (node.Expression is VbSyntax.MeExpressionSyntax)
			return node.Name.WithTriviaFrom(node);
		return base.VisitMemberAccessExpression(node);
	}
}


sealed class ProjectAssemblyLoadContext : AssemblyLoadContext
{
	readonly AssemblyDependencyResolver resolver;
	readonly IReadOnlyDictionary<string, string> additionalAssemblies;
	public ProjectAssemblyLoadContext(string assemblyPath, IEnumerable<string> additionalAssemblyPaths) : base(isCollectible: true)
	{
		resolver = new AssemblyDependencyResolver(assemblyPath);
		var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in additionalAssemblyPaths.Where(File.Exists)) {
			try {
				var name = AssemblyName.GetAssemblyName(path).Name;
				if (!String.IsNullOrEmpty(name) && !paths.ContainsKey(name)) paths.Add(name, path);
			} catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException) {
				// Native DLLs beside a selected control are dependencies for native loading, not
				// managed assemblies we may index or LoadFromAssemblyPath.
			}
		}
		additionalAssemblies = paths;
	}
	protected override Assembly? Load(AssemblyName assemblyName)
	{
		if (assemblyName.Name is "System.Windows.Forms" or "System.Drawing.Common" or "System.Drawing") return null;
		var path = resolver.ResolveAssemblyToPath(assemblyName);
		if (path != null) return LoadFromAssemblyPath(path);
		return assemblyName.Name != null && additionalAssemblies.TryGetValue(assemblyName.Name, out var additional)
			? LoadFromAssemblyPath(additional) : null;
	}
}

/// <summary>Minimal <see cref="INameCreationService"/> registered into the child design surface -
/// see CreateDesignSurface's own note on why this is needed at all: RPCs that add a component
/// always pass an explicit name, but a designer verb/smart-tag method (e.g. TabControlActionList's
/// "Add Tab") calls <c>host.CreateComponent(type)</c> itself with none, and without this service
/// registered the resulting component comes back completely unnamed.</summary>
sealed class DefaultNameCreationService : INameCreationService
{
	public string CreateName(IContainer? container, Type dataType)
	{
		var baseName = Char.ToLowerInvariant(dataType.Name[0]) + dataType.Name.Substring(1);
		if (container == null) return baseName + "1";
		for (var index = 1; ; index++) {
			var candidate = baseName + index;
			if (container.Components[candidate] == null) return candidate;
		}
	}

	public bool IsValidName(string name)
	{
		if (String.IsNullOrEmpty(name) || !(Char.IsLetter(name[0]) || name[0] == '_')) return false;
		return name.All(character => Char.IsLetterOrDigit(character) || character == '_');
	}

	public void ValidateName(string name)
	{
		if (!IsValidName(name)) throw new ArgumentException("Invalid component name: " + name, nameof(name));
	}
}
