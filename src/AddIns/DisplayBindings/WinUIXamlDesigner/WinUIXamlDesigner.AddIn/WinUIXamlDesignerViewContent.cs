using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Surface;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Designer.Shell;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.WinForms;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.WinUIXamlDesigner;

public sealed class WinUIXamlDesignerViewContent : AbstractViewContentHandlingLoadErrors,
	IOutlineContentHost, IToolsHost, IHasPropertyContainer, IUndoHandler
{
	readonly Grid root = new();
	readonly WinUIXamlHost previewHost;
	// A TextBlock cannot be selected/copied, which made the diagnostics wall a real project
	// triggers (doc/technotes/winui-designer.md "Real-World Project Preview Problem") impossible
	// to get out of the app - a read-only TextBox looks the same but supports selection and Ctrl+C
	// like any other text, without the bigger lift of routing diagnostics into the shared Error
	// List (that technote's roadmap item C, still open).
	readonly TextBox status = new() {
		Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap,
		IsReadOnly = true, IsReadOnlyCaretVisible = true, BorderThickness = new Thickness(0),
		Background = System.Windows.Media.Brushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
		MaxHeight = 200
	};
	readonly DocumentOutlineControl outline = new();
	readonly DesignerSelectionController shellSelection = new();
	readonly DesignerCommandController commands = new();
	readonly PropertyContainer propertyContainer = new();
	readonly WinUIXamlDocumentEditor editor = new();
	string documentError;
	// The editor starts out holding "", which would otherwise make an empty file's first load look
	// already designed.
	bool designedOnce;
	string loadedText = "";
	bool toolboxPopulated;
	// Mirrors WpfViewContent.wasChangedInDesigner. Without it, saving while the Design tab happens
	// to be the active view rewrites the file from this view's serialized document - discarding
	// whatever the Source view did, and reformatting a file the designer never touched.
	bool wasChangedInDesigner;

	public WinUIXamlDesignerViewContent(OpenedFile file, XamlFrameworkContext framework) : base(file)
	{
		Framework = framework;
		previewHost = new WinUIXamlHost(framework, file?.FileName?.ToString() ?? "Preview.xaml");
		previewHost.StateChanged += OnPreviewStateChanged;
		previewHost.ComponentTrayItemSelected += OnComponentTrayItemSelected;
		previewHost.ElementPicked += OnElementPickedOnSurface;
		previewHost.SelectionChanged += OnSelectionChangedOnSurface;
		previewHost.ElementDragCommitted += OnElementDragCommittedOnSurface;
		previewHost.ElementGroupDragCommitted += OnElementGroupDragCommittedOnSurface;
		previewHost.LayoutInsetEditCommitted += OnLayoutInsetEditCommittedOnSurface;
		previewHost.GridGuideDragCommitted += OnGridGuideDragCommittedOnSurface;
		previewHost.GridTrackSplitRequested += OnGridTrackSplitRequestedOnSurface;
		previewHost.ElementDoubleClicked += OnElementDoubleClickedOnSurface;
		previewHost.TextEditCommitted += OnTextEditCommittedOnSurface;
		previewHost.ElementPathPicked += OnElementPathPickedOnSurface;
		previewHost.ControlDropped += OnControlDroppedOnSurface;
		previewHost.ContextCommandRequested += OnContextCommandOnSurface;
		// The surface's default commands plus Cut/Duplicate and the WPF designer's Grid unit commands
		// (Fixed/Star/Auto) for the track under the pointer. Runtimes with the context-menu capability
		// (Uno, ProGPU) show this list; the unit commands need measured tracks, so they stay disabled
		// on ProGPU (see HasMeasuredGridTracks).
		previewHost.SetContextCommands(new[] {
			("Cut", "cut"), ("Copy", "copy"), ("Paste", "paste"), ("Duplicate", "duplicate"), ("-", ""), ("Delete", "delete"), ("-", ""),
			("Bring to Front", "bring-to-front"), ("Bring Forward", "bring-forward"), ("Send Backward", "send-backward"), ("Send to Back", "send-to-back"), ("-", ""),
			("Wrap in Grid", "wrap-grid"), ("Wrap in StackPanel", "wrap-stackpanel"), ("Unwrap", "unwrap"), ("-", ""),
			("Edit Style/Create Empty", "create-empty-style"), ("-", ""),
			("Grid Row/Fixed", "grid-row-Pixel"), ("Grid Row/Star", "grid-row-Star"), ("Grid Row/Auto", "grid-row-Auto"),
			("Grid Column/Fixed", "grid-column-Pixel"), ("Grid Column/Star", "grid-column-Star"), ("Grid Column/Auto", "grid-column-Auto")
		}, command => ParseGridUnitCommand(command) is { } unit
			? GridTrackAtContextPoint(unit.IsRow) is { } track && track.Unit != unit.Unit
			: null);
		previewHost.NudgeRequested += OnNudgeRequestedOnSurface;
		previewHost.UndoRedoRequested += OnUndoRedoRequestedOnSurface;
		commands.RegisterStandard(() => editor.CanUndo, () => ReplayHistory(editor.Undo()),
			() => editor.CanRedo, () => ReplayHistory(editor.Redo()),
			() => editor.FindElement(SelectedElementName) != null, DeleteSelectedCore);
		TabPageText = "Design";
		root.RowDefinitions.Add(new RowDefinition());
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		Grid.SetRow(previewHost, 0);
		Grid.SetRow(status, 1);
		root.Children.Add(previewHost);
		root.Children.Add(status);
		root.PreviewKeyDown += OnPreviewKeyDown;
		status.Text = previewHost.StatusText;
		// The canvas already explains why the surface is empty (its own overlay), so this view's
		// status row would only repeat the same sentence. Its Text stays populated for
		// od.winui-designer.status; only the on-screen row is hidden.
		if (previewHost.IsUnavailable)
			status.Visibility = Visibility.Collapsed;
		outline.SelectedItemChanged += OnOutlineSelectionChanged;
		outline.AllowDrop = true;
		// Existing outline verbs are name-addressed. Unnamed rows use a source-owned menu; when
		// resolution fails they still need an explicit disabled empty menu rather than null: a null
		// WPF ContextMenu bubbles to a named TreeViewItem ancestor and would make its destructive
		// verbs operate on the parent.
		outline.ContextMenuFactory = BuildOutlineContextMenu;
		outline.PreviewMouseLeftButtonDown += OnOutlineMouseDown;
		outline.PreviewMouseMove += OnOutlineMouseMove;
		outline.DragOver += OnOutlineDragOver;
		outline.Drop += OnOutlineDrop;
		UserContent = root;
	}

	public XamlFrameworkContext Framework { get; }
	public string StatusText => status.Text;
	public string BackendName => previewHost.BackendName;
	public bool HasRenderedPreview => previewHost.HasRenderedPreview;
	public bool IsLoading => previewHost.IsLoading;
	public int VisualStateLoadingShownCount => previewHost.VisualStateLoadingShownCount;
	public object OutlineContent => outline;

	/// <summary>WinUI/Uno shares the shell's Toolbox pad rather than hosting ProGPU's own chrome.</summary>
	public object ToolsContent => WinUIXamlToolbox.Instance.ToolboxControl;

	/// <summary>
	/// Feeds the shell's Properties pad. The adapter is backed by the XAML source element, not by
	/// the live ProGPU visual, so every property change lands as a source edit.
	/// </summary>
	public PropertyContainer PropertyContainer => propertyContainer;

	public string SelectedElementName { get; private set; }
	public bool CanUndo => commands.CanExecute("Undo");
	public bool CanRedo => commands.CanExecute("Redo");
	// IUndoHandler (routed from the workbench window's ApplicationCommands.Undo/Redo bindings,
	// so Ctrl+Z/Ctrl+Y keep working even when keyboard focus is in a tool pad).
	bool IUndoHandler.EnableUndo => CanUndo;
	bool IUndoHandler.EnableRedo => CanRedo;
	void IUndoHandler.Undo() => Undo();
	void IUndoHandler.Redo() => Redo();
	public string DocumentError => documentError;
	public IReadOnlyList<string> ElementNames() => editor.ElementNames();
	/// <summary>Binding observations published by the active runtime tree, flattened for the
	/// diagnostic/status surfaces. Empty is deliberately distinct from a success claim.</summary>
	public IReadOnlyList<(string ElementName, string Property, string Status)> BindingTelemetry()
	{
		var result = new List<(string ElementName, string Property, string Status)>();
		void Collect(DesignerElementNode? node)
		{
			if (node == null) return;
			if (!string.IsNullOrEmpty(node.Name))
				result.AddRange(node.Bindings.Select(binding => (node.Name, binding.Property, binding.Status)));
			foreach (var child in node.Children)
				Collect(child);
		}
		Collect(previewHost.ElementTree);
		return result;
	}
	public IReadOnlyList<(string Id, string Name, string Type)> ComponentTrayItems => previewHost.ComponentTrayItems;
	public bool SelectComponentTrayItem(string id) => previewHost.SelectComponentTrayItem(id);

	public Rect? QueryElementScreenBounds(string name) => previewHost.QueryElementScreenBounds(name);
	public string DescribeElementState(string name) => previewHost.DescribeElementState(name);

	public IReadOnlyList<(string Group, IReadOnlyList<string> States, string CurrentState)> GetVisualStateGroups()
		=> previewHost.GetVisualStateGroups();

	public void GoToVisualState(string group, string state) => previewHost.GoToVisualState(group, state);
	public int ResolvedNameCount => previewHost.ResolvedNameCount;
	public string LastPickDiagnostic => previewHost.LastPickDiagnostic;

	public string FrameProfile() => previewHost.FrameProfile();

	public string CompositorMetricsDump() => previewHost.CompositorMetricsDump();

	public string RenderProbeAndProfile() => previewHost.RenderProbeAndProfile();

	public string DumpDrawCalls() => previewHost.DumpDrawCalls();

	public string WinUICommandProbe() => previewHost.WinUICommandProbe();
	public string DiagnoseScreenAnchors() => previewHost.DiagnoseScreenAnchors();

	public string ImagePathProbe() => previewHost.ImagePathProbe();

	public void SetShowDiagnosticOverlay(bool value) => previewHost.SetShowDiagnosticOverlay(value);

	public void SetRecreateBitmapEachFrame(bool value) => previewHost.SetRecreateBitmapEachFrame(value);

	public void SetPresentViaBackgroundBrush(bool value) => previewHost.SetPresentViaBackgroundBrush(value);

	public int OutlineChildCount =>
		outline.Items.Count == 0 ? 0 : ((TreeViewItem)outline.Items[0]).Items.Count;

	/// <summary>Flattened names of the Design-view outline nodes - the named, visible source
	/// elements the surface lets the user select (see <see cref="RebuildOutline"/>). Exposed for
	/// DevFlow so tests can assert what the Outline actually shows, not just how many rows.</summary>
	public IReadOnlyList<string> OutlineNames()
	{
		var names = new List<string>();
		foreach (var root in shellSelection.Roots)
			CollectOutlineNames(root, names);
		return names;
	}

	static void CollectOutlineNames(DesignerElementNode node, List<string> names)
	{
		if (!string.IsNullOrEmpty(node.Name))
			names.Add(node.Name!);
		foreach (var child in node.Children)
			CollectOutlineNames(child, names);
	}

	/// <summary>Surface geometry (frame/selection/handle/element) for resize-drag tests.</summary>
	public DesignerSurfaceGeometry SurfaceGeometry()
		=> previewHost.SurfaceGeometry();

	/// <summary>Design-surface viewport (zoom 1.0 = fit; pan in surface DIPs).</summary>
	public (double Zoom, double PanX, double PanY) GetViewport() => previewHost.GetViewport();
	public void SetViewport(double zoom, double panX, double panY) => previewHost.SetViewport(zoom, panX, panY);
	public void FitView() => previewHost.FitView();
	public (double Width, double Height)? GetDesignSize() => previewHost.GetDesignSize();
	public void SetDesignSize(double width, double height) => previewHost.SetDesignSize(width, height);
	public void ResetDesignSize() => previewHost.ResetDesignSize();
	public void SetDesignTheme(string theme) => previewHost.SetDesignTheme(theme);
	public string GetDesignTheme() => previewHost.GetDesignTheme();
	public string ChildLog => previewHost.ChildLog;
	public string RenderSample() => previewHost.RenderSample();
	public IReadOnlyList<(string Message, string FileName, int Line, int Column, int EndColumn)> LastDiagnostics => previewHost.LastDiagnostics;
	public string ExportPng(string path) => previewHost.ExportPng(path);
	public (double RenderMs, int Width, int Height, double Dpi, int CompressedBytes, int RawBytes) RenderTiming()
		=> previewHost.RenderTiming();
	public double EffectiveDisplayDpi => previewHost.EffectiveDisplayDpi;
	public void SetSimulatedDpi(double? dpi) => previewHost.SetSimulatedDpi(dpi);

	/// <summary>The document-model parse error with its line/column extracted from the
	/// message ("Line 13, position 2"), when the current source did not parse.</summary>
	public (string Message, int Line, int Column)? DocumentErrorWithLocation
	{
		get
		{
			if (string.IsNullOrEmpty(DocumentError))
				return null;
			var match = System.Text.RegularExpressions.Regex.Match(DocumentError,
				@"[Ll]ine\s+(\d+)(?:[,;]\s*[Pp]osition\s+(\d+))?");
			var line = match.Success && int.TryParse(match.Groups[1].Value, out var l) ? l : 0;
			var column = match.Success && match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var c) ? c : 0;
			return (DocumentError, line, column);
		}
	}

	/// <summary>
	/// Switches to the diagnostic source file and jumps its caret to the 1-based line/column.
	/// A child can diagnose App.xaml or another supplied source file, so it is incorrect to always
	/// navigate the active document's paired Source view. The primary-document source editor belongs
	/// to another AddIn (AvalonEdit), reached reflectively through its TextEditor.Caret.
	/// </summary>
	public string GotoSourceLocation(int line, int column, string? diagnosticFileName = null)
	{
		var primaryFileName = PrimaryFile.FileName.ToString();
		if (!string.IsNullOrWhiteSpace(diagnosticFileName)
			&& !FileUtility.IsEqualFileName(primaryFileName, ResolveDiagnosticFileName(diagnosticFileName)))
		{
			var fileName = ResolveDiagnosticFileName(diagnosticFileName);
			SD.FileService.JumpToFilePosition(FileName.Create(fileName), Math.Max(1, line), Math.Max(1, column));
			return $"Jumped to {fileName}, line {line}, column {column}";
		}
		var window = SD.Workbench.ActiveViewContent?.WorkbenchWindow;
		if (window == null)
			return "No active document window";
		for (var index = 0; index < window.ViewContents.Count; index++)
		{
			if (window.ViewContents[index] is WinUIXamlDesignerViewContent)
				continue;
			window.SwitchView(index);
			try
			{
				// The source editor (AvalonEditViewContent) keeps its editor in a private
				// "codeEditor" field exposing PrimaryTextEditor.TextArea.Caret; reach it
				// reflectively since this AddIn does not reference the AvalonEdit AddIn.
				var content = window.ViewContents[index];
				var codeEditor = content.GetType().GetField("codeEditor",
					System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(content);
				var textEditor = codeEditor?.GetType().GetProperty("PrimaryTextEditor")?.GetValue(codeEditor);
				var textArea = textEditor?.GetType().GetProperty("TextArea")?.GetValue(textEditor);
				var caret = textArea?.GetType().GetProperty("Caret")?.GetValue(textArea);
				if (caret == null)
					return "Source view does not expose a text editor caret";
				caret.GetType().GetProperty("Line")?.SetValue(caret, Math.Max(1, line));
				caret.GetType().GetProperty("Column")?.SetValue(caret, Math.Max(1, column));
				textEditor?.GetType().GetMethod("ScrollToLine")?.Invoke(textEditor, new object[] { Math.Max(1, line) });
				return $"Jumped to line {line}, column {column}";
			}
			catch (Exception e)
			{
				return "Jump failed: " + e.GetBaseException().Message;
			}
		}
		return "No source view found";
	}

	string ResolveDiagnosticFileName(string diagnosticFileName)
		=> Path.IsPathRooted(diagnosticFileName)
			? diagnosticFileName
			: Path.Combine(Path.GetDirectoryName(PrimaryFile.FileName.ToString()) ?? "", diagnosticFileName);
	public bool Gridlines => previewHost.Gridlines;
	public void SetGridlines(bool show) => previewHost.SetGridlines(show);
	public bool ShowTabOrder => previewHost.ShowTabOrder;
	public void SetTabOrderMode(bool show) => previewHost.SetTabOrderMode(show);

	/// <summary>True while the runtime's child process is alive (the Uno design host).</summary>
	public bool IsChildProcessAlive => previewHost.IsChildProcessAlive;

	#region Editing operations

	bool syncingSelection;
	// A component-tray item has no corresponding design-surface rectangle. Keep that distinction
	// while it remains selected so a later render never asks the runtime to draw a phantom outline.
	bool componentTraySelection;

	public bool SelectElement(string name)
	{
		if (syncingSelection)
			return true;
		var element = editor.FindElement(name);
		if (element == null) {
			SelectedElementName = null;
			propertyContainer.SelectedObject = null;
			previewHost.ClearSelection();
			return false;
		}
		syncingSelection = true;
		try {
			componentTraySelection = false;
			SelectedElementName = name;
			shellSelection.Select(name);
			propertyContainer.SelectedObject = new WinUIXamlElementPropertyAdapter(element, editor.Document?.Root, SetAttributeThroughEditor, SetEventThroughEditor, EditRichTextBlock, value => editor.TryGetRichTextRuns(value, out _));
			SelectOutlineNode(name);
			// The runtime's selection set is managed by surface picks and MultiSelect -
			// this path only syncs the pad/outline, so it never collapses a multi-selection.
			previewHost.ShowSelection(name);
			RefreshGridGuides(name);
		}
		finally {
			syncingSelection = false;
		}
		return true;
	}

	void OnPreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.F2 || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase)
			return;
		RenameSelectedElement();
		e.Handled = true;
	}

	void RenameSelectedElement()
	{
		var oldName = SelectedElementName;
		if (string.IsNullOrEmpty(oldName) || editor.FindElement(oldName) == null)
			return;
		var newName = MessageService.ShowInputBox("Rename", "Enter a new element name:", oldName)?.Trim();
		if (string.IsNullOrEmpty(newName) || string.Equals(oldName, newName, StringComparison.Ordinal))
			return;
		if (!RenameSelectedElement(newName, out var error))
		{
			MessageService.ShowError(error);
		}
	}

	/// <summary>Renames the current selection as one source edit. Component-tray items deliberately
	/// use a full document refresh because they have no live visual identity for <c>design/rename</c>
	/// to address.</summary>
	public bool RenameSelectedElement(string newName, out string error)
	{
		var oldName = SelectedElementName;
		if (string.IsNullOrEmpty(oldName))
		{
			error = "No element is selected.";
			return false;
		}
		newName = newName?.Trim() ?? "";
		if (string.Equals(oldName, newName, StringComparison.Ordinal))
		{
			error = "";
			return true;
		}
		if (!editor.Rename(oldName, newName, out error))
			return false;
		// The local editor is authoritative; the child call is an optimization that preserves its
		// live visual identity while the full source remains the fallback for a rejected refresh.
		if (componentTraySelection)
		{
			SelectedElementName = newName;
			ApplyDocumentChange();
			previewHost.ClearSelection();
			previewHost.ClearGridGuides();
		}
		else
		{
			ApplyDocumentChange(text => previewHost.TryRename(oldName, newName, text));
			SelectElement(newName);
		}
		return true;
	}

	/// <summary>
	/// Toolbox insertion. <paramref name="containerName"/> is the x:Name of the element to insert
	/// into; null targets the root, matching a drop onto empty design-surface space.
	/// </summary>
	// The only two toolbox items that are genuine multi-child panels (see WinUIXamlToolbox's own
	// item list) - everything else (Button, TextBlock, Border, ...) has at most one child slot, so
	// dropping "onto" one of them is aiming at its container, not asking to become its own content.
	static readonly HashSet<string> PanelElementNames = new(StringComparer.Ordinal) { "Grid", "StackPanel" };

	public string InsertFromToolbox(string controlName, string containerName)
	{
		var container = containerName == null ? null : editor.FindElement(containerName);
		if (containerName != null && container == null)
			throw new InvalidOperationException("No element named '" + containerName + "' to insert into.");
		if (string.Equals(controlName, "ContextFlyout", StringComparison.Ordinal))
		{
			if (container == null)
				throw new InvalidOperationException("Drop ContextFlyout onto a named control.");
			var flyout = editor.InsertContextFlyout(container);
			ApplyDocumentChange();
			SelectComponentTrayElement(flyout);
			return (string)flyout.Attribute(WinUIXamlDocumentEditor.NameDirective);
		}
		if (string.Equals(controlName, "Flyout", StringComparison.Ordinal))
		{
			if (container == null)
				throw new InvalidOperationException("Drop Flyout onto a named Button or AppBarButton.");
			var flyout = editor.InsertFlyout(container);
			ApplyDocumentChange();
			SelectComponentTrayElement(flyout);
			return (string)flyout.Attribute(WinUIXamlDocumentEditor.NameDirective);
		}
		if (string.Equals(controlName, "CommandBarFlyout", StringComparison.Ordinal))
		{
			if (container == null)
				throw new InvalidOperationException("Drop CommandBarFlyout onto a named Button or AppBarButton.");
			var flyout = editor.InsertCommandBarFlyout(container);
			ApplyDocumentChange();
			SelectComponentTrayElement(flyout);
			return (string)flyout.Attribute(WinUIXamlDocumentEditor.NameDirective);
		}
		if (string.Equals(controlName, "MenuFlyoutItem", StringComparison.Ordinal))
		{
			// A MenuFlyout is source-only and has no surface bounds to receive a drop. Its tray
			// selection is therefore the explicit insertion target; do not reinterpret the visual
			// control under the cursor as the flyout's parent.
			var menu = componentTraySelection ? editor.FindElement(SelectedElementName) : container;
			if (!IsMenuFlyoutContainer(menu))
				throw new InvalidOperationException("Select a MenuFlyout or MenuFlyoutSubItem in the component tray before adding a MenuFlyoutItem.");
			var item = editor.InsertMenuFlyoutItem(menu);
			ApplyDocumentChange();
			SelectComponentTrayElement(item);
			return (string)item.Attribute(WinUIXamlDocumentEditor.NameDirective);
		}
		if (string.Equals(controlName, "ToggleMenuFlyoutItem", StringComparison.Ordinal))
		{
			var menu = componentTraySelection ? editor.FindElement(SelectedElementName) : container;
			if (!IsMenuFlyoutContainer(menu))
				throw new InvalidOperationException("Select a MenuFlyout or MenuFlyoutSubItem in the component tray before adding a ToggleMenuFlyoutItem.");
			var item = editor.InsertToggleMenuFlyoutItem(menu);
			ApplyDocumentChange();
			SelectComponentTrayElement(item);
			return (string)item.Attribute(WinUIXamlDocumentEditor.NameDirective);
		}
		if (string.Equals(controlName, "MenuFlyoutSubItem", StringComparison.Ordinal))
		{
			var menu = componentTraySelection ? editor.FindElement(SelectedElementName) : container;
			if (!IsMenuFlyoutContainer(menu))
				throw new InvalidOperationException("Select a MenuFlyout or MenuFlyoutSubItem in the component tray before adding a MenuFlyoutSubItem.");
			var item = editor.InsertMenuFlyoutSubItem(menu);
			ApplyDocumentChange();
			SelectComponentTrayElement(item);
			return (string)item.Attribute(WinUIXamlDocumentEditor.NameDirective);
		}
		// A drop resolves to the nearest NAMED source element under the cursor
		// (WinUIXamlHost.OnDrop -> ProGpuRuntimeHost.ResolveNameAt), which is very often a leaf
		// control - dropping onto PrimaryButton previously tried to insert a second child directly
		// into it, which the WinUI compiler rejects outright ("Member '$content' cannot contain
		// multiple values"). Walk up to the nearest actual panel so the drop lands as a sibling
		// near where the user aimed, matching what a real design surface does.
		while (container != null && !PanelElementNames.Contains(container.Name.LocalName))
			container = container.Parent;
		var inserted = editor.Insert(controlName, container);
		var name = (string)inserted.Attribute(WinUIXamlDocumentEditor.NameDirective);
		// design/add-element needs a named Panel to attach into; a drop onto the document root
		// (container == null, or the root panel simply has no x:Name) has nothing to address, so
		// that case keeps going through the full-document reload as before. Otherwise, reuse the
		// exact x:Name and XAML editor.Insert() already produced - the same "editor decides, RPC
		// mirrors" split used by design/set-property and design/set-bounds.
		var containerElementName = container == null ? null : (string)container.Attribute(WinUIXamlDocumentEditor.NameDirective);
		if (containerElementName != null)
		{
			var itemXaml = inserted.ToString();
			ApplyDocumentChange(xaml => previewHost.TryAddElement(containerElementName, itemXaml, xaml));
		}
		else
		{
			ApplyDocumentChange();
		}
		SelectElement(name);
		return name;
	}

	static bool IsMenuFlyoutContainer(XElement element)
	{
		return element != null && (string.Equals(element.Name.LocalName, "MenuFlyout", StringComparison.Ordinal)
			|| string.Equals(element.Name.LocalName, "MenuFlyoutSubItem", StringComparison.Ordinal));
	}

	public bool DeleteSelected() => commands.Execute("Delete");

	bool DeleteSelectedCore() => DeleteElement(SelectedElementName);

	/// <summary>Deletes the named element as a source edit and clears the selection.</summary>
	public bool DeleteElement(string name)
	{
		if (string.IsNullOrEmpty(name))
			return false;
		var element = editor.FindElement(name);
		if (element == null)
			return false;
		editor.Remove(element);
		SelectedElementName = null;
		propertyContainer.SelectedObject = null;
		previewHost.ClearSelection();
		ApplyDocumentChange(xaml => previewHost.TryDeleteElements(new[] { name }, xaml));
		return true;
	}

	public bool Undo() => commands.Execute("Undo");
	public bool Redo() => commands.Execute("Redo");

	bool ReplayHistory(bool moved)
	{
		if (!moved)
			return false;
		// The selected element may not exist in the restored revision, so re-resolve it by name.
		var previouslySelected = SelectedElementName;
		ApplyDocumentChange();
		if (previouslySelected != null && !SelectElement(previouslySelected)) {
			SelectedElementName = null;
			propertyContainer.SelectedObject = null;
		}
		return true;
	}

	void SetAttributeThroughEditor(XElement element, XName attribute, string value)
	{
		editor.SetAttribute(element, attribute, value);
		ApplyDocumentChange();
	}

	/// <summary>Applies an event-handler-name change (the Properties pad's Events view) and pushes
	/// it as a discrete design/set-event incremental render, the same way
	/// <see cref="OnTextEditCommittedOnSurface"/> uses design/set-property. A newly introduced
	/// handler name also gets its code-behind method created (VS-style).</summary>
	void SetEventThroughEditor(XElement element, XName attribute, string value)
	{
		var wasBound = !string.IsNullOrEmpty(element.Attribute(attribute)?.Value);
		editor.SetAttribute(element, attribute, value);
		var elementName = (string)element.Attribute(WinUIXamlDocumentEditor.NameDirective);
		var eventName = attribute.LocalName;
		var handlerName = value ?? "";
		if (elementName != null && !string.IsNullOrEmpty(handlerName) && !wasBound)
		{
			// Create the paired method in the .xaml.cs partial class so the new event
			// attribute reference actually compiles, then open the code-behind and jump
			// to the created method (VS-style).
			var rootElement = editor.Document?.Root;
			var line = WinUIXamlCodeBehind.CreateHandlerMethod(PrimaryFile.FileName.ToString(), rootElement, eventName, handlerName);
			if (line.HasValue)
			{
				var codePath = PrimaryFile.FileName.ToString() + ".cs";
				try {
					var view = SD.FileService.OpenFile(FileName.Create(codePath));
					var codeEditor = view?.GetService(typeof(ITextEditor)) as ITextEditor;
					codeEditor?.JumpTo(line.Value, 1);
				} catch (Exception ex) {
					LoggingService.Warn("WinUI designer: could not open code-behind " + codePath + ": " + ex.Message);
				}
			}
		}
		if (elementName == null)
		{
			ApplyDocumentChange();
			return;
		}
		ApplyDocumentChange(xaml => previewHost.TrySetEvent(elementName, eventName, handlerName, xaml));
	}

	/// <summary>
	/// The single point where an edit becomes visible: mark the file dirty so the shared
	/// OpenedFile machinery writes it (and the Source view picks it up on the next view switch),
	/// rebuild the outline from the new document, and re-render.
	/// </summary>
	void ApplyDocumentChange() => ApplyDocumentChange((Action<string>)null);

	/// <summary>Same as the parameterless overload, except the final render push can go out as
	/// a discrete DDP edit (design/set-property, design/set-bounds) instead of a full document
	/// reparse, when <paramref name="incrementalRender"/> is given. <c>editor</c> has already
	/// been updated by the caller either way - this only ever changes which render request goes
	/// out; undo/dirty/outline/save are unaffected. The runtime falls back to a full reload on
	/// its own if the incremental edit is rejected, so callers never need to know which path ran.</summary>
	void ApplyDocumentChange(Action<string> incrementalRender)
	{
		wasChangedInDesigner = true;
		PrimaryFile?.MakeDirty();
		RebuildOutline();
		previewHost.SetSelectableNames(editor.ElementNames());
		if (incrementalRender != null)
			incrementalRender(editor.Text);
		else
			previewHost.LoadXaml(editor.Text);
		status.Text = previewHost.StatusText;
	}

	#endregion

	void OnPreviewStateChanged(object sender, EventArgs e)
	{
		status.Text = previewHost.StatusText;
		// ApplySnapshot clears the tray from its transport snapshot. Reapply the source-owned
		// projection after every render so an asynchronous child update cannot erase it.
		RefreshComponentTray();
		// A settled render may have moved or resized the selected element; re-apply the
		// outline and Grid guides from the freshly indexed tree.
		if (SelectedElementName != null && !componentTraySelection)
		{
			previewHost.ShowSelection(SelectedElementName);
			RefreshGridGuides(SelectedElementName);
		}
		// Once the design host is ready it reports the toolbox catalog for the loaded
		// runtime; the shared Toolbox pad then lists the controls the project can actually use.
		if (!toolboxPopulated && previewHost.GetToolboxCatalog() is { Count: > 0 } catalog)
		{
			WinUIXamlToolbox.Instance.PopulateFromCatalog(catalog);
			toolboxPopulated = true;
		}
	}

	void RefreshComponentTray()
	{
		var tree = previewHost.ElementTree;
		if (tree == null || editor.Document?.Root == null)
		{
			previewHost.SetComponentTray(Array.Empty<(string Id, string Name, string Type)>());
			return;
		}
		var visibleNames = new HashSet<string>(StringComparer.Ordinal);
		CollectTreeNames(tree, visibleNames);
		var items = editor.Document.Root.Descendants()
			.Where(element => IsComponentTrayCandidate(element, visibleNames))
			.Select(element => (Id: editor.GetElementPath(element), Name: (string)element.Attribute(WinUIXamlDocumentEditor.NameDirective), Type: element.Name.LocalName))
			.Where(item => !string.IsNullOrEmpty(item.Id))
			.ToList();
		previewHost.SetComponentTray(items);
	}

	static void CollectTreeNames(DesignerElementNode node, ISet<string> names)
	{
		if (!string.IsNullOrEmpty(node.Name))
			names.Add(node.Name);
		foreach (var child in node.Children)
			CollectTreeNames(child, names);
	}

	// Property elements whose value is never part of the page's visual tree: flyouts (shown on demand), a
	// tool tip, keyboard accelerators, text highlighters, behaviors. Neither the render nor the Document
	// Outline (which mirrors the rendered tree) has a node for them, so the tray is their only place on the
	// design surface.
	static readonly HashSet<string> NonVisualOwnerMembers = new(StringComparer.Ordinal) {
		"Flyout", "ContextFlyout", "SelectionFlyout", "AttachedFlyout",
		"ToolTip", "KeyboardAccelerators", "TextHighlighters", "Behaviors"
	};

	internal static bool IsComponentTrayCandidate(XElement element, ISet<string> visibleNames)
	{
		var name = (string)element.Attribute(WinUIXamlDocumentEditor.NameDirective);
		if (string.IsNullOrEmpty(name) || visibleNames.Contains(name) || element.Name.LocalName.Contains('.'))
			return false;
		// Named resources/templates are declarations, not live component instances. Showing one
		// as a tray component would promise editable instance properties that do not exist.
		if (element.Ancestors().Any(ancestor => ancestor.Name.LocalName is "ResourceDictionary"
			or "Style" or "ControlTemplate" or "DataTemplate" or "ItemsPanelTemplate"
			or "VisualState" or "VisualStateGroup" || ancestor.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)))
			return false;
		// What makes something a tray component is what it IS - the value of a property that is not part of
		// the visual tree, or something inside one - not merely that the render has no bounds for it. A
		// visual control missing from the render (inside a Collapsed panel, an unselected tab, an unrealized
		// list item) is a control of the page, not a component: it is edited in the Source view, and in the
		// Outline once it renders. Treating every such one as a component filled the tray of pages like WinUI
		// Gallery's HomePage with ordinary controls.
		return element.Ancestors().Any(ancestor => ancestor.Name.LocalName is var local
			&& local.LastIndexOf('.') is var dot and > 0
			&& NonVisualOwnerMembers.Contains(local.Substring(dot + 1)));
	}

	void OnComponentTrayItemSelected(object sender, string id)
	{
		var element = editor.FindElementAtPath(id);
		SelectComponentTrayElement(element);
	}

	void SelectComponentTrayElement(XElement element)
	{
		if (element == null || syncingSelection)
			return;
		syncingSelection = true;
		try {
			// A tray item intentionally has no design rectangle. Select its source adapter without
			// pretending that a visual overlay could represent it.
			componentTraySelection = true;
			SelectedElementName = (string)element.Attribute(WinUIXamlDocumentEditor.NameDirective);
			propertyContainer.SelectedObject = new WinUIXamlElementPropertyAdapter(element, editor.Document?.Root, SetAttributeThroughEditor, SetEventThroughEditor, EditRichTextBlock, value => editor.TryGetRichTextRuns(value, out _));
			previewHost.ClearSelection();
			previewHost.ClearGridGuides();
		}
		finally {
			syncingSelection = false;
		}
	}

	/// <summary>
	/// Turns a committed design-surface drag into source edits: the position delta maps to
	/// the element's Margin (alignment-aware), and a size delta writes explicit
	/// Width/Height. Landed through the editor, so undo/redo and the dirty state behave
	/// exactly like any other designer edit.
	/// </summary>
	void OnElementDragCommittedOnSurface(object sender, ElementDragInfo info)
	{
		var element = editor.FindElement(info.Name);
		if (element == null)
			return;
		ApplyElementBounds(element, info);
	}

	void OnElementGroupDragCommittedOnSurface(object sender, IReadOnlyList<(string Name, double DX, double DY)> deltas)
	{
		var list = deltas.Where(d => editor.FindElement(d.Name) != null).ToList();
		if (list.Count == 0)
			return;
		ApplyElementDeltas(list);
		SelectElement(list[0].Name);
	}

	void OnLayoutInsetEditCommittedOnSurface(object sender, LayoutInsetEditInfo edit)
	{
		if (edit.Kind != "CanvasPosition" || editor.FindElement(edit.Name) is not { } element)
			return;
		var node = FindRuntimeCanvasNode(previewHost.ElementTree, edit.Name, out var ambiguous);
		if (ambiguous || node?.LayoutInsets is not { } insets)
			return;
		var (dx, dy) = edit.Edge switch {
			"Left" => (edit.Value - insets.Left, 0d),
			"Right" => (insets.Right - edit.Value, 0d),
			"Top" => (0d, edit.Value - insets.Top),
			"Bottom" => (0d, insets.Bottom - edit.Value),
			_ => (0d, 0d)
		};
		var changes = WinUICanvasPositionEditor.PositionDelta(element, dx, dy);
		if (changes.Count == 0)
			return;
		foreach (var change in changes)
			editor.SetAttribute(element, change.Attribute, change.Value);
		ApplyDocumentChange();
		SelectElement(edit.Name);
	}

	/// <summary>Shows Grid divider guides when a Grid is selected, hiding them otherwise.</summary>
	void RefreshGridGuides(string selectedName)
	{
		if (string.IsNullOrEmpty(selectedName) || editor.FindElement(selectedName) is not { Name.LocalName: "Grid" } grid)
		{
			previewHost.ClearGridGuides();
			return;
		}
		var (rows, cols) = GridGuides(selectedName);
		var runtimeGrid = FindRuntimeGridNode(previewHost.ElementTree, selectedName, out var ambiguousRuntimeGrid);
		if (ambiguousRuntimeGrid)
		{
			// A name is only namescope-local. Do not combine one template instance's
			// rectangle with another's source definitions.
			previewHost.ClearGridGuides();
			return;
		}
		var bounds = runtimeGrid == null ? previewHost.QueryElementBounds(selectedName)
			: (runtimeGrid.X, runtimeGrid.Y, runtimeGrid.Width, runtimeGrid.Height);
		if (bounds == null)
		{
			// A snapshot refresh can remove the selected Grid (or leave it without a
			// renderable node). Do not leave the previous frame's dividers behind.
			previewHost.ClearGridGuides();
			return;
		}
		previewHost.SetGridGuides(selectedName, bounds.Value.X, bounds.Value.Y, bounds.Value.Width, bounds.Value.Height, rows, cols);
	}

	void OnGridGuideDragCommittedOnSurface(object sender, (string Name, bool IsRow, int Index, double Position) args)
	{
		var result = args.IsRow
			? ResizeGridRow(args.Name, args.Index, args.Position)
			: ResizeGridColumn(args.Name, args.Index, args.Position);
		status.Text = result;
		// Re-show the guides at the new layout.
		RefreshGridGuides(args.Name);
	}

	void OnGridTrackSplitRequestedOnSurface(object sender, (string Name, bool IsRow, double Position) args)
	{
		status.Text = SplitGridTrack(args.Name, args.IsRow, args.Position);
		RefreshGridGuides(args.Name);
	}

	void OnNudgeRequestedOnSurface(object sender, (double DX, double DY) delta)
		=> NudgeSelection(delta.DX, delta.DY);

	void OnUndoRedoRequestedOnSurface(object sender, bool undo)
	{
		var moved = undo ? Undo() : Redo();
		status.Text = moved ? (undo ? "Undone" : "Redone") : "Nothing to " + (undo ? "undo" : "redo");
	}

	/// <summary>Nudges the selected element(s) by the given design-unit delta as a source edit.</summary>
	public string NudgeSelection(double dx, double dy)
	{
		var targets = SelectedElementBounds;
		if (targets.Count == 0)
			return "Nothing selected";
		var deltas = targets.Select(t => (t.Name, dx, dy)).ToList();
		ApplyElementDeltas(deltas);
		return $"nudged {deltas.Count} element(s)";
	}
	/// <summary>
	/// A double-click on the design surface starts inline text editing for text-bearing
	/// elements (TextBlock/TextBox/Button-like: Text or Content attribute, or authored direct
	/// leaf text); anything else or empty space resets the viewport to fit.
	/// </summary>
	void OnElementDoubleClickedOnSurface(object sender, ElementDoubleClickInfo info)
	{
		if (info == null || string.IsNullOrEmpty(info.Name))
		{
			previewHost.FitView();
			return;
		}
		var element = editor.FindElement(info.Name);
		if (element?.Name.LocalName == "RichTextBlock" && editor.TryGetRichTextRuns(element, out _)) {
			EditRichTextBlock(element);
			return;
		}
		if (element == null || !TryGetInlineText(element, out _, out var text))
		{
			previewHost.FitView();
			return;
		}
		previewHost.BeginTextEdit(info.X, info.Y, info.Width, info.Height, text);
	}

	void EditRichTextBlock(XElement element)
	{
		var sourcePath = editor.GetElementPath(element);
		if (!editor.TryGetRichTextRuns(element, out var runs)) {
			MessageService.ShowError("Formatted text can be edited only when it has one paragraph containing direct Run elements.");
			return;
		}
		if (!RichTextRunEditorDialog.Edit(Window.GetWindow(root), runs, out var changed))
			return;
		try {
			editor.SetRichTextRuns(element, changed);
		} catch (Exception exception) {
			MessageService.ShowError(exception.Message);
			return;
		}
		ApplyDocumentChange();
		if (editor.FindElementAtPath(sourcePath) is { } updated)
			SelectSourceElement(updated, sourcePath);
		else
			SelectElement(SelectedElementName);
	}

	/// <summary>Applies the inline-edited text to the source document.</summary>
	void OnTextEditCommittedOnSurface(object sender, string text)
	{
		var element = editor.FindElement(SelectedElementName);
		if (element == null)
			return;
		if (!TryGetInlineText(element, out var attribute, out _))
			return;
		if (attribute == null)
		{
			editor.SetTextContent(element, text);
			ApplyDocumentChange();
			SelectElement(SelectedElementName);
			return;
		}
		editor.SetAttribute(element, attribute, text);
		var propertyName = attribute.LocalName;
		var elementName = SelectedElementName;
		ApplyDocumentChange(xaml => previewHost.TrySetProperty(elementName, propertyName, text, xaml));
		SelectElement(SelectedElementName);
	}

	static XName TextAttributeFor(XElement element)
	{
		switch (element.Name.LocalName)
		{
			case "TextBlock":
			case "TextBox":
			case "PasswordBox":
			case "RichEditBox":
			case "NumberBox":
				return "Text";
			case "Button":
			case "CheckBox":
			case "RadioButton":
			case "ToggleSwitch":
			case "HyperlinkButton":
			case "RepeatButton":
			case "ToggleButton":
			case "AppBarButton":
				return "Content";
			default:
				return null;
		}
	}

	/// <summary>Returns the text an inline editor may safely replace. Attribute-backed text keeps
	/// its attribute form; a leaf with authored direct text preserves that form. Object/mixed
	/// content is excluded because a text edit must never erase a child element.</summary>
	internal static bool TryGetInlineText(XElement element, out XName attribute, out string text)
	{
		attribute = TextAttributeFor(element);
		text = "";
		if (attribute == null)
			return false;
		if (element.Attribute(attribute) is { } value)
		{
			text = value.Value;
			return true;
		}
		if (element.Elements().Any() || element.Nodes().Any(node => node is not XText))
			return false;
		var directText = string.Concat(element.Nodes().OfType<XText>().Select(node => node.Value));
		if (!string.IsNullOrWhiteSpace(directText))
		{
			attribute = null;
			text = directText;
		}
		return true;
	}

	/// <summary>Invokes a context command as if it came from the surface menu (scriptable).</summary>
	public void InvokeContextCommand(string command, string name = "")
	{
		if (string.IsNullOrEmpty(name))
			name = SelectedElementName;
		OnContextCommandOnSurface(this, (command, name));
	}

	/// <summary>In-memory fallback clipboard for environments without a usable system clipboard
	/// (LibreWPF's Clipboard bridge may be unavailable); the system clipboard is preferred.</summary>
	static string clipboardXaml;

	/// <summary>Copies XAML text to the system clipboard, falling back to the in-memory one.</summary>
	static void SetClipboardText(string xaml)
	{
		try
		{
			Clipboard.SetText(xaml);
		}
		catch
		{
			// System clipboard unavailable (LibreWPF): keep the in-memory fallback working.
		}
		clipboardXaml = xaml;
	}

	/// <summary>Reads XAML text from the system clipboard, falling back to the in-memory one.</summary>
	static string GetClipboardText()
	{
		try
		{
			if (!string.IsNullOrEmpty(Clipboard.GetText()))
				return Clipboard.GetText();
		}
		catch
		{
			// System clipboard unavailable - fall through to the in-memory copy.
		}
		return clipboardXaml;
	}

	/// <summary>
	/// Applies a design-surface context-menu command as a source edit: copy/paste,
	/// delete, z-order and wrap-in-container.
	/// </summary>
	void OnContextCommandOnSurface(object sender, (string Command, string Name) args)
	{
		switch (args.Command)
		{
			case "copy":
				if (editor.FindElement(args.Name) is { } copied)
				{
					var xaml = copied.ToString(SaveOptions.DisableFormatting);
					SetClipboardText(xaml);
					status.Text = "Copied " + args.Name;
				}
				break;
			case "paste":
				PasteElement(args.Name);
				break;
			case "cut":
				if (editor.FindElement(args.Name) is { } cut && cut.Parent != null)
				{
					SetClipboardText(cut.ToString(SaveOptions.DisableFormatting));
					DeleteElement(args.Name);
				}
				break;
			case "duplicate":
				DuplicateElement(args.Name);
				break;
			case "delete":
				DeleteElement(args.Name);
				break;
			case "bring-to-front":
			case "send-to-back":
				MoveSelectedToEdge(args.Name, args.Command == "bring-to-front");
				break;
			case "bring-forward":
			case "send-backward":
				MoveSelected(args.Name, e => editor.MoveOneStep(e, args.Command == "bring-forward"));
				break;
			case "wrap-grid":
			case "wrap-stackpanel":
				WrapSelected(args.Name, args.Command == "wrap-grid" ? "Grid" : "StackPanel");
				break;
			case "unwrap":
				UnwrapElement(editor.FindElement(args.Name));
				break;
			case "create-empty-style":
				CreateEmptyStyle(editor.FindElement(args.Name));
				break;
			default:
				if (ParseGridUnitCommand(args.Command) is { } unit)
					status.Text = SetGridTrackUnit(unit.IsRow, unit.Unit);
				break;
		}
	}

	/// <summary>Automation: records the context-menu point relative to the selection's bounds.</summary>
	public bool SetContextMenuPointRelativeToSelection(double x, double y)
	{
		if (string.IsNullOrEmpty(SelectedElementName) || previewHost.QueryElementBounds(SelectedElementName) is not { } bounds)
			return false;
		previewHost.ContextMenuDesignPoint = new System.Numerics.Vector2((float)(bounds.X + x), (float)(bounds.Y + y));
		return previewHost.ContextMenuDesignPoint != null;
	}

	public IReadOnlyList<(string Header, string Command, bool Enabled)> DescribeContextCommands() => previewHost.DescribeContextCommands();

	static (bool IsRow, string Unit)? ParseGridUnitCommand(string command)
		=> command.StartsWith("grid-row-", StringComparison.Ordinal) ? (true, command.Substring("grid-row-".Length))
		: command.StartsWith("grid-column-", StringComparison.Ordinal) ? (false, command.Substring("grid-column-".Length))
		: null;

	/// <summary>The selected Grid's authored row/column definition under the context-menu point,
	/// located with the same boundaries the visible dividers use (measured tracks when the runtime
	/// publishes them). Null when the point is outside the Grid or the track has no definition
	/// element to edit (an implicit single track).</summary>
	(string Grid, XElement Definition, string Unit, double Size)? GridTrackAtContextPoint(bool isRow)
	{
		var name = SelectedElementName;
		if (!previewHost.HasMeasuredGridTracks || string.IsNullOrEmpty(name) || previewHost.ContextMenuDesignPoint is not { } point
			|| editor.FindElement(name) is not { Name.LocalName: "Grid" } grid
			|| previewHost.QueryElementBounds(name) is not { } bounds)
			return null;
		var items = grid.Elements().FirstOrDefault(e => IsNamedElement(e, isRow ? "RowDefinitions" : "ColumnDefinitions"))?
			.Elements().Where(e => IsNamedElement(e, isRow ? "RowDefinition" : "ColumnDefinition")).ToList();
		var guides = GridGuides(name);
		var boundaries = isRow ? guides.RowOffsets : guides.ColOffsets;
		if (items == null || items.Count == 0 || boundaries.Length != items.Count + 1)
			return null;
		var local = isRow ? point.Y - bounds.Y : point.X - bounds.X;
		if (point.X < bounds.X || point.X >= bounds.X + bounds.Width || point.Y < bounds.Y || point.Y >= bounds.Y + bounds.Height)
			return null;
		for (var index = 0; index < items.Count; index++)
		{
			if (local >= boundaries[index] && local < boundaries[index + 1])
			{
				var text = ((string)items[index].Attribute(isRow ? "Height" : "Width"))?.Trim();
				var unit = string.IsNullOrEmpty(text) || text.EndsWith("*", StringComparison.Ordinal) ? "Star"
					: string.Equals(text, "Auto", StringComparison.OrdinalIgnoreCase) ? "Auto" : "Pixel";
				return (name, items[index], unit, boundaries[index + 1] - boundaries[index]);
			}
		}
		return null;
	}

	/// <summary>Changes the unit of the Grid track under the context-menu point as a source edit.
	/// Fixed keeps the track's current size (the measured one when the runtime publishes tracks),
	/// so switching never jumps the layout to a guessed length; Star becomes <c>*</c> and Auto
	/// <c>Auto</c>.</summary>
	public string SetGridTrackUnit(bool isRow, string unit)
	{
		if (GridTrackAtContextPoint(isRow) is not { } track)
			return "No Grid " + (isRow ? "row" : "column") + " under the pointer";
		var value = unit switch {
			"Pixel" => Math.Max(1, Math.Round(track.Size)).ToString(CultureInfo.InvariantCulture),
			"Star" => "*",
			"Auto" => "Auto",
			_ => null
		};
		if (value == null)
			return "Unknown Grid unit: " + unit;
		editor.SetAttribute(track.Definition, isRow ? "Height" : "Width", value);
		ApplyDocumentChange();
		RefreshGridGuides(track.Grid);
		return $"{track.Grid} {(isRow ? "row" : "column")} -> {value}";
	}

	/// <summary>Inserts the clipboard element into the selected container (or the root).</summary>
	void PasteElement(string containerName)
		=> PasteElement(editor.FindElement(containerName) ?? editor.Document.Root);

	void PasteElement(XElement container)
	{
		var clipboardText = GetClipboardText();
		if (string.IsNullOrWhiteSpace(clipboardText))
		{
			status.Text = "Designer clipboard is empty";
			return;
		}
		if (container == null)
		{
			status.Text = "Cannot paste into the selected element";
			return;
		}
		try
		{
			var pasted = editor.Paste(clipboardText, container);
			var name = (string)pasted.Attribute(WinUIXamlDocumentEditor.NameDirective);
			ApplyDocumentChange();
			SelectElement(name);
			status.Text = "Pasted " + name;
		}
		catch (Exception e)
		{
			status.Text = "Paste failed: " + e.GetBaseException().Message;
		}
	}

	/// <summary>Pastes a copy of the element right after it in the same parent, without touching
	/// the clipboard; the copy is selected.</summary>
	void DuplicateElement(string name)
	{
		if (editor.FindElement(name) is not { Parent: { } parent } element)
		{
			status.Text = "The root cannot be duplicated";
			return;
		}
		try
		{
			var pasted = editor.Paste(element.ToString(SaveOptions.DisableFormatting), parent, element);
			var pastedName = (string)pasted.Attribute(WinUIXamlDocumentEditor.NameDirective);
			ApplyDocumentChange();
			SelectElement(pastedName);
			status.Text = "Duplicated " + name + " as " + pastedName;
		}
		catch (Exception e)
		{
			status.Text = "Duplicate failed: " + e.GetBaseException().Message;
		}
	}

	/// <summary>Moves the element to the first or last position of its parent (z-order).</summary>
	void MoveSelectedToEdge(string name, bool front) => MoveSelected(name, element => editor.MoveToEdge(element, front));

	void MoveSelected(string name, Func<XElement, bool> move)
	{
		var element = editor.FindElement(name);
		if (element == null)
			return;
		var sourceOnly = componentTraySelection && string.Equals(SelectedElementName, name, StringComparison.Ordinal);
		if (!move(element))
			return;
		ApplyDocumentChange();
		if (sourceOnly)
			SelectComponentTrayElement(element);
		else
			SelectElement(name);
	}

	void MoveToEdge(XElement element, bool front)
	{
		if (element == null || !editor.MoveToEdge(element, front))
			return;
		ApplyDocumentChange();
		SelectSourceElement(element, path: null);
	}

	/// <summary>Wraps the selected element in a new Grid or StackPanel container.</summary>
	void WrapSelected(string name, string containerType)
	{
		var element = editor.FindElement(name);
		if (element == null)
			return;
		var wrapper = editor.WrapInContainer(element, containerType);
		if (wrapper == null)
			return;
		ApplyDocumentChange();
		SelectElement((string)wrapper.Attribute(WinUIXamlDocumentEditor.NameDirective));
	}

	void CreateEmptyStyle(XElement element)
	{
		var key = element == null ? null : editor.CreateEmptyStyle(element);
		if (key == null)
		{
			status.Text = "Create Empty Style needs a non-root element without a Style";
			return;
		}
		ApplyDocumentChange();
		SelectSourceElement(element, path: null);
		status.Text = "Created Style " + key;
	}

	/// <summary>Replaces a container with its children; the selection clears because the
	/// container's name is gone and the children keep their own.</summary>
	void UnwrapElement(XElement element)
	{
		if (element == null || !editor.Unwrap(element))
		{
			status.Text = "Only a non-empty Grid, StackPanel, Canvas, RelativePanel or Border can be unwrapped here";
			return;
		}
		SelectedElementName = null;
		propertyContainer.SelectedObject = null;
		previewHost.ClearSelection();
		ApplyDocumentChange();
	}

	void WrapElement(XElement element, string containerType)
	{
		var wrapper = element == null ? null : editor.WrapInContainer(element, containerType);
		if (wrapper == null)
			return;
		ApplyDocumentChange();
		SelectElement((string)wrapper.Attribute(WinUIXamlDocumentEditor.NameDirective));
	}

	/// <summary>
	/// Computes the row/column divider positions (design units, relative to the Grid's top-left)
	/// for a selected Grid, from its RowDefinitions/ColumnDefinitions. Star rows/columns are
	/// split proportionally over the remainder; Auto is approximated by sharing the remainder
	/// evenly - drag-resizing writes explicit pixel sizes back, which is what VS does.
	/// </summary>
	/// <summary>Matches a Grid property element (Grid.ColumnDefinitions etc.), tolerating
	/// the prefix some XAML parsers leave on the local name.</summary>
	static bool IsNamedElement(XElement e, string kind)
		=> e.Name.LocalName == kind || e.Name.LocalName.EndsWith("." + kind, StringComparison.Ordinal);

	public (double[] RowOffsets, double[] ColOffsets) GridGuides(string name)
	{
		var element = editor.FindElement(name);
		if (element == null || !string.Equals(element.Name.LocalName, "Grid", StringComparison.Ordinal))
			return (Array.Empty<double>(), Array.Empty<double>());
		// An out-of-process Uno/WinUI host can now tell us the resolved Grid layout. Prefer
		// that snapshot over source arithmetic: Auto rows, min/max constraints, pixel rounding
		// and a host-imposed root size all make a faithful answer impossible to derive from
		// RowDefinition/ColumnDefinition text. ProGPU and an unavailable/older runtime have no
		// such node telemetry yet, so retain the existing source-only fallback for them.
		if (FindRuntimeGridNode(previewHost.ElementTree, name, out _)?.GridTracks is { Accepted: true } measured)
			return (GridGuideBoundaries(measured.RowTracks),
				GridGuideBoundaries(measured.ColumnTracks));
		var bounds = previewHost.QueryElementBounds(name);
		if (bounds == null)
			return (Array.Empty<double>(), Array.Empty<double>());
		var rows = ParseGridLengths(element.Elements().FirstOrDefault(e => IsNamedElement(e, "RowDefinitions")), bounds.Value.Height, isRow: true);
		var cols = ParseGridLengths(element.Elements().FirstOrDefault(e => IsNamedElement(e, "ColumnDefinitions")), bounds.Value.Width, isRow: false);
		return (rows, cols);
	}

	/// <summary>Names are unique only inside a XAML namescope. A template/data-template can
	/// therefore contain the same name as the selected source Grid; only trust runtime telemetry
	/// when the tree has exactly one source-backed Grid with that name. Ambiguity deliberately
	/// returns null and tells the caller not to combine a name-indexed rectangle with it.</summary>
	static DesignerElementNode? FindRuntimeGridNode(DesignerElementNode? node, string name, out bool ambiguous)
	{
		DesignerElementNode? match = null;
		var hasAmbiguity = false;
		void Visit(DesignerElementNode? current)
		{
			if (current == null || hasAmbiguity) return;
			if (current.IsDesignable && string.Equals(current.Type, "Grid", StringComparison.Ordinal)
				&& string.Equals(current.Name, name, StringComparison.Ordinal)) {
				if (match != null) { hasAmbiguity = true; return; }
				match = current;
			}
			foreach (var child in current.Children)
				Visit(child);
		}
		Visit(node);
		ambiguous = hasAmbiguity;
		return hasAmbiguity ? null : match;
	}

	static DesignerElementNode? FindRuntimeCanvasNode(DesignerElementNode? node, string name, out bool ambiguous)
	{
		DesignerElementNode? match = null;
		var hasAmbiguity = false;
		void Visit(DesignerElementNode? current)
		{
			if (current == null || hasAmbiguity) return;
			if (current.IsDesignable && string.Equals(current.Name, name, StringComparison.Ordinal)
				&& current.LayoutInsets?.Kind == "CanvasPosition") {
				if (match != null) { hasAmbiguity = true; return; }
				match = current;
			}
			foreach (var child in current.Children)
				Visit(child);
		}
		Visit(node);
		ambiguous = hasAmbiguity;
		return hasAmbiguity ? null : match;
	}

	/// <summary>Converts track start offsets into the boundary list the shared raster canvas
	/// expects: every explicit track start followed by the measured Grid far edge.</summary>
	static double[] GridGuideBoundaries(IReadOnlyList<DesignerGridTrackInfo> tracks)
	{
		if (tracks.Count == 0) return Array.Empty<double>();
		var farEdge = tracks[^1].Offset + tracks[^1].Size;
		if (!double.IsFinite(farEdge) || farEdge < 0) return Array.Empty<double>();
		return tracks.Select(track => track.Offset).Append(farEdge).ToArray();
	}

	static double[] ParseGridLengths(XElement definitions, double total, bool isRow)
	{
		if (definitions == null)
			return new[] { total };
		var items = definitions.Elements()
			.Where(e => IsNamedElement(e, isRow ? "RowDefinition" : "ColumnDefinition"))
			.ToList();
		if (items.Count == 0)
			return new[] { total };
		var lengths = new (double Pixels, double Weight, bool Auto)[items.Count];
		double fixedTotal = 0, starTotal = 0;
		for (var i = 0; i < items.Count; i++)
		{
			var value = (string)items[i].Attribute(isRow ? "Height" : "Width");
			if (string.IsNullOrWhiteSpace(value) || value == "Auto")
			{
				lengths[i] = (0, 0, true);
			}
			else if (value.EndsWith("*", StringComparison.Ordinal))
			{
				var weight = value.TrimEnd('*');
				var w = string.IsNullOrEmpty(weight) ? 1.0 : double.Parse(weight, CultureInfo.InvariantCulture);
				lengths[i] = (0, w, false);
				starTotal += w;
			}
			else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
			{
				lengths[i] = (px, 0, false);
				fixedTotal += px;
			}
			else
			{
				lengths[i] = (0, 0, true);
			}
		}
		var autoCount = lengths.Count(l => l.Auto);
		var remainder = Math.Max(0, total - fixedTotal);
		var starUnit = starTotal > 0 ? remainder / starTotal : 0;
		var autoSize = autoCount > 0 && starTotal == 0 ? remainder / autoCount : 0;
		var offsets = new double[items.Count + 1];
		offsets[0] = 0;
		for (var i = 0; i < items.Count; i++)
		{
			var size = lengths[i].Auto
				? (starTotal > 0 ? 0 : autoSize)
				: lengths[i].Pixels + lengths[i].Weight * starUnit;
			offsets[i + 1] = offsets[i] + size;
		}
		return offsets;
	}

	/// <summary>Sets a Grid column's width (design units) as a source edit; index is the
	/// column index, width is the divider position from the Grid's left edge.</summary>
	public string ResizeGridColumn(string name, int index, double position)
	{
		var element = editor.FindElement(name);
		if (element == null || !string.Equals(element.Name.LocalName, "Grid", StringComparison.Ordinal))
			return "Not a Grid";
		return SetGridLength(element, name, index, position, isRow: false);
	}

	/// <summary>Sets a Grid row's height (design units) as a source edit; index is the row
	/// index, position is the divider from the Grid's top edge.</summary>
	public string ResizeGridRow(string name, int index, double position)
	{
		var element = editor.FindElement(name);
		if (element == null || !string.Equals(element.Name.LocalName, "Grid", StringComparison.Ordinal))
			return "Not a Grid";
		return SetGridLength(element, name, index, position, isRow: true);
	}

	string SetGridLength(XElement grid, string gridName, int index, double position, bool isRow)
	{
		var definitions = grid.Elements().FirstOrDefault(e => IsNamedElement(e, isRow ? "RowDefinitions" : "ColumnDefinitions"));
		if (definitions == null)
			return "Grid has no " + (isRow ? "RowDefinitions" : "ColumnDefinitions");
		var items = definitions.Elements()
			.Where(e => IsNamedElement(e, isRow ? "RowDefinition" : "ColumnDefinition"))
			.ToList();
		if (index < 0 || index >= items.Count)
			return "Index out of range";
		// Use the exact same boundaries the visible divider used. In particular, a runtime
		// GridTracks snapshot may contain Auto/min/max-constrained track sizes that cannot be
		// reconstructed from source text; recomputing them here made a correct-looking drag
		// write the wrong pixel value.
		var guides = GridGuides(gridName);
		var boundaries = isRow ? guides.RowOffsets : guides.ColOffsets;
		if (boundaries.Length != items.Count + 1)
			return "Grid track geometry unavailable";
		var bounds = previewHost.QueryElementBounds(gridName);
		if (bounds == null)
			return "Grid bounds unknown";
		// DesignSurface reports the released divider in design-canvas coordinates, while
		// GridTracks (and the source definition) are local to the Grid. A nested Grid
		// must therefore remove its own origin before the track math below.
		position -= isRow ? bounds.Value.Y : bounds.Value.X;
		// Convert the divider position into a size for THIS row/column: its share is the
		// difference between the new divider and the previous divider.
		var prev = boundaries[index];
		var next = boundaries[index + 2];
		if (next <= prev + 1)
			return "Grid track is too small to resize";
		position = Math.Clamp(position, prev + 1, next - 1);
		var newSize = Math.Max(1, position - prev);
		var attribute = isRow ? "Height" : "Width";
		editor.SetAttribute(items[index], attribute, Math.Round(newSize).ToString(CultureInfo.InvariantCulture));
		ApplyDocumentChange();
		return $"{gridName} {(isRow ? "row" : "column")} {index} -> {Math.Round(newSize)}";
	}

	/// <summary>Splits the measured Grid track under a Ctrl insertion rail. The source update,
	/// including all affected attached indices/spans, is one document-history entry.</summary>
	public string SplitGridTrack(string name, bool isRow, double position)
	{
		var grid = editor.FindElement(name);
		var node = FindRuntimeGridNode(previewHost.ElementTree, name, out var ambiguous);
		var gridTracks = node?.GridTracks;
		var tracks = isRow ? gridTracks?.RowTracks : gridTracks?.ColumnTracks;
		if (grid == null || node == null || ambiguous || gridTracks is not { Accepted: true } || tracks == null)
			return "Grid track geometry unavailable";
		// The shared canvas reports the insertion rail in Grid-local design coordinates.
		// Unlike a divider drag, this is not a canvas-space point and must not subtract
		// the Grid's origin again (nested Grids otherwise choose the wrong track).
		var error = "";
		if (!editor.TryMutate(() => WinUIGridTrackEditor.TrySplit(grid, isRow, tracks, position, out error)))
			return error;
		ApplyDocumentChange();
		return $"{name} {(isRow ? "row" : "column")} split";
	}

	/// <summary>
	/// A pick on an element the document never named: map its tree path back to the source and
	/// select it as-is.
	///
	/// Two things here were wrong and are worth naming, because between them nothing unnamed could
	/// ever be selected. The chain is walked INNERMOST FIRST: it arrives root-first, and taking the
	/// first entry that resolves therefore selected the outermost element every time - click a
	/// TextBlock inside a card, get the page's root Grid. And the element is NOT auto-named any
	/// more: this used to write a fresh x:Name into the user's markup just because they clicked
	/// something, so merely looking at a page dirtied the document and littered it with Grid1,
	/// ScrollView1, ... Selection is identified by tree path (see GetPickChain), which needs no
	/// name, and the Properties pad binds to the XElement rather than to a name anyway.
	/// </summary>
	void OnElementPathPickedOnSurface(object sender, string path)	{
		if (string.IsNullOrEmpty(path))
			return;
		var chain = previewHost.GetPickChain(path);
		for (var i = chain.Count - 1; i >= 0; i--)
		{
			var (type, typeIndex, nodePath) = chain[i];
			if (FindNthSourceElement(type, typeIndex) is { } element)
			{
				SelectSourceElement(element, nodePath ?? path);
				return;
			}
		}
	}

	/// <summary>Selects a source element that may have no x:Name, keyed by its tree
	/// <paramref name="path"/>. The name-keyed <see cref="SelectElement(string)"/> is used when the
	/// element does have one, so named elements keep the outline/multi-select behaviour they
	/// already had.</summary>
	void SelectSourceElement(XElement element, string? path)
	{
		var name = (string)element.Attribute(WinUIXamlDocumentEditor.NameDirective);
		if (!string.IsNullOrEmpty(name))
		{
			SelectElement(name);
			return;
		}
		if (syncingSelection)
			return;
		syncingSelection = true;
		try {
		componentTraySelection = false;
			SelectedElementName = null;
			propertyContainer.SelectedObject = new WinUIXamlElementPropertyAdapter(element, editor.Document?.Root, SetAttributeThroughEditor, SetEventThroughEditor, EditRichTextBlock, value => editor.TryGetRichTextRuns(value, out _));
			if (string.IsNullOrEmpty(path))
				previewHost.ClearSelection();
			else
				previewHost.ShowSelectionAtPath(path, element.Name.LocalName);
		}
		finally {
			syncingSelection = false;
		}
	}

	/// <summary>Finds the index-th element of the given XAML tag name in the source document.</summary>
	XElement FindNthSourceElement(string typeName, int index)
	{
		if (editor.Document?.Root == null || string.IsNullOrEmpty(typeName) || index < 0)
			return null;
		var matches = editor.Document.Root.DescendantsAndSelf()
			.Where(IsRuntimeSourceCandidate)
			.Where(e => string.Equals(e.Name.LocalName, typeName, StringComparison.Ordinal))
			.ToList();
		return index < matches.Count ? matches[index] : null;
	}

	/// <summary>Matches the source elements represented by the runtime's designable tree. A
	/// type occurrence index must never count resources, templates, property-element markup or an
	/// <c>x:Load="False"</c> subtree, or it would map an otherwise valid runtime path to a different
	/// source object of the same type. Collapsed elements remain in the runtime tree and must stay
	/// in this count even though the Design Outline hides them.</summary>
	internal static bool IsRuntimeSourceCandidate(XElement element)
	{
		if (element.Name.LocalName.Contains('.', StringComparison.Ordinal))
			return false;
		foreach (var current in element.AncestorsAndSelf())
		{
			if (IsResourceDefinitionElement(current) || IsVisualStateElement(current) || IsLoadDisabled(current))
				return false;
			if (current.Name.LocalName is "Style" or "ControlTemplate" or "DataTemplate" or "ItemsPanelTemplate")
				return false;
			// Flyouts are source-owned popup components, not part of the rendered page tree. Their
			// item elements must not shift the occurrence index of ordinary page controls.
			if (current.Name.LocalName.Contains("Flyout", StringComparison.Ordinal))
				return false;
		}
		return true;
	}

	static bool IsLoadDisabled(XElement element)
		=> string.Equals((string?)element.Attribute(XName.Get("Load", "http://schemas.microsoft.com/winfx/2006/xaml")),
			"False", StringComparison.OrdinalIgnoreCase);

	void ApplyElementBounds(XElement element, ElementDragInfo info)
	{		var positionDeltaX = info.EndX - info.StartX;		var positionDeltaY = info.EndY - info.StartY;
		var sizeDeltaX = info.EndWidth - info.StartWidth;
		var sizeDeltaY = info.EndHeight - info.StartHeight;

		if (!ApplyCanvasPositionDelta(element, positionDeltaX, positionDeltaY)) {
			var (left, top, right, bottom) = ParseMargin((string)element.Attribute("Margin"));
			var deltaX = (int)Math.Round(positionDeltaX);
			var deltaY = (int)Math.Round(positionDeltaY);
			switch ((string)element.Attribute("HorizontalAlignment"))
			{
				case "Right": right -= deltaX; break;
				case "Center": left += deltaX; right -= deltaX; break;
				default: left += deltaX; break;
			}
			switch ((string)element.Attribute("VerticalAlignment"))
			{
				case "Bottom": bottom -= deltaY; break;
				case "Center": top += deltaY; bottom -= deltaY; break;
				default: top += deltaY; break;
			}

			if (left != 0 || top != 0 || right != 0 || bottom != 0)
				editor.SetAttribute(element, "Margin", FormatMargin(left, top, right, bottom));
			else
				editor.SetAttribute(element, "Margin", null);
		}
		var sizeChanged = Math.Abs(sizeDeltaX) > 0.01 || Math.Abs(sizeDeltaY) > 0.01;
		if (sizeChanged)
		{
			editor.SetAttribute(element, "Width", ((int)Math.Round(info.EndWidth)).ToString(CultureInfo.InvariantCulture));
			editor.SetAttribute(element, "Height", ((int)Math.Round(info.EndHeight)).ToString(CultureInfo.InvariantCulture));
		}
		// design/set-bounds only ever applies Width/Height directly (plus Canvas.Left/Top when
		// the parent happens to be a Canvas); this codebase positions everything through Margin,
		// so only a pure resize (no position delta) is safe to send incrementally - a move must
		// go through the full document reload, same as before.
		var pureResize = sizeChanged && Math.Abs(positionDeltaX) < 0.01 && Math.Abs(positionDeltaY) < 0.01;
		if (pureResize)
		{
			var elementName = info.Name;
			var width = info.EndWidth;
			var height = info.EndHeight;
			ApplyDocumentChange(xaml => previewHost.TrySetBounds(elementName, 0, 0, width, height, xaml));
		}
		else
		{
			ApplyDocumentChange();
		}
		SelectElement(info.Name);
	}

	/// <summary>Applies a Canvas-specific position delta, returning false for normal layout
	/// containers that continue to use this designer's alignment-aware Margin convention.</summary>
	bool ApplyCanvasPositionDelta(XElement element, double deltaX, double deltaY)
	{
		var changes = WinUICanvasPositionEditor.PositionDelta(element, deltaX, deltaY);
		if (changes.Count == 0)
			return WinUICanvasPositionEditor.IsCanvasChild(element);
		foreach (var change in changes)
			editor.SetAttribute(element, change.Attribute, change.Value);
		return true;
	}

	/// <summary>
	/// Aligns all selected elements against the primary selection's edge: "left"/"center"/
	/// "right" (horizontal) or "top"/"middle"/"bottom" (vertical), landing as margin edits.
	/// </summary>
	public string AlignSelection(string mode)
	{
		var targets = SelectedElementBounds;
		if (targets.Count < 2)
			return "Select at least two elements to align";
		var (primary, _, _, _, _) = targets[0];
		var deltas = new List<(string Name, double DX, double DY)>();
		foreach (var (name, x, y, width, height) in targets)
		{
			if (name == primary)
				continue;
			var dx = mode switch
			{
				"left" => targets[0].X - x,
				"center" => targets[0].X + targets[0].Width / 2 - (x + width / 2),
				"right" => targets[0].X + targets[0].Width - (x + width),
				_ => 0
			};
			var dy = mode switch
			{
				"top" => targets[0].Y - y,
				"middle" => targets[0].Y + targets[0].Height / 2 - (y + height / 2),
				"bottom" => targets[0].Y + targets[0].Height - (y + height),
				_ => 0
			};
			if (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)
				deltas.Add((name, dx, dy));
		}
		if (deltas.Count == 0)
			return "Already aligned";
		ApplyElementDeltas(deltas);
		return $"{mode} aligned {deltas.Count} element(s)";
	}

	/// <summary>
	/// Distributes the selected elements evenly across the selection's bounding box:
	/// "horizontal" spaces centers along X, "vertical" along Y.
	/// </summary>
	public string DistributeSelection(string axis)
	{
		var targets = SelectedElementBounds;
		if (targets.Count < 3)
			return "Select at least three elements to distribute";
		if (axis == "horizontal")
		{
			var ordered = targets.OrderBy(t => t.X + t.Width / 2).ToList();
			var min = ordered[0].X + ordered[0].Width / 2;
			var max = ordered[^1].X + ordered[^1].Width / 2;
			var step = (max - min) / (ordered.Count - 1);
			var deltas = new List<(string, double, double)>();
			for (var i = 1; i < ordered.Count - 1; i++)
			{
				var targetCenter = min + step * i;
				var delta = targetCenter - (ordered[i].X + ordered[i].Width / 2);
				if (Math.Abs(delta) > 0.01)
					deltas.Add((ordered[i].Name, delta, 0));
			}
			if (deltas.Count == 0)
				return "Already distributed";
			ApplyElementDeltas(deltas);
			return $"distributed {deltas.Count} element(s) horizontally";
		}
		if (axis == "vertical")
		{
			var ordered = targets.OrderBy(t => t.Y + t.Height / 2).ToList();
			var min = ordered[0].Y + ordered[0].Height / 2;
			var max = ordered[^1].Y + ordered[^1].Height / 2;
			var step = (max - min) / (ordered.Count - 1);
			var deltas = new List<(string, double, double)>();
			for (var i = 1; i < ordered.Count - 1; i++)
			{
				var targetCenter = min + step * i;
				var delta = targetCenter - (ordered[i].Y + ordered[i].Height / 2);
				if (Math.Abs(delta) > 0.01)
					deltas.Add((ordered[i].Name, 0, delta));
			}
			if (deltas.Count == 0)
				return "Already distributed";
			ApplyElementDeltas(deltas);
			return $"distributed {deltas.Count} element(s) vertically";
		}
		return "Expected horizontal or vertical, got: " + axis;
	}

	/// <summary>Matches the selected elements' size to the primary: "width"/"height"/"both".</summary>
	public string MatchSizeSelection(string mode)
	{
		var targets = SelectedElementBounds;
		if (targets.Count < 2)
			return "Select at least two elements to match sizes";
		var (primary, _, _, pw, ph) = targets[0];
		var changed = 0;
		foreach (var (name, _, _, w, h) in targets)
		{
			if (name == primary)
				continue;
			var element = editor.FindElement(name);
			if (element == null)
				continue;
			if (mode is "width" or "both" && Math.Abs(w - pw) > 0.01)
			{
				editor.SetAttribute(element, "Width", Math.Round(pw).ToString(CultureInfo.InvariantCulture));
				changed++;
			}
			if (mode is "height" or "both" && Math.Abs(h - ph) > 0.01)
			{
				editor.SetAttribute(element, "Height", Math.Round(ph).ToString(CultureInfo.InvariantCulture));
				changed++;
			}
		}
		if (changed > 0)
			ApplyDocumentChange();
		return changed > 0 ? $"matched {mode} of {changed} attribute(s)" : "Already matched";
	}

	/// <summary>Applies positional deltas to several elements as a single source edit (one undo step).</summary>
	void ApplyElementDeltas(List<(string Name, double DX, double DY)> deltas)
	{
		foreach (var (name, dx, dy) in deltas)
		{
			var element = editor.FindElement(name);
			if (element == null)
				continue;
			if (ApplyCanvasPositionDelta(element, dx, dy))
				continue;
			var (left, top, right, bottom) = ParseMargin((string)element.Attribute("Margin"));
			var deltaX = (int)Math.Round(dx);
			var deltaY = (int)Math.Round(dy);
			switch ((string)element.Attribute("HorizontalAlignment"))
			{
				case "Right":
					right -= deltaX;
					break;
				case "Center":
					left += deltaX;
					right -= deltaX;
					break;
				default:
					left += deltaX;
					break;
			}
			switch ((string)element.Attribute("VerticalAlignment"))
			{
				case "Bottom":
					bottom -= deltaY;
					break;
				case "Center":
					top += deltaY;
					bottom -= deltaY;
					break;
				default:
					top += deltaY;
					break;
			}
			if (left != 0 || top != 0 || right != 0 || bottom != 0)
				editor.SetAttribute(element, "Margin", FormatMargin(left, top, right, bottom));
			else
				editor.SetAttribute(element, "Margin", null);
		}
		ApplyDocumentChange();
	}

	static (double Left, double Top, double Right, double Bottom) ParseMargin(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return (0, 0, 0, 0);
		var parts = value.Split(',');
		if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var all))
			return (all, all, all, all);
		if (parts.Length == 2
			&& double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lr)
			&& double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var tb))
			return (lr, tb, lr, tb);
		if (parts.Length == 4
			&& double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l)
			&& double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
			&& double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
			&& double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
			return (l, t, r, b);
		return (0, 0, 0, 0);
	}

	/// <summary>Keeps the source compact: "v", "h v", or "l t r b".</summary>
	static string FormatMargin(double left, double top, double right, double bottom)
	{
		if (left == right && top == bottom)
			return left == top
				? left.ToString(CultureInfo.InvariantCulture)
				: $"{left.ToString(CultureInfo.InvariantCulture)},{top.ToString(CultureInfo.InvariantCulture)}";
		return $"{left.ToString(CultureInfo.InvariantCulture)},{top.ToString(CultureInfo.InvariantCulture)}," +
			$"{right.ToString(CultureInfo.InvariantCulture)},{bottom.ToString(CultureInfo.InvariantCulture)}";
	}

	/// <summary>
	/// Clicking the design surface selects the corresponding *source* element, so a surface pick
	/// and an Outline pick end up in exactly the same state - one selection concept, not two.
	/// </summary>
	void OnElementPickedOnSurface(object sender, string name) => SelectElement(name);

	void OnSelectionChangedOnSurface(object sender, IReadOnlyList<string> names)
	{
		if (names == null || names.Count == 0)
			return;
		// The runtime keeps the primary selection first; sync it to the pad/outline and
		// remember the full set for multi-element actions.
		SelectElement(names[0]);
		shellSelection.Select(names);
		BindPropertySelection(names);
	}

	void BindPropertySelection(IReadOnlyList<string> names)
	{
		var adapters = names.Select(editor.FindElement).Where(element => element != null)
			.Select(element => (object)new WinUIXamlElementPropertyAdapter(element, editor.Document?.Root, SetAttributeThroughEditor, SetEventThroughEditor, EditRichTextBlock, value => editor.TryGetRichTextRuns(value, out _))).ToArray();
		propertyContainer.SelectedObject = adapters.Length > 1 ? new DesignerMultiPropertyAdapter(adapters) : adapters.FirstOrDefault();
	}

	/// <summary>The current multi-selection (primary first), or a single-element list.</summary>
	public IReadOnlyList<string> MultiSelectedNames
		=> shellSelection.SelectedIds.Count > 0
			? shellSelection.SelectedIds
			: SelectedElementName == null ? Array.Empty<string>() : new[] { SelectedElementName };

	/// <summary>Sets the design-surface multi-selection programmatically (primary = first).</summary>
	public void MultiSelect(IReadOnlyList<string> names)
	{
		previewHost.SelectElements(names);
		if (names.Count > 0) {
			SelectElement(names[0]);
			shellSelection.Select(names);
			BindPropertySelection(names);
		} else {
			shellSelection.Select(Array.Empty<string>());
		}
	}

	/// <summary>All selected elements with their design bounds (primary first).</summary>
	public IReadOnlyList<(string Name, double X, double Y, double Width, double Height)> SelectedElementBounds
		=> previewHost.SelectedElementBounds;

	/// <summary>
	/// A real Toolbox drag ends here. It goes through exactly the same insertion path as the
	/// programmatic one, so a drop cannot diverge from what the rest of the designer does.
	/// </summary>
	void OnControlDroppedOnSurface(object sender, (string ControlName, string ContainerName) drop)
	{
		var tool = WinUIXamlToolbox.Instance.FindItem(drop.ControlName);
		if (tool == null)
			return;
		try {
			InsertFromToolbox(tool.Name, drop.ContainerName);
		} catch (Exception exception) {
			status.Text = "Drop failed: " + exception.Message;
		}
	}

	void OnOutlineSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
	{
		var node = (e.NewValue as TreeViewItem)?.Tag as DesignerElementNode;
		if (node == null) {
			propertyContainer.SelectedObject = null;
			SelectedElementName = null;
			previewHost.ClearSelection();
			return;
		}
		var element = ResolveOutlineSourceElement(node);
		if (element == null) {
			propertyContainer.SelectedObject = null;
			SelectedElementName = null;
			previewHost.ClearSelection();
			return;
		}
		var selectionPath = !string.IsNullOrEmpty(node.Path) || string.IsNullOrEmpty(node.Id)
			? node.Path
			: null;
		SelectSourceElement(element, selectionPath);
	}

	protected override void LoadInternal(OpenedFile file, Stream stream)
	{
		using var reader = new StreamReader(stream, leaveOpen: true);
		var text = reader.ReadToEnd();
		// HasRenderedPreview: a runtime whose render failed (it keeps no frame) must still be re-asked on
		// the next activation, which is the only retry it gets.
		var alreadyDesigned = designedOnce && documentError == null && previewHost.HasRenderedPreview
			&& String.Equals(text, editor.Text, StringComparison.Ordinal);
		designedOnce = true;
		loadedText = text;
		wasChangedInDesigner = false;

		// Called again whenever this secondary view is re-activated, which is what keeps the design
		// surface in sync with hand edits. When the document model already holds exactly this text -
		// focus moved between the side-by-side panes, or the Source pane just received this view's own
		// edits - nothing is re-parsed or re-rendered and the selection stays: see
		// doc/technotes/designer-common.md's Design-tab-activation convention. A render of this text
		// still in flight is the runtime's own (versioned) business.
		if (alreadyDesigned)
			return;
		if (!editor.Reset(text, out documentError)) {
			outline.SetRoot(null);
			previewHost.LoadXaml(text);
			status.Text = $"{previewHost.StatusText} Document model error: {documentError}";
			return;
		}

		// A Source -> Design round trip reloads this view even when the text is unchanged, so keep
		// the selection by name (as ReplayHistory does) instead of emptying the Properties pad.
		var previouslySelected = SelectedElementName;
		SelectedElementName = null;
		propertyContainer.SelectedObject = null;
		RebuildOutline();
		previewHost.SetSelectableNames(editor.ElementNames());
		previewHost.LoadXaml(editor.Text);
		status.Text = previewHost.StatusText;
		if (previouslySelected != null)
			SelectElement(previouslySelected);
	}

	protected override void SaveInternal(OpenedFile file, Stream stream)
	{
		using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
		// Pass the loaded text straight back through when this view made no edit, so activating the
		// Design tab can never rewrite or reformat the document on its own.
		writer.Write(wasChangedInDesigner ? editor.Text : loadedText);
		writer.Flush();
	}

	public override void Dispose()
	{
		root.PreviewKeyDown -= OnPreviewKeyDown;
		previewHost.StateChanged -= OnPreviewStateChanged;
		previewHost.ComponentTrayItemSelected -= OnComponentTrayItemSelected;
		previewHost.ElementPicked -= OnElementPickedOnSurface;
		previewHost.ElementPathPicked -= OnElementPathPickedOnSurface;
		previewHost.GridGuideDragCommitted -= OnGridGuideDragCommittedOnSurface;
		previewHost.GridTrackSplitRequested -= OnGridTrackSplitRequestedOnSurface;
		previewHost.ControlDropped -= OnControlDroppedOnSurface;
		outline.SelectedItemChanged -= OnOutlineSelectionChanged;
		propertyContainer.Clear();
		previewHost.Dispose();
		base.Dispose();
	}

	#region Outline

	/// <summary>
	/// Rebuilds the Document Outline from the runtime's element tree when available, falling
	/// back to the source document's element tree (the classic XAML outline, available even
	/// before/without the child runtime). Both are projected onto the protocol's
	/// <see cref="DesignerElementNode"/> model consumed by <see cref="DocumentOutlineControl"/>.
	///
	/// The Outline is the DESIGN view's navigation tree, so it shows only what the user can see
	/// and edit on the design surface: resource definitions are never shown; framework template
	/// parts and other runtime-only nodes (which have no source counterpart) are folded into their
	/// nearest kept ancestor; and elements the source marks not-visible (Visibility=Collapsed or
	/// x:Load=False) are omitted with their subtree. The Code view's outline still shows the whole
	/// document, resources included.
	/// </summary>
	void RebuildOutline()
	{
		var previouslySelected = SelectedElementName;
		var sourceRoot = editor.Document?.Root;
		var runtimeTree = previewHost.ElementTree;
		DesignerElementNode? outlineRoot = null;
		if (runtimeTree != null)
		{
			outlineRoot = CopyOutlineNode(runtimeTree);
			outlineRoot.Children = ProjectOutlineChildren(runtimeTree.Children);
		}
		else if (sourceRoot != null)
		{
			outlineRoot = XmlOutlineNode(sourceRoot);
		}
		shellSelection.UpdateTree(outlineRoot);
		outline.SetRoots(shellSelection.Roots);
		// DocumentOutlineControl.SetRoot clears Items then re-selects the previous id via
		// SelectNodeById - but under LibreWPF, TreeView.SelectedItemChanged for a freshly-added
		// TreeViewItem doesn't always fire before this call returns (its container isn't
		// generated yet), while Items.Clear()'s OWN "nothing selected" SelectedItemChanged fires
		// synchronously. That left a window where OnOutlineSelectionChanged had already nulled
		// SelectedElementName from the clear, but the matching re-selection event was still
		// queued - observed live: editing a property through the Properties pad (which calls
		// this via ApplyDocumentChange) reported selectedName: null immediately afterward even
		// though the edit itself targeted the correct, still-selected element. Restore the field
		// directly rather than trusting that event ordering.
		if (previouslySelected != null && SelectedElementName != previouslySelected && editor.FindElement(previouslySelected) != null)
		{
			SelectedElementName = previouslySelected;
			SelectOutlineNode(previouslySelected);
		}
	}

	/// <summary>Projects runtime children onto the Design-view outline. An element the SOURCE
	/// DOCUMENT declares is kept with its projected subtree; anything else - a framework template
	/// part, a generated item container - is folded away with its kept descendants promoted; a
	/// node that is not visible drops its whole subtree.
	///
	/// "Declares" means <see cref="DesignerElementNode.IsDesignable"/>, not "has an x:Name". Gating
	/// on the name folded away every unnamed element, so a page whose markup names only a handful
	/// of controls showed an outline of just those few - the SettingsCards, StackPanels, Borders
	/// and TextBlocks the user can plainly see on the surface were simply absent. It was only ever
	/// invisible because this projection runs against the RUNTIME tree, and the outline silently
	/// falls back to the full source-XML projection whenever the page fails to render.</summary>
	List<DesignerElementNode> ProjectOutlineChildren(IEnumerable<DesignerElementNode> nodes)
	{
		var result = new List<DesignerElementNode>();
		foreach (var node in nodes)
		{
			if (IsHiddenInSource(node.Name) || !node.IsVisible)
				continue;
			if (!node.IsDesignable)
			{
				result.AddRange(ProjectOutlineChildren(node.Children));
				continue;
			}
			var kept = CopyOutlineNode(node);
			kept.Children = ProjectOutlineChildren(node.Children);
			result.Add(kept);
		}
		return result;
	}

	bool IsNamedInSource(string? name)
		=> !string.IsNullOrEmpty(name) && editor.FindElement(name) != null;

	/// <summary>True when the source element is marked not-visible on the design surface: either
	/// <c>Visibility="Collapsed"</c> (removed from layout, nothing drawn) or <c>x:Load="False"</c>
	/// (not instantiated). A value supplied through a Style/ThemeResource cannot be seen here, so
	/// only an explicit literal on the element counts.</summary>
	bool IsHiddenInSource(string? name)
	{
		if (string.IsNullOrEmpty(name) || editor.FindElement(name) is not { } element)
			return false;
		if (string.Equals((string?)element.Attribute("Visibility"), "Collapsed", StringComparison.OrdinalIgnoreCase))
			return true;
		return string.Equals(
			(string?)element.Attribute(XName.Get("Load", "http://schemas.microsoft.com/winfx/2006/xaml")),
			"False", StringComparison.OrdinalIgnoreCase);
	}

	static DesignerElementNode CopyOutlineNode(DesignerElementNode node)
		=> new() {
			Id = node.Id,
			Name = node.Name,
			Type = node.Type,
			Path = node.Path,
			X = node.X,
			Y = node.Y,
			Width = node.Width,
			Height = node.Height,
			ZIndex = node.ZIndex,
			BaselineOffset = node.BaselineOffset,
			IsDesignable = node.IsDesignable,
			IsVisible = node.IsVisible,
			LayoutMode = node.LayoutMode,
			IsTrayComponent = node.IsTrayComponent
		};

	/// <summary>Projects a source XAML element onto the protocol outline node model. The id is
	/// its x:Name when present, otherwise the document-local sibling-index path used by the
	/// source fallback selection contract. Resource
	/// definitions are not visual elements and elements the source marks not-visible are not on
	/// the design surface, so neither is part of the design outline.</summary>
	DesignerElementNode XmlOutlineNode(XElement element, string layoutMode = DesignerLayoutMode.Unknown)
	{
		var name = (string)element.Attribute(WinUIXamlDocumentEditor.NameDirective);
		return new DesignerElementNode {
			// An unnamed source element cannot use the empty Id: every such node would then be
			// indistinguishable to the Outline controller. Its document-local sibling-index path is
			// stable until a source mutation, and maps back without authoring an artificial x:Name.
			Id = name ?? editor.GetElementPath(element) ?? "",
			Name = name,
			Type = element.Name.LocalName,
			IsDesignable = true,
			LayoutMode = layoutMode,
			Children = element.Elements()
				.Where(child => !IsResourceDefinitionElement(child)
					&& !IsVisualStateElement(child)
					&& !IsHiddenSourceElement(child))
				.Select(child => XmlOutlineNode(child, DesignerLayoutMode.InferFromContainerType(element.Name.LocalName))).ToList()
		};
	}

	static bool IsResourceDefinitionElement(XElement element)
		=> element.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)
			|| element.Name.LocalName == "ResourceDictionary";

	/// <summary>
	/// <c>VisualStateManager.VisualStateGroups</c> and its whole subtree - the groups, their states,
	/// and those states' Setters/StateTriggers.
	///
	/// None of it is in the visual tree: a VisualState has no bounds and can never be selected on
	/// the surface, so listing it in an outline whose entire contract is "click a node, select that
	/// element on the canvas" only produces nodes that cannot honour it. (It shows up here at all
	/// because this XML projection is the fallback used before the first render lands, and it
	/// filtered only resource dictionaries.) The states are offered for PREVIEW through the
	/// toolbar's per-group state combos instead - see winui-designer.md, "Visual state preview".
	/// </summary>
	static bool IsVisualStateElement(XElement element)
		=> element.Name.LocalName.EndsWith(".VisualStateGroups", StringComparison.Ordinal);

	static bool IsHiddenSourceElement(XElement element)
	{
		if (string.Equals((string?)element.Attribute("Visibility"), "Collapsed", StringComparison.OrdinalIgnoreCase))
			return true;
		return string.Equals(
			(string?)element.Attribute(XName.Get("Load", "http://schemas.microsoft.com/winfx/2006/xaml")),
			"False", StringComparison.OrdinalIgnoreCase);
	}

	void SelectOutlineNode(string name)
	{
		outline.SelectNodeById(name);
	}

	#region Outline context menu

	ContextMenu BuildOutlineContextMenu(DesignerElementNode node)
	{
		if (!string.IsNullOrEmpty(node.Name))
			return BuildOutlineContextMenu(node.Name);
		var element = ResolveOutlineSourceElement(node);
		if (element == null)
			return new ContextMenu { IsEnabled = false };
		var menu = new ContextMenu();
		void Add(string header, string command)
		{
			var entry = new MenuItem { Header = header };
			entry.Click += (_, _) => ExecuteSourceOutlineCommand(element, command);
			menu.Items.Add(entry);
		}
		Add("Cut", "cut");
		Add("Copy", "copy");
		Add("Paste Into", "paste");
		Add("Duplicate", "duplicate");
		menu.Items.Add(new Separator());
		Add("Delete", "delete");
		Add("Bring to Front", "bring-to-front");
		Add("Bring Forward", "bring-forward");
		Add("Send Backward", "send-backward");
		Add("Send to Back", "send-to-back");
		menu.Items.Add(new Separator());
		Add("Wrap in Grid", "wrap-grid");
		Add("Wrap in StackPanel", "wrap-stackpanel");
		Add("Unwrap", "unwrap");
		Add("Create Empty Style", "create-empty-style");
		return menu;
	}

	/// <summary>Runs an Outline verb against an unnamed source element. The element was captured
	/// when the row's menu was built; after a source reload its document identity differs, so an
	/// old menu is rejected instead of resolving a recycled sibling-index path.</summary>
	void ExecuteSourceOutlineCommand(XElement element, string command)
	{
		if (element == null || element.Document != editor.Document)
		{
			status.Text = "The source changed; reopen the Outline menu";
			return;
		}
		switch (command)
		{
			case "copy":
				SetClipboardText(element.ToString(SaveOptions.DisableFormatting));
				status.Text = "Copied " + element.Name.LocalName;
				break;
			case "cut":
				if (element == editor.Document?.Root)
				{
					status.Text = "The document root cannot be cut";
					return;
				}
				SetClipboardText(element.ToString(SaveOptions.DisableFormatting));
				ExecuteSourceOutlineCommand(element, "delete");
				break;
			case "duplicate":
				if (element.Parent is not { } parent)
				{
					status.Text = "The root cannot be duplicated";
					return;
				}
				try
				{
					var pasted = editor.Paste(element.ToString(SaveOptions.DisableFormatting), parent, element);
					ApplyDocumentChange();
					SelectElement((string)pasted.Attribute(WinUIXamlDocumentEditor.NameDirective));
				}
				catch (Exception e)
				{
					status.Text = "Duplicate failed: " + e.GetBaseException().Message;
				}
				break;
			case "paste":
				PasteElement(element);
				break;
			case "delete":
				if (element == editor.Document?.Root)
				{
					status.Text = "The document root cannot be deleted";
					return;
				}
				editor.Remove(element);
				SelectedElementName = null;
				propertyContainer.SelectedObject = null;
				previewHost.ClearSelection();
				ApplyDocumentChange();
				break;
			case "bring-to-front":
				MoveToEdge(element, front: true);
				break;
			case "send-to-back":
				MoveToEdge(element, front: false);
				break;
			case "bring-forward":
			case "send-backward":
				if (editor.MoveOneStep(element, command == "bring-forward"))
				{
					ApplyDocumentChange();
					SelectSourceElement(element, path: null);
				}
				break;
			case "wrap-grid":
				WrapElement(element, "Grid");
				break;
			case "wrap-stackpanel":
				WrapElement(element, "StackPanel");
				break;
			case "unwrap":
				UnwrapElement(element);
				break;
			case "create-empty-style":
				CreateEmptyStyle(element);
				break;
		}
	}

	ContextMenu BuildOutlineContextMenu(string name)
	{
		var menu = new ContextMenu();
		void Add(string header, string command)
		{
			var entry = new MenuItem { Header = header };
			entry.Click += (_, _) => OnContextCommandOnSurface(this, (command, name));
			menu.Items.Add(entry);
		}
		Add("Cut", "cut");
		Add("Copy", "copy");
		Add("Paste Into", "paste");
		Add("Duplicate", "duplicate");
		menu.Items.Add(new Separator());
		Add("Delete", "delete");
		Add("Bring to Front", "bring-to-front");
		Add("Bring Forward", "bring-forward");
		Add("Send Backward", "send-backward");
		Add("Send to Back", "send-to-back");
		menu.Items.Add(new Separator());
		Add("Wrap in Grid", "wrap-grid");
		Add("Wrap in StackPanel", "wrap-stackpanel");
		Add("Unwrap", "unwrap");
		Add("Create Empty Style", "create-empty-style");
		return menu;
	}

	#endregion

	#region Outline drag-reorder

	TreeViewItem outlineDragItem;
	Point outlineDragStart;

	void OnOutlineMouseDown(object sender, MouseButtonEventArgs e)
	{
		outlineDragItem = GetOutlineItemAt(e.GetPosition(outline)) ?? outlineDragItem;
		outlineDragStart = e.GetPosition(outline);
	}

	void OnOutlineMouseMove(object sender, MouseEventArgs e)
	{
		if (outlineDragItem == null || e.LeftButton != MouseButtonState.Pressed)
			return;
		var position = e.GetPosition(outline);
		if (Math.Abs(position.X - outlineDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
			&& Math.Abs(position.Y - outlineDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
		{
			return;
		}
		if (outlineDragItem.Tag is not DesignerElementNode draggedNode)
			return;
		outlineDragItem = null;
		// The outline can contain unnamed source elements. Resolve the runtime path back to
		// source instead of manufacturing an x:Name merely to support a structural edit.
		if (ResolveOutlineSourceElement(draggedNode) is { } draggedElement)
			DragDrop.DoDragDrop(outline, draggedElement, DragDropEffects.Move);
	}

	TreeViewItem GetOutlineItemAt(Point point)
	{
		var hit = outline.InputHitTest(point) as DependencyObject;
		while (hit != null && hit is not TreeViewItem)
			hit = VisualTreeHelper.GetParent(hit);
		return hit as TreeViewItem;
	}

	void OnOutlineDragOver(object sender, DragEventArgs e)
	{
		if (e.Data.GetData(typeof(XElement)) is XElement)
			e.Effects = DragDropEffects.Move;
		else
			e.Effects = DragDropEffects.None;
		e.Handled = true;
	}

	void OnOutlineDrop(object sender, DragEventArgs e)
	{
		e.Handled = true;
		if (e.Data.GetData(typeof(XElement)) is not XElement dragged)
			return;
		var targetNode = GetOutlineItemAt(e.GetPosition(outline))?.Tag as DesignerElementNode;
		if (targetNode == null)
			return;
		if (ResolveOutlineSourceElement(targetNode) is { } target)
			MoveElementUnder(dragged, target);
	}

	/// <summary>Resolves an Outline node to its authoritative source element. Named nodes retain
	/// their fast direct lookup; unnamed runtime nodes are mapped through the host's pick chain,
	/// while source-only fallback nodes carry the editor's sibling-index path as their Id.</summary>
	XElement ResolveOutlineSourceElement(DesignerElementNode node)
	{
		if (!string.IsNullOrEmpty(node.Name))
			return editor.FindElement(node.Name);
		if (!string.IsNullOrEmpty(node.Path))
		{
			var chain = previewHost.GetPickChain(node.Path);
			foreach (var (type, typeIndex, path) in chain)
			{
				// Canvas picks may deliberately promote an unresolved template part to a source
				// ancestor. An Outline row instead names one exact node: promoting here would make
				// selection or drag/drop silently act on that parent.
				if (string.Equals(path, node.Path, StringComparison.Ordinal)
					&& FindNthSourceElement(type, typeIndex) is { } source)
					return source;
			}
		}
		// Runtime ids are visual paths. They happen to resemble our XML index paths, but the two
		// coordinate systems diverge around templates/property elements; only a source fallback
		// node (which has no runtime Path) is allowed to use its Id as a document path.
		return string.IsNullOrEmpty(node.Path) ? editor.FindElementAtPath(node.Id) : null;
	}

	/// <summary>Moves an element under another as a source edit (scriptable outline re-parent).</summary>
	public bool ReparentElement(string name, string targetContainer, out string result)
	{
		var element = editor.FindElement(name);
		var target = editor.FindElement(targetContainer);
		if (element == null || target == null)
		{
			result = "Element or target not found";
			return false;
		}
		if (MoveElementUnder(element, target))
		{
			result = $"moved {name} under {targetContainer}";
			return true;
		}
		result = "Cannot move an element under itself, its descendant, or from the document root";
		return false;
	}

	/// <summary>Moves an element under <paramref name="target"/> as a source
	/// edit (re-parenting in the Outline tree), refusing cycles and self-moves.</summary>
	bool MoveElementUnder(XElement element, XElement target)
	{
		if (!editor.Reparent(element, target))
			return false;
		var name = (string)element.Attribute(WinUIXamlDocumentEditor.NameDirective);
		ApplyDocumentChange();
		RebuildOutline();
		if (name != null)
			SelectElement(name);
		return true;
	}

	#endregion

	#endregion
}
