using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Shell;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.WinForms;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.Designer.Surface;

namespace ICSharpCode.GtkDesigner;

public sealed class GtkDesignerViewContent : AbstractViewContentHandlingLoadErrors, IOutlineContentHost, IToolsHost, IHasPropertyContainer, IUndoHandler, IFilterableToolbox, IToolboxSourceDropHandler
{
	public static readonly string[] ToolNames = { "GtkBox", "GtkGrid", "GtkCenterBox", "GtkPaned", "GtkScrolledWindow", "GtkLabel", "GtkButton", "GtkEntry", "GtkPasswordEntry", "GtkCheckButton", "GtkSwitch", "GtkSpinButton", "GtkDropDown", "GtkListBox", "GtkListView", "GtkGridView", "GtkColumnView", "GtkColumnViewColumn", "GtkImage", "GtkPicture", "GtkProgressBar", "GtkSeparator" };
	/// <summary>Libadwaita widgets, offered only for a document that declares &lt;requires lib="libadwaita"&gt;.</summary>
	public static readonly string[] AdwToolNames = { "AdwHeaderBar", "AdwToolbarView", "AdwStatusPage", "AdwClamp", "AdwPreferencesPage", "AdwPreferencesGroup", "AdwPreferencesWindow", "AdwNavigationView", "AdwNavigationPage", "AdwNavigationSplitView", "AdwTabView", "AdwTabBar", "AdwActionRow", "AdwEntryRow", "AdwSwitchRow", "AdwButtonContent", "AdwAvatar", "AdwBanner", "AdwSpinner" };
	bool documentRequiresAdw;
	internal bool IsToolName(string? name) => name != null && ( ToolNames.Contains(name, StringComparer.Ordinal) || documentRequiresAdw && AdwToolNames.Contains(name, StringComparer.Ordinal));
	/// <summary>The shared category for a GTK control ("Containers", "Inputs", ...), or the
	/// framework's own group when the control maps to no shared concept - which is how the
	/// Libadwaita-only widgets keep their own group instead of falling into "Other".</summary>
	static string CategoryFor(string typeName, string frameworkGroup)
	{
		var shared = DesignerToolboxCatalog.ResolveCategory(GtkControlMapper.Instance, typeName);
		return shared == DesignerToolboxCatalog.OtherCategory ? frameworkGroup : shared;
	}

	/// <summary>A Toolbox row for a GIR type name. The type keeps its full name - the host
	/// resolves the widget class from it and the icon mapper normalizes it - while the row shows the
	/// bare widget name, the way every other framework's toolbox does. GtkButton and AdwActionRow are
	/// namespace prefixes, not part of what the control is called.</summary>
	static DesignerToolboxItemInfo Item(string typeName, string frameworkGroup)
	{
		return new DesignerToolboxItemInfo {
			Name = typeName,
			DisplayName = DesignerTypeIcons.GetElementTypeName(typeName),
			TypeName = typeName,
			Category = CategoryFor(typeName, frameworkGroup)
		};
	}

	void RefreshToolbox()
	{
		tools.Register("gtk", ToolNames.Select(name => Item(name, "GTK 4")));
		tools.Register("gtk-adw", AdwToolNames.Select(name => Item(name, "Libadwaita")));
		tools.SetScopes(documentRequiresAdw ? new[] { "gtk", "gtk-adw" } : new[] { "gtk" });
	}
	readonly DocumentOutlineControl outline = new() { IconMapper = GtkControlMapper.Instance }; readonly PropertyContainer properties = new();
	readonly DesignerToolboxScope tools;
	readonly DesignerSelectionController selection;
	readonly DesignerPadController pads;
	readonly DesignerCommandController commands = new();
	readonly TextBlock diagnostic = new() { Foreground = Brushes.OrangeRed, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
	// The shared design canvas (ICSharpCode.DesignerCanvas addin), keyed by GtkBuilder object id.
	// GTK lays every widget out itself, so the canvas shows no resize handles and a drag is a
	// reorder among siblings (see CommitCanvasDrag).
	readonly DesignSurface canvas = new();
	readonly DesignSurfaceController canvasController;
	Dictionary<string, string> pathById = new(StringComparer.Ordinal);
	GtkDesignerHostClient? host; DesignerSessionState state = new(); DesignerElementNode? selected; string loadedText = "";
	CancellationTokenSource? renderCancellation; long requestedRenderRevision; long renderedRevision;
	FileSystemWatcher? cssWatcher; string[] watchedCssPaths = Array.Empty<string>(); DispatcherTimer? cssRefreshTimer; bool disposed;

	public GtkDesignerViewContent(OpenedFile file) : base(file)
	{
		tools = new DesignerToolboxScope(this, GtkControlMapper.Instance);
		// A blank GTK document needs a host container before any input or display widget can be
		// useful. Keep that authoring path first without changing the curated order inside either
		// category (the shared scope makes this a per-document preference, not a global sort).
		tools.SetCategoryPriority("Containers", 0);
		tools.ItemInvoked += (_, item) => Add(item.TypeName);
		RefreshToolbox();
		selection = new DesignerSelectionController(node => Adapter(node), nodes => new DesignerMultiPropertyAdapter(nodes.Select(node => (object)Adapter(node))));
		commands.RegisterStandard(() => host?.IsAlive == true && state.CanUndo, () => RewireAfterHistory(Mutate(() => host!.UndoAsync(state.Version).GetAwaiter().GetResult())),
			() => host?.IsAlive == true && state.CanRedo, () => RewireAfterHistory(Mutate(() => host!.RedoAsync(state.Version).GetAwaiter().GetResult())),
			() => selection.SelectedIds.Count > 0 && host?.IsAlive == true, DeleteSelectedCore);
		pads = new DesignerPadController(selection, outline.SetRoots, value => properties.SelectedObject = value, outline.SelectNodeById, node => { selected = node; canvasController?.RestoreSelection(selection.SelectedIds); });
		canvasController = new DesignSurfaceController(canvas, DesignSurfaceKeying.Id);
		TabPageText = "Design"; ConfigureCanvas(); var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.Children.Add(canvas); Grid.SetRow(diagnostic, 1); grid.Children.Add(diagnostic); UserContent = grid;
		outline.SelectionCommitted += (_, _) => pads.CommitOutlineSelection(outline.SelectedNode?.Id);
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => Undo(), (_, e) => e.CanExecute = commands.CanExecute(DesignerCommandNames.Undo)));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => Redo(), (_, e) => e.CanExecute = commands.CanExecute(DesignerCommandNames.Redo)));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, (_, _) => DeleteSelected(), (_, e) => e.CanExecute = commands.CanExecute(DesignerCommandNames.Delete)));
	}
	public object OutlineContent => outline; public object ToolsContent => tools.Activate(); public ListBox ToolboxControl => tools.Control; public int ZoomComboSelectedIndex => canvas.ZoomCombo.SelectedIndex; public PropertyContainer PropertyContainer => properties;
	public string? SelectedToolboxType => tools.SelectedItem?.TypeName;
	public DesignerToolboxItemInfo? SelectedToolboxItem => tools.SelectedItem;
	public bool SelectToolboxType(string type) => tools.Select(type);
	/// <summary>The row for <paramref name="type"/>, scrolled into view and settled - for driving a real drag.</summary>
	public FrameworkElement? ToolboxRow(string type, out string error) => tools.SettledRow(type, out error);
	public void FilterToolbox(string text) => tools.Filter(text);
	void IFilterableToolbox.Filter(string text) => FilterToolbox(text);
	int IFilterableToolbox.VisibleItemCount => ToolboxItemCount;
	string IFilterableToolbox.FilterText => ToolboxFilterText;
	public string ToolboxFilterText => tools.FilterText;
	bool IToolboxSourceDropHandler.CanAcceptToolboxDrop(IDataObject data) => IsToolName(ToolboxDragData.GetTypeName(data));
	/// <summary>A toolbox item dropped onto the .ui source beside this designer: the same GtkBuilder
	/// insertion a designer drop makes (a &lt;child&gt;&lt;object class id/&gt;, with starter text),
	/// planned against the source pane's own text so edits typed there and not yet seen here survive.</summary>
	ToolboxSourceEdit? IToolboxSourceDropHandler.PlanToolboxDrop(IDataObject data, string sourceText, int offset)
	{
		if (ToolboxDragData.GetTypeName(data) is not { } type || !IsToolName(type)) return null;
		var editor = new GtkUiDocumentEditor();
		if (!editor.Reset(sourceText) || !editor.AddAt(offset, type)) return null;
		return ToolboxSourceEdit.FromDifference(sourceText, editor.Text);
	}
	/// <summary>The shared canvas geometry (rendered frame, viewport, selection outline and
	/// handle) in screen coordinates - the same probe the WPF designer publishes, so a synthetic
	/// pointer gesture can start from the actual viewport background rather than a guessed point.</summary>
	public ICSharpCode.SharpDevelop.Designer.Presentation.DesignerSurfaceGeometry SurfaceGeometry() => canvasController.SurfaceGeometry();
	/// <summary>A rendered object's bounds in screen coordinates, through the canvas's viewport
	/// (null when it has none, or nothing is rendered).</summary>
	public Rect? ScreenBoundsOf(string id)
	{
		var node = state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => n.Id == id);
		if (node == null || node.Width <= 0 || node.Height <= 0 || !canvas.HasRender) return null;
		var topLeft = canvas.SurfacePointToScreen(node.X, node.Y);
		var bottomRight = canvas.SurfacePointToScreen(node.X + node.Width, node.Y + node.Height);
		return new Rect(topLeft, bottomRight);
	}
	public int ToolboxItemCount => tools.VisibleItemCount; public bool IsToolboxHosted => tools.IsHosted;
	public bool IsOutlineHosted => ReferenceEquals((SD.Services.GetService(typeof(IOutlinePadHost)) as IOutlinePadHost)?.HostedContent, outline); public int OutlineItemCount => ElementCount;
	public int ElementCount => state.Tree == null ? 0 : Flatten(state.Tree).Count(n => n.Id != "$interface"); public string SelectedId => selected?.Id ?? ""; public int HostProcessId => host?.ProcessId ?? 0;
	public string[] ElementIds => state.Tree == null ? Array.Empty<string>() : Flatten(state.Tree).Where(n => n.Id != "$interface").Select(n => n.Id).ToArray();
	/// <summary>The GTK host's own PNG of the design content, straight from its render texture, for
	/// capture on a platform where the WPF imaging stack cannot encode one. It shows what GTK produced,
	/// which is what separates "GTK rendered it wrong" from "the canvas placed it wrong" - the two
	/// candidate causes of a surface that looks distorted. The WPF visual capture
	/// (od.gtk-designer.screenshot) shows the composed canvas instead, chrome and all.</summary>
	/// <summary>The size the host reported for the frame <see cref="RenderPreviewPng"/> returns. Kept
	/// beside the bytes so a caller can compare them without racing a render: the canvas used to be told
	/// one size and handed another, and stretched one across the other.</summary>
	public (int Width, int Height)? RenderPreviewPngSize =>
		state.Render == null || string.IsNullOrEmpty(state.Render.PngBase64)
			? null
			: (state.Render.Width, state.Render.Height);

	public byte[]? RenderPreviewPng()
	{
		// Already in the session state: the host encodes a PNG with every render, so this needs no
		// round trip and cannot race a render in flight.
		var png = state.Render?.PngBase64;
		if (string.IsNullOrEmpty(png)) return null;
		try { return Convert.FromBase64String(png!); } catch (FormatException) { return null; }
	}

	public string RootId => state.Tree?.Id == "$interface" ? state.Tree.Children.FirstOrDefault()?.Id ?? "" : state.Tree?.Id ?? "";
	public bool CanvasFrameShown => canvas.FrameShown; public string CanvasDisplayDiagnostic => canvas.DisplayDiagnostic();
	/// <summary>Whether the shared canvas - not the shell or a pad - owns keyboard focus, which is
	/// what makes Delete/Tab/arrow keys reach it after a pointer gesture.</summary>
	public bool CanvasHasKeyboardFocus => canvas.IsKeyboardFocusWithin;
	public int ToolbarItemCount => canvas.VisibleToolbarItems.Count; public IReadOnlyList<string> ToolbarItems => canvas.VisibleToolbarItems; public string ToolbarCapabilities => canvas.Capabilities.ToString(); public double Zoom { get => canvas.ViewportScale; set => canvas.SetViewport(Math.Clamp(value, .25, 2), 0, 0); }
	public bool Gridlines => canvas.Gridlines; public bool FitMeasured { get; private set; } public void FitDesign() => FitView(); public void ShowGridlines(bool show) { canvas.IsGridEnabled = show; canvas.SetGridlines(show); }
	public bool HasNativeFrame => !string.IsNullOrEmpty(state.Render?.PngBase64); public int NativeFrameWidth => state.Render?.Width ?? 0; public int NativeFrameHeight => state.Render?.Height ?? 0; public int NativeBoundsCount => state.Tree == null ? 0 : Flatten(state.Tree).Count(n => n.Width > 0 && n.Height > 0);
	public string NativeFrameFingerprint => HasNativeFrame ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(state.Render!.PngBase64))) : "";
	public string[] Diagnostics => state.Diagnostics.Select(d => d.Message).ToArray();
	public string LoadError => HasLoadError && Control is ContentPresenter presenter && presenter.Content is TextBox error ? error.Text : "";
	public string HostLog => host?.ChildLog ?? "";
	public string HostSessionId => host?.SessionId ?? ""; public string HostDocumentId => host?.DocumentId ?? ""; public string HostPoolKey => host?.PoolKey ?? "gtk4"; public int ActiveHostLeases => GtkDesignerHostClient.ActiveLeaseCount; public int HostRecoveryCount => host?.RecoveryCount ?? 0;
	public string PreviewTheme => host?.PreviewTheme ?? "Light";
	public string CanvasTheme => canvas.ThemeState;
	public long RequestedRenderRevision => requestedRenderRevision; public long RenderedRevision => renderedRevision; public bool IsRenderPending => requestedRenderRevision > renderedRevision;
	public string Status => state.Accepted ? $"Ready: {ElementCount} GTK objects (host {host?.ProcessId}, native frame {(HasNativeFrame ? $"{NativeFrameWidth}x{NativeFrameHeight}" : "unavailable")})" : state.Error;
	public bool IsHostAlive => host?.IsAlive == true; public bool DocumentCanUndo => state.CanUndo; public bool DocumentCanRedo => state.CanRedo; public bool EnableUndo => commands.CanExecute("Undo"); public bool EnableRedo => commands.CanExecute("Redo");
	public void Undo() => commands.Execute("Undo"); public void Redo() => commands.Execute("Redo");
	public bool SelectById(string id) => selection.Select(id);
	public IReadOnlyList<string> SelectedIds => selection.SelectedIds;
	public bool SelectByIds(IEnumerable<string> ids) => pads.CommitSelection(ids);
	public DesignerElementNode? FindById(string id) => selection.Find(id);
	public bool HitTest(double x, double y) { var hit = canvasController.FindNodeAtDesignPoint(x, y); return hit != null && SelectById(hit.Id); }
	public bool SetSelectedProperty(string name, string value) => selected != null && SetProperty(selected.Id, name, value);
	bool SetProperty(string id, string name, string value) { if (host == null || !Mutate(() => host.SetPropertyAsync(state.Version, id, name, value).GetAwaiter().GetResult())) return false; return name == "$id" ? SelectById(value) : selection.Find(id) != null; }
	public bool Add(string type) { if (host == null || state.Tree == null) return false; var parent = selected == null ? Flatten(state.Tree).FirstOrDefault(IsContainer) : NearestContainer(selected); if (parent == null) return false; return AddTo(parent.Id, type, -1, -1); }
	/// <summary>Adds <paramref name="type"/> to the container <paramref name="parentId"/>; a drop
	/// passes its design point so the host places it by the container's child policy (GtkDropPlanner),
	/// a toolbox click passes (-1, -1) to append.</summary>
	bool AddTo(string parentId, string type, double x, double y) { if (host == null || state.Tree == null) return false; var before = Flatten(state.Tree).Select(n => n.Id).ToHashSet(StringComparer.Ordinal); if (!Mutate(() => host.AddElementAsync(state.Version, parentId, new DesignerToolboxItemInfo { Name = type, TypeName = type }, "", x, y,
			ToDropTarget(GtkDropPlanner.Plan(ToDropNode(state.Tree), x, y))).GetAwaiter().GetResult())) return false;
		// The host reports the widget it created; a GtkStack drop writes a GtkStackPage and a list
		// view a model/factory around it, and the widget is what the user just asked for. Fall back
		// to the newest object when the host does not report one.
		var added = state.Tree == null ? null
			: state.CreatedElementId is { } createdId ? Flatten(state.Tree).FirstOrDefault(n => n.Id == createdId)
			: Flatten(state.Tree).Where(n => !before.Contains(n.Id)).LastOrDefault();
		if (added != null) Select(added); return added != null; }
	public bool DeleteSelected() => commands.Execute("Delete");

	// The designer's own copy, kept beside the system clipboard so a multi-object copy round-trips
	// as separate snippets; any other text on the clipboard pastes as one snippet.
	static string[] clipboardSnippets = Array.Empty<string>();
	static string? clipboardText;

	/// <summary>Copies the selected objects as GtkBuilder &lt;child&gt; snippets to the clipboard.</summary>
	public bool CopySelection()
	{
		if (host == null || selection.SelectedIds.Count == 0) return false;
		var copied = host.CopyElementsAsync(state.Version, selection.SelectedIds.ToArray()).GetAwaiter().GetResult();
		if (!copied.Accepted) { diagnostic.Text = copied.Error; return false; }
		clipboardSnippets = copied.Snippets.ToArray();
		clipboardText = string.Join(Environment.NewLine, clipboardSnippets);
		try { Clipboard.SetText(clipboardText); } catch (Exception) { /* the designer's own copy still pastes */ }
		return true;
	}

	static string[] ClipboardSnippets()
	{
		string? text = null;
		try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch (Exception) { }
		if (text == null || text == clipboardText) return clipboardSnippets;
		return text.TrimStart().StartsWith("<child", StringComparison.Ordinal) ? new[] { text } : Array.Empty<string>();
	}

	/// <summary>Pastes into the selected object (or its nearest container; the document root
	/// without a selection) and selects what was pasted.</summary>
	public bool PasteIntoSelection()
	{
		var target = selection.SelectedIds.FirstOrDefault()
			?? (state.Tree?.Id == "$interface" ? state.Tree.Children.FirstOrDefault()?.Id : state.Tree?.Id);
		return target != null && Paste(target, ClipboardSnippets(), null);
	}

	/// <summary>Pastes the clipboard into the container under the context-menu point, at the insertion position that
	/// point plans for it (the same resolution as a toolbox drop); a box gets the position, other containers append.</summary>
	public bool PasteHere()
	{
		if (canvas.ContextMenuDesignPoint is not { } point || state.Tree == null || ClipboardSnippets() is not { Length: > 0 } snippets) return false;
		if (PlanDropAt(point.X, point.Y) is not { } plan) return false;
		var container = Flatten(state.Tree).FirstOrDefault(node => node.Id == plan.ContainerId);
		var after = plan.Index is int index && container != null && index <= container.Children.Count
			? (index > 0 ? container.Children[index - 1].Id : container.Children.Count > 0 ? GtkUiDocumentEditor.PasteFirst : null) : null;
		return Paste(plan.ContainerId, snippets, after);
	}

	/// <summary>Copies the selection and pastes it right after itself in the same container,
	/// without touching the clipboard.</summary>
	public bool DuplicateSelection()
	{
		if (host == null || selection.SelectedIds.Count == 0 || state.Tree == null) return false;
		var copied = host.CopyElementsAsync(state.Version, selection.SelectedIds.ToArray()).GetAwaiter().GetResult();
		var last = selection.SelectedIds[^1];
		DesignerElementNode? ParentOf(string id) => Flatten(state.Tree).FirstOrDefault(node => node.Children.Any(child => child.Id == id));
		var parent = ParentOf(last);
		// A duplicate is a sibling: a selection spanning containers has no single place to go.
		if (!copied.Accepted || parent == null || selection.SelectedIds.Any(id => ParentOf(id) != parent)) {
			diagnostic.Text = copied.Accepted ? "Duplicate needs objects that share one container." : copied.Error;
			return false;
		}
		return Paste(parent.Id, copied.Snippets.ToArray(), last);
	}

	/// <summary>Whether every selected object has the same (non-null) parent.</summary>
	bool SharesOneParent() => DesignerStandardCommands.SharesOneParent(state.Tree, selection.SelectedIds);

	/// <summary>Wraps the selected siblings in a new GtkBox/GtkFrame and selects it.</summary>
	public bool WrapSelected(string containerType)
	{
		if (host == null || selection.SelectedIds.Count == 0) return false;
		var ids = selection.SelectedIds.ToArray();
		try {
			if (!Mutate(() => host.WrapElementsAsync(state.Version, ids, containerType).GetAwaiter().GetResult())) return false;
		} catch (Exception ex) { diagnostic.Text = ex.GetBaseException().Message; return false; }
		return state.CreatedElementId is { } created && SelectById(created);
	}

	/// <summary>Replaces the selected container with its children and selects them.</summary>
	public bool UnwrapSelected()
	{
		if (host == null || selection.SelectedIds.Count != 1 || state.Tree == null) return false;
		var id = selection.SelectedIds[0];
		var ids = Flatten(state.Tree).FirstOrDefault(node => node.Id == id)?.Children.Select(child => child.Id).ToArray() ?? Array.Empty<string>();
		try {
			if (!Mutate(() => host.UnwrapElementAsync(state.Version, id).GetAwaiter().GetResult())) return false;
		} catch (Exception ex) { diagnostic.Text = ex.GetBaseException().Message; return false; }
		return ids.Length > 0 && SelectByIds(ids);
	}

	/// <summary>Sets halign or valign on every selected object (GtkWidget alignment nicks) as one undo step.</summary>
	public bool AlignSelected(string command)
	{
		var parts = command.Split('-');
		if (parts.Length != 3 || parts[0] != "align" || parts[1] is not ("h" or "v") || parts[2] is not ("fill" or "start" or "center" or "end")) return false;
		if (host == null || selection.SelectedIds.Count == 0) return false;
		var name = parts[1] == "h" ? "halign" : "valign";
		var ids = selection.SelectedIds.ToArray();
		try {
			if (!Mutate(() => host.SetPropertyManyAsync(state.Version, ids, name, parts[2]).GetAwaiter().GetResult())) return false;
		} catch (Exception ex) { diagnostic.Text = ex.GetBaseException().Message; return false; }
		return SelectByIds(ids);
	}

	/// <summary>The selected element and its parent when the parent is a GtkGrid.</summary>
	(DesignerElementNode Node, DesignerElementNode Grid)? SelectedGridChild()
	{
		if (selection.SelectedIds.Count != 1 || state.Tree == null) return null;
		var id = selection.SelectedIds[0];
		var grid = Flatten(state.Tree).FirstOrDefault(node => node.Type == "GtkGrid" && node.Children.Any(child => child.Id == id));
		return grid == null ? null : (grid.Children.First(child => child.Id == id), grid);
	}

	/// <summary>Grows or shrinks the selected grid child's column-span/row-span by one cell (never below 1).</summary>
	public bool SpanSelected(string command)
	{
		if (host == null || SelectedGridChild() is not { } cell) return false;
		var (property, delta) = command switch {
			"span-column-more" => ("layout:column-span", 1), "span-column-less" => ("layout:column-span", -1),
			"span-row-more" => ("layout:row-span", 1), "span-row-less" => ("layout:row-span", -1),
			_ => ("", 0)
		};
		if (delta == 0) return false;
		var current = int.TryParse(Value(cell.Node, property, "1"), out var v) ? v : 1;
		var next = Math.Max(1, current + delta);
		if (next == current) return false;
		var id = cell.Node.Id;
		return SetProperty(id, property, next.ToString(System.Globalization.CultureInfo.InvariantCulture)) && SelectById(id);
	}

	/// <summary>Opens an empty row above (or column left of) the selected grid child as one transaction
	/// (see GtkUiDocumentEditor.InsertGridTrack).</summary>
	public bool InsertGridTrack(string command)
	{
		if (host == null || SelectedGridChild() is not { } cell || command is not ("grid-insert-row" or "grid-insert-column")) return false;
		var id = cell.Node.Id;
		var row = command == "grid-insert-row";
		try {
			if (!Mutate(() => host.InsertGridTrackAsync(state.Version, id, row).GetAwaiter().GetResult())) return false;
		} catch (Exception ex) { diagnostic.Text = ex.GetBaseException().Message; return false; }
		return SelectById(id);
	}

	/// <summary>Deletes the row (or column) of the selected grid child, with the children wholly inside it, as one transaction.</summary>
	public bool DeleteGridTrack(string command)
	{
		if (host == null || SelectedGridChild() is not { } cell || command is not ("grid-delete-row" or "grid-delete-column")) return false;
		var id = cell.Node.Id;
		var row = command == "grid-delete-row";
		try {
			if (!Mutate(() => host.DeleteGridTrackAsync(state.Version, id, row).GetAwaiter().GetResult())) return false;
		} catch (Exception ex) { diagnostic.Text = ex.GetBaseException().Message; return false; }
		return true;
	}

	/// <summary>The single selected element's position among its parent's children, or null.</summary>
	(int Index, int Count)? SiblingPosition() => DesignerStandardCommands.SiblingPosition(state.Tree, selection.SelectedIds);
	static readonly string[] WrapContainers = { "GtkBox", "GtkFrame" };
	bool SelectedHasChildren() => DesignerStandardCommands.SelectedHasChildren(state.Tree, selection.SelectedIds);

	/// <summary>Moves the selected child within its container: child order is layout order here
	/// (a box or stack), so these are "move", not z-order "bring to front".</summary>
	public bool MoveSelected(string command)
	{
		if (SiblingPosition() is not { } position) return false;
		var delta = DesignerStandardCommands.MoveDelta(command, position.Index, position.Count);
		if (delta == 0) return false;
		try { return ReorderSelected(delta); }
		catch (Exception ex) { diagnostic.Text = ex.GetBaseException().Message; return false; }
	}

	bool Paste(string targetId, string[] snippets, string? afterId)
	{
		if (host == null || snippets.Length == 0) return false;
		try {
			if (!Mutate(() => host.PasteElementsAsync(state.Version, targetId, snippets, afterId).GetAwaiter().GetResult())) return false;
		} catch (Exception ex) { diagnostic.Text = ex.GetBaseException().Message; return false; }
		if (state.CreatedElementIds.Count > 0) SelectByIds(state.CreatedElementIds);
		return true;
	}
	bool DeleteSelectedCore() { if (selection.SelectedIds.Count == 0 || host == null) return false; var ids = selection.SelectedIds.ToArray(); return Mutate(() => host.DeleteElementsAsync(state.Version, ids).GetAwaiter().GetResult()); }
	public bool SetSelectedSignal(string signal, string handler) { if (selected == null) return false; var id = selected.Id; return SetEvent(id, signal, handler) && SelectById(id); }
	GtkPropertyAdapter Adapter(DesignerElementNode node) => new(node, (name, value) => SetProperty(node.Id, name, value), (name, value) => SetEvent(node.Id, name, value), name => ResetProperty(node.Id, name),
		items => host != null && Mutate(() => host.SetItemsAsync(state.Version, node.Id, items).GetAwaiter().GetResult()) && selection.Find(node.Id) != null);
	/// <summary>Removes a property so GTK's default applies (design/reset-property).</summary>
	bool ResetProperty(string id, string name) { if (host == null || !Mutate(() => host.ResetPropertyAsync(state.Version, id, name).GetAwaiter().GetResult())) return false; return selection.Find(id) != null; }
	bool SetEvent(string id, string signal, string handler)
	{
		if (host == null) return false;
		// A composite template must not carry <signal>: GTK's template builder resolves the
		// handler as a native symbol and aborts the app, because Gir.Core sets no managed
		// BuilderScope. Wire the template class instead ([Gtk.Connect] + an event subscription).
		if (IsCompositeTemplate) return SetTemplateEvent(id, signal, handler);
		var accepted = Mutate(() => host.SetEventAsync(state.Version, id, signal, handler).GetAwaiter().GetResult());
		if (accepted && !string.IsNullOrEmpty(handler)) UpdateCodeBehind(handler);
		return accepted;
	}

	/// <summary>Wires one signal of a composite template in its C# class (see
	/// <see cref="GtkCodeBehind.WireTemplateSignal"/>) and opens the generated handler.</summary>
	bool SetTemplateEvent(string id, string signal, string handler)
	{
		var uiPath = PrimaryFileName?.ToString();
		var className = TemplateClassName();
		if (string.IsNullOrEmpty(handler)) {
			// Clearing removes only the subscription the designer generated; the [Gtk.Connect]
			// field and the handler method stay, because they are user-owned code.
			if (string.IsNullOrEmpty(uiPath) || className == null) return true;
			// The handler the pad shows (read back from the class) identifies the subscription to remove.
			var shown = FindById(id)?.Events.FirstOrDefault(e => e.Name == signal)?.Handler ?? "";
			if (!GtkCodeBehind.UnwireTemplateSignal(uiPath, className, id, "On" + GtkSignalWiring.Pascal(signal), shown)) {
				diagnostic.Text = $"Composite template: no C# class named {className} was found to clear '{signal}'.";
				return false;
			}
			// MergeTemplateSignals only ever adds handlers, so drop the one the pad still shows.
			if (FindById(id) is { } cleared)
				foreach (var e in cleared.Events.Where(e => e.Name == signal)) e.Handler = null;
			Rebuild();
			return true;
		}
		var node = FindById(id);
		if (string.IsNullOrEmpty(uiPath) || className == null || node == null) {
			diagnostic.Text = "Composite template: select an object in a document with a <template class=\"...\"> root.";
			return false;
		}
		var info = host!.SignalInfoAsync(id, node.Type, signal, handler).GetAwaiter().GetResult();
		if (!info.Supported) {
			diagnostic.Text = $"Signal '{signal}' is not connected by the designer: {info.Reason}.";
			return false;
		}
		if (GtkCodeBehind.WireTemplateSignal(uiPath, className, id, node.Type, info.EventName, handler, info.Signature, info.DefaultBody) is not { } written) {
			diagnostic.Text = $"Composite template: no C# class named {className} was found to wire '{signal}'. Add a [Gtk.Connect(\"{id}\")] field and subscribe its event in Initialize().";
			return false;
		}
		MergeTemplateSignals();
		Rebuild();
		var view = SD.FileService.OpenFile(ICSharpCode.Core.FileName.Create(written.Path));
		(view?.GetService(typeof(ICSharpCode.SharpDevelop.Editor.ITextEditor)) as ICSharpCode.SharpDevelop.Editor.ITextEditor)?.JumpTo(written.Line, 1);
		return true;
	}

	/// <summary>Whether the open document is a Gir.Core composite template (a &lt;template&gt; root).</summary>
	bool IsCompositeTemplate => System.Text.RegularExpressions.Regex.IsMatch(loadedText, @"<template\b");

	/// <summary>The class name a composite template declares, or null.</summary>
	string? TemplateClassName()
	{
		var match = System.Text.RegularExpressions.Regex.Match(loadedText, @"<template\b[^>]*\bclass=""(?<name>[^""]+)""");
		return match.Success ? match.Groups["name"].Value : null;
	}

	/// <summary>A template document has no &lt;signal&gt; entries, so the Events pad reads the wired
	/// handlers back from the template class and merges them into the snapshot it shows.</summary>
	void MergeTemplateSignals()
	{
		if (!IsCompositeTemplate || state.Tree == null) return;
		var uiPath = PrimaryFileName?.ToString();
		var className = TemplateClassName();
		if (string.IsNullOrEmpty(uiPath) || className == null) return;
		var signals = GtkCodeBehind.TemplateSignals(uiPath, className);
		if (signals.Count == 0) return;
		foreach (var node in Flatten(state.Tree))
			foreach (var e in node.Events)
				if (signals.TryGetValue((node.Id, "On" + GtkSignalWiring.Pascal(e.Name)), out var wired))
					e.Handler = wired;
	}

	/// <summary>The selected popover/menu button's top-level menu items, or null.</summary>
	public IReadOnlyList<DesignerMenuNode>? MenuItems() { if (host == null || selected == null) return null; return host.MenuItemsAsync(selected.Id).GetAwaiter().GetResult(); }
	/// <summary>Replaces the selected popover/menu button's top-level menu items as one undo step.</summary>
	public bool SetMenuItems(IReadOnlyList<DesignerMenuNode> items)
	{
		if (host == null || selected == null) return false;
		var accepted = Mutate(() => host.SetMenuItemsAsync(state.Version, selected.Id, items.ToList()).GetAwaiter().GetResult());
		// A menu that names window actions ("win.save") gets their handler stubs and the companion's
		// AddMenuActions; once a companion exists it is rewritten even when the last action went away.
		if (accepted) UpdateCodeBehind(null, force: HasWindowAction(items));
		return accepted;
	}
	/// <summary>Undo or redo can bring back (or take away) a menu's window actions, a signal, or any other
	/// thing the companion and behavior class follow; once a companion exists it is brought in step again,
	/// the way a direct edit does.</summary>
	bool RewireAfterHistory(bool accepted)
	{
		if (accepted && PrimaryFileName?.ToString() is { Length: > 0 } uiPath && File.Exists(GtkCodeBehind.CompanionPath(uiPath))) UpdateCodeBehind(null);
		return accepted;
	}
	static bool HasWindowAction(IEnumerable<DesignerMenuNode> nodes) => nodes.Any(n => n.Action.StartsWith("win.", StringComparison.Ordinal) || HasWindowAction(n.Children));

	/// <summary>The selected list/grid/column view's inline factory template, or null.</summary>
	public string? FactoryTemplate() { if (host == null || selected == null) return null; return host.FactoryTemplateAsync(selected.Id).GetAwaiter().GetResult(); }
	/// <summary>Replaces the selected view's factory template as one undo step.</summary>
	/// <summary>The selected list, grid or column view's selection model class, or null.</summary>
	public string? SelectionModel() { if (host == null || selected == null) return null; return host.SelectionModelAsync(selected.Id).GetAwaiter().GetResult(); }
	/// <summary>Sets the selected view's selection model (None, Single or Multiple) as one undo step.</summary>
	public bool SetSelectionModel(string selectionClass)
	{
		if (host == null || selected == null) return false;
		if (SelectionModel() == selectionClass) return true;
		// A refusal (no model to select from, ...) arrives as an exception; report its reason under the canvas, as the other edits do.
		try { return Mutate(() => host.SetSelectionModelAsync(state.Version, selected.Id, selectionClass).GetAwaiter().GetResult()); }
		catch (Exception ex) { diagnostic.Text = ex.GetBaseException().Message; return false; }
	}
	/// <summary>The selected list, grid or column view's model chain (selection, sort, filter, data), outermost first; null when it has none.</summary>
	public IReadOnlyList<string>? ModelChain() { if (host == null || selected == null) return null; return host.ModelChainAsync(selected.Id).GetAwaiter().GetResult(); }
	/// <summary>Adds or removes the selected view's sort ("sort") or filter ("filter") model as one undo step; a refusal is shown under the canvas.</summary>
	public bool SetModelWrapper(string kind, bool add)
	{
		if (host == null || selected == null) return false;
		try { return Mutate(() => host.SetModelWrapperAsync(state.Version, selected.Id, kind, add).GetAwaiter().GetResult()); }
		catch (Exception ex) { diagnostic.Text = ex.GetBaseException().Message; return false; }
	}
	/// <summary>Whether the selected GtkColumnViewColumn has a sorter; null when it is not a column of a column view.</summary>
	public bool? ColumnSortable() { if (host == null || selected == null) return null; return host.ColumnSortableAsync(selected.Id).GetAwaiter().GetResult(); }
	/// <summary>Makes the selected column sortable (or not) as one undo step; a refusal is shown under the canvas.</summary>
	public bool SetColumnSortable(bool sortable)
	{
		if (host == null || selected == null) return false;
		if (ColumnSortable() == sortable) return true;
		try { return Mutate(() => host.SetColumnSortableAsync(state.Version, selected.Id, sortable).GetAwaiter().GetResult()); }
		catch (Exception ex) { diagnostic.Text = ex.GetBaseException().Message; return false; }
	}
	/// <summary>The class of the selected list view, grid view or column's inline factory, or null.</summary>
	public string? FactoryClass() { if (host == null || selected == null) return null; return host.FactoryClassAsync(selected.Id).GetAwaiter().GetResult(); }
	/// <summary>Switches the selected object's list-item factory between the builder-template and signal classes as one undo step; a
	/// refusal is shown under the canvas. A signal factory's handler stubs and connections are generated like any signal's.</summary>
	public bool SetFactoryClass(string factoryClass)
	{
		if (host == null || selected == null) return false;
		if (FactoryClass() == factoryClass) return true;
		try {
			if (!Mutate(() => host.SetFactoryClassAsync(state.Version, selected.Id, factoryClass).GetAwaiter().GetResult())) return false;
		} catch (Exception ex) { diagnostic.Text = ex.GetBaseException().Message; return false; }
		UpdateCodeBehind(null, force: factoryClass == GtkUiDocumentEditor.SignalFactory);
		return true;
	}
	/// <summary>The class of the selected list view's innermost inline data model, or null.</summary>
	public string? ModelData() { if (host == null || selected == null) return null; return host.ModelDataAsync(selected.Id).GetAwaiter().GetResult(); }
	/// <summary>Switches the selected list view's data model between a string list and a list store as one undo step; a refusal is shown under the canvas.</summary>
	public bool SetModelData(string modelClass)
	{
		if (host == null || selected == null) return false;
		if (ModelData() == modelClass) return true;
		try { return Mutate(() => host.SetModelDataAsync(state.Version, selected.Id, modelClass).GetAwaiter().GetResult()); }
		catch (Exception ex) { diagnostic.Text = ex.GetBaseException().Message; return false; }
	}
	/// <summary>The item-type of the selected list store (or of the store a selected list view ends in): "" unset, null no store.</summary>
	public string? ItemType() { if (host == null || selected == null) return null; return host.ItemTypeAsync(selected.Id).GetAwaiter().GetResult(); }
	/// <summary>Sets the selected list store's item-type as one undo step; a refusal is shown under the canvas.</summary>
	public bool SetItemType(string typeName)
	{
		if (host == null || selected == null) return false;
		if (ItemType() == (typeName ?? "").Trim()) return true;
		try { return Mutate(() => host.SetItemTypeAsync(state.Version, selected.Id, typeName ?? "").GetAwaiter().GetResult()); }
		catch (Exception ex) { diagnostic.Text = ex.GetBaseException().Message; return false; }
	}
	void RequestItemType()
	{
		var current = ItemType();
		var typeName = MessageService.ShowInputBox("Item type", "GType name of the list store's items (empty for GObject):", current ?? "");
		if (typeName != null) SetItemType(typeName);
	}
	public bool SetFactoryTemplate(string template) { if (host == null || selected == null) return false; return Mutate(() => host.SetFactoryTemplateAsync(state.Version, selected.Id, template).GetAwaiter().GetResult()); }

	/// <summary>Keeps X.cs and X.ui.cs in step with the .ui's signals (see GtkCodeBehind): with
	/// <paramref name="newHandler"/>, ensures the behavior class has that method and opens it there.</summary>
	/// <summary>The message under the canvas (a refusal, a render failure, a code-behind notice).</summary>
	public string DiagnosticText => diagnostic.Text;
	string? codeBehindNotice;
	void UpdateCodeBehind(string? newHandler, bool force = false)
	{
		codeBehindNotice = null;
		var uiPath = PrimaryFileName?.ToString();
		if (host == null || string.IsNullOrEmpty(uiPath)) return;
		// A composite template is instantiated by its own C# class (Gir.Core [Template]); a BuildUi
		// companion would build a second, detached copy. SetEvent refuses new signals for a
		// template; this guard covers a template that already carries one (hand-edited).
		if (IsCompositeTemplate) {
			if (newHandler != null) diagnostic.Text = "Composite template: connect this signal in the template's C# class; the designer only generates BuildUi for object-root .ui files.";
			return;
		}
		try {
			var hasSignals = loadedText.Contains("<signal", StringComparison.Ordinal) || newHandler != null;
			if (newHandler == null && !hasSignals && !force && !File.Exists(GtkCodeBehind.CompanionPath(uiPath))) return;
			if (GtkCodeBehind.EnsureBehaviorClass(uiPath) is not { } behavior) return;
			var wiring = host.SignalWiringAsync(Path.GetFileName(uiPath), behavior.Namespace, behavior.ClassName).GetAwaiter().GetResult();
			// The companion's AddMenuActions is called from application code; losing it (the last window
			// action left the menu) must not break that call site silently.
			var hadMenuActions = GtkCodeBehind.ReadCompanion(uiPath)?.Contains("AddMenuActions", StringComparison.Ordinal) == true;
			GtkCodeBehind.WriteCompanion(uiPath, wiring.Source);
			if (hadMenuActions && !wiring.Source.Contains("AddMenuActions", StringComparison.Ordinal)) {
				// Kept until the next code-behind update: every render rebuilds the diagnostic text.
				codeBehindNotice = $"No window actions remain, so AddMenuActions was removed from {Path.GetFileName(GtkCodeBehind.CompanionPath(uiPath))}; remove the call to it from your code.";
				diagnostic.Text += Environment.NewLine + codeBehindNotice;
			}
			var line = GtkCodeBehind.AddMissingHandlers(uiPath, behavior.ClassName, wiring.Handlers);
			if (newHandler != null && wiring.Handlers.FirstOrDefault(h => h.Handler == newHandler) is { Supported: false } skipped)
				diagnostic.Text = $"Signal '{skipped.Signal}' is not connected by the designer: {skipped.Reason}.";
			if (newHandler != null && line.HasValue) {
				var view = SD.FileService.OpenFile(ICSharpCode.Core.FileName.Create(GtkCodeBehind.BehaviorPath(uiPath)));
				(view?.GetService(typeof(ICSharpCode.SharpDevelop.Editor.ITextEditor)) as ICSharpCode.SharpDevelop.Editor.ITextEditor)?.JumpTo(line.Value, 1);
			}
		} catch (Exception ex) {
			ICSharpCode.Core.LoggingService.Warn("GTK designer: could not update the code-behind for " + uiPath + ": " + ex.Message);
		}
	}
	public bool ReorderSelected(int delta) { if (selected == null || host == null) return false; var id = selected.Id; return Mutate(() => host.ReorderAsync(state.Version, id, delta).GetAwaiter().GetResult()) && SelectById(id); }
	bool RenameSelected(string newId) { if (selected == null || host == null || selected.Id.StartsWith("$", StringComparison.Ordinal)) return false; var oldId = selected.Id; return Mutate(() => host.RenameAsync(state.Version, oldId, newId).GetAwaiter().GetResult()) && SelectById(newId); }
	/// <summary>Edits the selected list/grid/column view's cell template (the factory bytes).</summary>
	void EditFactoryTemplate()
	{
		if (selected == null || FactoryTemplate() is not { } template) return;
		var window = new Window {
			Title = "List item template", Width = 760, Height = 520,
			WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current?.MainWindow
		};
		var text = new TextBox {
			Text = template, AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Menlo, Consolas, monospace"),
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap
		};
		var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 6, 6, 0) };
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 6, 0, 0) };
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
		buttons.Children.Add(ok); buttons.Children.Add(cancel);
		var panel = new DockPanel { Margin = new Thickness(8) };
		DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(text);
		window.Content = panel;
		var accepted = false; ok.Click += (_, _) => { accepted = true; window.Close(); };
		window.ShowDialog();
		if (accepted && text.Text != template && !SetFactoryTemplate(text.Text)) MessageService.ShowError(state.Error);
	}

	/// <summary>Edits the selected popover's menu as the shared indented outline: one entry per line,
	/// <c>[section]</c>/<c>[submenu]</c> lines holding the entries indented below them.</summary>
	void EditMenu()
	{
		if (selected == null || MenuItems() is not { } items) return;
		var window = new Window {
			Title = "Menu items", Width = 560, Height = 420,
			WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current?.MainWindow
		};
		var text = new TextBox {
			Text = DesignerMenuOutline.Format(items),
			AcceptsReturn = true, FontFamily = new FontFamily("Menlo, Consolas, monospace"),
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		};
		var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 6, 6, 0) };
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 6, 0, 0) };
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
		buttons.Children.Add(ok); buttons.Children.Add(cancel);
		var panel = new DockPanel { Margin = new Thickness(8) };
		DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(text);
		window.Content = panel;
		var accepted = false; ok.Click += (_, _) => { accepted = true; window.Close(); };
		window.ShowDialog();
		if (!accepted) return;
		if (DesignerMenuOutline.Parse(text.Text, items, out var error) is not { } edited) { MessageService.ShowError(error); return; }
		if (!SetMenuItems(edited)) MessageService.ShowError(state.Error);
	}

	void RequestRename() { if (selected == null || selected.Id.StartsWith("$", StringComparison.Ordinal)) return; var newId = MessageService.ShowInputBox("Rename", "Enter a new object id:", selected.Name ?? selected.Id)?.Trim(); if (!string.IsNullOrEmpty(newId) && !string.Equals(newId, selected.Id, StringComparison.Ordinal) && !RenameSelected(newId)) MessageService.ShowError(state.Error); }
	public bool PointerReorder(string sourceId, string targetId) { if (state.Tree == null) return false; var source = FindById(sourceId); var target = FindById(targetId); return source != null && target != null && ReorderBetween(state.Tree, source, target); }
	// External .ui edits arrive through LoadInternal. Refresh never takes a stale on-disk copy over
	// unsaved designer edits; it only redraws the current source model and retains its history.
	public void RefreshDesign() => RefreshPreview();
	// A stylesheet affects only the native snapshot, never the GtkBuilder source model.
	void RefreshPreview() { if (host == null) return; state = host.RenderAsync(state.Version).GetAwaiter().GetResult(); Rebuild(); }
	public void SetPreviewTheme(string theme) { if (host == null) return; state = host.SetThemeAsync(state.Version, theme).GetAwaiter().GetResult(); canvas.SetTheme(theme); Rebuild(); }
	void ConfigureCssWatcher(string uiPath)
	{
		var directory = Path.GetDirectoryName(uiPath);
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
		// The same preview-only debounce also covers custom-widget declarations: neither CSS nor
		// .gtkdesigner.json is source-model input, so both must redraw without resetting undo.
		watchedCssPaths = new[] { Path.ChangeExtension(uiPath, ".css"), Path.Combine(directory, "style.css"), Path.Combine(directory, ".gtkdesigner.json") }
			.Concat(ResourceFiles(directory))
			.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		cssWatcher?.Dispose();
		try {
			cssWatcher = new FileSystemWatcher(directory, "*") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
			cssWatcher.Changed += CssChanged; cssWatcher.Created += CssChanged; cssWatcher.Deleted += CssChanged; cssWatcher.Renamed += CssRenamed;
			cssWatcher.EnableRaisingEvents = true;
		} catch (Exception ex) {
			cssWatcher = null;
			LoggingService.Warn("GTK designer: could not watch preview CSS: " + ex.Message);
		}
	}
	/// <summary>The GResource manifests beside the document and the files they list. The preview
	/// resolves <c>resource:///</c> when it builds the native tree, so an edited image or manifest
	/// has to redraw too - neither is part of the GtkBuilder source model.</summary>
	static IEnumerable<string> ResourceFiles(string directory)
	{
		string[] manifests;
		try {
			manifests = Directory.EnumerateFiles(directory, "*.gresource.xml", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal).ToArray();
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return Array.Empty<string>();
		}
		var files = new List<string>(manifests);
		foreach (var manifest in manifests) {
			try {
				var root = Path.GetDirectoryName(manifest)!;
				foreach (var file in System.Xml.Linq.XDocument.Load(manifest).Descendants("file")) {
					var name = file.Value.Trim();
					if (name.Length > 0) files.Add(Path.GetFullPath(Path.Combine(root, name)));
				}
			} catch { /* an unreadable manifest is reported by the preview host when it loads it */ }
		}
		return files;
	}
	void CssChanged(object sender, FileSystemEventArgs e) { if (IsWatchedPreviewPath(e.FullPath)) ScheduleCssRefresh(); }
	void CssRenamed(object sender, RenamedEventArgs e) { if (IsWatchedPreviewPath(e.FullPath) || IsWatchedPreviewPath(e.OldFullPath)) ScheduleCssRefresh(); }
	/// <summary>A manifest created after the watcher started is still a preview input: the glob
	/// matches it even though it was not in the list captured by <see cref="ConfigureCssWatcher"/>.</summary>
	bool IsWatchedPreviewPath(string path) => path.EndsWith(".gresource.xml", StringComparison.OrdinalIgnoreCase)
		|| watchedCssPaths.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase);
	void ScheduleCssRefresh()
	{
		var dispatcher = Application.Current?.Dispatcher;
		if (dispatcher == null) return;
		_ = dispatcher.BeginInvoke(new Action(() => {
			if (disposed) return;
			cssRefreshTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
			cssRefreshTimer.Tick -= CssRefreshTimerTick;
			cssRefreshTimer.Tick += CssRefreshTimerTick;
			cssRefreshTimer.Stop(); cssRefreshTimer.Start();
		}));
	}
	void CssRefreshTimerTick(object? sender, EventArgs e)
	{
		cssRefreshTimer?.Stop();
		if (!disposed && host?.IsAlive == true) RefreshPreview();
	}
	public void RestartDesignHost() { if (host == null) return; state = host.RestartPoolAsync().GetAwaiter().GetResult(); canvas.SetTheme(host.PreviewTheme); loadedText = host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text; Rebuild(); }
	public void TerminateDesignHost() { if (host == null) return; state = host.TerminateAndRecoverAsync().GetAwaiter().GetResult(); canvas.SetTheme(host.PreviewTheme); requestedRenderRevision = renderedRevision = state.Render?.Sequence ?? 0; Rebuild(); }
	public void ShowSource() { var window = WorkbenchWindow; if (window == null) return; for (var i = 0; i < window.ViewContents.Count; i++) if (!ReferenceEquals(window.ViewContents[i], this)) { window.SwitchView(i); return; } }
	bool Mutate(Func<DesignerSessionState> action) { state = action(); if (state.Accepted) { PrimaryFile?.MakeDirty(); QueueRender(); } Rebuild(); commands.Invalidate(); return state.Accepted; }
	void QueueRender()
	{
		if (host == null || !state.Accepted) return;
		var version = state.Version; requestedRenderRevision = version;
		renderCancellation?.Cancel(); renderCancellation?.Dispose(); renderCancellation = new CancellationTokenSource(); var token = renderCancellation.Token;
		_ = RenderLatestAsync(host, version, token);
	}
	async Task RenderLatestAsync(GtkDesignerHostClient renderingHost, long version, CancellationToken token)
	{
		try {
			var rendered = await renderingHost.RenderAsync(version, token).ConfigureAwait(false);
			await Application.Current.Dispatcher.InvokeAsync(() => { if (token.IsCancellationRequested || host != renderingHost || state.Version != version || rendered.Render?.Sequence != version) return; state = rendered; renderedRevision = version; Rebuild(); });
		} catch (OperationCanceledException) { } catch (Exception ex) { await Application.Current.Dispatcher.InvokeAsync(() => diagnostic.Text = "GTK render failed: " + ex.Message); }
	}
	void Rebuild()
	{
		diagnostic.Text = (HasNativeFrame ? Status : Status + " - no native frame: the GTK 4 runtime could not render this interface.")
			+ (codeBehindNotice == null ? "" : Environment.NewLine + codeBehindNotice);
		MergeTemplateSignals();
		pads.UpdateRoots(state.Tree == null ? null : state.Tree.Id == "$interface" ? state.Tree.Children : new[] { state.Tree });
		canvasController.ApplySnapshot(CanvasSnapshot());
		canvasController.RestoreSelection(selection.SelectedIds);
	}

	/// <summary>The session as the canvas shows it: the native frame, and the rendered root object
	/// (not the "$interface" wrapper) with a tree path on every node, which the canvas's hit test
	/// answers with.</summary>
	DesignerSessionState CanvasSnapshot()
	{
		var root = state.Tree?.Id == "$interface" ? state.Tree.Children.FirstOrDefault() : state.Tree;
		var paths = new Dictionary<string, string>(StringComparer.Ordinal);
		DesignerElementNode Copy(DesignerElementNode node, string path)
		{
			paths[node.Id] = path;
			var copy = new DesignerElementNode { Id = node.Id, Name = node.Name, Type = node.Type, X = node.X, Y = node.Y, Width = node.Width, Height = node.Height, ZIndex = node.ZIndex, BaselineOffset = node.BaselineOffset, Path = path, IsDesignable = node.Id != "$interface", IsVisible = node.IsVisible, LayoutMode = node.LayoutMode };
			for (var index = 0; index < node.Children.Count; index++)
				copy.Children.Add(Copy(node.Children[index], path.Length == 0 ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : path + "," + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
			return copy;
		}
		var tree = root == null ? null : Copy(root, "");
		pathById = paths;
		return new DesignerSessionState { Accepted = true, Version = state.Version, Render = HasNativeFrame ? state.Render : null, Tree = tree };
	}

	/// <summary>A committed canvas drag: GTK positions nothing freely, so dropping an object onto a
	/// sibling moves it to that sibling's place; anything else snaps back.</summary>
	void CommitCanvasDrag(ElementDragInfo drag)
	{
		var previewRoot = state.Tree?.Id == "$interface" ? state.Tree.Children.FirstOrDefault() : state.Tree;
		var source = previewRoot == null ? null : Flatten(previewRoot).FirstOrDefault(n => n.Id == drag.Name);
		var over = previewRoot == null ? null : NativeNodeAt(previewRoot, new Point(drag.EndX + drag.EndWidth / 2, drag.EndY + drag.EndHeight / 2), source);
		if (previewRoot == null || source == null || over == null || !ReorderBetween(previewRoot, source, over))
			canvasController.RestoreSelection(selection.SelectedIds);
	}

	void Select(DesignerElementNode? node, bool toggle = false) => selection.Select(node == null ? Array.Empty<DesignerElementNode>() : new[] { node }, toggle ? DesignerSelectionOperation.Toggle : DesignerSelectionOperation.Replace);
	void ConfigureCanvas()
	{
		// GTK owns layout, so no resize handles, design sizes or visual states. Theme is applied
		// only while its native frame is rendered and is restored before another pooled document.
		canvas.Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit | DesignerCanvasCapabilities.Gridlines
			| DesignerCanvasCapabilities.Theme | DesignerCanvasCapabilities.ShowNames | DesignerCanvasCapabilities.StatusBar;
		canvas.ResizeHandlesEnabled = false;
		canvas.SetContextCommands(DesignerStandardCommands.EditMenu(pasteHere: true).Append(("-", "")).Concat(DesignerStandardCommands.MoveMenu).Concat(new[] {
			("-", ""), ("Align/Horizontal: Fill", "align-h-fill"), ("Align/Horizontal: Start", "align-h-start"), ("Align/Horizontal: Center", "align-h-center"), ("Align/Horizontal: End", "align-h-end"),
			("Align/Vertical: Fill", "align-v-fill"), ("Align/Vertical: Start", "align-v-start"), ("Align/Vertical: Center", "align-v-center"), ("Align/Vertical: End", "align-v-end"),
			("-", ""), ("Grid/Column span +1", "span-column-more"), ("Grid/Column span -1", "span-column-less"), ("Grid/Row span +1", "span-row-more"), ("Grid/Row span -1", "span-row-less"),
			("Grid/Insert row above", "grid-insert-row"), ("Grid/Insert column left", "grid-insert-column"),
			("Grid/Delete row", "grid-delete-row"), ("Grid/Delete column", "grid-delete-column"),
			("-", ""), ("Edit List Item Template...", "edit-factory-template"), ("List Item Factory/Builder Template", "factory-GtkBuilderListItemFactory"), ("List Item Factory/Signals (code)", "factory-GtkSignalListItemFactory"), ("Sort Column/Enable", "column-sortable-on"), ("Sort Column/Disable", "column-sortable-off"), ("Model/Data: String List", "model-data-GtkStringList"), ("Model/Data: List Store", "model-data-GListStore"), ("Model/Item Type...", "model-item-type"), ("Model/Add Sorting", "model-add-sort"), ("Model/Remove Sorting", "model-remove-sort"), ("Model/Add Filtering", "model-add-filter"), ("Model/Remove Filtering", "model-remove-filter"), ("Selection/None", "selection-GtkNoSelection"), ("Selection/Single", "selection-GtkSingleSelection"), ("Selection/Multiple", "selection-GtkMultiSelection"), ("Edit Menu...", "edit-menu") })
			.Append(("-", "")).Concat(DesignerStandardCommands.WrapMenu(WrapContainers)));
		canvas.ContextCommandCanExecute = command => command switch {
			"grid-insert-row" or "grid-insert-column" or "grid-delete-row" or "grid-delete-column" => host?.IsAlive == true && SelectedGridChild() != null,
			var span when span.StartsWith("span-", StringComparison.Ordinal) => host?.IsAlive == true && SelectedGridChild() is { } cell
				&& (!span.EndsWith("less", StringComparison.Ordinal) || int.TryParse(Value(cell.Node, span.Contains("column") ? "layout:column-span" : "layout:row-span", "1"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var currentSpan) && currentSpan > 1),
			"paste" => host?.IsAlive == true && ClipboardSnippets().Length > 0,
			"paste-here" => host?.IsAlive == true && canvas.ContextMenuDesignPoint != null && ClipboardSnippets().Length > 0,
			var move when DesignerStandardCommands.IsMoveCommand(move) => host?.IsAlive == true && SiblingPosition() is { } position && DesignerStandardCommands.CanMove(move, position.Index, position.Count),
			"edit-factory-template" => host?.IsAlive == true && selected != null && Flatten(selected).Any(node => node.Type == "GtkBuilderListItemFactory"),
			"edit-menu" => host?.IsAlive == true && selected?.Type is "GtkPopoverMenu" or "GtkMenuButton",
			var factory when GtkUiDocumentEditor.FactoryCommand(factory) != null => host?.IsAlive == true && selected?.Type is "GtkListView" or "GtkGridView" or "GtkColumnViewColumn",
			var column when GtkUiDocumentEditor.ColumnSortCommand(column) != null => host?.IsAlive == true && selected?.Type == "GtkColumnViewColumn",
			var data when GtkUiDocumentEditor.ModelDataCommand(data) != null => host?.IsAlive == true && selected?.Type is "GtkListView" or "GtkGridView" or "GtkColumnView",
			"model-item-type" => host?.IsAlive == true && selected?.Type is "GtkListView" or "GtkGridView" or "GtkColumnView" or "GListStore",
			var model when GtkUiDocumentEditor.ModelCommand(model) != null => host?.IsAlive == true && selected?.Type is "GtkListView" or "GtkGridView" or "GtkColumnView",
			var selection when selection.StartsWith("selection-", StringComparison.Ordinal) => host?.IsAlive == true && selected?.Type is "GtkListView" or "GtkGridView" or "GtkColumnView",
			"unwrap" => host?.IsAlive == true && DesignerStandardCommands.CanUnwrap(selection.SelectedIds.Count, SelectedHasChildren(), SharesOneParent()),
			var wrap when DesignerStandardCommands.WrapContainer(wrap, WrapContainers) is { } container => host?.IsAlive == true && DesignerStandardCommands.CanWrap(selection.SelectedIds.Count, SharesOneParent(), containerHoldsOneChild: container == "GtkFrame"),
			_ => selection.SelectedIds.Count > 0 && host?.IsAlive == true
		};
		canvasController.ClearsSelectionOnEmptyClick = true;
		canvasController.SelectionChanged += (_, ids) => { if (!ids.SequenceEqual(selection.SelectedIds)) pads.CommitSelection(ids); };
		canvas.DesignThemeRequested += (_, theme) => SetPreviewTheme(theme);
		canvasController.ElementPicked += (_, id) => { if (!selection.SelectedIds.Contains(id)) SelectById(id); };
		canvasController.ElementDragCommitted += (_, drag) => CommitCanvasDrag(drag);
		canvasController.ElementGroupDragCommitted += (_, _) => canvasController.RestoreSelection(selection.SelectedIds);
		canvasController.ContextCommandRequested += (_, command) => {
			switch (command.Command) {
				case "delete": DeleteSelected(); break;
				case "copy": CopySelection(); break;
				case "cut": if (CopySelection()) DeleteSelected(); break;
				case "paste": PasteIntoSelection(); break;
				case "paste-here": PasteHere(); break;
				case "duplicate": DuplicateSelection(); break;
				case var move when DesignerStandardCommands.IsMoveCommand(move): MoveSelected(move); break;
				case var wrap when DesignerStandardCommands.WrapContainer(wrap, WrapContainers) is { } container: WrapSelected(container); break;
				case "unwrap": UnwrapSelected(); break;
				case "edit-factory-template": EditFactoryTemplate(); break;
				case "edit-menu": EditMenu(); break;
				case var factory when GtkUiDocumentEditor.FactoryCommand(factory) is { } factoryClass: SetFactoryClass(factoryClass); break;
				case var column when GtkUiDocumentEditor.ColumnSortCommand(column) is { } sortable: SetColumnSortable(sortable); break;
				case var data when GtkUiDocumentEditor.ModelDataCommand(data) is { } dataClass: SetModelData(dataClass); break;
				case "model-item-type": RequestItemType(); break;
				case var model when GtkUiDocumentEditor.ModelCommand(model) is { } change: SetModelWrapper(change.Kind, change.Add); break;
				case var selection when selection.StartsWith("selection-", StringComparison.Ordinal): SetSelectionModel(selection.Substring("selection-".Length)); break;
				case var span when span.StartsWith("span-", StringComparison.Ordinal): SpanSelected(span); break;
				case "grid-insert-row" or "grid-insert-column": InsertGridTrack(command.Command); break;
				case "grid-delete-row" or "grid-delete-column": DeleteGridTrack(command.Command); break;
				case var align when align.StartsWith("align-", StringComparison.Ordinal): AlignSelected(align); break;
			}
		};
		canvas.RenameRequested += (_, _) => RequestRename();
		canvasController.UndoRedoRequested += (_, undo) => { if (undo) Undo(); else Redo(); };
		canvas.AllowDrop = true;
		canvas.DragOver += (_, e) => {
			var isTool = IsToolName(ToolboxDragData.GetTypeName(e.Data));
			e.Effects = isTool ? DragDropEffects.Copy : DragDropEffects.None;
			ShowDropIndicator(isTool ? PlanDrop(e) : null);
			e.Handled = true;
		};
		canvas.DragLeave += (_, _) => ShowDropIndicator(null);
		canvas.Drop += (_, e) => {
			ShowDropIndicator(null);
			if (ToolboxDragData.GetTypeName(e.Data) is not { } type || !IsToolName(type)) return;
			var design = canvas.ToDesignPoint(e.GetPosition(canvas));
			// The container and position the indicator showed; the host re-plans from GTK's own
			// measured bounds and uses the same index/cell.
			if (PlanDrop(e) is { } plan) AddTo(plan.ContainerId, type, design.X, design.Y);
			else Add(type);
			e.Handled = true;
		};
	}
	/// <summary>The insertion line or target cell drawn while a toolbox item is dragged over the
	/// surface - the visible insertion point GTK 4's container-driven layout needs.</summary>
	readonly System.Windows.Controls.Border dropIndicator = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)), Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x78, 0xD4)) };
	internal GtkDropPlan? LastDropPlan { get; private set; }

	GtkDropPlan? PlanDrop(DragEventArgs e)
	{
		var previewRoot = state.Tree?.Id == "$interface" ? state.Tree.Children.FirstOrDefault() : state.Tree;
		if (previewRoot == null) return null;
		var design = canvas.ToDesignPoint(e.GetPosition(canvas));
		return PlanDropAt(previewRoot, design.X, design.Y);
	}

	internal bool DropAt(GtkDropPlan plan, string type, double x, double y) => AddTo(plan.ContainerId, type, x, y);

	internal GtkDropPlan? PlanDropAt(double x, double y)
	{
		var previewRoot = state.Tree?.Id == "$interface" ? state.Tree.Children.FirstOrDefault() : state.Tree;
		return previewRoot == null ? null : PlanDropAt(previewRoot, x, y);
	}

	static GtkDropPlan? PlanDropAt(DesignerElementNode root, double x, double y) => GtkDropPlanner.Plan(ToDropNode(root), x, y);

	static GtkDropNode ToDropNode(DesignerElementNode n)
	{
		string? Prop(string name) => n.Properties.FirstOrDefault(p => p.Name == name)?.Value;
		int Int(string name, int fallback) => int.TryParse(Prop(name), out var v) ? v : fallback;
		return new GtkDropNode(n.Id, n.Type, n.X, n.Y, n.Width, n.Height, n.Children.Select(ToDropNode).ToList(),
			Prop("orientation"), Int("layout:column", 0), Int("layout:row", 0), Int("layout:column-span", 1), Int("layout:row-span", 1));
	}

	/// <summary>The protocol form of a plan: the container plus whichever of index / cell the
	/// receiving container is addressed by. GTK has no free positioning, so Rect stays null - the
	/// point is resolved into a position in the container's own terms before it crosses.</summary>
	static DesignerDropTarget ToDropTarget(GtkDropPlan? plan)
	{
		if (plan == null)
			return new DesignerDropTarget();
		return new DesignerDropTarget {
			ContainerId = plan.ContainerId,
			Index = plan.Index,
			Cell = plan.Cell is { } cell ? new DesignerGridCell { Column = cell.Column, Row = cell.Row } : null,
			Arrangement = plan.Cell != null ? DesignerArrangements.Grid
				: plan.Index != null ? DesignerArrangements.Stack
				: DesignerArrangements.Freeform,
			Indicator = new[] { plan.Indicator.X, plan.Indicator.Y, plan.Indicator.Width, plan.Indicator.Height }
		};
	}

	void ShowDropIndicator(GtkDropPlan? plan)
	{
		LastDropPlan = plan;
		if (!canvas.ExtensionLayer.Children.Contains(dropIndicator)) canvas.ExtensionLayer.Children.Add(dropIndicator);
		if (plan == null) { dropIndicator.Visibility = Visibility.Collapsed; return; }
		var (x, y, width, height) = plan.Indicator;
		var origin = canvas.DesignToContentPoint(x, y);
		System.Windows.Controls.Canvas.SetLeft(dropIndicator, origin.X);
		System.Windows.Controls.Canvas.SetTop(dropIndicator, origin.Y);
		dropIndicator.Width = Math.Max(2, width * canvas.ViewportScale);
		dropIndicator.Height = Math.Max(2, height * canvas.ViewportScale);
		dropIndicator.Visibility = Visibility.Visible;
	}

	void FitView() { canvas.FitView(); FitMeasured = canvas.HasRender && canvas.IsFitMode; }
	static bool IsContainer(DesignerElementNode n) => GtkDropPlanner.Containers.Contains(n.Type);
	DesignerElementNode? NearestContainer(DesignerElementNode node) => DesignerStandardCommands.NearestContainer(state.Tree, node, IsContainer);
	static DesignerElementNode? NativeNodeAt(DesignerElementNode root, Point point, DesignerElementNode? except = null) => Flatten(root).Where(n => !ReferenceEquals(n, except) && n.Width > 0 && n.Height > 0 && point.X >= n.X && point.Y >= n.Y && point.X <= n.X + n.Width && point.Y <= n.Y + n.Height).OrderBy(n => n.Width * n.Height).FirstOrDefault();
	bool ReorderBetween(DesignerElementNode root, DesignerElementNode source, DesignerElementNode target) { var delta = DesignerStandardCommands.ReorderDelta(root, source, target); if (delta == 0) return false; Select(source); return ReorderSelected(delta); }
	static string Value(DesignerElementNode n, string key, string fallback) => n.Properties.FirstOrDefault(p => p.Name == key)?.Value ?? fallback; static IEnumerable<DesignerElementNode> Flatten(DesignerElementNode n) => DesignerStandardCommands.Flatten(n);
	DesignerDocumentSnapshot Snapshot(string text, long version) => new() { Version = version, PrimaryFileName = PrimaryFile?.FileName.ToString() ?? "", Files = { new DesignerSourceFileSnapshot { FileName = PrimaryFile?.FileName.ToString() ?? "", Kind = "Designer", Text = text } } };
	public int LoadCount { get; private set; }
	// In the split layout the source editor and this designer are both live, and every switch of the
	// active view hands the file between them: this view saves (SaveInternal sets loadedText), the
	// editor loads, and switching back reloads it here. Re-opening the host document for text it
	// produced itself reset the host's undo/redo history (session/open -> Editor.Reset) - so undo
	// stopped working after merely clicking into the XML. Only a real source change re-opens.
	protected override void LoadInternal(OpenedFile file, Stream stream) { LoadCount++; using var reader = new StreamReader(stream, leaveOpen: true); var text = reader.ReadToEnd(); ConfigureCssWatcher(file.FileName.ToString()); var requiresAdw = System.Text.RegularExpressions.Regex.IsMatch(text, @"<requires\s+lib\s*=\s*""libadwaita"""); if (requiresAdw != documentRequiresAdw) { documentRequiresAdw = requiresAdw; RefreshToolbox(); } if (host != null && host.IsAlive && state.Accepted && string.Equals(text, loadedText, StringComparison.Ordinal)) return; loadedText = text; if (host == null) { host = GtkDesignerHostClient.CreateAsync().GetAwaiter().GetResult(); host.Recovered += HostRecovered; } state = host.OpenAsync(Snapshot(loadedText, 1)).GetAwaiter().GetResult(); canvas.SetTheme(host.PreviewTheme); requestedRenderRevision = renderedRevision = state.Render?.Sequence ?? 0; Rebuild();
		OutputChannel.Write("GtkDesigner", "Host started for " + System.IO.Path.GetFileName(file.FileName.ToString())); }
	protected override void SaveInternal(OpenedFile file, Stream stream) { // The host can be down here: a restart moves focus into the source pane, and that view switch saves this view first.
		var text = host == null ? loadedText : host.CurrentTextOrRecovery(state.Version, loadedText); using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true); writer.Write(text); writer.Flush(); loadedText = text;
		// Signals may have changed since the companion was last written (removed, renamed, added
		// in the XML editor); WriteCompanion only touches the file when its content differs.
		if (host?.IsAlive == true) UpdateCodeBehind(null); }
	void HostRecovered(object? sender, DesignerSessionState recovered) { Application.Current.Dispatcher.BeginInvoke(new Action(() => { OutputChannel.Write("GtkDesigner", "Host recovered"); state = recovered; canvas.SetTheme(host?.PreviewTheme ?? "Light"); requestedRenderRevision = renderedRevision = recovered.Render?.Sequence ?? 0; Rebuild(); })); }
	public override void Dispose() { disposed = true; OutputChannel.Write("GtkDesigner", "Designer view disposed"); cssRefreshTimer?.Stop(); cssWatcher?.Dispose(); renderCancellation?.Cancel(); renderCancellation?.Dispose(); pads.Dispose(); properties.Clear(); if (host != null) host.Recovered -= HostRecovered; host?.Dispose(); base.Dispose(); }
}
