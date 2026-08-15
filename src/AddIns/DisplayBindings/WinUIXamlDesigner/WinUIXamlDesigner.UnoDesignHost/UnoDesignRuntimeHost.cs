using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>
/// <see cref="IWinUIXamlRuntimeHost"/> backed by the out-of-process Uno design host:
/// the child process runs a real Uno runtime, loads the document's XAML with
/// XamlReader, lays it out and renders it to a PNG that is displayed here. All state
/// crossings are JSON over loopback TCP - no WinUI type ever enters this process.
/// </summary>
sealed class UnoDesignRuntimeHost : IWinUIXamlRuntimeHost, IWinUIXamlSelectionOverlay, IWinUIXamlDesignView, IWinUIXamlDirectManipulation, IWinUIXamlTextEditing
{
	readonly UnoDesignSurfaceControl surface = new();
	readonly HashSet<string> selectableNames = new(StringComparer.Ordinal);
	readonly System.Windows.Threading.Dispatcher dispatcher;
	readonly string projectDirectory;
	UnoDesignClient client;
	Task connectTask;
	DesignSnapshot lastSnapshot;
	Dictionary<string, ElementNode> nodesByName = new();
	string lastPickDiagnostic = "no click yet";
	string lastLoadedText;
	int version;
	double? configuredDesignWidth;
	double? configuredDesignHeight;
	bool disposed;

	public UnoDesignRuntimeHost(XamlFrameworkContext framework, string documentFileName)
	{
		// The host may be constructed on the UI thread but fed XAML from a background loader
		// (AbstractViewContentHandlingLoadErrors.LoadInternal), and every async continuation
		// here settles on a thread-pool thread - so capture the UI dispatcher explicitly and
		// marshal all state/surface updates through it.
		dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
		projectDirectory = framework?.ProjectFileName == null
			? null
			: Path.GetDirectoryName(framework.ProjectFileName);
		StatusText = "Starting Uno design host…";
		surface.SurfacePointerPressed += OnSurfacePointerPressed;
		surface.ElementResolver = ResolveNameAt;
		surface.SurfaceElementDragStarted += OnSurfaceElementDragStarted;
		surface.SurfaceElementDragDelta += OnSurfaceElementDragDelta;
		surface.SurfaceElementDragCommitted += OnSurfaceElementDragCommitted;
		surface.SurfaceElementDoubleClicked += OnSurfaceElementDoubleClicked;
		surface.TextEditCommitted += OnSurfaceTextEditCommitted;
		connectTask = ConnectAsync(framework?.Kind.ToString() ?? "unknown");
	}

	#region IWinUIXamlTextEditing

	public event EventHandler<ElementDoubleClickInfo> ElementDoubleClicked;

	void OnSurfaceElementDoubleClicked(object sender, Vector2 point)
	{
		var name = ResolveNameAt(point);
		if (name == null || !nodesByName.TryGetValue(name, out var node))
		{
			ElementDoubleClicked?.Invoke(this, null);
			return;
		}
		ElementDoubleClicked?.Invoke(this, new ElementDoubleClickInfo {
			Name = name,
			X = node.X,
			Y = node.Y,
			Width = node.Width,
			Height = node.Height
		});
	}

	public void BeginTextEdit(double x, double y, double width, double height, string text)
		=> dispatcher.Invoke(() => surface.BeginTextEdit(x, y, width, height, text));

	public event EventHandler<string> TextEditCommitted;

	void OnSurfaceTextEditCommitted(object sender, string text)
		=> TextEditCommitted?.Invoke(this, text);

	#endregion

	async Task ConnectAsync(string kind)
	{
		try
		{
			client = await UnoDesignClient.StartAsync(CancellationToken.None);
			var capabilities = await client.GetCapabilitiesAsync();
			var appNote = await EnsureAppResourcesAsync();
			SetStatus($"Uno design host ready ({capabilities.Runtime} {capabilities.Version}) for {kind}.{appNote}");
		}
		catch (Exception e)
		{
			client?.Dispose();
			client = null;
			SetStatus("Uno design host failed to start: " + e.GetBaseException().Message);
		}
	}

	/// <summary>
	/// Sends the owning project's App.xaml resources to the child so StaticResource and
	/// ThemeResource resolve against the real app. Returns a status suffix describing any
	/// skip reason (or empty when nothing was skipped). If a design already rendered
	/// without resources, it is re-rendered so the resources take effect.
	/// </summary>
	async Task<string> EnsureAppResourcesAsync()
	{
		var appXaml = FindAppXaml();
		if (appXaml == null)
		{
			return "";
		}
		var errors = new List<string>();
		var xaml = AppResourceBuilder.Build(appXaml, errors);
		if (xaml == null)
		{
			return errors.Count == 0 ? "" : " App.xaml skipped: " + string.Join("; ", errors);
		}
		try
		{
			var result = await client.LoadAppResourcesAsync(xaml);
			if (!result.Success)
			{
				return " App.xaml skipped: " + result.Error;
			}
		}
		catch (Exception e)
		{
			return " App.xaml skipped: " + e.GetBaseException().Message;
		}
		var text = Volatile.Read(ref lastLoadedText);
		if (text != null)
		{
			_ = RenderAsync(text, Interlocked.Increment(ref version));
		}
		return "";
	}

	string FindAppXaml()
	{
		if (string.IsNullOrEmpty(projectDirectory))
		{
			return null;
		}
		var candidate = Path.Combine(projectDirectory, "App.xaml");
		return File.Exists(candidate) ? candidate : null;
	}

	void SetStatus(string text)
	{
		if (dispatcher.CheckAccess())
			ApplyStatus(text);
		else
			dispatcher.BeginInvoke(() => ApplyStatus(text));
	}

	void ApplyStatus(string text)
	{
		if (disposed)
			return;
		StatusText = text;
		StateChanged?.Invoke(this, EventArgs.Empty);
	}

	public UIElement WpfSurface => surface;
	public bool HasRenderedPreview { get; private set; }
	public string StatusText { get; private set; }
	public event EventHandler StateChanged;
	public event EventHandler<string> ElementPicked;

	public int ResolvedNameCount => nodesByName.Count;
	public string LastPickDiagnostic => lastPickDiagnostic;

	public void SetSelectableNames(IReadOnlyList<string> names)
	{
		selectableNames.Clear();
		if (names != null)
			foreach (var name in names)
				selectableNames.Add(name);
	}

	public void LoadXaml(string text)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		Volatile.Write(ref lastLoadedText, text);
		if (client == null)
		{
			// Still starting (or failed). Retry once the connect settles; a failed connect
			// already reports via StatusText, so just drop the retry in that case.
			_ = connectTask.ContinueWith(_ => dispatcher.BeginInvoke(() => OnConnectedRetry(text)));
			return;
		}
		_ = RenderAsync(text, Interlocked.Increment(ref version));
	}

	void OnConnectedRetry(string text)
	{
		if (disposed || client == null)
			return;
		SetStatus("Rendering…");
		_ = RenderAsync(text, Interlocked.Increment(ref version));
	}

	async Task RenderAsync(string text, int requested)
	{
		DesignSnapshot snapshot;
		try
		{
			var (width, height) = DesignSize(text);
			var dpi = DisplayDpi();
			snapshot = await client.LoadDesignAsync(text, width, height, dpi);
		}
		catch (Exception e)
		{
			if (disposed || Volatile.Read(ref version) != requested)
				return;
			SetStatus("Uno render failed: " + e.GetBaseException().Message);
			return;
		}
		if (disposed || Volatile.Read(ref version) != requested)
			return;
		if (!dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke(() => ApplySnapshot(snapshot, requested));
			return;
		}
		ApplySnapshot(snapshot, requested);
	}

	/// <summary>
	/// The display scale of the monitor hosting the design surface, measured on the UI
	/// thread. The child renders at this scale so the bitmap is pixel-perfect on Retina
	/// displays; the returned render Dpi is what actually sizes the surface. The
	/// UNO_DESIGN_DPI environment variable overrides the measured scale, to exercise the
	/// dpi-aware render path on a 1x display.
	/// </summary>
	double DisplayDpi()
	{
		if (double.TryParse(Environment.GetEnvironmentVariable("UNO_DESIGN_DPI"), NumberStyles.Float, CultureInfo.InvariantCulture, out var overrideDpi) && overrideDpi > 0)
		{
			return overrideDpi;
		}
		try
		{
			return dispatcher.Invoke(() => Math.Max(1.0, VisualTreeHelper.GetDpi(surface).DpiScaleX));
		}
		catch
		{
			return 1.0;
		}
	}

	void ApplySnapshot(DesignSnapshot snapshot, int requested)
	{
		if (disposed || Volatile.Read(ref version) != requested)
			return;
		lastSnapshot = snapshot;
		nodesByName = IndexTree(snapshot.Tree);
		if (snapshot.Render != null)
		{
			surface.SetRender(snapshot.Render);
			HasRenderedPreview = true;
		}
		StatusText = snapshot.Diagnostics.Count == 0
			? $"Rendered by Uno design host ({FormatSize(snapshot.Render)})."
			: string.Join(Environment.NewLine, snapshot.Diagnostics.Select(d => d.Message));
		StateChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>
	/// Reports the design size in logical units (the bitmap is dpi-scaled pixels), with
	/// the scale appended when it differs from 1x, e.g. "640×480 @ 2x".
	/// </summary>
	static string FormatSize(RenderResult render)
	{
		if (render == null)
			return "no frame";
		var logicalWidth = Math.Round(render.Width / render.Dpi);
		var logicalHeight = Math.Round(render.Height / render.Dpi);
		return render.Dpi > 1.01
			? $"{logicalWidth}×{logicalHeight} @ {render.Dpi:0.##}x"
			: $"{logicalWidth}×{logicalHeight}";
	}

	/// <summary>
	/// The design surface is sized to the document root's own Width/Height when declared;
	/// otherwise to the configured design size; otherwise to a desktop-like 1280x720, so
	/// pages without an explicit size get a meaningful canvas instead of a fixed 640x480.
	/// </summary>
	(double Width, double Height) DesignSize(string text)
	{
		var (documentWidth, documentHeight) = ParseDocumentSize(text);
		var width = documentWidth > 0 ? documentWidth : configuredDesignWidth ?? 1280;
		var height = documentHeight > 0 ? documentHeight : configuredDesignHeight ?? 720;
		return (width, height);
	}

	static (double Width, double Height) ParseDocumentSize(string text)
	{
		try
		{
			var root = XDocument.Parse(text).Root;
			if (root == null)
				return (0, 0);
			var width = ParseDimension((string)root.Attribute("Width"));
			var height = ParseDimension((string)root.Attribute("Height"));
			return (width, height);
		}
		catch
		{
			return (0, 0);
		}
	}

	static double ParseDimension(string value)
	{
		if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result > 0)
			return result;
		return 0;
	}

	/// <summary>
	/// Answers a click with the innermost named element that also exists in the source
	/// document; a hit usually lands on a control-template part that has no x:Name.
	/// </summary>
	public string ResolveNameAt(Vector2 point)
	{
		var design = surface.ToDesignPoint(new Point(point.X, point.Y));
		if (client == null)
		{
			lastPickDiagnostic = "no design host";
			return null;
		}
		try
		{
			var result = client.HitTestAsync(design.X, design.Y).GetAwaiter().GetResult();
			lastPickDiagnostic = $"point={design.X:F0},{design.Y:F0} chain=[{string.Join(",", result.Chain)}]";
			foreach (var name in result.Chain)
			{
				if (selectableNames.Contains(name))
					return name;
			}
			return result.Chain.FirstOrDefault();
		}
		catch (Exception e)
		{
			lastPickDiagnostic = "hit-test failed: " + e.Message;
			return null;
		}
	}

	void OnSurfacePointerPressed(object sender, Vector2 point)
	{
		var name = ResolveNameAt(point);
		if (name != null)
			ElementPicked?.Invoke(this, name);
	}

	string dragName;
	string dragHandle;
	(double X, double Y, double Width, double Height) dragStartRect;
	double dragDeltaX;
	double dragDeltaY;

	void OnSurfaceElementDragStarted(object sender, (string Name, string Handle) info)
	{
		dragName = info.Name;
		dragHandle = info.Handle;
		dragDeltaX = 0;
		dragDeltaY = 0;
		dragStartRect = nodesByName.TryGetValue(info.Name, out var node)
			? (node.X, node.Y, node.Width, node.Height)
			: surface.CurrentSelection;
		// Selecting the dragged element keeps the Properties pad and outline in sync.
		ElementPicked?.Invoke(this, info.Name);
	}

	void OnSurfaceElementDragDelta(object sender, (double DX, double DY) delta)
	{
		if (dragName == null)
			return;
		var scale = surface.ViewportScale;
		dragDeltaX = delta.DX / scale;
		dragDeltaY = delta.DY / scale;
		var rect = ApplyHandle(dragStartRect, dragDeltaX, dragDeltaY);
		surface.ShowSelection(rect.X, rect.Y, rect.Width, rect.Height, dragName);
	}

	void OnSurfaceElementDragCommitted(object sender, (double DX, double DY) delta)
	{
		if (dragName == null)
			return;
		var scale = surface.ViewportScale;
		dragDeltaX = delta.DX / scale;
		dragDeltaY = delta.DY / scale;
		var end = ApplyHandle(dragStartRect, dragDeltaX, dragDeltaY);
		ElementDragCommitted?.Invoke(this, new ElementDragInfo {
			Name = dragName,
			StartX = dragStartRect.X,
			StartY = dragStartRect.Y,
			StartWidth = dragStartRect.Width,
			StartHeight = dragStartRect.Height,
			EndX = end.X,
			EndY = end.Y,
			EndWidth = end.Width,
			EndHeight = end.Height
		});
		dragName = null;
	}

	/// <summary>Applies a move/resize delta to a design rect for the given handle.</summary>
	(double X, double Y, double Width, double Height) ApplyHandle(
		(double X, double Y, double Width, double Height) rect, double dx, double dy)
	{
		switch (dragHandle)
		{
			case "e": return (rect.X, rect.Y, rect.Width + dx, rect.Height);
			case "s": return (rect.X, rect.Y, rect.Width, rect.Height + dy);
			case "se": return (rect.X, rect.Y, rect.Width + dx, rect.Height + dy);
			case "w": return (rect.X + dx, rect.Y, rect.Width - dx, rect.Height);
			case "n": return (rect.X, rect.Y + dy, rect.Width, rect.Height - dy);
			case "nw": return (rect.X + dx, rect.Y + dy, rect.Width - dx, rect.Height - dy);
			case "sw": return (rect.X + dx, rect.Y, rect.Width - dx, rect.Height + dy);
			case "ne": return (rect.X, rect.Y + dy, rect.Width + dx, rect.Height - dy);
			default: return (rect.X + dx, rect.Y + dy, rect.Width, rect.Height);
		}
	}

	/// <summary>Raised with the committed drag: the element and its start/end design rect.</summary>
	public event EventHandler<ElementDragInfo> ElementDragCommitted;

	public (double X, double Y, double Width, double Height)? QueryElementBounds(string name)
	{
		if (string.IsNullOrEmpty(name) || !nodesByName.TryGetValue(name, out var node))
			return null;
		return (node.X, node.Y, node.Width, node.Height);
	}

	/// <summary>Draws the selection outline over the named element's design bounds.</summary>
	public void ShowSelection(string name)
	{
		if (string.IsNullOrEmpty(name) || !nodesByName.TryGetValue(name, out var node))
		{
			surface.ClearSelection();
			return;
		}
		surface.ShowSelection(node.X, node.Y, node.Width, node.Height, name);
	}

	public void ClearSelection() => surface.ClearSelection();

	#region IWinUIXamlDesignView

	public (double Zoom, double PanX, double PanY) GetViewport() => surface.Viewport;

	public double GetViewportScale() => surface.ViewportScale;

	public void SetViewport(double zoom, double panX, double panY)
		=> dispatcher.Invoke(() => surface.SetViewport(zoom, panX, panY));

	public void FitView() => dispatcher.Invoke(() => surface.FitView());

	public (double X, double Y) DesignToSurfacePoint(double x, double y)
	{
		var point = dispatcher.Invoke(() => surface.DesignToSurfacePoint(x, y));
		return (point.X, point.Y);
	}

	public (double Width, double Height)? GetDesignSize()
	{
		if (configuredDesignWidth is null || configuredDesignHeight is null)
			return null;
		return (configuredDesignWidth.Value, configuredDesignHeight.Value);
	}

	public void SetDesignSize(double width, double height)
	{
		configuredDesignWidth = Math.Max(1, width);
		configuredDesignHeight = Math.Max(1, height);
		var text = Volatile.Read(ref lastLoadedText);
		if (text != null)
		{
			_ = RenderAsync(text, Interlocked.Increment(ref version));
		}
	}

	public void ResetDesignSize()
	{
		configuredDesignWidth = null;
		configuredDesignHeight = null;
		var text = Volatile.Read(ref lastLoadedText);
		if (text != null)
		{
			_ = RenderAsync(text, Interlocked.Increment(ref version));
		}
	}

	#endregion

	public string DescribeElementState(string name)
	{
		if (string.IsNullOrEmpty(name) || !nodesByName.TryGetValue(name, out var node))
			return "not found";
		return $"type={node.Type} bounds=({node.X:F0},{node.Y:F0}) {node.Width:F0}x{node.Height:F0} children={node.Children.Count}";
	}

	public string FrameProfile()
	{
		var render = lastSnapshot?.Render;
		return render == null ? "no frame" : $"render {render.Width}x{render.Height} png={render.Data.Length / 1024}KB";
	}

	public string CompositorMetricsDump() => "not applicable (out-of-process Uno host)";
	public string RenderProbeAndProfile() => "not applicable (out-of-process Uno host)";
	public string DumpDrawCalls() => "not applicable (out-of-process Uno host)";
	public string WinUICommandProbe() => "not applicable (out-of-process Uno host)";
	public string ImagePathProbe() => "not applicable (out-of-process Uno host)";
	public void SetShowDiagnosticOverlay(bool value) { }
	public void SetRecreateBitmapEachFrame(bool value) { }
	public void SetPresentViaBackgroundBrush(bool value) { }

	static Dictionary<string, ElementNode> IndexTree(ElementNode node)
	{
		var index = new Dictionary<string, ElementNode>(StringComparer.Ordinal);
		if (node == null)
			return index;
		void Walk(ElementNode current)
		{
			if (current.Name != null)
				index[current.Name] = current;
			foreach (var child in current.Children)
				Walk(child);
		}
		Walk(node);
		return index;
	}

	public void Dispose()
	{
		if (disposed)
			return;
		disposed = true;
		surface.SurfacePointerPressed -= OnSurfacePointerPressed;
		nodesByName.Clear();
		lastSnapshot = null;
		client?.Dispose();
		client = null;
	}
}
