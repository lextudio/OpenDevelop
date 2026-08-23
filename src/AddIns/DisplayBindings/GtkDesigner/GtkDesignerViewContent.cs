using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
	GtkDesignerHostClient? host; DesignerSessionState state = new(); DesignerElementNode? selected; string loadedText = "";
	double zoom = 1; bool gridlines;

	public GtkDesignerViewContent(OpenedFile file) : base(file)
	{
		TabPageText = "Design"; ConfigureCanvas(); var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.Children.Add(canvas); Grid.SetRow(diagnostic, 1); grid.Children.Add(diagnostic); UserContent = grid;
		outline.SelectedItemChanged += (_, _) => Select((outline.SelectedItem as TreeViewItem)?.Tag as DesignerElementNode);
		toolbox.MouseDoubleClick += (_, _) => { if (toolbox.SelectedItem is string type) Add(type); }; toolbox.KeyDown += (_, e) => { if (e.Key == Key.Enter && toolbox.SelectedItem is string type) { Add(type); e.Handled = true; } };
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => Undo())); grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => Redo())); grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, (_, _) => DeleteSelected()));
	}
	public object OutlineContent => outline; public object ToolsContent => toolbox; public PropertyContainer PropertyContainer => properties;
	public int ToolboxItemCount => toolbox.Items.Count; public bool IsToolboxHosted => ReferenceEquals((SD.Services.GetService(typeof(IToolsPadHost)) as IToolsPadHost)?.HostedContent, toolbox);
	public bool IsOutlineHosted => ReferenceEquals((SD.Services.GetService(typeof(IOutlinePadHost)) as IOutlinePadHost)?.HostedContent, outline); public int OutlineItemCount => ElementCount;
	public int ElementCount => state.Tree == null ? 0 : Flatten(state.Tree).Count(n => n.Id != "$interface"); public string SelectedId => selected?.Id ?? ""; public int HostProcessId => host?.ProcessId ?? 0;
	public int ToolbarItemCount => canvas.VisibleToolbarItems.Count; public IReadOnlyList<string> ToolbarItems => canvas.VisibleToolbarItems; public string ToolbarCapabilities => canvas.Capabilities.ToString(); public double Zoom { get => zoom; set { zoom = Math.Clamp(value, .25, 2); surface.LayoutTransform = new ScaleTransform(zoom, zoom); } }
	public bool Gridlines => gridlines; public void FitDesign() => FitView(); public void ShowGridlines(bool show) { canvas.IsGridEnabled = show; SetGridlines(show); }
	public string Status => state.Accepted ? $"Ready: {ElementCount} GTK objects (host {host?.ProcessId})" : state.Error;
	public bool EnableUndo => host?.IsAlive == true; public bool EnableRedo => host?.IsAlive == true;
	public void Undo() => Mutate(() => host!.UndoAsync(state.Version).GetAwaiter().GetResult()); public void Redo() => Mutate(() => host!.RedoAsync(state.Version).GetAwaiter().GetResult());
	public bool SelectById(string id) { var node = state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => n.Id == id); if (node == null) return false; Select(node); return true; }
	public bool SetSelectedProperty(string name, string value) { if (selected == null || host == null) return false; var old = selected.Id; Mutate(() => host.SetPropertyAsync(state.Version, old, name, value).GetAwaiter().GetResult()); return SelectById(name == "$id" ? value : old); }
	public bool Add(string type) { if (host == null || state.Tree == null) return false; var parent = selected != null && IsContainer(selected) ? selected : Flatten(state.Tree).FirstOrDefault(IsContainer); if (parent == null) return false; var before = Flatten(state.Tree).Select(n => n.Id).ToHashSet(StringComparer.Ordinal); Mutate(() => host.AddElementAsync(state.Version, parent.Id, new DesignerToolboxItemInfo { Name = type, TypeName = type }, "", 0, 0).GetAwaiter().GetResult()); var added = state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => !before.Contains(n.Id)); if (added != null) Select(added); return added != null; }
	public bool DeleteSelected() { if (selected == null || host == null) return false; Mutate(() => host.DeleteElementsAsync(state.Version, new[] { selected.Id }).GetAwaiter().GetResult()); return true; }
	public void RefreshDesign() { if (host == null) return; var text = host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text; state = host.UpdateAsync(Snapshot(text, state.Version + 1)).GetAwaiter().GetResult(); loadedText = text; Rebuild(); }
	public void RestartDesignHost() { var text = host == null ? loadedText : host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text; host?.Dispose(); host = GtkDesignerHostClient.CreateAsync().GetAwaiter().GetResult(); state = host.OpenAsync(Snapshot(text, state.Version + 1)).GetAwaiter().GetResult(); loadedText = text; Rebuild(); }
	public void ShowSource() { var window = WorkbenchWindow; if (window == null) return; for (var i = 0; i < window.ViewContents.Count; i++) if (!ReferenceEquals(window.ViewContents[i], this)) { window.SwitchView(i); return; } }
	void Mutate(Func<DesignerSessionState> action) { state = action(); PrimaryFile?.MakeDirty(); Rebuild(); }
	void Rebuild() { var selectedId = selected?.Id; diagnostic.Text = Status; outline.Items.Clear(); if (state.Tree != null) foreach (var root in state.Tree.Id == "$interface" ? (IEnumerable<DesignerElementNode>)state.Tree.Children : new[] { state.Tree }) outline.Items.Add(Tree(root)); surface.Child = state.Tree == null ? new TextBlock { Text = "No GTK 4 object tree found.", Foreground = Brushes.White } : Preview(state.Tree.Id == "$interface" ? state.Tree.Children.FirstOrDefault() : state.Tree); selected = null; properties.SelectedObject = null; if (selectedId != null) SelectById(selectedId); }
	TreeViewItem Tree(DesignerElementNode node) { var item = new TreeViewItem { Header = $"{node.Name}  ({node.Type})", Tag = node, IsExpanded = true }; foreach (var child in node.Children) item.Items.Add(Tree(child)); return item; }
	FrameworkElement Preview(DesignerElementNode? node) { if (node == null) return new TextBlock { Text = "Empty GTK interface" }; FrameworkElement result; if (IsContainer(node)) { var panel = new StackPanel { Background = Brushes.White, MinWidth = 480, MinHeight = 48, Orientation = Value(node, "orientation", "vertical") == "horizontal" ? Orientation.Horizontal : Orientation.Vertical }; foreach (var child in node.Children) panel.Children.Add(Preview(child)); result = panel; } else if (node.Type == "GtkButton") result = new Button { Content = Value(node, "label", node.Id) }; else if (node.Type is "GtkEntry" or "GtkPasswordEntry") result = new TextBox { Text = Value(node, "text", ""), MinWidth = 160 }; else if (node.Type == "GtkCheckButton") result = new CheckBox { Content = Value(node, "label", node.Id) }; else if (node.Type == "GtkProgressBar") result = new ProgressBar { Value = 45, Width = 180, Height = 18 }; else result = new TextBlock { Text = Value(node, "label", node.Id) }; result.Margin = new Thickness(5); result.PreviewMouseLeftButtonDown += (_, e) => { Select(node); e.Handled = true; }; return result; }
	void Select(DesignerElementNode? node) { selected = node; properties.SelectedObject = node == null ? null : new GtkPropertyAdapter(node, (name, value) => SetSelectedProperty(name, value)); }
	void ConfigureCanvas() { canvas.Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit | DesignerCanvasCapabilities.Gridlines; foreach (var label in new[] { "Fit", "25%", "50%", "75%", "100%", "125%", "150%", "200%" }) canvas.ZoomCombo.Items.Add(label); canvas.ZoomCombo.SelectedIndex = 4; canvas.ZoomChanged += (_, _) => { if (canvas.ZoomCombo.SelectedIndex == 0) FitView(); else Zoom = new[] { .25, .5, .75, 1, 1.25, 1.5, 2 }[canvas.ZoomCombo.SelectedIndex - 1]; }; canvas.FitRequested += (_, _) => FitView(); canvas.GridRequested += (_, show) => SetGridlines(show); canvas.ContentHost.Content = new ScrollViewer { Content = surface, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; }
	void FitView() => Zoom = 1;
	void SetGridlines(bool show) { gridlines = show; surface.Background = show ? GridBrush() : Brushes.DimGray; }
	static Brush GridBrush() { var drawing = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(70, 70, 70)), new Pen(new SolidColorBrush(Color.FromRgb(100, 100, 100)), 1), new RectangleGeometry(new Rect(0, 0, 16, 16))); var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute }; brush.Freeze(); return brush; }
	static bool IsContainer(DesignerElementNode n) => n.Type is "GtkBox" or "GtkGrid" or "GtkCenterBox" or "GtkPaned" or "GtkScrolledWindow" or "GtkApplicationWindow" or "GtkWindow";
	static string Value(DesignerElementNode n, string key, string fallback) => n.Properties.FirstOrDefault(p => p.Name == key)?.Value ?? fallback; static IEnumerable<DesignerElementNode> Flatten(DesignerElementNode n) => new[] { n }.Concat(n.Children.SelectMany(Flatten));
	DesignerDocumentSnapshot Snapshot(string text, long version) => new() { Version = version, PrimaryFileName = PrimaryFile?.FileName.ToString() ?? "", Files = { new DesignerSourceFileSnapshot { FileName = PrimaryFile?.FileName.ToString() ?? "", Kind = "Designer", Text = text } } };
	protected override void LoadInternal(OpenedFile file, Stream stream) { using var reader = new StreamReader(stream, leaveOpen: true); loadedText = reader.ReadToEnd(); host ??= GtkDesignerHostClient.CreateAsync().GetAwaiter().GetResult(); state = host.OpenAsync(Snapshot(loadedText, 1)).GetAwaiter().GetResult(); Rebuild(); }
	protected override void SaveInternal(OpenedFile file, Stream stream) { var text = host == null ? loadedText : host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text; using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true); writer.Write(text); writer.Flush(); loadedText = text; }
	public override void Dispose() { properties.Clear(); host?.Dispose(); base.Dispose(); }
}
