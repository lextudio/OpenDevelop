using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Windows; using System.Windows.Controls; using System.Windows.Input; using System.Windows.Media;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Presentation; using ICSharpCode.SharpDevelop.Designer.Remote; using ICSharpCode.SharpDevelop.Designer.Shell; using ICSharpCode.SharpDevelop.Gui; using ICSharpCode.SharpDevelop.WinForms; using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.Designer.Surface;
using System.Threading.Tasks;
namespace ICSharpCode.MewUIDesigner;

public sealed class MewUIDesignerViewContent : AbstractViewContentHandlingLoadErrors, IOutlineContentHost, IToolsHost, IHasPropertyContainer, IUndoHandler, IFilterableToolbox, IDesignCanvasBackend, IToolboxSourceDropHandler
{
	public static readonly string[] ToolNames = { "StackPanel", "Grid", "DockPanel", "WrapPanel", "Border", "ScrollViewer", "Label", "Button", "TextBox", "CheckBox", "RadioButton", "Slider", "ProgressBar", "ComboBox", "ListBox", "Image" };
	readonly DocumentOutlineControl outline = new() { IconMapper = MewUIControlMapper.Instance }; readonly PropertyContainer properties = new(); readonly TextBlock diagnostic = new() { Foreground = Brushes.OrangeRed, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap }; readonly OpenedFile mxamlFile;
	// The shared design canvas (ICSharpCode.DesignerCanvas addin) showing the host's real MewUI
	// render, keyed by element id (the Name, or a path-based id for an unnamed element). MewUI panels lay children out, so there are no resize handles
	// and a drag is a reorder among siblings (see CommitCanvasDrag).
	readonly DesignSurface canvas = new();
	readonly DesignSurfaceController canvasController;
	Dictionary<string, string> pathById = new(StringComparer.Ordinal);
	readonly DesignerToolboxScope tools;
	readonly DesignerSelectionController selection;
	readonly DesignerPadController pads;
	readonly DesignerCommandController commands = new();
	MewUIDesignerHostClient? host; DesignerSessionState state = new(); DesignerElementNode? selected; string loadedMxamlText = "";
	public MewUIDesignerViewContent(OpenedFile file) : base(file)
	{
		tools = new DesignerToolboxScope(this, MewUIControlMapper.Instance);
		tools.Register("mewui", ToolNames.Select(name => new DesignerToolboxItemInfo { Name = name, DisplayName = name, TypeName = name, Category = DesignerToolboxCatalog.ResolveCategory(MewUIControlMapper.Instance, name) }));
		tools.SetScopes("mewui");
		tools.ItemInvoked += (_, item) => Add(item.TypeName);
		selection = new DesignerSelectionController(node => Adapter(node), nodes => new DesignerMultiPropertyAdapter(nodes.Select(node => (object)Adapter(node))));
		commands.RegisterStandard(() => host?.IsAlive == true && state.CanUndo, () => { Mutate(() => host!.UndoAsync(state.Version).GetAwaiter().GetResult()); return true; },
			() => host?.IsAlive == true && state.CanRedo, () => { Mutate(() => host!.RedoAsync(state.Version).GetAwaiter().GetResult()); return true; },
			() => selection.SelectedIds.Count > 0 && host?.IsAlive == true, DeleteSelectedCore);
		pads = new DesignerPadController(selection, outline.SetRoots, value => properties.SelectedObject = value, outline.SelectNodeById, node => { selected = node; canvasController?.RestoreSelection(selection.SelectedIds); });
		canvasController = new DesignSurfaceController(canvas, this, DesignSurfaceKeying.Id);
		mxamlFile = file; TabPageText = "Design";
		ConfigureCanvas(); var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.Children.Add(canvas); Grid.SetRow(diagnostic, 1); grid.Children.Add(diagnostic); UserContent = grid;
		outline.SelectionCommitted += (_, _) => pads.CommitOutlineSelection(outline.SelectedNode?.Id);
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => Undo(), (_, e) => e.CanExecute = commands.CanExecute(DesignerCommandNames.Undo)));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => Redo(), (_, e) => e.CanExecute = commands.CanExecute(DesignerCommandNames.Redo)));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, (_, _) => DeleteSelected(), (_, e) => e.CanExecute = commands.CanExecute(DesignerCommandNames.Delete)));
	}
	public object OutlineContent => outline; public object ToolsContent => tools.Activate(); public ListBox ToolboxControl => tools.Control; public int ZoomComboSelectedIndex => canvas.ZoomCombo.SelectedIndex; public PropertyContainer PropertyContainer => properties; public string Status => state.Accepted ? $"Ready: {ElementCount} elements (host {host?.ProcessId}, MewUI frame {(HasNativeFrame ? $"{NativeFrameWidth}x{NativeFrameHeight}" : "unavailable")})" : state.Error; public string WindowClassName => state.Tree?.Name ?? ""; public int ElementCount => state.Tree == null ? 0 : Flatten(state.Tree).Count(); public bool IsDesignerDirty => mxamlFile.IsDirty; public string SelectedName => selected?.Name ?? ""; public int HostProcessId => host?.ProcessId ?? 0;
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
	bool IToolboxSourceDropHandler.CanAcceptToolboxDrop(IDataObject data) => ToolboxDragData.GetTypeName(data) is { } type && ToolNames.Contains(type, StringComparer.Ordinal);
	/// <summary>A toolbox item dropped onto the .mxaml source beside this designer: an element on a line
	/// of its own in the innermost container holding the drop point - a panel takes any number of
	/// children, a content control (Border, ScrollViewer, Window, ...) only its first.</summary>
	ToolboxSourceEdit? IToolboxSourceDropHandler.PlanToolboxDrop(IDataObject data, string sourceText, int offset)
	{
		if (ToolboxDragData.GetTypeName(data) is not { } type || !ToolNames.Contains(type, StringComparer.Ordinal)) return null;
		var point = XmlToolboxDropPlanner.Plan(sourceText, offset, AcceptsSourceChild);
		if (point == null) return null;
		var (at, text) = XmlToolboxDropPlanner.InsertElement(sourceText, point, "<" + type + " />");
		return new ToolboxSourceEdit(at, 0, text, at + text.Length);
	}
	static readonly HashSet<string> PanelTypes = new(StringComparer.Ordinal) { "StackPanel", "Grid", "DockPanel", "WrapPanel", "Canvas", "TabControl" };
	static readonly HashSet<string> ContentTypes = new(StringComparer.Ordinal) { "Window", "Border", "ScrollViewer", "GroupBox", "TabItem", "ContentControl" };
	/// <summary>Property elements (&lt;Grid.RowDefinitions&gt;) are not content.</summary>
	static bool AcceptsSourceChild(XmlMarkupElement element)
		=> !element.IsEmpty && (PanelTypes.Contains(element.Name) || ContentTypes.Contains(element.Name) && !element.Children.Any(c => !c.Name.Contains('.')));
	/// <summary>A rendered element's bounds in screen coordinates, through the canvas's viewport
	/// (null when it has none, or nothing is rendered).</summary>
	public Rect? ScreenBoundsOf(string id)
	{
		var node = state.Tree == null || string.IsNullOrEmpty(id) ? null : Flatten(state.Tree).FirstOrDefault(n => n.Id == id) ?? Flatten(state.Tree).FirstOrDefault(n => n.Name == id);
		if (node == null || node.Width <= 0 || node.Height <= 0 || !canvas.HasRender) return null;
		return new Rect(canvas.SurfacePointToScreen(node.X, node.Y), canvas.SurfacePointToScreen(node.X + node.Width, node.Y + node.Height));
	}
	public int ToolboxItemCount => tools.VisibleItemCount; public bool IsToolboxHosted => tools.IsHosted; public bool IsOutlineHosted => ReferenceEquals((SD.Services.GetService(typeof(IOutlinePadHost)) as IOutlinePadHost)?.HostedContent, outline); public int OutlineItemCount => ElementCount;
	public int ToolbarItemCount => canvas.VisibleToolbarItems.Count; public IReadOnlyList<string> ToolbarItems => canvas.VisibleToolbarItems; public string ToolbarCapabilities => canvas.Capabilities.ToString(); public double Zoom { get => canvas.ViewportScale; set => canvas.SetViewport(Math.Clamp(value, .25, 2), 0, 0); }
	public bool Gridlines => canvas.Gridlines; public bool FitMeasured { get; private set; } public void FitDesign() => FitView(); public void ShowGridlines(bool show) { canvas.IsGridEnabled = show; canvas.SetGridlines(show); }
	public bool HasNativeFrame => state.Render is { Width: > 0, Height: > 0 } r && (!string.IsNullOrEmpty(r.Data) || !string.IsNullOrEmpty(r.PngBase64)); public int NativeFrameWidth => state.Render?.Width ?? 0; public int NativeFrameHeight => state.Render?.Height ?? 0; public int NativeBoundsCount => state.Tree == null ? 0 : Flatten(state.Tree).Count(n => n.Width > 0 && n.Height > 0);
	public string[] Diagnostics => state.Diagnostics.Select(d => d.Message).ToArray();
	public string HostLogTail { get { var log = host?.ChildLog ?? ""; return log.Length <= 2000 ? log : log[^2000..]; } }
	public string HostSessionId => host?.SessionId ?? ""; public string HostDocumentId => host?.DocumentId ?? ""; public string HostPoolKey => host?.PoolKey ?? "mewui"; public int ActiveHostLeases => MewUIDesignerHostClient.ActiveLeaseCount; public int HostRecoveryCount => host?.RecoveryCount ?? 0;
	public bool EnableUndo => commands.CanExecute("Undo"); public bool EnableRedo => commands.CanExecute("Redo");
	public void Undo() => commands.Execute("Undo"); public void Redo() => commands.Execute("Redo");
	public bool Add(string type) { if (host == null || state.Tree == null) return false; var parent = (selected != null ? NearestContainer(selected) : null) ?? Flatten(state.Tree).FirstOrDefault(IsContainer); if (parent == null) return false; var before = Flatten(state.Tree).Select(n => n.Id).ToHashSet(); Mutate(() => host.AddElementAsync(state.Version, parent.Id, new DesignerToolboxItemInfo { Name = type, TypeName = type }, "", 0, 0).GetAwaiter().GetResult()); var added = state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => !before.Contains(n.Id)); if (added != null) Select(added); return added != null; }
	public bool SetSelectedProperty(string name, string value) => selected != null && SetProperty(selected.Id, name, value);
	bool SetProperty(string id, string name, string value) { if (host == null) return false; Mutate(() => host.SetPropertyAsync(state.Version, id, name, value).GetAwaiter().GetResult()); return name == "$name" ? SelectByName(value) : selection.Find(id) != null; }
	public bool SetSelectedEvent(string name, string handler) => selected != null && SetEvent(selected.Id, name, handler);
	MewUIPropertyAdapter Adapter(DesignerElementNode node) => new(node, (name, value) => SetProperty(node.Id, name, value), (name, value) => SetEvent(node.Id, name, value));
	bool SetEvent(string id, string name, string handler) { if (host == null) return false; Mutate(() => host.SetEventAsync(state.Version, id, name, handler).GetAwaiter().GetResult()); return selection.Find(id) != null; }
	public bool SelectByName(string name) { var node = selection.Flatten().FirstOrDefault(n => n.Name == name || n.Id == name); return node != null && selection.Select(node); }
	public IReadOnlyList<string> SelectedIds => selection.SelectedIds;
	public bool SelectByNames(IEnumerable<string> names) { var ids = names.Select(name => selection.Flatten().FirstOrDefault(node => node.Name == name || node.Id == name)?.Id).Where(id => id != null).Cast<string>().ToArray(); return ids.Length > 0 && pads.CommitSelection(ids); }
	public bool DeleteSelected() => commands.Execute("Delete");
	bool DeleteSelectedCore() { if (selection.SelectedIds.Count == 0 || host == null) return false; var ids = selection.SelectedIds.ToArray(); Mutate(() => host.DeleteElementsAsync(state.Version, ids).GetAwaiter().GetResult()); return true; }
	public bool ReorderSelected(int delta) { if (selected == null || host == null) return false; var id = selected.Id; Mutate(() => host.ReorderAsync(state.Version, id, delta).GetAwaiter().GetResult()); return SelectByName(id); }
	public void RefreshDesign() { if (host == null) return; var text = host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text; state = host.UpdateAsync(Snapshot(text, state.Version + 1)).GetAwaiter().GetResult(); loadedMxamlText = text; Rebuild(); }
	public void RestartDesignHost() { if (host == null) return; state = host.RestartPoolAsync().GetAwaiter().GetResult(); loadedMxamlText = host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text; Rebuild(); }
	public void TerminateDesignHost() { if (host == null) return; state = host.TerminateAndRecoverAsync().GetAwaiter().GetResult(); Rebuild(); }
	void Mutate(Func<DesignerSessionState> action) { state = action(); mxamlFile.MakeDirty(); Rebuild(); commands.Invalidate(); }
	void Rebuild()
	{
		diagnostic.Text = HasNativeFrame ? Status : Status + " - no MewUI frame: the MewUI runtime could not render this document on this platform.";
		pads.UpdateTree(state.Tree);
		canvasController.ApplySnapshot(CanvasSnapshot());
		canvasController.RestoreSelection(selection.SelectedIds);
	}

	/// <summary>The session as the canvas shows it: the MewUI frame, and the tree with a path on
	/// every node, which the canvas's hit test answers with.</summary>
	DesignerSessionState CanvasSnapshot()
	{
		var paths = new Dictionary<string, string>(StringComparer.Ordinal);
		DesignerElementNode Copy(DesignerElementNode node, string path)
		{
			paths[node.Id] = path;
			var copy = new DesignerElementNode { Id = node.Id, Name = node.Name, Type = node.Type, X = node.X, Y = node.Y, Width = node.Width, Height = node.Height, Path = path, IsDesignable = true, IsVisible = node.IsVisible };
			for (var index = 0; index < node.Children.Count; index++)
				copy.Children.Add(Copy(node.Children[index], path.Length == 0 ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : path + "," + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
			return copy;
		}
		var tree = state.Tree == null ? null : Copy(state.Tree, "");
		pathById = paths;
		return new DesignerSessionState { Accepted = true, Version = state.Version, Render = HasNativeFrame ? state.Render : null, Tree = tree };
	}

	/// <summary>The canvas's hit test. It runs on the UI thread from a pointer press, so it is
	/// answered locally from the real MewUI layout the host already sent with the frame (the same
	/// bounds its design/hit-test RPC uses), never with a blocking round-trip.</summary>
	DesignCanvasHit? IDesignCanvasBackend.HitTest(double x, double y)
	{
		var hit = state.Tree == null ? null : NodeAt(state.Tree, new Point(x, y));
		return hit != null && pathById.TryGetValue(hit.Id, out var path) ? new DesignCanvasHit(true, path, new[] { hit.Id }) : new DesignCanvasHit(false, null, Array.Empty<string>());
	}

	/// <summary>A committed canvas drag: dropping an element onto a sibling moves it to that
	/// sibling's place; anything else snaps back.</summary>
	void CommitCanvasDrag(ElementDragInfo drag)
	{
		var source = state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => n.Id == drag.Name);
		var over = state.Tree == null ? null : NodeAt(state.Tree, new Point(drag.EndX + drag.EndWidth / 2, drag.EndY + drag.EndHeight / 2), source);
		if (state.Tree == null || source == null || over == null || !ReorderBetween(state.Tree, source, over))
			canvasController.RestoreSelection(selection.SelectedIds);
	}
	void Select(DesignerElementNode? n, bool toggle = false) => selection.Select(n == null ? Array.Empty<DesignerElementNode>() : new[] { n }, toggle ? DesignerSelectionOperation.Toggle : DesignerSelectionOperation.Replace);
	void ConfigureCanvas()
	{
		canvas.Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit | DesignerCanvasCapabilities.Gridlines;
		canvas.ResizeHandlesEnabled = false;
		canvas.SetContextCommands(new[] { ("Delete", "delete") });
		canvasController.ClearsSelectionOnEmptyClick = true;
		canvasController.SelectionChanged += (_, ids) => { if (!ids.SequenceEqual(selection.SelectedIds)) pads.CommitSelection(ids); };
		canvasController.ElementPicked += (_, id) => { if (!selection.SelectedIds.Contains(id)) SelectByName(id); };
		canvasController.ElementDragCommitted += (_, drag) => CommitCanvasDrag(drag);
		canvasController.ElementGroupDragCommitted += (_, _) => canvasController.RestoreSelection(selection.SelectedIds);
		canvasController.ContextCommandRequested += (_, command) => { if (command.Command == "delete") DeleteSelected(); };
		canvasController.UndoRedoRequested += (_, undo) => { if (undo) Undo(); else Redo(); };
		canvas.AllowDrop = true;
		canvas.DragOver += (_, e) => { e.Effects = ToolboxDragData.GetTypeName(e.Data) is { } dragged && ToolNames.Contains(dragged, StringComparer.Ordinal) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
		canvas.Drop += (_, e) => {
			if (ToolboxDragData.GetTypeName(e.Data) is not { } type || !ToolNames.Contains(type, StringComparer.Ordinal)) return;
			var design = canvas.ToDesignPoint(e.GetPosition(canvas));
			var over = state.Tree == null ? null : NodeAt(state.Tree, new Point(design.X, design.Y));
			if (over != null) Select(over);
			Add(type);
			e.Handled = true;
		};
	}
	void FitView() { canvas.FitView(); FitMeasured = canvas.HasRender && canvas.IsFitMode; }
	static DesignerElementNode? NodeAt(DesignerElementNode root, Point point, DesignerElementNode? except = null) => Flatten(root).Where(n => !ReferenceEquals(n, except) && n.Width > 0 && n.Height > 0 && point.X >= n.X && point.Y >= n.Y && point.X <= n.X + n.Width && point.Y <= n.Y + n.Height).OrderBy(n => n.Width * n.Height).FirstOrDefault();
	bool ReorderBetween(DesignerElementNode root, DesignerElementNode source, DesignerElementNode target) { var parent = Flatten(root).FirstOrDefault(p => p.Children.Contains(source) && p.Children.Contains(target)); if (parent == null) return false; var delta = parent.Children.IndexOf(target) - parent.Children.IndexOf(source); if (delta == 0) return false; Select(source); return ReorderSelected(delta); }
	static bool IsContainer(DesignerElementNode n) => n.Type is "StackPanel" or "Grid" or "DockPanel" or "WrapPanel" or "Window" or "Border" or "ScrollViewer" or "GroupBox" or "TabControl" or "TabItem" or "ContentControl";
	DesignerElementNode? NearestContainer(DesignerElementNode n) { if (state.Tree == null) return null; for (var current = n; ; ) { if (IsContainer(current)) return current; var parent = Flatten(state.Tree).FirstOrDefault(p => p.Children.Contains(current)); if (parent == null) return null; current = parent; } }
	static string Value(DesignerElementNode n, string key, string fallback) => n.Properties.FirstOrDefault(p => p.Name == key)?.Value ?? fallback; static IEnumerable<DesignerElementNode> Flatten(DesignerElementNode n) => new[] { n }.Concat(n.Children.SelectMany(Flatten));
	DesignerDocumentSnapshot Snapshot(string text, long version) => new() { Version = version, PrimaryFileName = PrimaryFile?.FileName.ToString() ?? "", DesignerFileName = mxamlFile.FileName.ToString(), Files = { new DesignerSourceFileSnapshot { FileName = mxamlFile.FileName.ToString(), Kind = "MewUI", Text = text } } };
	// The split layout hands the file between the live source editor and this designer on every
	// active-view switch. Re-opening the host document for text it produced itself reset its undo
	// history (session/open zeroes UndoDepth), so only a real source change re-opens.
	protected override void LoadInternal(OpenedFile file, Stream stream) { using var reader = new StreamReader(stream, leaveOpen: true); var text = reader.ReadToEnd(); if (host != null && host.IsAlive && state.Accepted && string.Equals(text, loadedMxamlText, StringComparison.Ordinal)) return; loadedMxamlText = text; if (host == null) { host = MewUIDesignerHostClient.CreateAsync().GetAwaiter().GetResult(); host.Recovered += HostRecovered; } state = host.OpenAsync(Snapshot(loadedMxamlText, 1)).GetAwaiter().GetResult(); Rebuild();
		OutputChannel.Write("MewUI", $"Host started (PID {host.ProcessId}) for {mxamlFile.FileName}"); }
	protected override void SaveInternal(OpenedFile file, Stream stream) { // Single authoritative document: the host's canonical MXAML is the only thing we persist.
	  // The host can be down here: a restart moves focus into the source pane, and that view switch saves this view first.
		var text = host == null ? loadedMxamlText : host.CurrentTextOrRecovery(state.Version, loadedMxamlText); using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true); writer.Write(text); writer.Flush(); loadedMxamlText = text; }
	void HostRecovered(object? sender, DesignerSessionState recovered) {
		// Recovery runs on the broker's worker thread while the initiating command may be
		// synchronously waiting on the UI thread. OutputChannel is UI-affine, so writing before
		// BeginInvoke deadlocks both sides. Marshal the complete notification to the dispatcher.
		Application.Current.Dispatcher.BeginInvoke(new Action(() => {
			OutputChannel.Write("MewUI", $"Host recovered (new PID {host?.ProcessId})");
			state = recovered;
			Rebuild();
		}));
	}
	public override void Dispose() { pads.Dispose(); properties.Clear(); OutputChannel.Write("MewUI", "Designer view disposed"); if (host != null) host.Recovered -= HostRecovered; host?.Dispose(); base.Dispose(); }
}
