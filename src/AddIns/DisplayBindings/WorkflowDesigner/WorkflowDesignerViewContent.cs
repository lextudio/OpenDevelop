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
using ICSharpCode.SharpDevelop.Designer.Shell;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.WorkflowDesigner;

/// <summary>Renders a loaded CoreWF activity tree as nested boxes on a WPF canvas - the
/// from-scratch design surface doc/technotes/workflow-designer.md settled on once it became
/// clear System.Activities.Design (the classic rehosted designer) has no path to .NET 10/
/// cross-platform. Modeled on MewUIDesignerViewContent's shape (load/select/edit/save against
/// an out-of-process host, DesignerSelectionController driving the Properties/Outline pads),
/// with drag-drop/undo/redo left for a later pass - see the technote's phased plan, step 3.</summary>
public sealed class WorkflowDesignerViewContent : AbstractViewContentHandlingLoadErrors, IOutlineContentHost, IToolsHost, IHasPropertyContainer
{
	public static readonly string[] ToolNames = { "Sequence", "If", "WriteLine", "Assign", "Delay", "Parallel", "While" };

	readonly ScrollViewer scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
	readonly StackPanel surface = new() { Margin = new Thickness(24) };
	readonly DesignerCanvas canvas = new();
	readonly DocumentOutlineControl outline = new();
	readonly ListBox toolbox = new() { DisplayMemberPath = nameof(DesignerToolboxItemInfo.DisplayName) };
	readonly PropertyContainer properties = new();
	readonly TextBlock diagnostic = new() { Foreground = Brushes.OrangeRed, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
	readonly OpenedFile xamlFile;
	readonly DesignerToolboxController toolboxModel = new();
	readonly DesignerSelectionController selection;
	readonly DesignerPadController pads;
	readonly DesignerCommandController commands = new();
	readonly Dictionary<string, Border> boxesById = new();

	WorkflowDesignerHostClient? host;
	DesignerSessionState state = new();
	DesignerElementNode? selected;
	string loadedXamlText = "";
	double zoom = 1;

	public WorkflowDesignerViewContent(OpenedFile file) : base(file)
	{
		toolboxModel.SetItems(ToolNames.Select(name => new DesignerToolboxItemInfo { Name = name, DisplayName = name, TypeName = name, Category = "Workflow" }));
		toolbox.ItemsSource = toolboxModel.VisibleItems;
		toolbox.SelectionChanged += (_, _) => toolboxModel.Select((toolbox.SelectedItem as DesignerToolboxItemInfo)?.TypeName);
		toolbox.MouseDoubleClick += (_, _) => { if (toolboxModel.SelectedItem is { } item) Add(item.TypeName); };

		// Ordered multi-selection + shared pad/command bridge (designer-common.md's 2026-08-24
		// "push for more code reuse" pass) - matches MewUI/GTK4 wiring instead of the hand-rolled
		// TreeChanged/SelectionChanged subscriptions an earlier draft of this file used.
		selection = new DesignerSelectionController(node => Adapter(node), nodes => new DesignerMultiPropertyAdapter(nodes.Select(node => (object)Adapter(node))));
		commands.Register(DesignerCommandNames.Delete, () => selection.SelectedIds.Count > 0 && host?.IsAlive == true, DeleteSelectedCore);
		pads = new DesignerPadController(selection, outline.SetRoots, value => properties.SelectedObject = value, outline.SelectNodeById, node => { selected = node; HighlightSelection(); });
		outline.SelectionCommitted += (_, _) => pads.CommitOutlineSelection(outline.SelectedNode?.Id);

		xamlFile = file;
		TabPageText = "Design";
		ConfigureCanvas();
		var grid = new Grid();
		grid.RowDefinitions.Add(new RowDefinition());
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.Children.Add(canvas);
		Grid.SetRow(diagnostic, 1);
		grid.Children.Add(diagnostic);
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, (_, _) => DeleteSelected()));
		UserContent = grid;
	}

	/// <summary>Wires up the shared zoom/fit toolbar every other designer backend uses
	/// (designer-common.md) - the closest in-repo equivalent to VS's shell bar
	/// (workflow-designer-shell-features.md), rather than a bespoke workflow-only chrome.
	/// Gridlines/Theme/ShowNames/DesignSize don't map to an activity tree, so only Zoom+Fit
	/// are enabled.</summary>
	void ConfigureCanvas()
	{
		canvas.Capabilities = DesignerCanvasCapabilities.Zoom | DesignerCanvasCapabilities.Fit;
		foreach (var label in new[] { "Fit", "50%", "75%", "100%", "125%", "150%", "200%" }) canvas.ZoomCombo.Items.Add(label);
		canvas.ZoomCombo.SelectedIndex = 3;
		canvas.ZoomChanged += (_, _) => { if (canvas.ZoomCombo.SelectedIndex > 0) Zoom = new[] { .5, .75, 1, 1.25, 1.5, 2 }[canvas.ZoomCombo.SelectedIndex - 1]; };
		canvas.FitRequested += (_, _) => FitView();
		scroller.Content = surface;
		canvas.ContentHost.Content = scroller;
	}

	public double Zoom { get => zoom; set { zoom = Math.Clamp(value, .25, 3); surface.LayoutTransform = new ScaleTransform(zoom, zoom); } }

	void FitView()
	{
		var width = surface.ActualWidth;
		var height = surface.ActualHeight;
		if (width > 0 && height > 0 && scroller.ViewportWidth > 0 && scroller.ViewportHeight > 0)
			Zoom = Math.Min(scroller.ViewportWidth / width, scroller.ViewportHeight / height);
		if (canvas.ZoomCombo.SelectedIndex != 0) canvas.ZoomCombo.SelectedIndex = 0;
	}

	public object OutlineContent => outline;
	public object ToolsContent => toolbox;
	public PropertyContainer PropertyContainer => properties;
	public string Status => state.Accepted ? $"Ready: {ElementCount} activities (host {host?.ProcessId})" : state.Error;
	public int ElementCount => state.Tree == null ? 0 : Flatten(state.Tree).Count();
	public int HostProcessId => host?.ProcessId ?? 0;

	public bool Add(string type)
	{
		if (host == null || state.Tree == null) return false;
		var parentId = selected?.Id ?? state.Tree.Id;
		var before = Flatten(state.Tree).Select(n => n.Id).ToHashSet();
		Mutate(() => host.AddElementAsync(state.Version, parentId, new DesignerToolboxItemInfo { Name = type, TypeName = type }, "", 0, 0).GetAwaiter().GetResult());
		var added = state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => !before.Contains(n.Id));
		if (added != null) selection.Select(added);
		return added != null;
	}

	WorkflowPropertyAdapter Adapter(DesignerElementNode node) => new(node, (name, value) => SetProperty(node.Id, name, value));

	public bool SetSelectedProperty(string name, string value) => selected != null && SetProperty(selected.Id, name, value);

	bool SetProperty(string id, string name, string value)
	{
		if (host == null) return false;
		Mutate(() => host.SetPropertyAsync(state.Version, id, name, value).GetAwaiter().GetResult());
		return selection.Find(id) != null;
	}

	public bool DeleteSelected() => commands.Execute(DesignerCommandNames.Delete);

	bool DeleteSelectedCore()
	{
		if (selection.SelectedIds.Count == 0 || host == null) return false;
		var ids = selection.SelectedIds.ToArray();
		Mutate(() => host.DeleteElementsAsync(state.Version, ids).GetAwaiter().GetResult());
		return true;
	}

	void Mutate(Func<DesignerSessionState> action)
	{
		state = action();
		xamlFile.MakeDirty();
		Rebuild();
		commands.Invalidate();
	}

	void Rebuild()
	{
		diagnostic.Text = Status;
		pads.UpdateTree(state.Tree);
		boxesById.Clear();
		surface.Children.Clear();
		if (state.Tree != null) surface.Children.Add(Box(state.Tree));
	}

	/// <summary>Activity types with a child-activity collection (Sequence.Activities and
	/// friends) - the ones whose empty body should show VS's "Drop activity here" hint text
	/// rather than rendering as blank (how-to-add-activities-to-the-toolbox.md's toolbox
	/// convention). Leaf activities (WriteLine, Delay, ...) never show it.</summary>
	static bool IsContainer(string type) => type is "Sequence" or "Parallel" or "While" or "DoWhile" or "Flowchart";

	Border Box(DesignerElementNode node)
	{
		var stack = new StackPanel();
		stack.Children.Add(HeaderLabel(node));
		if (node.Children.Count == 0 && IsContainer(node.Type))
			stack.Children.Add(new TextBlock { Text = "Drop activity here", FontStyle = FontStyles.Italic, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) });
		foreach (var child in node.Children) stack.Children.Add(Box(child));
		var border = new Border {
			BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4),
			Background = Brushes.White, Margin = new Thickness(4), Padding = new Thickness(8), Child = stack
		};
		border.MouseLeftButtonDown += (_, e) => {
			var toggle = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
			selection.Select(new[] { node }, toggle ? DesignerSelectionOperation.Toggle : DesignerSelectionOperation.Replace);
			e.Handled = true;
		};
		boxesById[node.Id] = border;
		return border;
	}

	/// <summary>DisplayName header label - double-click swaps it for a TextBox that commits on
	/// Enter/lost-focus, matching sequence-activity-designer.md's "the value can be edited in the
	/// property grid or directly on the header of the activity designer".</summary>
	FrameworkElement HeaderLabel(DesignerElementNode node)
	{
		var label = new TextBlock { Text = $"{node.Type}" + (string.IsNullOrEmpty(node.Name) || node.Name == node.Type ? "" : $" \"{node.Name}\""), FontWeight = FontWeights.Bold };
		label.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) BeginEditHeader(node, label); };
		return label;
	}

	void BeginEditHeader(DesignerElementNode node, TextBlock label)
	{
		if (label.Parent is not Panel parent) return;
		var index = parent.Children.IndexOf(label);
		var editor = new TextBox { Text = node.Name ?? "", MinWidth = 80 };
		void Commit()
		{
			parent.Children.RemoveAt(index);
			parent.Children.Insert(index, HeaderLabel(node));
			if (editor.Text != node.Name) SetProperty(node.Id, "$displayName", editor.Text);
		}
		editor.LostFocus += (_, _) => Commit();
		editor.KeyDown += (_, e) => {
			if (e.Key == Key.Enter) { Keyboard.ClearFocus(); e.Handled = true; }
			else if (e.Key == Key.Escape) { editor.Text = node.Name ?? ""; Keyboard.ClearFocus(); e.Handled = true; }
		};
		parent.Children.RemoveAt(index);
		parent.Children.Insert(index, editor);
		editor.Focus();
		editor.SelectAll();
	}

	void HighlightSelection()
	{
		foreach (var box in boxesById.Values) box.Background = Brushes.White;
		foreach (var id in selection.SelectedIds)
			if (boxesById.TryGetValue(id, out var selectedBox))
				selectedBox.Background = Brushes.LightSteelBlue;
	}

	static IEnumerable<DesignerElementNode> Flatten(DesignerElementNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));

	DesignerDocumentSnapshot Snapshot(string text, long version) => new() {
		Version = version, PrimaryFileName = PrimaryFile?.FileName.ToString() ?? "", DesignerFileName = xamlFile.FileName.ToString(),
		Files = { new DesignerSourceFileSnapshot { FileName = xamlFile.FileName.ToString(), Kind = "Designer", Text = text } }
	};

	protected override void LoadInternal(OpenedFile file, Stream stream)
	{
		using var reader = new StreamReader(stream, leaveOpen: true);
		loadedXamlText = reader.ReadToEnd();
		if (host == null) host = WorkflowDesignerHostClient.CreateAsync().GetAwaiter().GetResult();
		state = host.OpenAsync(Snapshot(loadedXamlText, 1)).GetAwaiter().GetResult();
		Rebuild();
		OutputChannel.Write("WorkflowDesigner", $"Host started (PID {host.ProcessId}) for {xamlFile.FileName}");
	}

	protected override void SaveInternal(OpenedFile file, Stream stream)
	{
		var text = host == null ? loadedXamlText : host.FlushAsync(state.Version).GetAwaiter().GetResult().Files[0].Text;
		using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
		writer.Write(text);
		writer.Flush();
		loadedXamlText = text;
	}

	public override void Dispose()
	{
		properties.Clear();
		OutputChannel.Write("WorkflowDesigner", "Designer view disposed");
		host?.Dispose();
		base.Dispose();
	}
}
