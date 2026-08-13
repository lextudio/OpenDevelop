using System;
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

    public ProGpuWinUIHostControl()
    {
        Focusable = true;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    void Start()
    {
        if (loaded || disposed) return;
        context = new WgpuContext();
        context.Initialize(null);
        compositor = new Compositor(context, TextureFormat.Bgra8Unorm);
        loaded = true;
        // WPF arranges before it raises Loaded, so the arrange that sized this control already ran
        // while EnsureSurface was still short-circuiting on !loaded. Without re-arranging here the
        // render target is never created and RenderFrame returns on every tick.
        InvalidateArrange();
        CompositionTarget.Rendering += RenderFrame;
    }

    void Stop()
    {
        if (!loaded) return;
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
            var dpi = VisualTreeHelper.GetDpi(this);
            WinUIRoot.UpdateAnimations(1f / 60f);
            WinUIRoot.Measure(new Vector2((float)ActualWidth, (float)ActualHeight));
            WinUIRoot.Arrange(new ProGPU.Scene.Rect(0, 0, (float)ActualWidth, (float)ActualHeight));
            compositor.RenderOffscreen(WinUIRoot, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, texture, 0, (float)dpi.DpiScaleX);
            CopyTextureToBitmap();
            HasPresentedFrame = true;
            InvalidateVisual();
        }
        finally { rendering = false; }
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
        base.OnRender(drawingContext);
        if (bitmap != null) drawingContext.DrawImage(bitmap, new System.Windows.Rect(0, 0, ActualWidth, ActualHeight));
    }

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
