using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
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
			if (runtime is IWinUIXamlDirectManipulation manipulation)
				manipulation.ElementDragCommitted += OnRuntimeElementDragCommitted;
			if (runtime is IWinUIXamlTextEditing textEditing) {
				textEditing.ElementDoubleClicked += OnRuntimeElementDoubleClicked;
				textEditing.TextEditCommitted += OnRuntimeTextEditCommitted;
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

	/// <summary>Raised with a committed design-surface drag (move/resize), if the runtime supports it.</summary>
	public event EventHandler<ElementDragInfo> ElementDragCommitted;

	void OnRuntimeElementDragCommitted(object sender, ElementDragInfo info)
		=> ElementDragCommitted?.Invoke(this, info);

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
		var designPoint = new Point(bounds.Value.X, bounds.Value.Y);
		double scale = 1.0;
		var surfacePoint = designPoint;
		if (runtime is IWinUIXamlDesignView view)
		{
			var translated = view.DesignToSurfacePoint(designPoint.X, designPoint.Y);
			surfacePoint = new Point(translated.X, translated.Y);
			scale = view.GetViewportScale();
		}
		var origin = PointToScreen(surfacePoint);
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

	public void Dispose()
	{
		DragOver -= OnDragOver;
		Drop -= OnDrop;
		if (runtime != null) {
			runtime.StateChanged -= OnRuntimeStateChanged;
			runtime.ElementPicked -= OnRuntimeElementPicked;
			if (runtime is IWinUIXamlDirectManipulation manipulation)
				manipulation.ElementDragCommitted -= OnRuntimeElementDragCommitted;
			if (runtime is IWinUIXamlTextEditing textEditing) {
				textEditing.ElementDoubleClicked -= OnRuntimeElementDoubleClicked;
				textEditing.TextEditCommitted -= OnRuntimeTextEditCommitted;
			}
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
	/// <summary>Raised once an asynchronous <see cref="LoadXaml"/> has settled.</summary>
	event EventHandler StateChanged;
	void LoadXaml(string text);

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
