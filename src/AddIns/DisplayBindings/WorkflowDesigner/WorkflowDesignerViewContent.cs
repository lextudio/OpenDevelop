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
/// with shared command-driven undo/redo, toolbox drag/drop and workflow-level argument editing
/// layered on top - see the technote's phased plan.</summary>
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
	readonly StackPanel breadcrumbs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
	readonly Canvas overview = new() { Width = 150, Height = 72, Background = Brushes.Gainsboro, Margin = new Thickness(8, 2, 8, 2) };
	readonly HashSet<string> collapsed = new();

	WorkflowDesignerHostClient? host;
	DesignerSessionState state = new();
	DesignerElementNode? selected;
	string drillRootId = "";
	string loadedXamlText = "";
	double zoom = 1;
	bool awaitingWorkflowShortcut;

	public WorkflowDesignerViewContent(OpenedFile file) : base(file)
	{
		toolboxModel.SetItems(ToolNames.Select(name => new DesignerToolboxItemInfo { Name = name, DisplayName = name, TypeName = name, Category = "Workflow" }));
		toolbox.ItemsSource = toolboxModel.VisibleItems;
		toolbox.SelectionChanged += (_, _) => toolboxModel.Select((toolbox.SelectedItem as DesignerToolboxItemInfo)?.TypeName);
		toolbox.MouseDoubleClick += (_, _) => { if (toolboxModel.SelectedItem is { } item) Add(item.TypeName); };
		toolbox.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && toolboxModel.SelectedItem is { } item) DragDrop.DoDragDrop(toolbox, item, DragDropEffects.Copy); };

		// Ordered multi-selection + shared pad/command bridge (designer-common.md's 2026-08-24
		// "push for more code reuse" pass) - matches MewUI/GTK4 wiring instead of the hand-rolled
		// TreeChanged/SelectionChanged subscriptions an earlier draft of this file used.
		selection = new DesignerSelectionController(node => Adapter(node), nodes => new DesignerMultiPropertyAdapter(nodes.Select(node => (object)Adapter(node))));
		commands.RegisterStandard(() => state.CanUndo && host?.IsAlive == true, Undo, () => state.CanRedo && host?.IsAlive == true, Redo,
			() => selection.SelectedIds.Count > 0 && host?.IsAlive == true, DeleteSelectedCore);
		pads = new DesignerPadController(selection, outline.SetRoots, value => properties.SelectedObject = value, outline.SelectNodeById, node => { selected = node; HighlightSelection(); });
		outline.SelectionCommitted += (_, _) => pads.CommitOutlineSelection(outline.SelectedNode?.Id);

		xamlFile = file;
		TabPageText = "Design";
		ConfigureCanvas();
		var grid = new Grid();
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition());
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.Children.Add(breadcrumbs);
		breadcrumbs.Children.Add(overview);
		grid.Children.Add(canvas);
		Grid.SetRow(canvas, 1);
		Grid.SetRow(diagnostic, 2);
		grid.Children.Add(diagnostic);
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Delete, (_, _) => DeleteSelected()));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => commands.Execute(DesignerCommandNames.Undo)));
		grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => commands.Execute(DesignerCommandNames.Redo)));
		grid.PreviewKeyDown += (_, e) => {
			if (awaitingWorkflowShortcut) { awaitingWorkflowShortcut = false; if (e.Key == Key.A) { ShowArguments(); e.Handled = true; } else if (e.Key == Key.V) { ShowVariables(); e.Handled = true; } return; }
			if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.E) { awaitingWorkflowShortcut = true; e.Handled = true; }
		};
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
		surface.AllowDrop = true;
		surface.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(DesignerToolboxItemInfo)) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
		surface.Drop += (_, e) => { if (e.Data.GetData(typeof(DesignerToolboxItemInfo)) is DesignerToolboxItemInfo item) Add(item.TypeName); e.Handled = true; };
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

	public bool Add(string type, string? targetParentId = null)
	{
		if (host == null || state.Tree == null) return false;
		var parentId = targetParentId ?? selected?.Id ?? state.Tree.Id;
		var before = Flatten(state.Tree).Select(n => n.Id).ToHashSet();
		try {
			Mutate(() => host.AddElementAsync(state.Version, parentId, new DesignerToolboxItemInfo { Name = type, TypeName = type }, "", 0, 0).GetAwaiter().GetResult());
		} catch (Exception exception) {
			diagnostic.Text = $"Cannot insert {type}: {exception.Message}";
			return false;
		}
		var added = state.Tree == null ? null : Flatten(state.Tree).FirstOrDefault(n => !before.Contains(n.Id));
		if (added != null) selection.Select(added);
		else diagnostic.Text = $"Cannot insert {type}: the selected activity does not accept another child.";
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

	bool Undo()
	{
		if (host == null) return false;
		Mutate(() => host.UndoAsync(state.Version).GetAwaiter().GetResult());
		return true;
	}

	bool Redo()
	{
		if (host == null) return false;
		Mutate(() => host.RedoAsync(state.Version).GetAwaiter().GetResult());
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
		if (state.Tree != null) {
			var root = Find(state.Tree, drillRootId) ?? state.Tree;
			if (root == state.Tree) drillRootId = "";
			BuildBreadcrumbs(state.Tree, root);
			BuildOverview(state.Tree, root.Id);
			surface.Children.Add(Box(root));
		}
	}

	void BuildOverview(DesignerElementNode root, string currentId)
	{
		overview.Children.Clear();
		var nodes = Flatten(root).ToArray();
		for (var index = 0; index < nodes.Length; index++) {
			var node = nodes[index];
			var depth = node.Id.Length == 0 ? 0 : node.Id.Count(character => character == '.') + 1;
			var rectangle = new Border { Width = Math.Max(18, 62 - depth * 8), Height = 7, Background = node.Id == currentId ? Brushes.SteelBlue : Brushes.SlateGray, ToolTip = node.Name };
			Canvas.SetLeft(rectangle, 4 + depth * 12);
			Canvas.SetTop(rectangle, 4 + index * 62.0 / Math.Max(1, nodes.Length - 1));
			rectangle.MouseLeftButtonDown += (_, e) => { drillRootId = node.Id; Rebuild(); e.Handled = true; };
			overview.Children.Add(rectangle);
		}
	}

	static DesignerElementNode? Find(DesignerElementNode root, string id) => root.Id == id ? root : root.Children.Select(child => Find(child, id)).FirstOrDefault(value => value != null);

	void BuildBreadcrumbs(DesignerElementNode root, DesignerElementNode current)
	{
		breadcrumbs.Children.Clear();
		var path = current.Id.Length == 0 ? new[] { root } : new[] { root }.Concat(current.Id.Split('.').Select((_, i) => Find(root, string.Join('.', current.Id.Split('.').Take(i + 1)))!).Where(node => node != null));
		foreach (var node in path) {
			var button = new Button { Content = node.Name, Margin = new Thickness(2, 0, 2, 0), IsEnabled = node.Id != current.Id };
			button.Click += (_, _) => { drillRootId = node.Id; Rebuild(); };
			breadcrumbs.Children.Add(button);
		}
		var expand = new Button { Content = "Expand All", Margin = new Thickness(12, 0, 2, 0) }; expand.Click += (_, _) => { collapsed.Clear(); Rebuild(); }; breadcrumbs.Children.Add(expand);
		var collapse = new Button { Content = "Collapse All", Margin = new Thickness(2, 0, 2, 0) }; collapse.Click += (_, _) => { foreach (var node in Flatten(current).Where(node => node.Children.Count > 0)) collapsed.Add(node.Id); Rebuild(); }; breadcrumbs.Children.Add(collapse);
		var restore = new Button { Content = "Restore", Margin = new Thickness(2, 0, 2, 0) }; restore.Click += (_, _) => { drillRootId = ""; collapsed.Clear(); Rebuild(); }; breadcrumbs.Children.Add(restore);
		var arguments = new Button { Content = "Arguments", Margin = new Thickness(8, 0, 2, 0) }; arguments.Click += (_, _) => ShowArguments(); breadcrumbs.Children.Add(arguments);
		var variables = new Button { Content = "Variables", Margin = new Thickness(2, 0, 2, 0) }; variables.Click += (_, _) => ShowVariables(); breadcrumbs.Children.Add(variables);
		breadcrumbs.Children.Add(overview);
	}

	void ShowArguments()
	{
		if (host == null) return;
		var rows = host.GetArgumentsAsync().GetAwaiter().GetResult();
		var grid = new DataGrid { ItemsSource = rows, IsReadOnly = true, AutoGenerateColumns = false, Margin = new Thickness(8) };
		grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding(nameof(WorkflowArgumentInfo.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
		grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new System.Windows.Data.Binding(nameof(WorkflowArgumentInfo.TypeName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
		grid.Columns.Add(new DataGridTextColumn { Header = "Default", Binding = new System.Windows.Data.Binding(nameof(WorkflowArgumentInfo.DefaultValue)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
		var name = new TextBox { Width = 130, Margin = new Thickness(4), ToolTip = "Argument name" };
		var type = new TextBox { Width = 110, Margin = new Thickness(4), Text = "String", ToolTip = "Argument type" };
		var defaultValue = new TextBox { Width = 110, Margin = new Thickness(4), ToolTip = "Literal default value (optional)" };
		var add = new Button { Content = "Create Argument", Margin = new Thickness(4) };
		var error = new TextBlock { Foreground = Brushes.OrangeRed, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
		void Refresh() { rows = host.GetArgumentsAsync().GetAwaiter().GetResult(); grid.ItemsSource = rows; }
		bool RunMutation(Func<DesignerSessionState> action) { try { error.Text = ""; Mutate(action); Refresh(); return true; } catch (Exception exception) { error.Text = exception.Message; diagnostic.Text = exception.Message; return false; } }
		add.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text) && RunMutation(() => host.AddArgumentAsync(state.Version, name.Text, type.Text, defaultValue.Text).GetAwaiter().GetResult())) { name.Clear(); defaultValue.Clear(); } };
		var remove = new Button { Content = "Delete Selected", Margin = new Thickness(4) };
		remove.Click += (_, _) => { if (grid.SelectedItem is WorkflowArgumentInfo argument) RunMutation(() => host.RemoveArgumentAsync(state.Version, argument.Name).GetAwaiter().GetResult()); };
		var update = new Button { Content = "Update Selected", Margin = new Thickness(4) };
		update.Click += (_, _) => { if (grid.SelectedItem is WorkflowArgumentInfo argument && !string.IsNullOrWhiteSpace(name.Text)) RunMutation(() => host.UpdateArgumentAsync(state.Version, argument.Name, name.Text, type.Text, defaultValue.Text).GetAwaiter().GetResult()); };
		grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is WorkflowArgumentInfo argument) { name.Text = argument.Name; type.Text = argument.TypeName; defaultValue.Text = argument.DefaultValue; } };
		grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Delete && grid.SelectedItem is WorkflowArgumentInfo argument) { RunMutation(() => host.RemoveArgumentAsync(state.Version, argument.Name).GetAwaiter().GetResult()); e.Handled = true; } };
		var panel = new DockPanel(); var bar = new StackPanel { Orientation = Orientation.Horizontal }; bar.Children.Add(name); bar.Children.Add(type); bar.Children.Add(defaultValue); bar.Children.Add(add); bar.Children.Add(update); bar.Children.Add(remove); bar.Children.Add(error); DockPanel.SetDock(bar, Dock.Top); panel.Children.Add(bar); panel.Children.Add(grid);
		new Window { Title = "Workflow Arguments", Width = 540, Height = 320, Content = panel }.ShowDialog();
	}

	void ShowVariables()
	{
		if (host == null) return;
		var rows = host.GetVariablesAsync().GetAwaiter().GetResult();
		var grid = new DataGrid { ItemsSource = rows, IsReadOnly = true, AutoGenerateColumns = false, Margin = new Thickness(8) };
		grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new System.Windows.Data.Binding(nameof(WorkflowVariableInfo.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
		grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new System.Windows.Data.Binding(nameof(WorkflowVariableInfo.TypeName)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
		grid.Columns.Add(new DataGridTextColumn { Header = "Scope", Binding = new System.Windows.Data.Binding(nameof(WorkflowVariableInfo.Scope)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
		var name = new TextBox { Width = 130, Margin = new Thickness(4), ToolTip = "Variable name" };
		var type = new TextBox { Width = 110, Margin = new Thickness(4), Text = "String", ToolTip = "Variable type" };
		var add = new Button { Content = "Create Variable", Margin = new Thickness(4) };
		var remove = new Button { Content = "Delete Selected", Margin = new Thickness(4) };
		var error = new TextBlock { Foreground = Brushes.OrangeRed, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
		void Refresh() { rows = host.GetVariablesAsync().GetAwaiter().GetResult(); grid.ItemsSource = rows; }
		bool RunMutation(Func<DesignerSessionState> action) { try { error.Text = ""; Mutate(action); Refresh(); return true; } catch (Exception exception) { error.Text = exception.Message; diagnostic.Text = exception.Message; return false; } }
		add.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text) && RunMutation(() => host.AddVariableAsync(state.Version, name.Text, type.Text).GetAwaiter().GetResult())) name.Clear(); };
		remove.Click += (_, _) => { if (grid.SelectedItem is WorkflowVariableInfo variable) RunMutation(() => host.RemoveVariableAsync(state.Version, variable.Name).GetAwaiter().GetResult()); };
		grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is WorkflowVariableInfo variable) { name.Text = variable.Name; type.Text = variable.TypeName; } };
		grid.PreviewKeyDown += (_, e) => { if (e.Key == Key.Delete && grid.SelectedItem is WorkflowVariableInfo variable) { RunMutation(() => host.RemoveVariableAsync(state.Version, variable.Name).GetAwaiter().GetResult()); e.Handled = true; } };
		var panel = new DockPanel(); var bar = new StackPanel { Orientation = Orientation.Horizontal }; bar.Children.Add(name); bar.Children.Add(type); bar.Children.Add(add); bar.Children.Add(remove); bar.Children.Add(error); DockPanel.SetDock(bar, Dock.Top); panel.Children.Add(bar); panel.Children.Add(grid);
		new Window { Title = "Workflow Variables (root scope)", Width = 540, Height = 320, Content = panel }.ShowDialog();
	}

	/// <summary>Activity types with a child-activity collection (Sequence.Activities and
	/// friends) - the ones whose empty body should show VS's "Drop activity here" hint text
	/// rather than rendering as blank (how-to-add-activities-to-the-toolbox.md's toolbox
	/// convention). Leaf activities (WriteLine, Delay, ...) never show it.</summary>
	static bool IsContainer(string type) => type is "Sequence" or "Parallel" or "While" or "DoWhile" or "If" or "TryCatch" or "Flowchart";

	Border Box(DesignerElementNode node)
	{
		var stack = new StackPanel();
		stack.Children.Add(HeaderLabel(node));
		if (node.Children.Count == 0 && IsContainer(node.Type))
			stack.Children.Add(new TextBlock { Text = "Drop activity here", FontStyle = FontStyles.Italic, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) });
		if (!collapsed.Contains(node.Id)) foreach (var child in node.Children) stack.Children.Add(Box(child));
		var border = new Border {
			BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4),
			Background = Brushes.White, Margin = new Thickness(4), Padding = new Thickness(8), Child = stack
		};
		border.AllowDrop = true;
		border.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(DesignerToolboxItemInfo)) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
		border.Drop += (_, e) => { if (e.Data.GetData(typeof(DesignerToolboxItemInfo)) is DesignerToolboxItemInfo item) Add(item.TypeName, node.Id); e.Handled = true; };
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
		label.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) BeginEditHeader(node, label); else { drillRootId = node.Id; Rebuild(); } e.Handled = true; } };
		if (node.Children.Count == 0) return label;
		var header = new StackPanel { Orientation = Orientation.Horizontal };
		var chevron = new Button { Content = collapsed.Contains(node.Id) ? "›" : "⌄", Width = 20, Height = 20, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 0), ToolTip = collapsed.Contains(node.Id) ? "Expand" : "Collapse" };
		chevron.Click += (_, _) => { if (!collapsed.Add(node.Id)) collapsed.Remove(node.Id); Rebuild(); };
		header.Children.Add(chevron);
		header.Children.Add(label);
		return header;
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
