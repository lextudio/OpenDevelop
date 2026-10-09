using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Surface;
using ICSharpCode.SharpDevelop.Widgets;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;
using XamlStudio.Toolkit.Services;

namespace ICSharpCode.WinUIXamlDesigner.ProGPUHost;

public static class ProGpuRuntimeHostBootstrap
{
    public static void Register() => WinUIXamlRuntimeHostRegistry.Register(Create);

    /// <summary>
    /// Lifecycle probes for the technote's "unloading a document releases its runtime" acceptance
    /// item. Closing a designer must drop its host and let the collectible preview assembly - and
    /// therefore the WinUI tree built from it - be collected; a leak here would accumulate a whole
    /// preview ALC per document open, which nothing else in the suite would notice.
    /// </summary>
    public static int LiveHostCount => ProGpuRuntimeHost.LiveHostCount;

    public static bool LastPreviewRootAlive()
    {
        for (var attempt = 0; attempt < 3; attempt++) {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return ProGpuRuntimeHost.LastPreviewRootAlive;
    }

    static IWinUIXamlRuntimeHost Create(XamlFrameworkContext framework, string documentFileName) =>
        // A ProGPU WinUI project is designed by ProGPU and nothing else; an Uno project falls back to
        // ProGPU only when the Uno child host is unavailable (or OD_WINUI_RUNTIME=progpu asks for it).
        framework?.Runtime is XamlRuntimeKind.ProGpuWinUI or XamlRuntimeKind.Uno ? new ProGpuRuntimeHost(framework, documentFileName) : null;
}

/// <summary>
/// The ProGPU WinUI backend of the shared design canvas. ProGPU renders in process: this class
/// compiles and runs the document with ProGPU's own Microsoft.UI.Xaml implementation, renders the
/// tree offscreen to a frame and builds its design tree, and hands both to the canvas
/// (<see cref="DesignSurfaceController"/>) - the same state an out-of-process host sends over DDP.
/// So a ProGPU page gets exactly the canvas every other designer has: toolbar, zoom and Fit, design
/// sizes, theme, gridlines, selection with handles, multi-select, drag and resize, inline text,
/// tab order. The shared canvas derives hits from the same published design tree.
/// </summary>
sealed class ProGpuRuntimeHost : IWinUIXamlRuntimeHost, IWinUIXamlSelectionOverlay,
	IWinUIXamlDesignView, IWinUIXamlDirectManipulation, IWinUIXamlTextEditing, IWinUIXamlLifecycleProbe,
	IWinUIXamlPathPick, IWinUIXamlTheme, IWinUIXamlMultiSelection, IWinUIXamlContextCommands,
	IWinUIXamlGridGuides, IWinUIXamlNudge
{
    readonly DesignSurface surface = new();
    readonly DesignSurfaceController canvas;
    readonly ProGpuOffscreenRenderer renderer = new();
    readonly ProGpuXamlExecutor executor;
    readonly XamlRenderService renderService;
    readonly XamlFrameworkContext framework;
    // x:Names from the source document, resolved against the rendered tree's namescope after each
    // render so the design tree and hit tests answer with names the shell understands.
    IReadOnlyList<string> selectableNames = Array.Empty<string>();
    readonly Dictionary<Microsoft.UI.Xaml.FrameworkElement, string> namesByElement = new();
    Microsoft.UI.Xaml.FrameworkElement root;
    ProGpuDesignTree designTree;
    // Guards against an earlier, slower render overwriting the result of a later edit.
    int version;
    long frameSequence;
    double lastRenderMs;
    double lastRenderDpi;
    double? simulatedDpi;
    double? configuredDesignWidth;
    double? configuredDesignHeight;
    string theme = "Light";
    bool disposed;

    static int liveHostCount;
    static WeakReference lastPreviewRoot = new(null);
    internal static int LiveHostCount => Volatile.Read(ref liveHostCount);
    internal static bool LastPreviewRootAlive => lastPreviewRoot.IsAlive;

    public ProGpuRuntimeHost(XamlFrameworkContext framework, string documentFileName)
    {
        this.framework = framework;
        Interlocked.Increment(ref liveHostCount);
        executor = new ProGpuXamlExecutor(documentFileName);
        renderService = new XamlRenderService(executor);
        StatusText = "ProGPU WinUI host ready.";

        surface.BackendName = "ProGPU";
        // No visual-state or component-tray support in this backend yet: show only what it does.
        surface.Capabilities = DesignerCanvasCapabilities.All & ~DesignerCanvasCapabilities.VisualStates & ~DesignerCanvasCapabilities.ComponentTray;
        surface.SetDesignThemes(new[] { "Light", "Dark" });
        surface.DesignThemeRequested += (_, requested) => SetDesignTheme(requested);
        surface.SizePresetRequested += (_, preset) => {
            var (width, height) = preset switch {
                "phone" => (390.0, 844.0),
                "tablet" => (768.0, 1024.0),
                "desktop" => (1280.0, 720.0),
                _ => (0.0, 0.0)
            };
            if (width > 0 && height > 0)
                SetDesignSize(width, height);
        };

        // The first render usually happens before the surface is in a window, where the display
        // scale still reads 1: render again once it is, at the real scale.
        surface.Loaded += (_, _) => { if (Math.Abs(EffectiveDisplayDpi - lastRenderDpi) > 0.01) Present(); };

        canvas = new DesignSurfaceController(surface);
        canvas.ElementPicked += (_, name) => ElementPicked?.Invoke(this, name);
        canvas.ElementPathPicked += (_, path) => ElementPathPicked?.Invoke(this, path);
        canvas.SelectionChanged += (_, names) => SelectionChanged?.Invoke(this, names);
        canvas.ElementDragCommitted += (_, info) => ElementDragCommitted?.Invoke(this, info);
        canvas.ElementGroupDragCommitted += (_, moves) => ElementGroupDragCommitted?.Invoke(this, moves);
        canvas.ElementDoubleClicked += (_, info) => ElementDoubleClicked?.Invoke(this, info);
        canvas.TextEditCommitted += (_, text) => TextEditCommitted?.Invoke(this, text);
        canvas.GridGuideDragCommitted += (_, args) => GridGuideDragCommitted?.Invoke(this, args);
        canvas.ContextCommandRequested += (_, args) => ContextCommandRequested?.Invoke(this, args);
        canvas.NudgeRequested += (_, delta) => NudgeRequested?.Invoke(this, delta);
        canvas.UndoRedoRequested += (_, undo) => UndoRedoRequested?.Invoke(this, undo);

        // ThemeManager.CurrentTheme is a process-wide static that every real ProGPU host sets
        // explicitly on startup - it otherwise stays at its library default of Dark.
        Microsoft.UI.Xaml.ThemeManager.CurrentTheme = Microsoft.UI.Xaml.ElementTheme.Light;

        // Window's constructor is where every real ProGPU host initializes the process-wide default
        // font (PopupService.DefaultFont) - this offscreen host never creates a Window, so without
        // this the previewed document's text renders as nothing at all.
        EnsureDefaultFont();
    }

    static bool defaultFontInitialized;

    static void EnsureDefaultFont()
    {
        if (defaultFontInitialized)
            return;
        defaultFontInitialized = true;
        if (Microsoft.UI.Xaml.Controls.PopupService.DefaultFont != null)
            return;
        string fontPath = "/System/Library/Fonts/Supplemental/Arial.ttf";
        if (!global::System.IO.File.Exists(fontPath))
            fontPath = "Arial.ttf";
        if (global::System.IO.File.Exists(fontPath))
            Microsoft.UI.Xaml.Controls.PopupService.DefaultFont = new ProGPU.Text.TtfFont(fontPath);
    }

    public UIElement WpfSurface => surface;
    public bool HasRenderedPreview { get; private set; }
    public string StatusText { get; private set; }
    public event EventHandler StateChanged;
    public event EventHandler<string> ElementPicked;

    public DesignerElementNode? ElementTree => canvas.LastSnapshot?.Tree;
    public DesignerSurfaceGeometry SurfaceGeometry() => canvas.SurfaceGeometry();

    /// <summary>There is no child host process in this in-process runtime.</summary>
    public string ChildLog => "(in-process host)";
    public bool IsChildProcessAlive => true;

    #region Rendering

    public void LoadXaml(string text)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var requested = Interlocked.Increment(ref version);
        _ = RenderAsync(text, requested);
    }

    async Task RenderAsync(string text, int requested)
    {
        try {
            var result = await renderService.RenderAsync(text).ConfigureAwait(true);
            if (disposed || Volatile.Read(ref version) != requested)
                return;
            if (result.Element is Microsoft.UI.Xaml.FrameworkElement element) {
                ApplyFluentTheme(element);
                root = element;
                lastPreviewRoot = new WeakReference(element);
                ResolveNameScope();
                Present();
            } else {
                // Keep the last good frame and tree, and report why.
                StatusText = Describe(result);
            }
        } catch (Exception exception) {
            if (disposed || Volatile.Read(ref version) != requested)
                return;
            StatusText = exception.GetBaseException().Message;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Renders the current tree and hands frame + design tree to the canvas.</summary>
    void Present()
    {
        if (root == null || disposed)
            return;
        var (width, height) = DesignSize();
        var dpi = EffectiveDisplayDpi;
        var (pixels, pixelWidth, pixelHeight, milliseconds) = renderer.Render(root, width, height, dpi);
        FillPageBackground(pixels, theme == "Dark");
        lastRenderMs = milliseconds;
        lastRenderDpi = dpi;
        designTree = ProGpuDesignTree.Build(root, namesByElement);
        var frame = new DesignerRenderFrame {
            Sequence = ++frameSequence,
            Width = pixelWidth,
            Height = pixelHeight,
            Dpi = dpi,
            Data = DesignerFrameCodec.EncodeDeflateBase64(pixels),
            RenderMs = milliseconds,
        };
        canvas.SetSelectableNames(selectableNames);
        canvas.ApplySnapshot(new DesignerSessionState { Accepted = true, Render = frame, Tree = designTree.Tree });
        HasRenderedPreview = true;
        StatusText = dpi > 1.01
            ? $"Rendered by ProGPU WinUI ({width:0}×{height:0} @ {dpi:0.##}x)."
            : $"Rendered by ProGPU WinUI ({width:0}×{height:0}).";
    }

    /// <summary>
    /// Composites the frame over the theme's page colour. A page without a Background renders
    /// transparent, which on the canvas shows the canvas's own edge pattern through the design
    /// instead of a page - the colour an app window would give it (white, or #1C1C1E dark).
    /// The pixels are premultiplied, so each one becomes src + bg * (1 - alpha).
    /// </summary>
    static void FillPageBackground(byte[] pixels, bool dark)
    {
        byte bgB = dark ? (byte)0x1E : (byte)0xFF, bgG = dark ? (byte)0x1C : (byte)0xFF, bgR = dark ? (byte)0x1C : (byte)0xFF;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 255)
                continue;
            var remaining = 255 - alpha;
            pixels[i] = (byte)(pixels[i] + bgB * remaining / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] + bgG * remaining / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] + bgR * remaining / 255);
            pixels[i + 3] = 255;
        }
    }

    /// <summary>The page's own Width/Height when it declares them, else the chosen design size,
    /// else a desktop-like 1280x720 - the same rule as the other WinUI backends.</summary>
    (double Width, double Height) DesignSize()
    {
        var width = root != null && !float.IsNaN(root.Width) && root.Width > 0 ? root.Width : configuredDesignWidth ?? 1280;
        var height = root != null && !float.IsNaN(root.Height) && root.Height > 0 ? root.Height : configuredDesignHeight ?? 720;
        return (width, height);
    }

    /// <summary>Merges the Fluent theme dictionary into the previewed root's own Resources, so every
    /// standard control under it resolves a default style/template. Each fresh preview root gets its
    /// own merge: the root is a new instance out of a new collectible preview assembly every render.</summary>
    static void ApplyFluentTheme(Microsoft.UI.Xaml.FrameworkElement element) =>
        element.Resources.MergedDictionaries.Add(ProGPU.WinUI.Themes.Fluent.FluentThemeResources.CreateDictionary());

    static string Describe(XamlStudio.Toolkit.Models.XamlRenderResultContext result)
    {
        if (result.Errors == null || result.Errors.Count == 0)
            return "ProGPU produced no preview element for this document.";
        return string.Join(Environment.NewLine, System.Linq.Enumerable.Select(result.Errors, static e => e.Message));
    }

    public double EffectiveDisplayDpi
    {
        get
        {
            if (simulatedDpi is { } simulated && simulated > 0)
                return simulated;
            if (double.TryParse(Environment.GetEnvironmentVariable("UNO_DESIGN_DPI"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var overrideDpi) && overrideDpi > 0)
                return overrideDpi;
            return Math.Max(1.0, System.Windows.Media.VisualTreeHelper.GetDpi(surface).DpiScaleX);
        }
    }

    public void SetSimulatedDpi(double? dpi)
    {
        simulatedDpi = dpi;
        Present();
    }

    #endregion

    #region Names

    public void SetSelectableNames(IReadOnlyList<string> names)
    {
        selectableNames = names ?? Array.Empty<string>();
        ResolveNameScope();
        canvas.SetSelectableNames(selectableNames);
    }

    /// <summary>
    /// Maps the rendered elements back to their x:Name. The generated program publishes names
    /// through the WinUI namescope (XamlTemplateFactory.RegisterName), so FindName is the
    /// supported way back - the emitter never assigns FrameworkElement.Name.
    /// </summary>
    void ResolveNameScope()
    {
        namesByElement.Clear();
        if (root == null) return;
        foreach (var name in selectableNames) {
            if (root.FindName(name) is Microsoft.UI.Xaml.FrameworkElement element)
                namesByElement[element] = name;
        }
    }

    public int ResolvedNameCount => namesByElement.Count;
    public string LastPickDiagnostic => canvas.LastPickDiagnostic;
    public string ResolveNameAt(System.Numerics.Vector2 point) => canvas.ResolveNameAt(point);
    public (double X, double Y, double Width, double Height)? QueryElementBounds(string name) => canvas.QueryElementBounds(name);

    /// <summary>
    /// Diagnostic-only dump of a named rendered element's style/template/box-model state, for
    /// "renders but doesn't look right" symptoms a bounds query alone can't distinguish.
    /// </summary>
    public string DescribeElementState(string name)
    {
        if (root == null || string.IsNullOrEmpty(name))
            return "no root";
        if (root.FindName(name) is not Microsoft.UI.Xaml.FrameworkElement element)
            return "not found";
        var describe = $"type={element.GetType().FullName} actualSize={element.Size.X}x{element.Size.Y} width={element.Width} height={element.Height} " +
            $"visibility={element.Visibility} opacity={element.Opacity}";
        if (element is Microsoft.UI.Xaml.Controls.Control control) {
            describe += $" hasTemplate={control.HasTemplate} style={(control.Style != null ? "set" : "null")} " +
                $"template={(control.Template != null ? "set" : "null")} " +
                $"background={DescribeBrush(control.Background)} foreground={DescribeBrush(control.Foreground)} " +
                $"content={(control as Microsoft.UI.Xaml.Controls.ContentControl)?.Content}";
        }
        return describe;
    }

    static string DescribeBrush(object brush)
    {
        if (brush == null)
            return "null";
        if (brush is ProGPU.Vector.SolidColorBrush solid)
            return $"Solid({solid.Color})";
        return brush.GetType().Name + ":" + brush;
    }

    #endregion

    #region Selection, manipulation, text (the canvas does the work)

    public event EventHandler<string> ElementPathPicked;
    public IReadOnlyList<(string Type, int TypeIndex, string Path)> GetPickChain(string path) => canvas.GetPickChain(path);
    public event EventHandler<IReadOnlyList<string>> SelectionChanged;
    public IReadOnlyList<string> SelectedNames => canvas.SelectedNames;
    public void SelectElements(IReadOnlyList<string> names) => canvas.SelectElements(names);
    public void ShowSelection(string name) => canvas.ShowSelection(name);
    public void ShowSelectionAtPath(string path, string label) => canvas.ShowSelectionAtPath(path, label);
    public void ClearSelection() => canvas.ClearSelection();
    public event EventHandler<ElementDragInfo> ElementDragCommitted;
    public event EventHandler<IReadOnlyList<(string Name, double DX, double DY)>> ElementGroupDragCommitted;
    public event EventHandler<(string Name, bool IsRow, int Index, double Position)> GridGuideDragCommitted;
    public event EventHandler<ElementDoubleClickInfo> ElementDoubleClicked;
    public void BeginTextEdit(double x, double y, double width, double height, string text) => canvas.BeginTextEdit(x, y, width, height, text);
    public event EventHandler<string> TextEditCommitted;
    public event EventHandler<(string Command, string Name)> ContextCommandRequested;
    public event EventHandler<(double DX, double DY)> NudgeRequested;
    public event EventHandler<bool> UndoRedoRequested;
    public void SetGridGuides(string name, double x, double y, double width, double height, double[] rowOffsets, double[] colOffsets)
        => canvas.SetGridGuides(name, x, y, width, height, rowOffsets, colOffsets);
    public void ClearGridGuides() => canvas.ClearGridGuides();

    #endregion

    #region View

    public bool Gridlines => canvas.Gridlines;
    public void SetGridlines(bool show) => canvas.SetGridlines(show);
    public bool ShowTabOrder => canvas.ShowTabOrder;
    public void SetTabOrderMode(bool show) => canvas.SetTabOrderMode(show);
    public (double Zoom, double PanX, double PanY) GetViewport() => canvas.GetViewport();
    public double GetViewportScale() => canvas.GetViewportScale();
    public void SetViewport(double zoom, double panX, double panY) => canvas.SetViewport(zoom, panX, panY);
    public void FitView() => canvas.FitView();
    public (double X, double Y) DesignToSurfacePoint(double x, double y) => canvas.DesignToSurfacePoint(x, y);
    public (double X, double Y) DesignToScreenPoint(double x, double y) => canvas.DesignToScreenPoint(x, y);

    public (double Width, double Height)? GetDesignSize()
        => configuredDesignWidth is { } w && configuredDesignHeight is { } h ? (w, h) : null;

    public void SetDesignSize(double width, double height)
    {
        configuredDesignWidth = Math.Max(1, width);
        configuredDesignHeight = Math.Max(1, height);
        Present();
    }

    public void ResetDesignSize()
    {
        configuredDesignWidth = null;
        configuredDesignHeight = null;
        Present();
    }

    public void SetDesignTheme(string requested)
    {
        if (!string.Equals(requested, "Light", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(requested, "Dark", StringComparison.OrdinalIgnoreCase))
            return;
        theme = string.Equals(requested, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        surface.SetTheme(theme);
        Microsoft.UI.Xaml.ThemeManager.CurrentTheme = theme == "Dark" ? Microsoft.UI.Xaml.ElementTheme.Dark : Microsoft.UI.Xaml.ElementTheme.Light;
        root?.NotifyThemeChanged();
        Present();
    }

    public string GetDesignTheme() => theme;

    #endregion

    #region Diagnostics

    /// <summary>Samples the last frame at fixed points (center, top-left, mid-left) as "#RRGGBB",
    /// the same points as the other WinUI backends.</summary>
    public string RenderSample()
    {
        var render = canvas.LastSnapshot?.Render;
        if (render == null || string.IsNullOrEmpty(render.Data))
            return "no frame";
        var w = render.Width;
        var h = render.Height;
        var px = DesignerFrameCodec.DecodeBgra32(render);
        string Sample(double fx, double fy)
        {
            var i = ((int)(fy * h) * w + (int)(fx * w)) * 4;
            return $"#{px[i + 2]:X2}{px[i + 1]:X2}{px[i]:X2}";
        }
        return $"{w}x{h} center={Sample(0.5, 0.5)} topleft={Sample(0.03, 0.05)} midleft={Sample(0.05, 0.5)}";
    }

    public string ExportPng(string path)
    {
        var render = canvas.LastSnapshot?.Render;
        if (render == null || string.IsNullOrEmpty(render.Data))
            return "Nothing to export (no design loaded)";
        try {
            var dpi = 96 * render.Dpi;
            var source = System.Windows.Media.Imaging.BitmapSource.Create(render.Width, render.Height, dpi, dpi,
                System.Windows.Media.PixelFormats.Pbgra32, null, DesignerFrameCodec.DecodeBgra32(render), render.Width * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using (var stream = System.IO.File.Create(path))
                encoder.Save(stream);
            return $"Wrote {path} ({render.Width}x{render.Height})";
        } catch (Exception exception) {
            return "Export failed: " + exception.GetBaseException().Message;
        }
    }

    public (double RenderMs, int Width, int Height, double Dpi, int CompressedBytes, int RawBytes) RenderTiming()
    {
        var render = canvas.LastSnapshot?.Render;
        if (render == null)
            return (0, 0, 0, 0, 0, 0);
        var raw = render.Width * render.Height * 4;
        return (lastRenderMs, render.Width, render.Height, render.Dpi, render.Data.Length, raw);
    }

    public string FrameProfile()
    {
        var render = canvas.LastSnapshot?.Render;
        return render == null ? "no frame" : $"render {render.Width}x{render.Height} data={render.Data.Length / 1024}KB {lastRenderMs:0} ms";
    }

    public string CompositorMetricsDump() => renderer.CompositorMetricsDump();
    public string DumpDrawCalls() => renderer.DumpDrawCalls();
    public string WinUICommandProbe() => ProGpuOffscreenRenderer.WinUICommandProbe(root);
    public string DiagnoseScreenAnchors() => canvas.DiagnoseScreenAnchors();
    // The in-window presentation these probed (WPF bitmap upload, OnRender overlays) is gone: frames
    // are presented by the shared canvas now, so there is nothing ProGPU-specific left to probe.
    public string RenderProbeAndProfile() => "not applicable (frames are presented by the shared canvas)";
    public string ImagePathProbe() => "not applicable (frames are presented by the shared canvas)";
    public void SetShowDiagnosticOverlay(bool value) { }
    public void SetRecreateBitmapEachFrame(bool value) { }
    public void SetPresentViaBackgroundBrush(bool value) { }

    #endregion

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Drop every strong reference to the preview tree BEFORE unloading its collectible load
        // context - WinUiXamlLivePreviewSession.Reset explicitly requires the caller to have
        // detached CurrentRoot from its visual host first, otherwise the ALC stays pinned.
        namesByElement.Clear();
        designTree = null;
        root = null;
        canvas.Dispose();
        executor.Dispose();
        renderer.Dispose();
        Interlocked.Decrement(ref liveHostCount);
    }
}
