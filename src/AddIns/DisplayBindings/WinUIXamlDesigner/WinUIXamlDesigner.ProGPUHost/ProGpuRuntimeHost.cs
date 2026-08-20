using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.SharpDevelop.Designer.Presentation;
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
        new ProGpuRuntimeHost(framework, documentFileName);
}

sealed class ProGpuRuntimeHost : IWinUIXamlRuntimeHost
{
    readonly ProGpuWinUIHostControl control = new();
    readonly ProGpuXamlExecutor executor;
    readonly XamlRenderService renderService;
    readonly XamlFrameworkContext framework;
    // x:Names from the source document, resolved against the rendered tree's namescope after each
    // render so a hit test can be answered with a name the shell side understands.
    IReadOnlyList<string> selectableNames = Array.Empty<string>();
    readonly Dictionary<Microsoft.UI.Xaml.FrameworkElement, string> namesByElement = new();
    bool showTabOrder;
    // Guards against an earlier, slower render overwriting the result of a later edit.
    int version;
    bool disposed;

    static int liveHostCount;
    static WeakReference lastPreviewRoot = new(null);
    internal static int LiveHostCount => Volatile.Read(ref liveHostCount);
    internal static bool LastPreviewRootAlive => lastPreviewRoot.IsAlive;

    public ProGpuRuntimeHost(XamlFrameworkContext framework, string documentFileName)
    {
        this.framework = framework;
        Interlocked.Increment(ref liveHostCount);
        control.SurfacePointerPressed += OnSurfacePointerPressed;
        executor = new ProGpuXamlExecutor(documentFileName);
        renderService = new XamlRenderService(executor);
        StatusText = $"ProGPU WinUI host ready for {framework?.Kind}.";

        // ThemeManager.CurrentTheme is a process-wide static that every real ProGPU host (Samples,
        // Samples.Uno, Samples.Avalonia) sets explicitly on startup - it otherwise stays at its
        // library default of Dark, which styles controls with near-white foregrounds/backgrounds
        // that render invisibly on this host's plain white WPF canvas. Light matches what a design
        // surface is expected to look like regardless of the previewed app's own theme choice.
        Microsoft.UI.Xaml.ThemeManager.CurrentTheme = Microsoft.UI.Xaml.ElementTheme.Light;

        // Window's constructor is where every real ProGPU host initializes the process-wide default
        // font (PopupService.DefaultFont) - this offscreen host never creates a Window, so without
        // this line RichTextBlock.GetOrUpdateRenderCommandCache sees ActiveFont == null and emits
        // zero glyph commands: the previewed document's text renders as nothing at all.
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

    public UIElement WpfSurface => control;
    public bool HasRenderedPreview => control.HasPresentedFrame;
    public string StatusText { get; private set; }
    public event EventHandler StateChanged;
    public event EventHandler<string> ElementPicked;

    /// <summary>In-process runtime: no protocol element tree; the shell falls back to the
    /// source document's element tree for the Document Outline pad.</summary>
    public DesignerElementNode? ElementTree => null;

    /// <summary>In-process runtime: no remote rendered frame; the shell falls back to the
    /// source document's element tree for the Document Outline pad.</summary>
    public DesignerSurfaceGeometry SurfaceGeometry() => default;

    /// <summary>There is no child host process in this in-process runtime.</summary>
    public string ChildLog => "(in-process host)";

    /// <summary>Pixel samples of the last rendered frame.</summary>
    public string RenderSample() => control.RenderSample();

    /// <summary>Exports the current design to a PNG file.</summary>
    public string ExportPng(string path) => control.ExportPng(path);

    /// <summary>Performance report of the last render.</summary>
    public (double RenderMs, int Width, int Height, double Dpi, int CompressedBytes, int RawBytes) RenderTiming()
        => control.RenderTiming();

    /// <summary>The effective display scale (including any debug simulation).</summary>
    public double EffectiveDisplayDpi => control.EffectiveDpiScale;

    /// <summary>Sets or clears the simulated display scale (test hook).</summary>
    public void SetSimulatedDpi(double? dpi) => control.SetSimulatedDpi(dpi);

    /// <summary>Whether the design-space gridlines overlay is shown.</summary>
    public bool Gridlines => control.Gridlines;

    /// <summary>Shows or hides the design-space gridlines overlay.</summary>
    public void SetGridlines(bool show) => control.SetGridlines(show);

    /// <summary>Whether the tab-order badge overlay is shown.</summary>
    public bool ShowTabOrder => showTabOrder;

    /// <summary>Toggles the tab-order badge overlay. The retired in-process profile keeps the
    /// state for contract/DevFlow parity (<c>od.winui-designer.tab-order</c>) but renders no
    /// badges: <see cref="ElementTree"/> is null here, so there are no per-element bounds to
    /// badge - the Uno out-of-process host is the supported WinUI path that actually draws them.</summary>
    public void SetTabOrderMode(bool show) => showTabOrder = show;

    /// <summary>
    /// How many of the document's x:Names actually resolved against the rendered namescope, and
    /// what the last surface click hit. Without these, a click that fails to select is
    /// indistinguishable between "the name map is empty" and "the pointer never arrived".
    /// </summary>
    public int ResolvedNameCount => namesByElement.Count;
    public string LastPickDiagnostic { get; private set; } = "no click yet";

    public string FrameProfile() => control.FrameProfile();
    public string CompositorMetricsDump() => control.CompositorMetricsDump();
    public string RenderProbeAndProfile() => control.RenderProbeAndProfile();
    public string DumpDrawCalls() => control.DumpDrawCalls();
    public string WinUICommandProbe() => control.WinUICommandProbe();
    public string DiagnoseScreenAnchors() => "not applicable (retired ProGPU profile)";
    public string ImagePathProbe() => control.ImagePathProbe();
    public void SetShowDiagnosticOverlay(bool value) => control.ShowDiagnosticOverlay = value;
    public void SetRecreateBitmapEachFrame(bool value) => control.RecreateBitmapEachFrame = value;
    public void SetPresentViaBackgroundBrush(bool value) => control.PresentViaBackgroundBrush = value;

    public void SetSelectableNames(IReadOnlyList<string> names)
    {
        selectableNames = names ?? Array.Empty<string>();
        ResolveNameScope();
    }

    /// <summary>
    /// Maps the rendered elements back to their x:Name. The generated program publishes names
    /// through the WinUI namescope (XamlTemplateFactory.RegisterName), so FindName is the
    /// supported way back - the emitter never assigns FrameworkElement.Name.
    /// </summary>
    void ResolveNameScope()
    {
        namesByElement.Clear();
        var root = control.WinUIRoot;
        if (root == null) return;
        foreach (var name in selectableNames) {
            if (root.FindName(name) is Microsoft.UI.Xaml.FrameworkElement element)
                namesByElement[element] = name;
        }
    }

    /// <summary>
    /// Answers a click with the nearest ancestor that exists in the XAML source. Walking up
    /// matters because a hit usually lands on a control-template part (a Button's inner text),
    /// which has no counterpart in the document.
    /// </summary>
    void OnSurfacePointerPressed(System.Numerics.Vector2 point)
    {
        var name = ResolveNameAt(point);
        if (name != null)
            ElementPicked?.Invoke(this, name);
    }

    /// <summary>
    /// The x:Name of the nearest source-backed element under <paramref name="point"/>, or null.
    /// Also used to decide which container a Toolbox drop lands in.
    /// </summary>
    public string ResolveNameAt(System.Numerics.Vector2 point)
    {
        // InputSystem.HitTest hit-tests against InputSystem.Current.Root, which is only ever set
        // by ProGpuWinUIHostControl.SelectInput - itself only called from real mouse move/down/up
        // on THIS control. A WPF DragEventArgs.Drop (the toolbox-drag path, WinUIXamlHost.OnDrop)
        // never routes through those handlers, so on a drag that starts from the Toolbox and never
        // first moves the mouse over this design surface, Current.Root can still be null (or stale
        // from a different host), and HitTest bails out unconditionally before even considering
        // the point - producing a hit-test point that looked numerically correct against this
        // element's own bounds, yet resolved to nothing at all (confirmed live via
        // LastPickDiagnostic during the drag-drop investigation). Make the root explicit here
        // rather than depending on incidental prior mouse traffic having set it.
        Microsoft.UI.Xaml.Input.InputSystem.Current.Root = control.WinUIRoot;
        var hit = Microsoft.UI.Xaml.Input.InputSystem.HitTest(point);
        LastPickDiagnostic = $"point={point.X:F0},{point.Y:F0} hit={hit?.GetType().Name ?? "null"} resolved={namesByElement.Count}";
        var walked = 0;
        while (hit != null) {
            if (namesByElement.TryGetValue(hit, out var name)) {
                LastPickDiagnostic += $" -> {name} after {walked} parent hop(s)";
                return name;
            }
            hit = hit.Parent as Microsoft.UI.Xaml.FrameworkElement;
            walked++;
        }
        LastPickDiagnostic += $" -> no named ancestor after {walked} hop(s)";
        return null;
    }

    /// <summary>
    /// Surface-local bounds of a named rendered element, or null when it is not in the current
    /// namescope. Returned as plain numbers so no Microsoft.UI.Xaml type crosses the boundary.
    /// </summary>
    public (double X, double Y, double Width, double Height)? QueryElementBounds(string name)
    {
        var root = control.WinUIRoot;
        if (root == null || string.IsNullOrEmpty(name))
            return null;
        if (root.FindName(name) is not Microsoft.UI.Xaml.FrameworkElement element)
            return null;
        var origin = element.TransformToVisual(root).TransformPoint(System.Numerics.Vector2.Zero);
        // Width/Height stay NaN unless the markup set them explicitly; the arranged Size is the
        // measured extent. Same fallback DesignerCanvas itself uses when sizing its adorners.
        var width = float.IsNaN(element.Width) ? element.Size.X : element.Width;
        var height = float.IsNaN(element.Height) ? element.Size.Y : element.Height;
        return (origin.X, origin.Y, width, height);
    }

    /// <summary>
    /// Diagnostic-only dump of a named rendered element's style/template/box-model state, for
    /// tracking down "renders but doesn't look right" symptoms that a bounds query alone can't
    /// distinguish (e.g. an unstyled Control vs. one whose ControlTemplate never got built).
    /// </summary>
    public string DescribeElementState(string name)
    {
        var root = control.WinUIRoot;
        if (root == null || string.IsNullOrEmpty(name))
            return "no root";
        if (root.FindName(name) is not Microsoft.UI.Xaml.FrameworkElement element)
            return "not found";
        var describe = $"type={element.GetType().FullName} actualSize={element.Size.X}x{element.Size.Y} width={element.Width} height={element.Height} " +
            $"visibility={element.Visibility} opacity={element.Opacity}";
        if (element is Microsoft.UI.Xaml.Controls.Control control2) {
            describe += $" hasTemplate={control2.HasTemplate} style={(control2.Style != null ? "set" : "null")} " +
                $"template={(control2.Template != null ? "set" : "null")} " +
                $"background={DescribeBrush(control2.Background)} foreground={DescribeBrush(control2.Foreground)} " +
                $"padding={control2.Padding.Left},{control2.Padding.Top},{control2.Padding.Right},{control2.Padding.Bottom} " +
                $"content={(control2 as Microsoft.UI.Xaml.Controls.ContentControl)?.Content} " +
                $"borderThickness={control2.BorderThickness.Left},{control2.BorderThickness.Top} " +
                $"borderBrush={DescribeBrush(control2.BorderBrush)}";
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
                control.WinUIRoot = element;
                lastPreviewRoot = new WeakReference(element);
                ResolveNameScope();
                StatusText = $"Rendered by ProGPU for {framework?.Kind}.";
            } else {
                // The session retains its last good tree, so leave WinUIRoot alone and report why.
                StatusText = Describe(result);
            }
        } catch (Exception exception) {
            if (disposed || Volatile.Read(ref version) != requested)
                return;
            StatusText = exception.GetBaseException().Message;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Merges the Fluent theme dictionary into the previewed root's own Resources, so every
    /// standard control under it resolves a default style/template via
    /// FrameworkElement.TryFindResource's ancestor-Resources walk. Each fresh preview root gets
    /// its own merge (a plain Add, no de-duplication needed): the root is a brand-new instance out
    /// of a brand-new collectible preview assembly every render, never reused across renders.
    /// </summary>
    static void ApplyFluentTheme(Microsoft.UI.Xaml.FrameworkElement root) =>
        root.Resources.MergedDictionaries.Add(
            ProGPU.WinUI.Themes.Fluent.FluentThemeResources.CreateDictionary());

    static string Describe(XamlStudio.Toolkit.Models.XamlRenderResultContext result)
    {
        if (result.Errors == null || result.Errors.Count == 0)
            return "ProGPU produced no preview element for this document.";
        return string.Join(Environment.NewLine, System.Linq.Enumerable.Select(result.Errors, static e => e.Message));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        control.SurfacePointerPressed -= OnSurfacePointerPressed;
        // Drop every strong reference to the preview tree BEFORE unloading its collectible load
        // context - WinUiXamlLivePreviewSession.Reset explicitly requires the caller to have
        // detached CurrentRoot from its visual host first, otherwise the ALC stays pinned.
        namesByElement.Clear();
        control.WinUIRoot = null;
        executor.Dispose();
        control.Dispose();
        Interlocked.Decrement(ref liveHostCount);
    }
}
