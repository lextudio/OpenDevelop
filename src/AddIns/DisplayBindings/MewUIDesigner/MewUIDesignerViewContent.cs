using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Windows; using System.Windows.Controls; using System.Windows.Input; using System.Windows.Media;
using ICSharpCode.SharpDevelop; using ICSharpCode.SharpDevelop.Designer.Remote; using ICSharpCode.SharpDevelop.Designer.Shell; using ICSharpCode.SharpDevelop.Gui; using ICSharpCode.SharpDevelop.WinForms; using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.SharpDevelop.Widgets;
namespace ICSharpCode.MewUIDesigner;

public sealed class MewUIDesignerViewContent : AbstractViewContentHandlingLoadErrors, IOutlineContentHost, IToolsHost, IHasPropertyContainer, IUndoHandler, IFilterableToolbox
{
	public static readonly string[] ToolNames = { "StackPanel", "Grid", "DockPanel", "WrapPanel", "Border", "ScrollViewer", "Label", "Button", "TextBox", "CheckBox", "RadioButton", "Slider", "ProgressBar", "ComboBox", "ListBox", "Image" };
	readonly Border surface = new() { Padding = new Thickness(24), Background = Brushes.DimGray }; readonly DocumentOutlineControl outline = new(); readonly ListBox toolbox = new() { DisplayMemberPath = nameof(DesignerToolboxItemInfo.DisplayName) }; readonly PropertyContainer properties = new(); readonly TextBlock diagnostic = new() { Foreground = Brushes.OrangeRed, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap }; readonly OpenedFile mxamlFile; readonly DesignerCanvas canvas = new();
	readonly DesignerToolboxController toolboxModel = new();
	readonly DesignerSelectionController selection;
	readonly DesignerPadController pads;
	readonly DesignerCommandController commands = new();
	readonly ScrollViewer scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
	readonly Dictionary<string, FrameworkElement> previewTargetsById = new();
	bool draggingFromToolbox; bool syncingToolbox; string? pressedToolboxType;
	MewUIDesignerHostClient? host; DesignerSessionState state = new(); DesignerElementNode? selected; string loadedMxamlText = "";
	double zoom = 1; bool gridlines;
	public MewUIDesignerViewContent(OpenedFile file) : base(file)
	{
		toolboxModel.SetItems(ToolNames.Select(name => new DesignerToolboxItemInfo { Name = name, DisplayName = name, TypeName = name, Category = "MewUI" }));
		toolbox.ItemsSource = toolboxModel.VisibleItems;
		toolbox.Tag = this;
		toolboxModel.ItemsChanged += (_, _) => { syncingToolbox = true; toolbox.ItemsSource = toolboxModel.VisibleItems; toolbox.SelectedItem = toolboxModel.SelectedItem; syncingToolbox = false; };
		toolboxModel.SelectionChanged += (_, _) => { syncingToolbox = true; toolbox.SelectedItem = toolboxModel.SelectedItem; syncingToolbox = false; };
		toolbox.SelectionChanged += (_, _) => { if (!syncingToolbox) toolboxModel.Select((toolbox.SelectedItem as DesignerToolboxItemInfo)?.TypeName); };
		selection = new DesignerSelectionController(node => Adapter(node), nodes => new DesignerMultiPropertyAdapter(nodes.Select(node => (object)Adapter(node))));
		commands.RegisterStandard(() => host?.IsAlive == true && state.CanUndo, () => { Mutate(() => host!.UndoAsync(state.Version).GetAwaiter().GetResult()); return true; },
			() => host?.IsAlive == true && state.CanRedo, () => { Mutate(() => host!.RedoAsync(state.Version).GetAwaiter().GetResult()); return true; },
			() => selection.SelectedIds.Count > 0 && host?.IsAlive == true, DeleteSelectedCore);
		pads = new DesignerPadController(selection, outline.SetRoots, value => properties.SelectedObject = value, outline.SelectNodeById, node => selected = node);
		mxamlFile = file; TabPageText = "Design";
		ConfigureCanvas(); ShowGridlines(false); var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.Children.Add(canvas); Grid.SetRow(diagnostic, 1); grid.Children.Add(diagnostic); UserContent = grid;
		outline.SelectionCommitted += (_, _) => pads.CommitOutlineSelection(outline.SelectedNode?.Id); toolbox.MouseDoubleClick += (_, _) => { if (toolboxModel.SelectedItem is { } item) Add(item.TypeName); }; toolbox.KeyDown += (_, e) => { if (e.Key == Key.Enter && toolboxModel.SelectedItem is { } item) { Add(item.TypeName); e.Handled = true; } };
		// Mirrors GtkDesignerViewContent's toolbox drag source - lets a real synthetic mouse
		// press/move/release (od.ui/actions) drive DragDrop.DoDragDrop end to end onto a
		// container panel in Preview(), instead of only exercising od.mewui-designer.toolbox.insert.
		// Guarded against re-entrancy: WPF only supports one active DoDragDrop session at a
		// time, so calling it again on every subsequent PreviewMouseMove while the button stays
		// down (which fires repeatedly across a real or synthetic multi-step drag) cancels the
		// prior, still-in-flight session before it ever reaches the drop target's DragOver -
		// found and fixed via the identical bug live in GtkDesignerViewContent (see its own
		// comment on the same handler for the verified symptom: debugDragOverCount staying 0).
		// Latch what was pressed rather than reading SelectedItem when the drag starts - leaving the
		// list drags across neighbouring rows and ListBox's drag-selection retargets SelectedItem to
		// each one passed over (measured on the GTK designer: pressing one row dropped its neighbour).
		toolbox.PreviewMouseDown += (_, e) => { draggingFromToolbox = false; pressedToolboxType = ToolboxTypeAt(e.GetPosition(toolbox)); };
		toolbox.PreviewMouseMove += (_, e) => {
			if (e.LeftButton != MouseButtonState.Pressed) { draggingFromToolbox = false; return; }
			var type = pressedToolboxType ?? toolboxModel.SelectedItem?.TypeName;
			if (draggingFromToolbox || type == null) return;
			draggingFromToolbox = true;
			DragDrop.DoDragDrop(toolbox, new DataObject(DataFormats.StringFormat, type), DragDropEffects.Copy);
			draggingFromToolbox = false;
		};
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => Undo(), (_, e) => e.CanExecute = commands.CanExecute(DesignerCommandNames.Undo)));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => Redo(), (_, e) => e.CanExecute = commands.CanExecute(DesignerCommandNames.Redo)));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, (_, _) => DeleteSelected(), (_, e) => e.CanExecute = commands.CanExecute(DesignerCommandNames.Delete)));
	}
	public object OutlineContent => outline; public object ToolsContent => toolbox; public ListBox ToolboxControl => toolbox; public int ZoomComboSelectedIndex => canvas.ZoomCombo.SelectedIndex; public PropertyContainer PropertyContainer => properties; public string Status => state.Accepted ? $"Ready: {ElementCount} elements (host {host?.ProcessId})" : state.Error; public string WindowClassName => state.Tree?.Name ?? ""; public int ElementCount => state.Tree == null ? 0 : Flatten(state.Tree).Count(); public bool IsDesignerDirty => mxamlFile.IsDirty; public string SelectedName => selected?.Name ?? ""; public int HostProcessId => host?.ProcessId ?? 0;
	public string? SelectedToolboxType => toolboxModel.SelectedItem?.TypeName;
	public DesignerToolboxItemInfo? SelectedToolboxItem => toolboxModel.SelectedItem;
	public bool SelectToolboxType(string type) { var ok = toolboxModel.Select(type); if (ok) toolbox.SelectedItem = toolboxModel.SelectedItem; return ok; }
	public void FilterToolbox(string text) => toolboxModel.Filter(text);
	void IFilterableToolbox.Filter(string text) => FilterToolbox(text);
	int IFilterableToolbox.VisibleItemCount => ToolboxItemCount;
	string IFilterableToolbox.FilterText => ToolboxFilterText;
	public string ToolboxFilterText => toolboxModel.FilterText;
	public FrameworkElement? FindPreviewTarget(string id) => previewTargetsById.GetValueOrDefault(id);
	string? ToolboxTypeAt(Point point)
	{
		for (var hit = toolbox.InputHitTest(point) as DependencyObject; hit != null; hit = VisualTreeHelper.GetParent(hit))
			if (hit is ListBoxItem row) return (row.DataContext as DesignerToolboxItemInfo)?.TypeName;
		return null;
	}
	public int ToolboxItemCount => toolbox.Items.Count; public bool IsToolboxHosted => ReferenceEquals((SD.Services.GetService(typeof(IToolsPadHost)) as IToolsPadHost)?.HostedContent, toolbox); public bool IsOutlineHosted => ReferenceEquals((SD.Services.GetService(typeof(IOutlinePadHost)) as IOutlinePadHost)?.HostedContent, outline); public int OutlineItemCount => ElementCount;
	public int ToolbarItemCount => canvas.VisibleToolbarItems.Count; public IReadOnlyList<string> ToolbarItems => canvas.VisibleToolbarItems; public string ToolbarCapabilities => canvas.Capabilities.ToString(); public double Zoom { get => zoom; set { zoom = Math.Clamp(value, .25, 2); surface.LayoutTransform = new ScaleTransform(zoom, zoom); } }
	public bool Gridlines => gridlines; public bool FitMeasured { get; private set; } public void FitDesign() => FitView(); public void ShowGridlines(bool show) { canvas.IsGridEnabled = show; SetGridlines(show); }
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
	void Rebuild() { diagnostic.Text = Status; pads.UpdateTree(state.Tree); canvas.ContentHost.Content ??= null; previewTargetsById.Clear(); surface.Child = state.Tree == null ? new TextBlock { Text = "No MewUI control tree found.", Foreground = Brushes.White } : Preview(state.Tree); }
	FrameworkElement Preview(DesignerElementNode n) { FrameworkElement result; if (IsContainer(n)) { var panel = new StackPanel { Background = Brushes.White, MinWidth = 480, MinHeight = n.Type == "Window" ? 320 : 40, AllowDrop = true }; foreach (var child in n.Children) panel.Children.Add(Preview(child)); panel.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }; panel.Drop += (_, e) => { if (e.Data.GetData(DataFormats.StringFormat) is string type && ToolNames.Contains(type, StringComparer.Ordinal)) { Select(n); Add(type); } e.Handled = true; }; result = panel; } else if (n.Type == "Button") result = new Button { Content = Value(n, "Content", n.Name ?? n.Id) }; else if (n.Type == "TextBox") result = new TextBox { Text = Value(n, "Text", "TextBox") }; else if (n.Type == "CheckBox") result = new CheckBox { Content = Value(n, "Content", n.Name ?? n.Id) }; else if (n.Type == "Slider") result = new Slider { Width = 180 }; else if (n.Type == "ProgressBar") result = new ProgressBar { Width = 180, Height = 18, Value = 45 }; else result = new TextBlock { Text = Value(n, "Text", Value(n, "Content", n.Name ?? n.Id)) }; result.Margin = new Thickness(5); result.PreviewMouseLeftButtonDown += (_, e) => { Select(n, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); e.Handled = true; }; previewTargetsById[n.Id] = result; return result; }
	void Select(DesignerElementNode? n, bool toggle = false) => selection.Select(n == null ? Array.Empty<DesignerElementNode>() : new[] { n }, toggle ? DesignerSelectionOperation.Toggle : DesignerSelectionOperation.Replace);
	void ConfigureCanvas() { canvas.Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit | DesignerCanvasCapabilities.Gridlines; foreach (var label in new[] { "Fit", "25%", "50%", "75%", "100%", "125%", "150%", "200%" }) canvas.ZoomCombo.Items.Add(label); canvas.ZoomCombo.SelectedIndex = 4; canvas.ZoomChanged += (_, _) => { if (canvas.ZoomCombo.SelectedIndex == 0) FitView(); else Zoom = new[] { .25, .5, .75, 1, 1.25, 1.5, 2 }[canvas.ZoomCombo.SelectedIndex - 1]; }; canvas.FitRequested += (_, _) => FitView(); canvas.GridRequested += (_, show) => SetGridlines(show); scroller.Content = surface; canvas.ContentHost.Content = scroller; }
	void FitView() { var child = surface.Child as FrameworkElement; var width = (child?.ActualWidth ?? 0) + surface.Padding.Left + surface.Padding.Right; var height = (child?.ActualHeight ?? 0) + surface.Padding.Top + surface.Padding.Bottom; FitMeasured = width > 0 && height > 0 && scroller.ViewportWidth > 0 && scroller.ViewportHeight > 0; Zoom = FitMeasured ? Math.Min(scroller.ViewportWidth / width, scroller.ViewportHeight / height) : 1; if (canvas.ZoomCombo.SelectedIndex != 0) canvas.ZoomCombo.SelectedIndex = 0; }
	void SetGridlines(bool show) { gridlines = show; surface.Background = show ? GridBrush() : Brushes.DimGray; }
	static Brush GridBrush() { var drawing = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(70, 70, 70)), new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 100)), 1), new RectangleGeometry(new Rect(0, 0, 16, 16))); var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute }; brush.Freeze(); return brush; }
	static bool IsContainer(DesignerElementNode n) => n.Type is "StackPanel" or "Grid" or "DockPanel" or "WrapPanel" or "Window" or "Border" or "ScrollViewer" or "GroupBox" or "TabControl" or "TabItem" or "ContentControl";
	DesignerElementNode? NearestContainer(DesignerElementNode n) { if (state.Tree == null) return null; for (var current = n; ; ) { if (IsContainer(current)) return current; var parent = Flatten(state.Tree).FirstOrDefault(p => p.Children.Contains(current)); if (parent == null) return null; current = parent; } }
	static string Value(DesignerElementNode n, string key, string fallback) => n.Properties.FirstOrDefault(p => p.Name == key)?.Value ?? fallback; static IEnumerable<DesignerElementNode> Flatten(DesignerElementNode n) => new[] { n }.Concat(n.Children.SelectMany(Flatten));
	DesignerDocumentSnapshot Snapshot(string text, long version) => new() { Version = version, PrimaryFileName = PrimaryFile?.FileName.ToString() ?? "", DesignerFileName = mxamlFile.FileName.ToString(), Files = { new DesignerSourceFileSnapshot { FileName = mxamlFile.FileName.ToString(), Kind = "MewUI", Text = text } } };
	protected override void LoadInternal(OpenedFile file, Stream stream) { using var reader = new StreamReader(stream, leaveOpen: true); loadedMxamlText = reader.ReadToEnd(); if (host == null) { host = MewUIDesignerHostClient.CreateAsync().GetAwaiter().GetResult(); host.Recovered += HostRecovered; } state = host.OpenAsync(Snapshot(loadedMxamlText, 1)).GetAwaiter().GetResult(); Rebuild();
		OutputChannel.Write("MewUI", $"Host started (PID {host.ProcessId}) for {mxamlFile.FileName}"); }
	protected override void SaveInternal(OpenedFile file, Stream stream) { // Single authoritative document: the host's canonical MXAML is the only thing we persist.
	  var text = host == null ? loadedMxamlText : host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text; using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true); writer.Write(text); writer.Flush(); loadedMxamlText = text; }
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
