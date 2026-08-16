using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.Backend;
using ProGPU.Scene;
using Silk.NET.Input;
using Silk.NET.WebGPU;
using WinUIElement = Microsoft.UI.Xaml.FrameworkElement;
using WpfControl = System.Windows.Controls.Control;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
using WpfDrawingContext = System.Windows.Media.DrawingContext;
using SilkMouseButton = Silk.NET.Input.MouseButton;

namespace ICSharpCode.WinUIXamlDesigner.ProGPUHost;

/// <summary>Hosts a ProGPU WinUI visual tree inside a WPF visual tree via an offscreen surface.</summary>
public unsafe sealed class ProGpuWinUIHostControl : WpfControl, IDisposable
{
    WgpuContext context;
    Compositor compositor;
    GpuTexture texture;
    Silk.NET.WebGPU.Buffer* stagingBuffer;
    uint stagingBufferSize;
    uint bytesPerRow;
    WriteableBitmap bitmap;
    byte[] lastFrameBytes;
    readonly WindowInputState input = new();
    bool loaded;
    bool rendering;
    bool disposed;

    public WinUIElement WinUIRoot
    {
        get => input.Root;
        set { input.Root = value; InvalidateVisual(); }
    }

    public bool HasPresentedFrame { get; private set; }

    /// <summary>Milliseconds the last frame took to rasterize and copy to the bitmap.</summary>
    public double LastRenderMs { get; private set; }

    /// <summary>
    /// The display scale the compositor renders at: the simulated scale (test hook) wins, then
    /// the UNO_DESIGN_DPI environment override, then the real WPF DPI of this control.
    /// </summary>
    public double EffectiveDpiScale
    {
        get
        {
            if (simulatedDpi is { } simulated && simulated > 0)
                return simulated;
            if (double.TryParse(Environment.GetEnvironmentVariable("UNO_DESIGN_DPI"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var overrideDpi) && overrideDpi > 0)
                return overrideDpi;
            return Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);
        }
    }

    /// <summary>Sets or clears the simulated display scale (test hook); the next composition tick re-renders with it.</summary>
    public void SetSimulatedDpi(double? dpi) => simulatedDpi = dpi;

    /// <summary>Whether the design-space gridlines overlay is shown.</summary>
    public bool Gridlines => gridlines;

    /// <summary>Shows or hides the design-space gridlines overlay.</summary>
    public void SetGridlines(bool show)
    {
        gridlines = show;
        InvalidateVisual();
    }

    /// <summary>
    /// Samples the last rendered frame at fixed points (center, top-left, mid-left) and returns
    /// them as "#RRGGBB" strings - for pixel-level verification that a re-render actually changed
    /// the drawing. Same sampling points as the Uno host so the two runtimes agree.
    /// </summary>
    public string RenderSample()
    {
        if (lastFrameBytes == null || bitmap == null || !HasPresentedFrame)
            return "no frame";
        var w = bitmap.PixelWidth;
        var h = bitmap.PixelHeight;
        if (w <= 0 || h <= 0 || lastFrameBytes.Length < bytesPerRow * h)
            return "bad frame";
        static string Sample(byte[] px, int stride, int w, int h, double fx, double fy)
        {
            var i = ((int)(fy * h) * stride + (int)(fx * w)) * 4;
            // BGRA order from the compositor's Bgra8Unorm target.
            return $"#{px[i + 2]:X2}{px[i + 1]:X2}{px[i]:X2}";
        }
        var center = Sample(lastFrameBytes, (int)bytesPerRow, w, h, 0.5, 0.5);
        var topLeft = Sample(lastFrameBytes, (int)bytesPerRow, w, h, 0.03, 0.05);
        var midLeft = Sample(lastFrameBytes, (int)bytesPerRow, w, h, 0.05, 0.5);
        return $"{w}x{h} center={center} topleft={topLeft} midleft={midLeft}";
    }

    /// <summary>Exports the current design to a PNG file, from the last frame's staging bytes.</summary>
    public string ExportPng(string path)
    {
        if (lastFrameBytes == null || bitmap == null || !HasPresentedFrame)
            return "Nothing to export (no design loaded)";
        try
        {
            var w = bitmap.PixelWidth;
            var h = bitmap.PixelHeight;
            var source = System.Windows.Media.Imaging.BitmapSource.Create(
                w, h, 96 * EffectiveDpiScale, 96 * EffectiveDpiScale, PixelFormats.Pbgra32, null, lastFrameBytes, (int)bytesPerRow);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using (var stream = System.IO.File.Create(path))
                encoder.Save(stream);
            return $"Wrote {path} ({w}x{h})";
        }
        catch (Exception exception)
        {
            return "Export failed: " + exception.GetBaseException().Message;
        }
    }

    /// <summary>Performance report of the last render (ms, pixel size, effective scale, wire bytes).</summary>
    public (double RenderMs, int Width, int Height, double Dpi, int CompressedBytes, int RawBytes) RenderTiming()
    {
        if (bitmap == null || !HasPresentedFrame)
            return (0, 0, 0, 0, 0, 0);
        var w = bitmap.PixelWidth;
        var h = bitmap.PixelHeight;
        var raw = w * h * 4;
        // In-process: the staging copy is the only "wire" and carries no compression.
        return (LastRenderMs, w, h, EffectiveDpiScale, raw, raw);
    }

    /// <summary>Temporary diagnostic: replay LibreWPF's image adapter path step by step to find where it fails.</summary>
    public string ImagePathProbe()
    {
        if (bitmap == null)
            return "no bitmap";
        var results = new System.Collections.Generic.List<string> { $"bitmap={bitmap.PixelWidth}x{bitmap.PixelHeight} fmt={bitmap.Format}" };
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;

        // Step 1: TryGetGpuTexture (the sink's gate)
        var adapterType = System.Type.GetType(
            "System.Windows.Media.ProGPU.Composition.Mil.WpfBitmapSourceImageAdapter, ProGPU.Wpf",
            throwOnError: false);
        results.Add($"adapterType={(adapterType != null ? "found" : "MISSING")}");
        if (adapterType != null)
        {
            var tryGet = adapterType.GetMethod("TryGetGpuTexture", flags);
            var args = new object[] { bitmap, null };
            var ok = (bool?)tryGet?.Invoke(null, args);
            results.Add($"TryGetGpuTexture={ok} texture={(args[1] != null ? "created" : "null")}");
        }

        // Step 2: the bitmap's own portable pixel snapshot
        if (bitmap is ProGPU.Wpf.Interop.IPortableBitmapSourcePixelsSource portable)
        {
            var ok2 = portable.TryGetPortableBitmapSourcePixels(out var pixels);
            results.Add($"portablePixels={ok2} w={pixels?.Width} h={pixels?.Height} fmt={pixels?.Format} stride={pixels?.Stride}");
        }
        else
        {
            results.Add("portablePixels=interface NOT implemented on bitmap");
        }

        // Step 3: context identity — which context owns the cached texture vs the render contexts
        results.Add($"ourHostContext={(context != null ? "ours:" + context.GetHashCode() : "null")}");
        results.Add($"currentOnDevFlowThread={(ProGPU.Backend.WgpuContext.Current != null ? ProGPU.Backend.WgpuContext.Current.GetHashCode().ToString() : "null")}");
        results.Add($"capturedAtOnRender={(capturedOnRenderContext != null ? capturedOnRenderContext.GetHashCode().ToString() : "null")}");
        if (ProGPU.Backend.WgpuContext.TryGetFirstActiveContext(out var active))
            results.Add($"firstActive={active.GetHashCode()}");
        else
            results.Add("firstActive=null");

        return string.Join(" | ", results);
    }

    /// <summary>Temporary diagnostic: walk the WinUI visual tree, call OnRender on every node, and report commands.</summary>
    public string WinUICommandProbe()
    {
        if (WinUIRoot == null)
            return "no root";
        var ctx = new ProGPU.Scene.DrawingContext();
        var byType = new System.Collections.Generic.Dictionary<string, int>();
        var nodeTypes = new System.Collections.Generic.Dictionary<string, int>();
        var nodeCount = 0;
        void Walk(ProGPU.Scene.Visual visual)
        {
            nodeCount++;
            var typeName = visual.GetType().Name;
            nodeTypes[typeName] = nodeTypes.TryGetValue(typeName, out var c) ? c + 1 : 1;
            visual.OnRender(ctx);
            if (visual is ProGPU.Scene.ContainerVisual container)
            {
                foreach (var child in container.Children)
                    Walk(child);
            }
        }
        Walk(WinUIRoot);
        foreach (var command in ctx.Commands)
        {
            var name = command.Type.ToString();
            byType[name] = byType.TryGetValue(name, out var n) ? n + 1 : 1;
        }
        var nodeSummary = string.Join(",", nodeTypes.Select(static kv => kv.Key + ":" + kv.Value));
        var cmdSummary = byType.Count == 0 ? "none" : string.Join(",", byType.Select(static kv => kv.Key + "=" + kv.Value));
        return $"nodes={nodeCount} [{nodeSummary}] commands={ctx.Commands.Count} [{cmdSummary}]";
    }

    /// <summary>Temporary diagnostic: dump the compositor's compiled draw calls via reflection.</summary>
    public string DumpDrawCalls()
    {
        if (compositor == null)
            return "no compositor";
        var listField = typeof(ProGPU.Scene.Compositor).GetField(
            "_drawCalls",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (listField == null || listField.GetValue(compositor) is not System.Collections.IList list)
            return "no draw call list";
        var parts = new System.Collections.Generic.List<string> { $"count={list.Count}" };
        foreach (var dc in list)
        {
            var t = dc?.GetType();
            if (t == null) continue;
            object? G(string name) => t.GetField(name)?.GetValue(dc);
            parts.Add($"type={G("Type")} solidRect={G("IsSolidRect")} solidRounded={G("IsSolidRounded")} " +
                $"idxStart={G("IndexStart")} idxCount={G("IndexCount")} clip={(G("ClipRect") != null ? G("ClipRect") : "null")} " +
                $"blend={G("BlendMode")} brush={(G("Brush") != null ? G("Brush") : "null")}");
        }
        return string.Join(" | ", parts);
    }

    /// <summary>Temporary diagnostic: render a hand-built ProGPU rounded rect (no WinUI) and report the frame.</summary>
    public string RenderProbeAndProfile()
    {
        if (compositor == null || context == null || bitmap == null || texture == null)
            return "not ready";
        var probe = new ProGPU.Scene.DrawingVisual();
        probe.Offset = System.Numerics.Vector2.Zero;
        probe.Size = new System.Numerics.Vector2(bitmap.PixelWidth, bitmap.PixelHeight);
        var brush = new ProGPU.Vector.SolidColorBrush(new System.Numerics.Vector4(1f, 0f, 0f, 1f));
        probe.Context.DrawRoundedRectangle(brush, null, new ProGPU.Scene.Rect(10, 10, 200, 80), 4f);
        compositor.RenderOffscreen(probe, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, texture, 0, 1f);
        CopyTextureToBitmap();
        return FrameProfile();
    }

    /// <summary>Temporary diagnostic: what the ProGPU compositor compiled for the last offscreen render.</summary>
    public string CompositorMetricsDump()
    {
        if (compositor == null)
            return "no compositor";
        var m = compositor.Metrics;
        return $"target={m.RenderTargetWidth}x{m.RenderTargetHeight} dpi={m.DpiScale} " +
            $"drawCalls={m.DrawCallsCount} vectorVerts={m.VectorVerticesCount} vectorIdx={compositor.VectorIndexCount} " +
            $"textVerts={m.TextVerticesCount} textStyles={m.ActiveTextStyleCount} brushes={m.ActiveBrushCount} " +
            $"glyphBatches={m.GlyphRasterBatchSubmissions} glyphs={m.GlyphOutlineCompiledCount} " +
            $"pathAtlasPaths={m.PathAtlasCurrentFramePathCount} pathAtlasCached={m.PathAtlasCachedCount} " +
            $"glyphAtlas={m.GlyphAtlasSize} colorGlyphAtlas={m.ColorGlyphAtlasSize} maskPasses={m.MaskRenderPassCount} " +
            $"pipelines={m.SceneRenderPipelineCount} computePipelines={m.SceneComputePipelineCount} effectPipelines={m.EffectPipelineCount} " +
            $"shaders={m.SceneShaderCount} commands={m.RecordedCommandCount} " +
            $"retainedScenes={m.RetainedCompositionSceneCount} retainedNodes={m.RetainedCompositionSceneNodeCount} " +
            $"retainedFallback={m.RetainedCompositionFallbackNodeCount} retainedCustom={m.RetainedCompositionCustomVisualNodeCount} " +
            $"retainedFullSync={m.RetainedCompositionSceneFullSynchronizations} retainedIncremental={m.RetainedCompositionSceneIncrementalSynchronizations} " +
            $"retainedUnchanged={m.RetainedCompositionSceneUnchangedReuses} " +
            $"compileMs={m.VisualTreeCompileTimeMs:F1} uploadMs={m.GpuUploadTimeMs:F1} renderMs={m.RenderPassTimeMs:F1} frameMs={m.FrameTimeMs:F1} " +
            $"sceneCacheHit={m.SceneCacheHit} sceneCacheMiss={m.SceneCacheMissReason}";
    }

    /// <summary>Temporary diagnostic: where the presented frame actually has non-white pixels.</summary>
    public string FrameProfile()
    {
        if (bitmap == null || !HasPresentedFrame)
            return "no frame";
        var w = bitmap.PixelWidth;
        var h = bitmap.PixelHeight;
        bitmap.Lock();
        try
        {
            byte* back = (byte*)bitmap.BackBuffer;
            int stride = bitmap.BackBufferStride;
            int total = 0, firstRow = -1, lastRow = -1;
            var lines = new System.Collections.Generic.List<string>();
            var band = Math.Max(1, h / 20);
            var samples = new System.Collections.Generic.List<string>();
            for (var y = 0; y < h; y++)
            {
                byte* p = back + (long)y * stride;
                int rowCount = 0;
                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                for (var x = 0; x < w; x++)
                {
                    if (p[0] < 240 || p[1] < 240 || p[2] < 240)
                        rowCount++;
                    sumR += p[2];
                    sumG += p[1];
                    sumB += p[0];
                    sumA += p[3];
                    p += 4;
                }
                if (rowCount > 0)
                {
                    if (firstRow < 0) firstRow = y;
                    lastRow = y;
                }
                total += rowCount;
                if (y % band == 0)
                    lines.Add($"y{y}:nw={rowCount} rgb=({sumR / w},{sumG / w},{sumB / w}) a={sumA / w}");
                if (y < 60)
                    lines.Add($"y{y}:nw={rowCount} a={sumA / w}");
            }
            foreach (var (x, y) in new[] { (343, 8), (343, 12), (343, 28), (343, 40), (343, 200), (10, 8), (600, 30) })
            {
                byte* p = back + (long)y * stride + x * 4;
                samples.Add($"({x},{y})=(B{p[0]},G{p[1]},R{p[2]},A{p[3]})");
            }
            return $"w={w} h={h} nonWhite={total} firstRow={firstRow} lastRow={lastRow} | " + string.Join(" ", lines) + " | px: " + string.Join(" ", samples);
        }
        finally
        {
            bitmap.Unlock();
        }
    }

    public ProGpuWinUIHostControl()
    {
        Focusable = true;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    void Start()
    {
        if (loaded || disposed) return;
        // WgpuContext.Initialize sets the thread-static WgpuContext.Current to *this* context.
        // Clobbering Current here would make LibreWPF's BitmapSource image adapter (which
        // prefers Current when creating GPU textures for DrawImage) upload our frame bitmap
        // onto THIS context, while the WPF window is composited on LibreWPF's OWN context -
        // a cross-device texture that silently renders nothing on screen. Save the caller's
        // Current and restore it after we finish creating our offscreen context.
        var previousCurrent = ProGPU.Backend.WgpuContext.Current;
        context = new WgpuContext();
        context.Initialize(null);
        ProGPU.Backend.WgpuContext.Current = previousCurrent;
        compositor = new Compositor(context, TextureFormat.Bgra8Unorm);
        loaded = true;
        // WPF arranges before it raises Loaded, so the arrange that sized this control already ran
        // while EnsureSurface was still short-circuiting on !loaded. Without re-arranging here the
        // render target is never created and RenderFrame returns on every tick.
        InvalidateArrange();
        CompositionTarget.Rendering += RenderFrame;
        ProGPU.Backend.WgpuContext.OnWebGpuError += OnWebGpuError;
        ProGPU.Backend.WgpuContext.OnWebGpuDeviceLost += OnWebGpuDeviceLost;
    }

    void OnWebGpuError(Silk.NET.WebGPU.ErrorType type, string message) =>
        System.Diagnostics.Debug.WriteLine($"[OpenDevelop-Host] WebGPU error {type}: {message}");

    void OnWebGpuDeviceLost(Silk.NET.WebGPU.DeviceLostReason reason, string message) =>
        System.Diagnostics.Debug.WriteLine($"[OpenDevelop-Host] WebGPU device lost {reason}: {message}");

    void Stop()
    {
        if (!loaded) return;
        ProGPU.Backend.WgpuContext.OnWebGpuError -= OnWebGpuError;
        ProGPU.Backend.WgpuContext.OnWebGpuDeviceLost -= OnWebGpuDeviceLost;
        CompositionTarget.Rendering -= RenderFrame;
        ReleaseSurface();
        compositor?.Dispose();
        compositor = null;
        context?.Dispose();
        context = null;
        loaded = false;
    }

    protected override WpfSize MeasureOverride(WpfSize constraint)
    {
        var width = double.IsFinite(constraint.Width) ? constraint.Width : 800;
        var height = double.IsFinite(constraint.Height) ? constraint.Height : 600;
        WinUIRoot?.Measure(new Vector2((float)width, (float)height));
        return new WpfSize(width, height);
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        WinUIRoot?.Arrange(new ProGPU.Scene.Rect(0, 0, (float)finalSize.Width, (float)finalSize.Height));
        EnsureSurface(finalSize);
        return finalSize;
    }

    void EnsureSurface(WpfSize logicalSize)
    {
        if (!loaded || context == null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        uint width = (uint)Math.Max(1, Math.Ceiling(logicalSize.Width * dpi.DpiScaleX));
        uint height = (uint)Math.Max(1, Math.Ceiling(logicalSize.Height * dpi.DpiScaleY));
        if (bitmap?.PixelWidth == width && bitmap.PixelHeight == height) return;

        ReleaseSurface();
        texture = new GpuTexture(context, width, height, TextureFormat.Bgra8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.CopySrc, "OpenDevelop WinUI Designer",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        bytesPerRow = (width * 4 + 255) & ~255u;
        stagingBufferSize = bytesPerRow * height;
        var descriptor = new BufferDescriptor { Usage = BufferUsage.MapRead | BufferUsage.CopyDst, Size = stagingBufferSize };
        stagingBuffer = context.Api.DeviceCreateBuffer(context.Device, &descriptor);
        bitmap = new WriteableBitmap((int)width, (int)height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null);
    }

    void RenderFrame(object sender, EventArgs args)
    {
        if (rendering || !loaded || WinUIRoot == null || texture == null || stagingBuffer == null || bitmap == null) return;
        rendering = true;
        try
        {
            var dpi = EffectiveDpiScale;
            WinUIRoot.UpdateAnimations(1f / 60f);
            WinUIRoot.Measure(new Vector2((float)ActualWidth, (float)ActualHeight));
            WinUIRoot.Arrange(new ProGPU.Scene.Rect(0, 0, (float)ActualWidth, (float)ActualHeight));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            compositor.RenderOffscreen(WinUIRoot, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, texture, 0, (float)dpi);
            CopyTextureToBitmap();
            LastRenderMs = stopwatch.Elapsed.TotalMilliseconds;
            if (RecreateBitmapEachFrame)
                RecreateBitmapFromStaging();
            if (PresentViaBackgroundBrush)
                Background = new System.Windows.Media.ImageBrush(bitmap) { Stretch = System.Windows.Media.Stretch.Fill };
            HasPresentedFrame = true;
            InvalidateVisual();
        }
        finally { rendering = false; }
    }

    /// <summary>Temporary diagnostic: recreate the WriteableBitmap each frame instead of in-place updates.</summary>
    public bool RecreateBitmapEachFrame { get; set; }

    /// <summary>Temporary diagnostic: present the frame via Background = ImageBrush instead of OnRender DrawImage.</summary>
    public bool PresentViaBackgroundBrush { get; set; }

    void RecreateBitmapFromStaging()
    {
        if (lastFrameBytes == null || bitmap == null)
            return;
        var w = bitmap.PixelWidth;
        var h = bitmap.PixelHeight;
        var fresh = new WriteableBitmap(w, h, 96 * VisualTreeHelper.GetDpi(this).DpiScaleX, 96 * VisualTreeHelper.GetDpi(this).DpiScaleY, PixelFormats.Pbgra32, null);
        fresh.Lock();
        fixed (byte* p = lastFrameBytes)
        {
            byte* dst = (byte*)fresh.BackBuffer;
            for (var row = 0; row < h; row++)
                System.Buffer.MemoryCopy(p + row * bytesPerRow, dst + row * fresh.BackBufferStride, fresh.BackBufferStride, w * 4);
        }
        fresh.AddDirtyRect(new Int32Rect(0, 0, w, h));
        fresh.Unlock();
        bitmap = fresh;
    }

    void CopyTextureToBitmap()
    {
        var encoderDescriptor = new CommandEncoderDescriptor();
        var encoder = context.Api.DeviceCreateCommandEncoder(context.Device, &encoderDescriptor);
        var source = new ImageCopyTexture { Texture = texture.TexturePtr, Aspect = TextureAspect.All };
        var destination = new ImageCopyBuffer { Buffer = stagingBuffer, Layout = new TextureDataLayout { BytesPerRow = bytesPerRow, RowsPerImage = (uint)bitmap.PixelHeight } };
        var extent = new Extent3D { Width = (uint)bitmap.PixelWidth, Height = (uint)bitmap.PixelHeight, DepthOrArrayLayers = 1 };
        context.Api.CommandEncoderCopyTextureToBuffer(encoder, &source, &destination, &extent);
        var commandDescriptor = new CommandBufferDescriptor();
        var command = context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        context.Submit(1, &command);
        context.Api.CommandBufferRelease(command);
        context.Api.CommandEncoderRelease(encoder);

        bool pending = true;
        PfnBufferMapCallback callback = PfnBufferMapCallback.From((_, _) => pending = false);
        context.Api.BufferMapAsync(stagingBuffer, MapMode.Read, 0, stagingBufferSize, callback, null);
        while (pending) context.PollDevice(false);
        byte* sourceBytes = (byte*)context.Api.BufferGetConstMappedRange(stagingBuffer, 0, stagingBufferSize);
        if (lastFrameBytes == null || lastFrameBytes.Length != stagingBufferSize)
            lastFrameBytes = new byte[stagingBufferSize];
        fixed (byte* p = lastFrameBytes)
            System.Buffer.MemoryCopy(sourceBytes, p, stagingBufferSize, stagingBufferSize);
        bitmap.Lock();
        for (var row = 0; row < bitmap.PixelHeight; row++)
            System.Buffer.MemoryCopy(sourceBytes + row * bytesPerRow, (byte*)bitmap.BackBuffer + row * bitmap.BackBufferStride,
                bitmap.BackBufferStride, bitmap.PixelWidth * 4);
        bitmap.AddDirtyRect(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        bitmap.Unlock();
        context.Api.BufferUnmap(stagingBuffer);
        GC.KeepAlive(callback);
    }

    protected override void OnRender(WpfDrawingContext drawingContext)
    {
        capturedOnRenderContext = ProGPU.Backend.WgpuContext.Current;
        base.OnRender(drawingContext);
        if (bitmap != null) drawingContext.DrawImage(bitmap, new System.Windows.Rect(0, 0, ActualWidth, ActualHeight));
        if (gridlines)
        {
            var pen = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0x60, 0x60, 0x60)), 0.5);
            const double step = 24;
            for (var x = step; x < ActualWidth; x += step)
                drawingContext.DrawLine(pen, new System.Windows.Point(x, 0), new System.Windows.Point(x, ActualHeight));
            for (var y = step; y < ActualHeight; y += step)
                drawingContext.DrawLine(pen, new System.Windows.Point(0, y), new System.Windows.Point(ActualWidth, y));
        }
        if (ShowDiagnosticOverlay)
        {
            var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.Red, 2);
            drawingContext.DrawRectangle(null, pen, new System.Windows.Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));
            drawingContext.DrawText(
                new System.Windows.Media.FormattedText(
                    $"frame={bitmap != null}, size={ActualWidth:F0}x{ActualHeight:F0}",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface("Segoe UI"),
                    12,
                    System.Windows.Media.Brushes.Red),
                new System.Windows.Point(4, 4));
            if (diagWriteable == null)
            {
                var bmp = new WriteableBitmap(64, 64, 96, 96, PixelFormats.Pbgra32, null);
                bmp.Lock();
                unsafe
                {
                    byte* p = (byte*)bmp.BackBuffer;
                    for (var i = 0; i < 64 * 64; i++)
                    {
                        p[0] = 0; p[1] = 0; p[2] = 255; p[3] = 255;
                        p += 4;
                    }
                }
                bmp.AddDirtyRect(new Int32Rect(0, 0, 64, 64));
                bmp.Unlock();
                diagWriteable = bmp;
            }
            drawingContext.DrawImage(diagWriteable, new System.Windows.Rect(10, 80, 64, 64));
            if (diagSource == null)
            {
                var pixels = new byte[64 * 64 * 4];
                for (var y = 0; y < 64; y++)
                    for (var x = 0; x < 64; x++)
                    {
                        var idx = (y * 64 + x) * 4;
                        var check = ((x / 8) + (y / 8)) % 2 == 0;
                        pixels[idx] = check ? (byte)0 : (byte)255;
                        pixels[idx + 1] = check ? (byte)0 : (byte)255;
                        pixels[idx + 2] = check ? (byte)255 : (byte)0;
                        pixels[idx + 3] = 255;
                    }
                diagSource = System.Windows.Media.Imaging.BitmapSource.Create(64, 64, 96, 96, PixelFormats.Pbgra32, null, pixels, 64 * 4);
            }
            drawingContext.DrawImage(diagSource, new System.Windows.Rect(90, 80, 64, 64));
        }
    }

    WriteableBitmap diagWriteable;
    System.Windows.Media.Imaging.BitmapSource diagSource;
    ProGPU.Backend.WgpuContext capturedOnRenderContext;
    double? simulatedDpi;
    bool gridlines;

    /// <summary>Temporary diagnostic: draw a red border + status text in OnRender.</summary>
    public bool ShowDiagnosticOverlay { get; set; }

    void SelectInput(WpfPoint point)
    {
        input.Root = WinUIRoot;
        input.LastMousePos = new Vector2((float)point.X, (float)point.Y);
        InputSystem.Current = input;
    }

    protected override void OnMouseMove(MouseEventArgs e) { var p = e.GetPosition(this); SelectInput(p); InputSystem.InjectMouseMove(new Vector2((float)p.X, (float)p.Y)); e.Handled = true; }
    /// <summary>
    /// Raised with the surface-local point of a left press, after the input has been delivered to
    /// the WinUI tree. The designer uses it to pick the element under the cursor; the host itself
    /// stays free of any selection policy.
    /// </summary>
    public event Action<Vector2> SurfacePointerPressed;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var position = e.GetPosition(this);
        SelectInput(position);
        InputSystem.InjectMouseDown(ToSilk(e.ChangedButton));
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            SurfacePointerPressed?.Invoke(new Vector2((float)position.X, (float)position.Y));
        e.Handled = true;
    }
    protected override void OnMouseUp(MouseButtonEventArgs e) { SelectInput(e.GetPosition(this)); InputSystem.InjectMouseUp(ToSilk(e.ChangedButton)); e.Handled = true; }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { SelectInput(e.GetPosition(this)); InputSystem.InjectMouseScroll(new Vector2(0, e.Delta / 120f)); e.Handled = true; }
    protected override void OnTextInput(TextCompositionEventArgs e) { foreach (var c in e.Text) InputSystem.InjectKeyChar(c); e.Handled = true; }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { InputSystem.Current = input; InputSystem.InjectFocusLost(); base.OnLostKeyboardFocus(e); }

    static SilkMouseButton ToSilk(System.Windows.Input.MouseButton button) => button switch
    {
        System.Windows.Input.MouseButton.Right => SilkMouseButton.Right,
        System.Windows.Input.MouseButton.Middle => SilkMouseButton.Middle,
        _ => SilkMouseButton.Left
    };

    void ReleaseSurface()
    {
        if (stagingBuffer != null && context != null) { context.Api.BufferDestroy(stagingBuffer); context.Api.BufferRelease(stagingBuffer); stagingBuffer = null; }
        texture?.Dispose(); texture = null; bitmap = null; HasPresentedFrame = false;
    }

    public void Dispose() { if (disposed) return; disposed = true; Stop(); WinUIRoot = null; }
}
