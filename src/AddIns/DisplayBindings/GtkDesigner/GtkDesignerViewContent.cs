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
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.WinForms;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.SharpDevelop.Widgets;

namespace ICSharpCode.GtkDesigner;

public sealed class GtkDesignerViewContent : AbstractViewContentHandlingLoadErrors, IOutlineContentHost, IToolsHost, IHasPropertyContainer, IUndoHandler
{
	public static readonly string[] ToolNames = { "GtkBox", "GtkGrid", "GtkCenterBox", "GtkPaned", "GtkScrolledWindow", "GtkLabel", "GtkButton", "GtkEntry", "GtkPasswordEntry", "GtkCheckButton", "GtkSwitch", "GtkSpinButton", "GtkDropDown", "GtkListBox", "GtkListView", "GtkGridView", "GtkImage", "GtkPicture", "GtkProgressBar", "GtkSeparator" };
	readonly TreeView outline = new(); readonly ListBox toolbox = new() { ItemsSource = ToolNames }; readonly PropertyContainer properties = new();
	readonly Border surface = new() { Padding = new Thickness(24), Background = Brushes.DimGray };
	readonly TextBlock diagnostic = new() { Foreground = Brushes.OrangeRed, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
	readonly DesignerCanvas canvas = new();
	readonly Dictionary<string, FrameworkElement> nativeTargetsById = new();
	bool draggingFromToolbox; string? pressedToolboxType;
	readonly ScrollViewer scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
	GtkDesignerHostClient? host; DesignerSessionState state = new(); DesignerElementNode? selected; string preferredSelectionId = ""; string loadedText = "";
	CancellationTokenSource? renderCancellation; long requestedRenderRevision; long renderedRevision;
	double zoom = 1; bool gridlines;

	public GtkDesignerViewContent(OpenedFile file) : base(file)
	{
		TabPageText = "Design"; ConfigureCanvas(); var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.Children.Add(canvas); Grid.SetRow(diagnostic, 1); grid.Children.Add(diagnostic); UserContent = grid;
		outline.SelectedItemChanged += (_, _) => Select((outline.SelectedItem as TreeViewItem)?.Tag as DesignerElementNode);
		toolbox.MouseDoubleClick += (_, _) => { if (toolbox.SelectedItem is string type) Add(type); }; toolbox.KeyDown += (_, e) => { if (e.Key == Key.Enter && toolbox.SelectedItem is string type) { Add(type); e.Handled = true; } };
		// Latch what was pressed, rather than reading toolbox.SelectedItem when the drag actually
		// starts: leaving the list drags the pointer across neighbouring rows, and ListBox's own
		// drag-selection retargets SelectedItem to each one it passes over. Measured: pressing
		// GtkSwitch and dragging up to the canvas dropped a GtkCheckButton (the row above) instead.
		toolbox.PreviewMouseDown += (_, e) => { draggingFromToolbox = false; pressedToolboxType = ToolboxTypeAt(e.GetPosition(toolbox)); };
		// Guard against re-entrancy: WPF only supports one active DoDragDrop session at a time,
		// so calling it again on every subsequent PreviewMouseMove while the button stays down
		// (which fires repeatedly for a real or synthetic multi-step drag) would cancel the prior,
		// still-in-flight session before it reaches the drop target.
		toolbox.PreviewMouseMove += (_, e) => {
			if (e.LeftButton != MouseButtonState.Pressed) { draggingFromToolbox = false; return; }
			var type = pressedToolboxType ?? toolbox.SelectedItem as string;
			if (draggingFromToolbox || type == null) return;
			draggingFromToolbox = true;
			DragDrop.DoDragDrop(toolbox, new DataObject(DataFormats.StringFormat, type), DragDropEffects.Copy);
			draggingFromToolbox = false;
		};
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => Undo())); grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => Redo())); grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, (_, _) => DeleteSelected()));
	}
	public object OutlineContent => outline; public object ToolsContent => toolbox; public ListBox ToolboxControl => toolbox; public int ZoomComboSelectedIndex => canvas.ZoomCombo.SelectedIndex; public PropertyContainer PropertyContainer => properties;
	public FrameworkElement? FindNativeTarget(string id) => nativeTargetsById.GetValueOrDefault(id);
	string? ToolboxTypeAt(Point point)
	{
		for (var hit = toolbox.InputHitTest(point) as DependencyObject; hit != null; hit = VisualTreeHelper.GetParent(hit))
			if (hit is ListBoxItem row) return row.DataContext as string;
		return null;
	}
	public int ToolboxItemCount => toolbox.Items.Count; public bool IsToolboxHosted => ReferenceEquals((SD.Services.GetService(typeof(IToolsPadHost)) as IToolsPadHost)?.HostedContent, toolbox);
	public bool IsOutlineHosted => ReferenceEquals((SD.Services.GetService(typeof(IOutlinePadHost)) as IOutlinePadHost)?.HostedContent, outline); public int OutlineItemCount => ElementCount;
	public int ElementCount => state.Tree == null ? 0 : Flatten(state.Tree).Count(n => n.Id != "$interface"); public string SelectedId => selected?.Id ?? ""; public int HostProcessId => host?.ProcessId ?? 0;
	public string RootId => state.Tree?.Id == "$interface" ? state.Tree.Children.FirstOrDefault()?.Id ?? "" : state.Tree?.Id ?? "";
	public int ToolbarItemCount => canvas.VisibleToolbarItems.Count; public IReadOnlyList<string> ToolbarItems => canvas.VisibleToolbarItems; public string ToolbarCapabilities => canvas.Capabilities.ToString(); public double Zoom { get => zoom; set { zoom = Math.Clamp(value, .25, 2); surface.LayoutTransform = new ScaleTransform(zoom, zoom); } }
	public bool Gridlines => gridlines; public bool FitMeasured { get; private set; } public void FitDesign() => FitView(); public void ShowGridlines(bool show) { canvas.IsGridEnabled = show; SetGridlines(show); }
	public bool HasNativeFrame => !string.IsNullOrEmpty(state.Render?.PngBase64); public int NativeFrameWidth => state.Render?.Width ?? 0; public int NativeFrameHeight => state.Render?.Height ?? 0; public int NativeBoundsCount => state.Tree == null ? 0 : Flatten(state.Tree).Count(n => n.Width > 0 && n.Height > 0);
	public string NativeFrameFingerprint => HasNativeFrame ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(state.Render!.PngBase64))) : "";
	public string[] Diagnostics => state.Diagnostics.Select(d => d.Message).ToArray();
	public string HostLog => host?.ChildLog ?? "";
	public string HostSessionId => host?.SessionId ?? ""; public string HostDocumentId => host?.DocumentId ?? ""; public string HostPoolKey => host?.PoolKey ?? "gtk4"; public int ActiveHostLeases => GtkDesignerHostClient.ActiveLeaseCount; public int HostRecoveryCount => host?.RecoveryCount ?? 0;
	public long RequestedRenderRevision => requestedRenderRevision; public long RenderedRevision => renderedRevision; public bool IsRenderPending => requestedRenderRevision > renderedRevision;
	public string Status => state.Accepted ? $"Ready: {ElementCount} GTK objects (host {host?.ProcessId}, native frame {(HasNativeFrame ? $"{NativeFrameWidth}x{NativeFrameHeight}" : "unavailable")})" : state.Error;
	public bool EnableUndo => host?.IsAlive == true; public bool EnableRedo => host?.IsAlive == true;
	public void Undo() => Mutate(() => host!.UndoAsync(state.Version).GetAwaiter().GetResult()); public void Redo() => Mutate(() => host!.RedoAsync(state.Version).GetAwaiter().GetResult());
	public bool SelectById(string id) { var node = state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => n.Id == id); if (node == null) return false; Select(node); return true; }
	public DesignerElementNode? FindById(string id) => state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => n.Id == id);
	public bool HitTest(double x, double y) { if (host == null) return false; var result = host.HitTestAsync(state.Version, x, y).GetAwaiter().GetResult(); return result.Hit && SelectById(result.ComponentName); }
	public bool SetSelectedProperty(string name, string value) { if (selected == null || host == null) return false; var old = selected.Id; Mutate(() => host.SetPropertyAsync(state.Version, old, name, value).GetAwaiter().GetResult()); return SelectById(name == "$id" ? value : old); }
	public bool Add(string type) { if (host == null || state.Tree == null) return false; var parent = selected == null ? Flatten(state.Tree).FirstOrDefault(IsContainer) : NearestContainer(selected); if (parent == null) return false; var before = Flatten(state.Tree).Select(n => n.Id).ToHashSet(StringComparer.Ordinal); Mutate(() => host.AddElementAsync(state.Version, parent.Id, new DesignerToolboxItemInfo { Name = type, TypeName = type }, "", 0, 0).GetAwaiter().GetResult()); var added = state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => !before.Contains(n.Id)); if (added != null) Select(added); return added != null; }
	public bool DeleteSelected() { if (selected == null || host == null) return false; Mutate(() => host.DeleteElementsAsync(state.Version, new[] { selected.Id }).GetAwaiter().GetResult()); return true; }
	public bool SetSelectedSignal(string signal, string handler) { if (selected == null || host == null) return false; var id = selected.Id; Mutate(() => host.SetEventAsync(state.Version, id, signal, handler).GetAwaiter().GetResult()); return SelectById(id); }
	public bool ReorderSelected(int delta) { if (selected == null || host == null) return false; var id = selected.Id; Mutate(() => host.ReorderAsync(state.Version, id, delta).GetAwaiter().GetResult()); return SelectById(id); }
	public bool PointerReorder(string sourceId, string targetId) { if (state.Tree == null) return false; var source = FindById(sourceId); var target = FindById(targetId); return source != null && target != null && ReorderBetween(state.Tree, source, target); }
	public void RefreshDesign() { if (host == null) return; var text = host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text; state = host.UpdateAsync(Snapshot(text, state.Version + 1)).GetAwaiter().GetResult(); loadedText = text; Rebuild(); }
	public void RestartDesignHost() { if (host == null) return; state = host.RestartPoolAsync().GetAwaiter().GetResult(); loadedText = host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text; Rebuild(); }
	public void TerminateDesignHost() { if (host == null) return; state = host.TerminateAndRecoverAsync().GetAwaiter().GetResult(); requestedRenderRevision = renderedRevision = state.Render?.Sequence ?? 0; Rebuild(); }
	public void ShowSource() { var window = WorkbenchWindow; if (window == null) return; for (var i = 0; i < window.ViewContents.Count; i++) if (!ReferenceEquals(window.ViewContents[i], this)) { window.SwitchView(i); return; } }
	void Mutate(Func<DesignerSessionState> action) { state = action(); PrimaryFile?.MakeDirty(); Rebuild(); QueueRender(); }
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
	void Rebuild() { var selectedId = selected?.Id ?? preferredSelectionId; diagnostic.Text = Status; outline.Items.Clear(); nativeTargetsById.Clear(); if (state.Tree != null) foreach (var outlineRoot in state.Tree.Id == "$interface" ? (IEnumerable<DesignerElementNode>)state.Tree.Children : new[] { state.Tree }) outline.Items.Add(Tree(outlineRoot)); var previewRoot = state.Tree?.Id == "$interface" ? state.Tree.Children.FirstOrDefault() : state.Tree; surface.Child = previewRoot == null ? new TextBlock { Text = "No GTK 4 object tree found.", Foreground = Brushes.White } : NativePreview(previewRoot); selected = null; properties.SelectedObject = null; if (!string.IsNullOrEmpty(selectedId)) SelectById(selectedId); }
	FrameworkElement NativePreview(DesignerElementNode root)
	{
		if (!HasNativeFrame) return Preview(root);
		var bytes = Convert.FromBase64String(state.Render!.PngBase64); var bitmap = new BitmapImage();
		using (var stream = new MemoryStream(bytes)) { bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); }
		var image = new Image { Source = bitmap, Stretch = Stretch.None };
		var hits = new Canvas { Background = Brushes.Transparent, AllowDrop = true };
		DesignerElementNode? dragged = null; Point dragStart = default;
		var insertion = new Border { Height = 3, Background = Brushes.DodgerBlue, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
		foreach (var node in Flatten(root).Where(n => n.Width > 0 && n.Height > 0).OrderByDescending(n => n.Width * n.Height)) {
			var target = new Border { Width = node.Width, Height = node.Height, Background = Brushes.Transparent, Tag = node };
			Canvas.SetLeft(target, node.X); Canvas.SetTop(target, node.Y);
			nativeTargetsById[node.Id] = target;
			target.PreviewMouseLeftButtonDown += (_, e) => { dragged = node; dragStart = e.GetPosition(hits); Select(node); target.CaptureMouse(); e.Handled = true; };
			target.PreviewMouseMove += (_, e) => { if (dragged == null || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(hits) - dragStart).Length < 4) return; var over = NativeNodeAt(root, e.GetPosition(hits)); if (over == null || ReferenceEquals(over, dragged)) return; insertion.Width = over.Width; Canvas.SetLeft(insertion, over.X); Canvas.SetTop(insertion, over.Y); insertion.Visibility = Visibility.Visible; };
			target.PreviewMouseLeftButtonUp += (_, e) => { target.ReleaseMouseCapture(); insertion.Visibility = Visibility.Collapsed; var source = dragged; dragged = null; var over = NativeNodeAt(root, e.GetPosition(hits)); if (source != null && over != null && !ReferenceEquals(source, over)) ReorderBetween(root, source, over); e.Handled = true; };
			hits.Children.Add(target);
		}
		hits.Children.Add(insertion);
		hits.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
		hits.Drop += (_, e) => { if (e.Data.GetData(DataFormats.StringFormat) is not string type || !ToolNames.Contains(type, StringComparer.Ordinal)) return; var over = NativeNodeAt(root, e.GetPosition(hits)); if (over != null) Select(over); Add(type); e.Handled = true; };
		var result = new Grid { Width = state.Render.Width, Height = state.Render.Height, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top }; result.Children.Add(image); result.Children.Add(hits); return result;
	}
	TreeViewItem Tree(DesignerElementNode node) { var item = new TreeViewItem { Header = $"{node.Name}  ({node.Type})", Tag = node, IsExpanded = true }; foreach (var child in node.Children) item.Items.Add(Tree(child)); return item; }
	FrameworkElement Preview(DesignerElementNode? node) { if (node == null) return new TextBlock { Text = "Empty GTK interface" }; FrameworkElement result; if (IsContainer(node)) { var panel = new StackPanel { Background = Brushes.White, MinWidth = 480, MinHeight = 48, Orientation = Value(node, "orientation", "vertical") == "horizontal" ? Orientation.Horizontal : Orientation.Vertical }; foreach (var child in node.Children) panel.Children.Add(Preview(child)); result = panel; } else if (node.Type == "GtkButton") result = new Button { Content = Value(node, "label", node.Id) }; else if (node.Type is "GtkEntry" or "GtkPasswordEntry") result = new TextBox { Text = Value(node, "text", ""), MinWidth = 160 }; else if (node.Type == "GtkCheckButton") result = new CheckBox { Content = Value(node, "label", node.Id) }; else if (node.Type == "GtkProgressBar") result = new ProgressBar { Value = 45, Width = 180, Height = 18 }; else result = new TextBlock { Text = Value(node, "label", node.Id) }; result.Margin = new Thickness(5); result.PreviewMouseLeftButtonDown += (_, e) => { Select(node); e.Handled = true; }; return result; }
	void Select(DesignerElementNode? node) { selected = node; if (node != null) preferredSelectionId = node.Id; properties.SelectedObject = node == null ? null : new GtkPropertyAdapter(node, (name, value) => SetSelectedProperty(name, value)); }
	void ConfigureCanvas() { canvas.Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit | DesignerCanvasCapabilities.Gridlines; foreach (var label in new[] { "Fit", "25%", "50%", "75%", "100%", "125%", "150%", "200%" }) canvas.ZoomCombo.Items.Add(label); canvas.ZoomCombo.SelectedIndex = 4; canvas.ZoomChanged += (_, _) => { if (canvas.ZoomCombo.SelectedIndex == 0) FitView(); else Zoom = new[] { .25, .5, .75, 1, 1.25, 1.5, 2 }[canvas.ZoomCombo.SelectedIndex - 1]; }; canvas.FitRequested += (_, _) => FitView(); canvas.GridRequested += (_, show) => SetGridlines(show); scroller.Content = surface; canvas.ContentHost.Content = scroller; }
	void FitView() { var child = surface.Child as FrameworkElement; var width = (child?.ActualWidth ?? 0) + surface.Padding.Left + surface.Padding.Right; var height = (child?.ActualHeight ?? 0) + surface.Padding.Top + surface.Padding.Bottom; FitMeasured = width > 0 && height > 0 && scroller.ViewportWidth > 0 && scroller.ViewportHeight > 0; Zoom = FitMeasured ? Math.Min(scroller.ViewportWidth / width, scroller.ViewportHeight / height) : 1; if (canvas.ZoomCombo.SelectedIndex != 0) canvas.ZoomCombo.SelectedIndex = 0; }
	void SetGridlines(bool show) { gridlines = show; surface.Background = show ? GridBrush() : Brushes.DimGray; }
	static Brush GridBrush() { var drawing = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(70, 70, 70)), new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 100)), 1), new RectangleGeometry(new Rect(0, 0, 16, 16))); var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute }; brush.Freeze(); return brush; }
	static bool IsContainer(DesignerElementNode n) => n.Type is "GtkBox" or "GtkGrid" or "GtkCenterBox" or "GtkPaned" or "GtkScrolledWindow" or "GtkApplicationWindow" or "GtkWindow";
	DesignerElementNode? NearestContainer(DesignerElementNode node) { if (state.Tree == null) return null; for (var current = node; ; ) { if (IsContainer(current)) return current; var parent = Flatten(state.Tree).FirstOrDefault(p => p.Children.Contains(current)); if (parent == null) return null; current = parent; } }
	static DesignerElementNode? NativeNodeAt(DesignerElementNode root, Point point) => Flatten(root).Where(n => n.Width > 0 && n.Height > 0 && point.X >= n.X && point.Y >= n.Y && point.X <= n.X + n.Width && point.Y <= n.Y + n.Height).OrderBy(n => n.Width * n.Height).FirstOrDefault();
	bool ReorderBetween(DesignerElementNode root, DesignerElementNode source, DesignerElementNode target) { var parent = Flatten(root).FirstOrDefault(p => p.Children.Contains(source) && p.Children.Contains(target)); if (parent == null) return false; var delta = parent.Children.IndexOf(target) - parent.Children.IndexOf(source); if (delta == 0) return false; Select(source); return ReorderSelected(delta); }
	static string Value(DesignerElementNode n, string key, string fallback) => n.Properties.FirstOrDefault(p => p.Name == key)?.Value ?? fallback; static IEnumerable<DesignerElementNode> Flatten(DesignerElementNode n) => new[] { n }.Concat(n.Children.SelectMany(Flatten));
	DesignerDocumentSnapshot Snapshot(string text, long version) => new() { Version = version, PrimaryFileName = PrimaryFile?.FileName.ToString() ?? "", Files = { new DesignerSourceFileSnapshot { FileName = PrimaryFile?.FileName.ToString() ?? "", Kind = "Designer", Text = text } } };
	protected override void LoadInternal(OpenedFile file, Stream stream) { using var reader = new StreamReader(stream, leaveOpen: true); loadedText = reader.ReadToEnd(); if (host == null) { host = GtkDesignerHostClient.CreateAsync().GetAwaiter().GetResult(); host.Recovered += HostRecovered; } state = host.OpenAsync(Snapshot(loadedText, 1)).GetAwaiter().GetResult(); requestedRenderRevision = renderedRevision = state.Render?.Sequence ?? 0; Rebuild(); }
	protected override void SaveInternal(OpenedFile file, Stream stream) { var text = host == null ? loadedText : host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text; using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true); writer.Write(text); writer.Flush(); loadedText = text; }
	void HostRecovered(object? sender, DesignerSessionState recovered) { Application.Current.Dispatcher.BeginInvoke(new Action(() => { state = recovered; requestedRenderRevision = renderedRevision = recovered.Render?.Sequence ?? 0; Rebuild(); })); }
	public override void Dispose() { renderCancellation?.Cancel(); renderCancellation?.Dispose(); properties.Clear(); if (host != null) host.Recovered -= HostRecovered; host?.Dispose(); base.Dispose(); }
}
