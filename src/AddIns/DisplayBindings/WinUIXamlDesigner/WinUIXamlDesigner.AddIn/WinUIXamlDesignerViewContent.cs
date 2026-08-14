using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.WinUIXamlDesigner;

public sealed class WinUIXamlDesignerViewContent : AbstractViewContentHandlingLoadErrors,
	IOutlineContentHost, IToolsHost, IHasPropertyContainer
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
	readonly TreeView outline = new();
	readonly PropertyContainer propertyContainer = new();
	readonly WinUIXamlDocumentEditor editor = new();
	string documentError;
	string loadedText = "";
	// Mirrors WpfViewContent.wasChangedInDesigner. Without it, saving while the Design tab happens
	// to be the active view rewrites the file from this view's serialized document - discarding
	// whatever the Source view did, and reformatting a file the designer never touched.
	bool wasChangedInDesigner;

	public WinUIXamlDesignerViewContent(OpenedFile file, XamlFrameworkContext framework) : base(file)
	{
		Framework = framework;
		previewHost = new WinUIXamlHost(framework, file?.FileName?.ToString() ?? "Preview.xaml");
		previewHost.StateChanged += OnPreviewStateChanged;
		previewHost.ElementPicked += OnElementPickedOnSurface;
		previewHost.ControlDropped += OnControlDroppedOnSurface;
		TabPageText = "Design";
		root.RowDefinitions.Add(new RowDefinition());
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		Grid.SetRow(previewHost, 0);
		Grid.SetRow(status, 1);
		root.Children.Add(previewHost);
		root.Children.Add(status);
		status.Text = previewHost.StatusText;
		outline.SelectedItemChanged += OnOutlineSelectionChanged;
		UserContent = root;
	}

	public XamlFrameworkContext Framework { get; }
	public string StatusText => status.Text;
	public bool HasRenderedPreview => previewHost.HasRenderedPreview;
	public object OutlineContent => outline;

	/// <summary>WinUI/Uno shares the shell's Toolbox pad rather than hosting ProGPU's own chrome.</summary>
	public object ToolsContent => WinUIXamlToolbox.Instance.ToolboxControl;

	/// <summary>
	/// Feeds the shell's Properties pad. The adapter is backed by the XAML source element, not by
	/// the live ProGPU visual, so every property change lands as a source edit.
	/// </summary>
	public PropertyContainer PropertyContainer => propertyContainer;

	public string SelectedElementName { get; private set; }
	public bool CanUndo => editor.CanUndo;
	public bool CanRedo => editor.CanRedo;
	public string DocumentError => documentError;
	public IReadOnlyList<string> ElementNames() => editor.ElementNames();

	public Rect? QueryElementScreenBounds(string name) => previewHost.QueryElementScreenBounds(name);
	public string DescribeElementState(string name) => previewHost.DescribeElementState(name);
	public int ResolvedNameCount => previewHost.ResolvedNameCount;
	public string LastPickDiagnostic => previewHost.LastPickDiagnostic;

	public string FrameProfile() => previewHost.FrameProfile();

	public string CompositorMetricsDump() => previewHost.CompositorMetricsDump();

	public string RenderProbeAndProfile() => previewHost.RenderProbeAndProfile();

	public string DumpDrawCalls() => previewHost.DumpDrawCalls();

	public string WinUICommandProbe() => previewHost.WinUICommandProbe();

	public string ImagePathProbe() => previewHost.ImagePathProbe();

	public void SetShowDiagnosticOverlay(bool value) => previewHost.SetShowDiagnosticOverlay(value);

	public void SetRecreateBitmapEachFrame(bool value) => previewHost.SetRecreateBitmapEachFrame(value);

	public void SetPresentViaBackgroundBrush(bool value) => previewHost.SetPresentViaBackgroundBrush(value);

	public int OutlineChildCount =>
		outline.Items.Count == 0 ? 0 : ((TreeViewItem)outline.Items[0]).Items.Count;

	#region Editing operations

	public bool SelectElement(string name)
	{
		var element = editor.FindElement(name);
		if (element == null) {
			SelectedElementName = null;
			propertyContainer.SelectedObject = null;
			return false;
		}
		SelectedElementName = name;
		propertyContainer.SelectedObject = new WinUIXamlElementPropertyAdapter(element, SetAttributeThroughEditor);
		SelectOutlineNode(name);
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
		// A drop resolves to the nearest NAMED source element under the cursor
		// (WinUIXamlHost.OnDrop -> ProGpuRuntimeHost.ResolveNameAt), which is very often a leaf
		// control - dropping onto PrimaryButton previously tried to insert a second child directly
		// into it, which the WinUI compiler rejects outright ("Member '$content' cannot contain
		// multiple values"). Walk up to the nearest actual panel so the drop lands as a sibling
		// near where the user aimed, matching what a real design surface does.
		while (container != null && !PanelElementNames.Contains(container.Name.LocalName))
			container = container.Parent;
		var inserted = editor.Insert(controlName, container);
		ApplyDocumentChange();
		var name = (string)inserted.Attribute(WinUIXamlDocumentEditor.NameDirective);
		SelectElement(name);
		return name;
	}

	public void DeleteSelected()
	{
		var element = editor.FindElement(SelectedElementName)
			?? throw new InvalidOperationException("Nothing is selected.");
		editor.Remove(element);
		SelectedElementName = null;
		propertyContainer.SelectedObject = null;
		ApplyDocumentChange();
	}

	public bool Undo() => ReplayHistory(editor.Undo());
	public bool Redo() => ReplayHistory(editor.Redo());

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

	/// <summary>
	/// The single point where an edit becomes visible: mark the file dirty so the shared
	/// OpenedFile machinery writes it (and the Source view picks it up on the next view switch),
	/// rebuild the outline from the new document, and re-render.
	/// </summary>
	void ApplyDocumentChange()
	{
		wasChangedInDesigner = true;
		PrimaryFile?.MakeDirty();
		RebuildOutline();
		previewHost.SetSelectableNames(editor.ElementNames());
		previewHost.LoadXaml(editor.Text);
		status.Text = previewHost.StatusText;
	}

	#endregion

	void OnPreviewStateChanged(object sender, EventArgs e) => status.Text = previewHost.StatusText;

	/// <summary>
	/// Clicking the design surface selects the corresponding *source* element, so a surface pick
	/// and an Outline pick end up in exactly the same state - one selection concept, not two.
	/// </summary>
	void OnElementPickedOnSurface(object sender, string name) => SelectElement(name);

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
		if ((e.NewValue as TreeViewItem)?.Tag is not XElement element) {
			propertyContainer.SelectedObject = null;
			SelectedElementName = null;
			return;
		}
		SelectedElementName = (string)element.Attribute(WinUIXamlDocumentEditor.NameDirective);
		propertyContainer.SelectedObject = new WinUIXamlElementPropertyAdapter(element, SetAttributeThroughEditor);
	}

	protected override void LoadInternal(OpenedFile file, Stream stream)
	{
		using var reader = new StreamReader(stream, leaveOpen: true);
		var text = reader.ReadToEnd();
		loadedText = text;
		wasChangedInDesigner = false;

		// Called again whenever this secondary view is re-activated after the Source view changed
		// the file, which is what keeps the design surface in sync with hand edits.
		if (!editor.Reset(text, out documentError)) {
			outline.ItemsSource = null;
			previewHost.LoadXaml(text);
			status.Text = $"{previewHost.StatusText} Document model error: {documentError}";
			return;
		}

		SelectedElementName = null;
		propertyContainer.SelectedObject = null;
		RebuildOutline();
		previewHost.SetSelectableNames(editor.ElementNames());
		previewHost.LoadXaml(editor.Text);
		status.Text = previewHost.StatusText;
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
		previewHost.StateChanged -= OnPreviewStateChanged;
		previewHost.ElementPicked -= OnElementPickedOnSurface;
		previewHost.ControlDropped -= OnControlDroppedOnSurface;
		outline.SelectedItemChanged -= OnOutlineSelectionChanged;
		propertyContainer.Clear();
		previewHost.Dispose();
		base.Dispose();
	}

	#region Outline

	void RebuildOutline()
	{
		outline.ItemsSource = editor.Document?.Root == null
			? null
			: new[] { CreateOutline(editor.Document.Root) };
	}

	void SelectOutlineNode(string name)
	{
		if (outline.Items.Count == 0)
			return;
		var match = EnumerateOutline((TreeViewItem)outline.Items[0])
			.FirstOrDefault(item => item.Tag is XElement element
				&& string.Equals((string)element.Attribute(WinUIXamlDocumentEditor.NameDirective), name, StringComparison.Ordinal));
		if (match != null)
			match.IsSelected = true;
	}

	static IEnumerable<TreeViewItem> EnumerateOutline(TreeViewItem item)
	{
		yield return item;
		foreach (var child in item.Items.OfType<TreeViewItem>())
			foreach (var descendant in EnumerateOutline(child))
				yield return descendant;
	}

	static TreeViewItem CreateOutline(XElement element)
	{
		var name = (string)element.Attribute(WinUIXamlDocumentEditor.NameDirective);
		var item = new TreeViewItem {
			Header = name == null ? element.Name.LocalName : element.Name.LocalName + " (" + name + ")",
			Tag = element,
			IsExpanded = true
		};
		foreach (var child in element.Elements())
			item.Items.Add(CreateOutline(child));
		return item;
	}

	#endregion
}
