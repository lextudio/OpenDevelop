using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>WPF-side boundary for an isolated WinUI/Uno designer runtime.</summary>
public sealed class WinUIXamlHost : ContentControl, IDisposable
{
	IWinUIXamlRuntimeHost runtime;

	public WinUIXamlHost(XamlFrameworkContext framework, string documentFileName)
	{
		Framework = framework ?? throw new ArgumentNullException(nameof(framework));
		HorizontalContentAlignment = HorizontalAlignment.Stretch;
		VerticalContentAlignment = VerticalAlignment.Stretch;
		runtime = WinUIXamlRuntimeHostRegistry.Create(framework, documentFileName);
		if (runtime != null) {
			runtime.StateChanged += OnRuntimeStateChanged;
			runtime.ElementPicked += OnRuntimeElementPicked;
			if (runtime is IWinUIXamlMultiSelection multi)
				multi.SelectionChanged += OnRuntimeSelectionChanged;
			if (runtime is IWinUIXamlContextCommands context)
				context.ContextCommandRequested += OnRuntimeContextCommandRequested;
			if (runtime is IWinUIXamlDirectManipulation manipulation) {
				manipulation.ElementDragCommitted += OnRuntimeElementDragCommitted;
				manipulation.ElementGroupDragCommitted += OnRuntimeElementGroupDragCommitted;
				manipulation.GridGuideDragCommitted += OnRuntimeGridGuideDragCommitted;
			}
			if (runtime is IWinUIXamlTextEditing textEditing) {
				textEditing.ElementDoubleClicked += OnRuntimeElementDoubleClicked;
				textEditing.TextEditCommitted += OnRuntimeTextEditCommitted;
			}
			if (runtime is IWinUIXamlPathPick pathPick)
				pathPick.ElementPathPicked += OnRuntimeElementPathPicked;
			if (runtime is IWinUIXamlNudge nudge) {
				nudge.NudgeRequested += OnRuntimeNudgeRequested;
				nudge.UndoRedoRequested += OnRuntimeUndoRedoRequested;
			}
		}
		Content = runtime?.WpfSurface ?? new TextBlock {
			Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, Text = StatusText
		};
		AllowDrop = true;
		DragOver += OnDragOver;
		Drop += OnDrop;
	}

	/// <summary>Raised after an asynchronous render settles, so the view can refresh status.</summary>
	public event EventHandler StateChanged;

	void OnRuntimeStateChanged(object sender, EventArgs e) => StateChanged?.Invoke(this, EventArgs.Empty);

	/// <summary>Raised with the x:Name of the element the user clicked on the design surface.</summary>
	public event EventHandler<string> ElementPicked;

	void OnRuntimeElementPicked(object sender, string name) => ElementPicked?.Invoke(this, name);

	/// <summary>Raised with a design-surface context-menu command and the primary selection.</summary>
	public event EventHandler<(string Command, string Name)> ContextCommandRequested;

	void OnRuntimeContextCommandRequested(object sender, (string Command, string Name) args)
		=> ContextCommandRequested?.Invoke(this, args);

	/// <summary>Raised when the design-surface selection (possibly multiple elements) changes.</summary>
	public event EventHandler<IReadOnlyList<string>> SelectionChanged;

	void OnRuntimeSelectionChanged(object sender, IReadOnlyList<string> names)
		=> SelectionChanged?.Invoke(this, names);

	/// <summary>The selected element names, primary first.</summary>
	public IReadOnlyList<string> SelectedNames => runtime is IWinUIXamlMultiSelection multi
		? multi.SelectedNames
		: Array.Empty<string>();

	/// <summary>Sets the multi-selection programmatically; the first name becomes primary.</summary>
	public void SelectElements(IReadOnlyList<string> names)
	{
		if (runtime is IWinUIXamlMultiSelection multi)
			multi.SelectElements(names);
	}

	/// <summary>Selects a single element, resetting any multi-selection.</summary>
	public void SelectElement(string name)
	{
		if (runtime is IWinUIXamlMultiSelection multi && name != null)
			multi.SelectElements(new[] { name });
	}

	/// <summary>The selected element names in design coordinates (name → bounds), primary first.</summary>
	public IReadOnlyList<(string Name, double X, double Y, double Width, double Height)> SelectedElementBounds
	{
		get
		{
			var result = new List<(string, double, double, double, double)>();
			if (runtime is not IWinUIXamlMultiSelection multi)
				return result;
			foreach (var name in multi.SelectedNames)
			{
				if (QueryElementBounds(name) is { } b)
					result.Add((name, b.X, b.Y, b.Width, b.Height));
			}
			return result;
		}
	}

	/// <summary>Raised with a committed design-surface drag (move/resize), if the runtime supports it.</summary>
	public event EventHandler<ElementDragInfo> ElementDragCommitted;

	void OnRuntimeElementDragCommitted(object sender, ElementDragInfo info)
		=> ElementDragCommitted?.Invoke(this, info);

	/// <summary>Raised when a multi-selection group drag commits, with each element's delta.</summary>
	public event EventHandler<IReadOnlyList<(string Name, double DX, double DY)>> ElementGroupDragCommitted;

	void OnRuntimeElementGroupDragCommitted(object sender, IReadOnlyList<(string Name, double DX, double DY)> deltas)
		=> ElementGroupDragCommitted?.Invoke(this, deltas);

	/// <summary>Raised when a Grid row/column divider drag commits (name, isRow, index, design position).</summary>
	public event EventHandler<(string Name, bool IsRow, int Index, double Position)> GridGuideDragCommitted;

	void OnRuntimeGridGuideDragCommitted(object sender, (string Name, bool IsRow, int Index, double Position) args)
		=> GridGuideDragCommitted?.Invoke(this, args);

	/// <summary>Shows the Grid row/column divider guides (design rect + offsets).</summary>
	public void SetGridGuides(string name, double x, double y, double width, double height, double[] rowOffsets, double[] colOffsets)
	{
		if (runtime is IWinUIXamlGridGuides guides)
			guides.SetGridGuides(name, x, y, width, height, rowOffsets, colOffsets);
	}

	/// <summary>Hides the Grid row/column divider guides.</summary>
	public void ClearGridGuides()
	{
		if (runtime is IWinUIXamlGridGuides guides)
			guides.ClearGridGuides();
	}

	/// <summary>Raised on a design-surface double-click (null = empty space).</summary>
	public event EventHandler<ElementDoubleClickInfo> ElementDoubleClicked;

	void OnRuntimeElementDoubleClicked(object sender, ElementDoubleClickInfo info)
		=> ElementDoubleClicked?.Invoke(this, info);

	/// <summary>Raised with the committed inline-edited text.</summary>
	public event EventHandler<string> TextEditCommitted;

	void OnRuntimeTextEditCommitted(object sender, string text)
		=> TextEditCommitted?.Invoke(this, text);

	/// <summary>Starts inline text editing over the given design rect, if the runtime supports it.</summary>
	public void BeginTextEdit(double x, double y, double width, double height, string text)
	{
		if (runtime is IWinUIXamlTextEditing textEditing)
			textEditing.BeginTextEdit(x, y, width, height, text);
	}

	public void SetSelectableNames(IReadOnlyList<string> names) =>
		runtime?.SetSelectableNames(names);

	/// <summary>
	/// Draws a selection outline over the named element on the design surface. Optional:
	/// runtimes that do not implement <see cref="IWinUIXamlSelectionOverlay"/> ignore it.
	/// </summary>
	public void ShowSelection(string name)
	{
		if (runtime is IWinUIXamlSelectionOverlay overlay)
			overlay.ShowSelection(name);
	}

	public void ClearSelection()
	{
		if (runtime is IWinUIXamlSelectionOverlay overlay)
			overlay.ClearSelection();
	}

	/// <summary>Current design-surface viewport (zoom 1.0 = fit; pan in surface DIPs).</summary>
	public (double Zoom, double PanX, double PanY) GetViewport()
		=> runtime is IWinUIXamlDesignView view ? view.GetViewport() : (1.0, 0, 0);

	public void SetViewport(double zoom, double panX, double panY)
	{
		if (runtime is IWinUIXamlDesignView view)
			view.SetViewport(zoom, panX, panY);
	}

	public void FitView()
	{
		if (runtime is IWinUIXamlDesignView view)
			view.FitView();
	}

	public (double Width, double Height)? GetDesignSize()
		=> runtime is IWinUIXamlDesignView view ? view.GetDesignSize() : null;

	public void SetDesignSize(double width, double height)
	{
		if (runtime is IWinUIXamlDesignView view)
			view.SetDesignSize(width, height);
	}

	public void ResetDesignSize()
	{
		if (runtime is IWinUIXamlDesignView view)
			view.ResetDesignSize();
	}

	/// <summary>The rendered element tree (protocol model), for the Document Outline pad.</summary>
	public DesignerElementNode? ElementTree => (runtime as IWinUIXamlRuntimeHost)?.ElementTree;

	/// <summary>Surface geometry (frame/selection/handle/element) for resize-drag tests.</summary>
	public DesignerSurfaceGeometry SurfaceGeometry()
		=> (runtime as IWinUIXamlRuntimeHost)?.SurfaceGeometry() ?? default;

	/// <summary>The runtime's toolbox catalog (the controls its loaded runtime provides), if available.</summary>
	public IReadOnlyList<DesignerToolboxItemInfo> GetToolboxCatalog()
		=> runtime is IWinUIXamlToolboxCatalog catalog ? catalog.GetToolboxCatalog() : Array.Empty<DesignerToolboxItemInfo>();

	/// <summary>True while the runtime's child process is alive, when the runtime exposes one.</summary>
	public bool IsChildProcessAlive => runtime is IWinUIXamlLifecycleProbe probe && probe.IsChildProcessAlive;

	/// <summary>Raised with the tree path of an unnamed element picked on the design surface.</summary>
	public event EventHandler<string> ElementPathPicked;

	void OnRuntimeElementPathPicked(object sender, string path)
		=> ElementPathPicked?.Invoke(this, path);

	/// <summary>Raised when the user nudges the selection with arrow keys (design units).</summary>
	public event EventHandler<(double DX, double DY)> NudgeRequested;

	void OnRuntimeNudgeRequested(object sender, (double DX, double DY) delta)
		=> NudgeRequested?.Invoke(this, delta);

	/// <summary>Raised when the user presses Ctrl+Z/Ctrl+Y on the surface (undo: true/false).</summary>
	public event EventHandler<bool> UndoRedoRequested;

	void OnRuntimeUndoRedoRequested(object sender, bool undo)
		=> UndoRedoRequested?.Invoke(this, undo);

	/// <summary>The picked element's chain (with same-type indexes), for mapping an unnamed pick to the source.</summary>
	public IReadOnlyList<(string Type, int TypeIndex)> GetPickChain(string path)
		=> runtime is IWinUIXamlPathPick pick ? pick.GetPickChain(path) : Array.Empty<(string, int)>();

	/// <summary>Switches the design's Light/Dark theme, when the runtime supports it.</summary>
	public void SetDesignTheme(string theme)
	{
		if (runtime is IWinUIXamlTheme themed)
			themed.SetDesignTheme(theme);
	}

	/// <summary>Returns "Light" or "Dark" per the current design theme, or null when unsupported.</summary>
	public string GetDesignTheme()
		=> runtime is IWinUIXamlTheme themed ? themed.GetDesignTheme() : null;

	/// <summary>Last lines of the Uno child host's stdout/stderr, for diagnosing render issues.</summary>
	public string ChildLog => runtime.ChildLog;

	/// <summary>The last render's diagnostics (message + source line/column when known).</summary>
	public IReadOnlyList<(string Message, int Line, int Column)> LastDiagnostics => runtime is IWinUIXamlDiagnostics diagnostics
		? diagnostics.LastDiagnostics
		: Array.Empty<(string, int, int)>();

	/// <summary>Exports the current design to a PNG file (via the child host).</summary>
	public string ExportPng(string path) => runtime.ExportPng(path);

	/// <summary>Performance report of the last render.</summary>
	public (double RenderMs, int Width, int Height, double Dpi, int CompressedBytes, int RawBytes) RenderTiming()
		=> runtime.RenderTiming();

	/// <summary>The effective display scale (including any debug simulation).</summary>
	public double EffectiveDisplayDpi => runtime.EffectiveDisplayDpi;

	/// <summary>Sets or clears the simulated display scale (test hook).</summary>
	public void SetSimulatedDpi(double? dpi) => runtime.SetSimulatedDpi(dpi);

	/// <summary>Pixel samples ("WxH center=#RRGGBB ...") of the last rendered frame.</summary>
	public string RenderSample() => runtime.RenderSample();

	/// <summary>Whether the design-space gridlines overlay is shown.</summary>
	public bool Gridlines => runtime.Gridlines;

	/// <summary>Shows or hides the design-space gridlines overlay.</summary>
	public void SetGridlines(bool show) => runtime.SetGridlines(show);

	/// <summary>Whether the tab-order badge overlay is shown.</summary>
	public bool ShowTabOrder => runtime.ShowTabOrder;

	/// <summary>Toggles the tab-order badge overlay - matching the WinForms designer's own
	/// tab-order view.</summary>
	public void SetTabOrderMode(bool show) => runtime.SetTabOrderMode(show);

	public (double X, double Y, double Width, double Height)? QueryElementBounds(string name) =>
		runtime?.QueryElementBounds(name);

	public string DescribeElementState(string name) => runtime?.DescribeElementState(name) ?? "no runtime";

	public string ResolveNameAt(System.Numerics.Vector2 point) => runtime?.ResolveNameAt(point);

	public int ResolvedNameCount => runtime?.ResolvedNameCount ?? 0;
	public string LastPickDiagnostic => runtime?.LastPickDiagnostic ?? "no runtime";

	public string FrameProfile() => runtime?.FrameProfile() ?? "no runtime";

	public string CompositorMetricsDump() => runtime?.CompositorMetricsDump() ?? "no runtime";

	public string RenderProbeAndProfile() => runtime?.RenderProbeAndProfile() ?? "no runtime";

	public string DumpDrawCalls() => runtime?.DumpDrawCalls() ?? "no runtime";

	public string WinUICommandProbe() => runtime?.WinUICommandProbe() ?? "no runtime";
	public string DiagnoseScreenAnchors() => runtime?.DiagnoseScreenAnchors() ?? "no runtime";

	public string ImagePathProbe() => runtime?.ImagePathProbe() ?? "no runtime";

	public void SetShowDiagnosticOverlay(bool value) => runtime?.SetShowDiagnosticOverlay(value);

	public void SetRecreateBitmapEachFrame(bool value) => runtime?.SetRecreateBitmapEachFrame(value);

	public void SetPresentViaBackgroundBrush(bool value) => runtime?.SetPresentViaBackgroundBrush(value);

	/// <summary>Translates surface-local element bounds into screen coordinates for pointer input.</summary>
	public Rect? QueryElementScreenBounds(string name)
	{
		var bounds = QueryElementBounds(name);
		if (bounds == null || !IsVisible)
			return null;
		double scale = 1.0;
		var surfacePoint = new Point(bounds.Value.X, bounds.Value.Y);
		if (runtime is IWinUIXamlDesignView view)
		{
			var translated = view.DesignToScreenPoint(bounds.Value.X, bounds.Value.Y);
			return new Rect(translated.X, translated.Y,
				bounds.Value.Width * view.GetViewportScale(), bounds.Value.Height * view.GetViewportScale());
		}
		var origin = Content is UIElement surface ? surface.PointToScreen(surfacePoint) : PointToScreen(surfacePoint);
		return new Rect(origin.X, origin.Y, bounds.Value.Width * scale, bounds.Value.Height * scale);
	}

	/// <summary>
	/// Raised when a Toolbox item is dropped on the design surface, with the control to create and
	/// the x:Name of the container it was dropped into (null for the root). Resolving the container
	/// here - from the real drop point - is what makes a drag land where the user aimed.
	/// </summary>
	public event EventHandler<(string ControlName, string ContainerName)> ControlDropped;

	void OnDragOver(object sender, DragEventArgs e)
	{
		e.Effects = e.Data.GetDataPresent(WinUIXamlToolbox.DragDataFormat)
			? DragDropEffects.Copy
			: DragDropEffects.None;
		e.Handled = true;
	}

	void OnDrop(object sender, DragEventArgs e)
	{
		if (!e.Data.GetDataPresent(WinUIXamlToolbox.DragDataFormat))
			return;
		var controlName = e.Data.GetData(WinUIXamlToolbox.DragDataFormat) as string;
		if (string.IsNullOrEmpty(controlName))
			return;
		// ResolveNameAt/QueryElementBounds work in the ProGPU render SURFACE's own local
		// coordinate space (control, this ContentControl's Content) - getting the position
		// relative to "this" (the outer WinUIXamlHost wrapper) instead was wrong whenever the
		// wrapper's own bounds/origin differ from its content's, and made every drop resolve to a
		// point nowhere near the actual cursor (confirmed live via LastPickDiagnostic: dropping
		// dead-center on a button's own on-screen bounds reported a hit-test point far outside
		// them), so the container never resolved and every drop silently fell back to the
		// document root instead of the container the user visibly aimed at.
		var point = Content is UIElement surface ? e.GetPosition(surface) : e.GetPosition(this);
		var container = ResolveNameAt(new System.Numerics.Vector2((float)point.X, (float)point.Y));
		ControlDropped?.Invoke(this, (controlName, container));
		e.Handled = true;
	}

	public XamlFrameworkContext Framework { get; }
	public bool HasRenderedPreview => runtime?.HasRenderedPreview == true;
	public string StatusText => runtime?.StatusText ??
		"WinUI/Uno runtime host is not installed. The WPF XamlReader compatibility renderer is disabled.";
	public void LoadXaml(string text) => runtime?.LoadXaml(text ?? string.Empty);

	/// <summary>Applies a single property change to the live render without a full XAML
	/// reparse, when the runtime supports it; otherwise (or on any rejection) falls back to
	/// <paramref name="fallbackXaml"/> through the normal full-document <see cref="LoadXaml"/>
	/// path. Fire-and-forget, matching <see cref="LoadXaml"/>'s own async rendering model -
	/// <c>editor</c> (the source-of-truth/undo buffer) has already been updated by the caller
	/// before this runs; this only affects which render request goes out.</summary>
	public void TrySetProperty(string elementName, string propertyName, string value, string fallbackXaml)
	{
		if (runtime is IWinUIXamlIncrementalRender incremental)
			incremental.TrySetProperty(elementName, propertyName, value, fallbackXaml);
		else
			LoadXaml(fallbackXaml);
	}

	/// <summary>Applies a width/height-only resize to the live render without a full XAML
	/// reparse, when the runtime supports it; otherwise falls back to <paramref name="fallbackXaml"/>.
	/// Only meant for a pure resize (no position change) - see
	/// <see cref="IWinUIXamlIncrementalRender.TrySetBounds"/>.</summary>
	public void TrySetBounds(string elementName, double x, double y, double width, double height, string fallbackXaml)
	{
		if (runtime is IWinUIXamlIncrementalRender incremental)
			incremental.TrySetBounds(elementName, x, y, width, height, fallbackXaml);
		else
			LoadXaml(fallbackXaml);
	}

	/// <summary>Applies an event-handler-name change to the live render without a full XAML
	/// reparse, when the runtime supports it; otherwise falls back to <paramref name="fallbackXaml"/>.</summary>
	public void TrySetEvent(string elementName, string eventName, string handlerName, string fallbackXaml)
	{
		if (runtime is IWinUIXamlIncrementalRender incremental)
			incremental.TrySetEvent(elementName, eventName, handlerName, fallbackXaml);
		else
			LoadXaml(fallbackXaml);
	}

	/// <summary>Adds a new element (already-resolved x:Name baked into <paramref name="itemXaml"/>)
	/// as a child of the named container without a full XAML reparse, when the runtime supports it;
	/// otherwise falls back to <paramref name="fallbackXaml"/>.</summary>
	public void TryAddElement(string containerName, string itemXaml, string fallbackXaml)
	{
		if (runtime is IWinUIXamlIncrementalRender incremental)
			incremental.TryAddElement(containerName, itemXaml, fallbackXaml);
		else
			LoadXaml(fallbackXaml);
	}

	/// <summary>Removes the named elements from the live render without a full XAML reparse, when
	/// the runtime supports it; otherwise falls back to <paramref name="fallbackXaml"/>.</summary>
	public void TryDeleteElements(string[] elementNames, string fallbackXaml)
	{
		if (runtime is IWinUIXamlIncrementalRender incremental)
			incremental.TryDeleteElements(elementNames, fallbackXaml);
		else
			LoadXaml(fallbackXaml);
	}

	/// <summary>Renames an element in the live render without a full XAML reparse, when the
	/// runtime supports it; otherwise falls back to <paramref name="fallbackXaml"/>. Landed as an
	/// unused capability - no call site in this shell renames an already-named element today.</summary>
	public void TryRename(string elementName, string newName, string fallbackXaml)
	{
		if (runtime is IWinUIXamlIncrementalRender incremental)
			incremental.TryRename(elementName, newName, fallbackXaml);
		else
			LoadXaml(fallbackXaml);
	}

	public void Dispose()
	{
		DragOver -= OnDragOver;
		Drop -= OnDrop;
		if (runtime != null) {
			runtime.StateChanged -= OnRuntimeStateChanged;
			runtime.ElementPicked -= OnRuntimeElementPicked;
		if (runtime is IWinUIXamlDirectManipulation manipulation) {
			manipulation.ElementDragCommitted -= OnRuntimeElementDragCommitted;
			manipulation.ElementGroupDragCommitted -= OnRuntimeElementGroupDragCommitted;
		}
			if (runtime is IWinUIXamlTextEditing textEditing) {
				textEditing.ElementDoubleClicked -= OnRuntimeElementDoubleClicked;
				textEditing.TextEditCommitted -= OnRuntimeTextEditCommitted;
			}
			if (runtime is IWinUIXamlPathPick pathPick)
				pathPick.ElementPathPicked -= OnRuntimeElementPathPicked;
		}
		runtime?.Dispose();
		runtime = null;
		Content = null;
	}
}

/// <summary>
/// Implemented by the independent XAML Studio/Uno runtime assembly. Microsoft.UI.Xaml objects
/// never cross this interface; only the WPF hosting surface does.
/// </summary>
public interface IWinUIXamlRuntimeHost : IDisposable
{
	UIElement WpfSurface { get; }
	bool HasRenderedPreview { get; }
	string StatusText { get; }
	/// <summary>Last lines of the child host process's stdout/stderr, when there is one.</summary>
	string ChildLog { get; }
	/// <summary>Pixel samples ("WxH center=#RRGGBB ...") of the last rendered frame.</summary>
	string RenderSample();
	/// <summary>Exports the current design to a PNG file.</summary>
	string ExportPng(string path);
	/// <summary>Performance report of the last render (ms, size, wire bytes).</summary>
	(double RenderMs, int Width, int Height, double Dpi, int CompressedBytes, int RawBytes) RenderTiming();
	/// <summary>The effective display scale (including any debug simulation).</summary>
	double EffectiveDisplayDpi { get; }
	/// <summary>Sets or clears the simulated display scale (test hook).</summary>
	void SetSimulatedDpi(double? dpi);
	/// <summary>Whether the design-space gridlines overlay is shown.</summary>
	bool Gridlines { get; }
	/// <summary>Shows or hides the design-space gridlines overlay.</summary>
	void SetGridlines(bool show);
	/// <summary>Whether the tab-order badge overlay is shown.</summary>
	bool ShowTabOrder { get; }
	/// <summary>Toggles the tab-order badge overlay.</summary>
	void SetTabOrderMode(bool show);
	/// <summary>Raised once an asynchronous <see cref="LoadXaml"/> has settled.</summary>
	event EventHandler StateChanged;
	void LoadXaml(string text);

	/// <summary>
	/// The rendered element tree (protocol model), for the shared Document Outline pad.
	/// Null until the runtime has produced a design snapshot.
	/// </summary>
	DesignerElementNode? ElementTree { get; }

	/// <summary>Surface geometry (frame/selection/handle/element) for resize-drag tests.</summary>
	DesignerSurfaceGeometry SurfaceGeometry();

	/// <summary>
	/// The x:Names the document defines. The runtime resolves them against the rendered tree's
	/// namescope so a click can be reported back as a name.
	/// </summary>
	void SetSelectableNames(IReadOnlyList<string> names);

	/// <summary>Surface-local bounds of a rendered element, as plain numbers.</summary>
	(double X, double Y, double Width, double Height)? QueryElementBounds(string name);

	/// <summary>Diagnostic-only dump of a named element's style/template/box-model state.</summary>
	string DescribeElementState(string name);

	/// <summary>Diagnostics for why a design-surface click did or did not resolve to an element.</summary>
	int ResolvedNameCount { get; }
	string LastPickDiagnostic { get; }

	/// <summary>Temporary diagnostic: row profile of the presented frame's non-white pixels.</summary>
	string FrameProfile();

	/// <summary>Temporary diagnostic: ProGPU compositor metrics for the last offscreen render.</summary>
	string CompositorMetricsDump();

	/// <summary>Temporary diagnostic: render a hand-built ProGPU rounded rect and report the frame.</summary>
	string RenderProbeAndProfile();

	/// <summary>Temporary diagnostic: dump the compositor's compiled draw calls via reflection.</summary>
	string DumpDrawCalls();

	/// <summary>Temporary diagnostic: call the WinUI root's OnRender and report the commands it emits.</summary>
	string WinUICommandProbe();

	/// <summary>Temporary diagnostic: replay LibreWPF's image adapter path.</summary>
	string ImagePathProbe();

	/// <summary>Temporary diagnostic: screen origins of every candidate PointToScreen anchor, to
	/// measure which lines up with the verified-correct surface-geometry frame origin.</summary>
	string DiagnoseScreenAnchors();

	/// <summary>Temporary diagnostic: toggle the red OnRender overlay.</summary>
	void SetShowDiagnosticOverlay(bool value);

	/// <summary>Temporary diagnostic: recreate the bitmap each frame.</summary>
	void SetRecreateBitmapEachFrame(bool value);

	/// <summary>Temporary diagnostic: present via Background brush.</summary>
	void SetPresentViaBackgroundBrush(bool value);

	/// <summary>x:Name of the nearest source-backed element at a surface-local point, or null.</summary>
	string ResolveNameAt(System.Numerics.Vector2 point);

	/// <summary>
	/// Raised when the user clicks an element on the design surface, carrying that element's
	/// x:Name. Only the name crosses this boundary - never a <c>Microsoft.UI.Xaml</c> object -
	/// so the shell side keeps resolving selection against the XAML source document.
	/// </summary>
	event EventHandler<string> ElementPicked;
}

/// <summary>
/// Optional capability: the runtime host switches the design's Light/Dark theme and re-renders.
/// </summary>
public interface IWinUIXamlTheme
{
	void SetDesignTheme(string theme);
	string GetDesignTheme();
}

/// <summary>
/// Optional capability: the design surface forwards arrow-key nudge requests (design units)
/// and undo/redo shortcut presses (Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z).
/// </summary>
public interface IWinUIXamlNudge
{
	event EventHandler<(double DX, double DY)> NudgeRequested;
	event EventHandler<bool> UndoRedoRequested;
}

/// <summary>
/// Optional capability: the design surface offers a context menu (copy/paste/delete,
/// z-order, wrap-in-container) whose commands are forwarded to the shell for source edits.
/// </summary>
public interface IWinUIXamlContextCommands
{
	event EventHandler<(string Command, string Name)> ContextCommandRequested;
}

/// <summary>
/// Optional capability: the runtime shows draggable row/column divider guides for a
/// selected Grid, and reports divider drags for source edits.
/// </summary>
public interface IWinUIXamlGridGuides
{
	void SetGridGuides(string name, double x, double y, double width, double height, double[] rowOffsets, double[] colOffsets);
	void ClearGridGuides();
}

/// <summary>
/// Optional capability: the runtime reports its last render diagnostics (message plus
/// source line/column when the XAML parser provided them).
/// </summary>
public interface IWinUIXamlDiagnostics
{
	IReadOnlyList<(string Message, int Line, int Column)> LastDiagnostics { get; }
}

/// <summary>
/// Optional capability: the runtime tracks a design-surface selection that may span
/// multiple elements (primary first), enabling align/distribute actions.
/// </summary>
public interface IWinUIXamlMultiSelection
{
	IReadOnlyList<string> SelectedNames { get; }
	event EventHandler<IReadOnlyList<string>> SelectionChanged;
	void SelectElements(IReadOnlyList<string> names);
}

/// <summary>
/// Optional capability: the runtime host maps a pick on an unnamed element back to its tree
/// path, so the shell can auto-name the element and select it (making the Properties pad work
/// for controls that lack an x:Name).
/// </summary>
public interface IWinUIXamlPathPick
{
	/// <summary>Raised with the tree path of an unnamed element under the pick point.</summary>
	event EventHandler<string> ElementPathPicked;
	/// <summary>The element at the given tree path, plus each ancestor, with each node's index
	/// among same-type nodes in tree order (root first).</summary>
	IReadOnlyList<(string Type, int TypeIndex)> GetPickChain(string path);
}

/// <summary>
/// Optional capability: the runtime host reports its child-process lifecycle, so the
/// runtime-stats probe can assert that closing the document actually releases the host.
/// </summary>
public interface IWinUIXamlLifecycleProbe
{
	bool IsChildProcessAlive { get; }
}

/// <summary>
/// Optional capability: the runtime host reports its toolbox catalog (the controls the loaded
/// runtime actually provides), so the shared Toolbox pad can match the project's real controls.
/// </summary>
public interface IWinUIXamlToolboxCatalog
{
	IReadOnlyList<DesignerToolboxItemInfo> GetToolboxCatalog();
}

/// <summary>
/// Optional capability: the runtime host exposes its design surface viewport (pan/zoom)
/// and the configurable design canvas size. Declined by runtimes without viewport
/// support (e.g. ProGPU), which simply keeps the current behavior.
/// </summary>
public interface IWinUIXamlDesignView
{
	(double Zoom, double PanX, double PanY) GetViewport();
	double GetViewportScale();
	void SetViewport(double zoom, double panX, double panY);
	void FitView();
	/// <summary>Design-space point to surface-local DIPs, honoring the viewport.</summary>
	(double X, double Y) DesignToSurfacePoint(double x, double y);
	/// <summary>A DESIGN-space point (the same space <c>QueryElementBounds</c>/<c>nodesByName</c>
	/// report element positions in) to real screen coordinates, for driving synthetic pointer
	/// input at a named element - honors the current viewport/zoom/scroll via
	/// <see cref="DesignToSurfacePoint"/>, then <c>PointToScreen</c> on the scroll viewport (NOT
	/// the surface control itself, which sits above it by the shared toolbar's height).</summary>
	(double X, double Y) DesignToScreenPoint(double x, double y);
	(double Width, double Height)? GetDesignSize();
	void SetDesignSize(double width, double height);
	void ResetDesignSize();
}

/// <summary>
/// A committed design-surface drag: the named element and its start/end rects in design
/// coordinates. The shell turns the rects into source edits (Margin/Width/Height).
/// </summary>
public sealed class ElementDragInfo
{
	public string Name { get; set; }
	public double StartX { get; set; }
	public double StartY { get; set; }
	public double StartWidth { get; set; }
	public double StartHeight { get; set; }
	public double EndX { get; set; }
	public double EndY { get; set; }
	public double EndWidth { get; set; }
	public double EndHeight { get; set; }
}

/// <summary>
/// Optional capability: the runtime host reports design-surface drags (move/resize) so the
/// shell can turn them into source edits. Declined by runtimes without direct manipulation.
/// </summary>
public interface IWinUIXamlDirectManipulation
{
	event EventHandler<ElementDragInfo> ElementDragCommitted;
	/// <summary>Raised when a multi-selection group drag commits, with each element's delta.</summary>
	event EventHandler<IReadOnlyList<(string Name, double DX, double DY)>> ElementGroupDragCommitted;
	/// <summary>Raised when a Grid row/column divider drag commits (name, isRow, index, design position).</summary>
	event EventHandler<(string Name, bool IsRow, int Index, double Position)> GridGuideDragCommitted;
}

/// <summary>
/// Optional: a runtime that can refresh its render from a single discrete edit (DDP's
/// design/set-property / design/set-bounds) instead of a full document reparse. The source
/// document (the caller's undo/save buffer) has already been updated before either method is
/// called - these only choose which render request the runtime sends. Both are fire-and-forget
/// (matching <see cref="IWinUIXamlRuntimeHost.LoadXaml"/>'s own async model): on any rejection
/// or exception the implementation must fall back to a full <c>LoadXaml(fallbackXaml)</c> itself,
/// so the caller never needs to know which path was actually taken.
/// </summary>
public interface IWinUIXamlIncrementalRender
{
	void TrySetProperty(string elementName, string propertyName, string value, string fallbackXaml);
	/// <summary>Only meant for a pure resize (no position change) - most panels here position
	/// children through Margin, which this does not touch; only Width/Height apply generally,
	/// with Canvas.Left/Top applying too when the parent happens to be a Canvas.</summary>
	void TrySetBounds(string elementName, double x, double y, double width, double height, string fallbackXaml);
	/// <summary>DDP design/set-event: validates the element/event names against the live tree and
	/// re-renders. No live code-behind instance exists in this design host, so no handler is
	/// actually invoked - this only keeps the incremental render path in sync.</summary>
	void TrySetEvent(string elementName, string eventName, string handlerName, string fallbackXaml);
	/// <summary>DDP design/add-element: parses <paramref name="itemXaml"/> (the exact markup the
	/// caller's own editor already produced, x:Name included) and appends it as a child of the
	/// named parent Panel.</summary>
	void TryAddElement(string containerName, string itemXaml, string fallbackXaml);
	/// <summary>DDP design/delete-elements: removes each named element from its Panel parent.</summary>
	void TryDeleteElements(string[] elementNames, string fallbackXaml);
	/// <summary>DDP design/rename: renames the live element. Landed as an unused capability - no
	/// call site in this shell renames an already-named element today.</summary>
	void TryRename(string elementName, string newName, string fallbackXaml);
}

/// <summary>
/// A double-click on a design element: its name and design rect. A null value means the
/// double-click hit empty space.
/// </summary>
public sealed class ElementDoubleClickInfo
{
	public string Name { get; set; }
	public double X { get; set; }
	public double Y { get; set; }
	public double Width { get; set; }
	public double Height { get; set; }
}

/// <summary>
/// Optional capability: the runtime host reports design-surface double-clicks and supports
/// inline text editing over the design (the shell decides editability and applies the
/// committed text as a source edit).
/// </summary>
public interface IWinUIXamlTextEditing
{
	event EventHandler<ElementDoubleClickInfo> ElementDoubleClicked;
	void BeginTextEdit(double x, double y, double width, double height, string text);
	event EventHandler<string> TextEditCommitted;
}

/// <summary>
/// Optional capability: the runtime host draws a selection outline over a named element
/// on its design surface. Declined by runtimes without an overlay (e.g. ProGPU), which
/// simply keeps the current behavior.
/// </summary>
public interface IWinUIXamlSelectionOverlay
{
	void ShowSelection(string name);
	void ClearSelection();
}

public static class WinUIXamlRuntimeHostRegistry
{
	static readonly List<Func<XamlFrameworkContext, string, IWinUIXamlRuntimeHost>> factories = new();

	/// <summary>
	/// Registers a runtime host factory. The most recently registered factory is tried first;
	/// a factory that declines (returns null - e.g. the out-of-process Uno host when its child
	/// binary is missing) falls through to the previous one, so ProGPU remains the safety net.
	/// </summary>
	public static void Register(Func<XamlFrameworkContext, string, IWinUIXamlRuntimeHost> runtimeFactory)
	{
		factories.Add(runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory)));
	}

	internal static IWinUIXamlRuntimeHost Create(XamlFrameworkContext framework, string documentFileName)
	{
		for (var i = factories.Count - 1; i >= 0; i--) {
			if (factories[i].Invoke(framework, documentFileName) is { } host)
				return host;
		}
		return null;
	}
}
