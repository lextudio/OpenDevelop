#if MEWUI_RENDER
using System.Globalization;
using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using ICSharpCode.SharpDevelop.Designer.Remote;
using LeXtudio.MewUI.Xaml;

namespace ICSharpCode.MewUIDesigner.Host;

/// <summary>Instantiates the real MewUI built-in controls for an MXAML tree and renders them
/// offscreen through the window's own frame path (theme, DPI and layout are the window's).</summary>
static class MewUIRenderer
{
	const int DefaultWidth = 480, DefaultHeight = 360;
	static readonly object gate = new();
	static IRenderDevice? device;
	static readonly Assembly controls = typeof(Button).Assembly;

	static readonly string version = typeof(Button).Assembly.GetName().Version?.ToString() ?? "?";
	static string? cachedText; static Result? cachedResult;

	/// <summary>Bounds are keyed by tree path ("0", "0,2", ...), so a cached frame stays valid for a re-parsed document. They are ABSOLUTE (window) coordinates:
	/// MewUI's Element.Bounds is the arranged rect in the root's space, not the parent's
	/// (measured: a Button inside a StackPanel with a 40px left margin reports X=40).</summary>
	public sealed record Result(DesignerRenderFrame Frame, Dictionary<string, (double X, double Y, double Width, double Height)> Bounds, List<string> Diagnostics);

	/// <summary>Renders <paramref name="root"/>; <paramref name="text"/> is the document's canonical
	/// text, and an unchanged text reuses the previous frame.</summary>
	public static Result TryRender(MxamlObject root, string text, long sequence)
	{
		lock (gate) {
			if (cachedResult != null && cachedText == text) {
				cachedResult.Frame.Sequence = sequence;
				return cachedResult;
			}
			var diagnostics = new List<string>();
			IRenderSurface? surface = null;
			try {
				if (EnsureDevice(diagnostics) is not { } renderFrame) return new Result(new DesignerRenderFrame(), new(), diagnostics);
				var map = new Dictionary<Element, string>();
				var isWindow = root.Type == "Window";
				var content = isWindow ? (root.Children.Count > 0 ? Build(root.Children[0], "0,0", map, diagnostics) : null) : Build(root, "0", map, diagnostics);
				var width = Size(root, "Width", DefaultWidth); var height = Size(root, "Height", DefaultHeight);
				var window = new Window { Content = content };
				// The frame path draws what is already arranged; it does not lay out by itself.
				if (content != null) { content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); }
				surface = device!.CreateSurface(RenderSurfaceDescriptor.Offscreen(width, height, 1.0, true, "mewui-design"));
				renderFrame.Invoke(window, new object[] { surface });
				device.FlushAsyncWork().Wait();
				var pixels = new byte[width * height * 4];
				if (!device.TryReadPixels(surface, pixels, width * 4)) { device.RequestReadback(surface).Wait(); device.TryReadPixels(surface, pixels, width * 4); }
				var bounds = map.ToDictionary(p => p.Value, p => (p.Key.Bounds.X, p.Key.Bounds.Y, p.Key.Bounds.Width, p.Key.Bounds.Height));
				if (isWindow) bounds["0"] = (0, 0, width, height);
				cachedText = text;
				return cachedResult = new Result(new DesignerRenderFrame { Sequence = sequence, Width = width, Height = height, Dpi = 1, Data = DesignerFrameCodec.EncodeDeflateBase64(pixels) }, bounds, diagnostics);
			} catch (Exception e) {
				Console.Error.WriteLine("MewUI render failed: " + e);
				diagnostics.Add($"MewUI {version} render failed: {e.GetBaseException().Message}");
				return new Result(new DesignerRenderFrame(), new(), diagnostics);
			} finally {
				(surface as IDisposable)?.Dispose();
			}
		}
	}

	/// <summary>Binds the MewUI internals the offscreen render needs. Each is non-public or loaded by
	/// name, so a MewUI upgrade that moves one names it in a diagnostic instead of failing blind.</summary>
	static MethodInfo? EnsureDevice(List<string> diagnostics)
	{
		// GDI on Windows: a software backend, so an offscreen frame needs no GPU device or window.
		var (backendDll, backendType) = OperatingSystem.IsWindows()
			? ("Aprillz.MewUI.Backend.Gdi.dll", "GdiGraphicsFactory")
			: ("Aprillz.MewUI.Backend.MewVG.MacOS.dll", "MewVGMacOSGraphicsFactory");
		var renderFrame = typeof(Window).GetMethod("RenderFrameToSurface", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, new[] { typeof(IRenderSurface) });
		if (renderFrame == null) { diagnostics.Add($"MewUI {version}: Window.RenderFrameToSurface(IRenderSurface) not found; the designer needs updating for this MewUI version."); return null; }
		if (device != null) return renderFrame;
		var path = Path.Combine(AppContext.BaseDirectory, backendDll);
		if (!File.Exists(path)) { diagnostics.Add($"MewUI {version}: backend {backendDll} is not deployed next to the host."); return null; }
		var backend = Assembly.LoadFrom(path).GetTypes().FirstOrDefault(t => t.Name == backendType);
		if (backend == null) { diagnostics.Add($"MewUI {version}: {backendType} not found in {backendDll}."); return null; }
		if ((backend.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? Activator.CreateInstance(backend, true)) is not IRenderDevice renderDevice || renderDevice is not IGraphicsFactory factory) { diagnostics.Add($"MewUI {version}: {backendType} is not an IGraphicsFactory/IRenderDevice."); return null; }
		// The setter is non-public; controls resolve their text/graphics services through it.
		var setter = typeof(Application).GetProperty("DefaultGraphicsFactory", BindingFlags.Public | BindingFlags.Static)?.GetSetMethod(true);
		if (setter == null) { diagnostics.Add($"MewUI {version}: Application.DefaultGraphicsFactory setter not found."); return null; }
		setter.Invoke(null, new object[] { factory });
		device = renderDevice;
		return renderFrame;
	}

	static int Size(MxamlObject n, string name, int fallback) => double.TryParse(n.FindAttribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? (int)Math.Ceiling(v) : fallback;

	static Element? Build(MxamlObject node, string path, Dictionary<Element, string> map, List<string> diagnostics)
	{
		var type = controls.GetType("Aprillz.MewUI.Controls." + node.Type);
		if (type == null || !typeof(Element).IsAssignableFrom(type) || Activator.CreateInstance(type) is not Element element) {
			diagnostics.Add("Unknown MewUI control: " + node.Type);
			return new Label { Text = node.Type };
		}
		map[element] = path;
		foreach (var a in node.Attributes.Where(a => !a.IsEvent)) Apply(element, a.Name, a.Value, diagnostics);
		var children = node.Children.Select((c, i) => Build(c, path + "," + i, map, diagnostics)).OfType<Element>().ToArray();
		if (children.Length > 0) {
			if (element is Panel panel) panel.Children(children);
			else if (type.GetProperty("Content") is { CanWrite: true } content && content.PropertyType.IsAssignableFrom(typeof(Element))) content.SetValue(element, children[0]);
			else if (type.GetProperty("Child") is { CanWrite: true } child) child.SetValue(element, children[0]);
			else diagnostics.Add(node.Type + " cannot hold children");
		}
		return element;
	}

	static void Apply(Element element, string name, string value, List<string> diagnostics)
	{
		if (name is "Class") return;
		var dot = name.IndexOf('.');
		if (dot > 0) { ApplyAttached(element, name[..dot], name[(dot + 1)..], value, diagnostics); return; }
		var property = element.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
		// MXAML written against MewUI 0.12 used Text on content controls; 0.22 takes a Label.
		if (property == null && name == "Text") property = element.GetType().GetProperty("Content");
		if (property == null || !property.CanWrite) { diagnostics.Add($"{element.GetType().Name}.{name} is not settable"); return; }
		if (typeof(Element).IsAssignableFrom(property.PropertyType) && property.Name is not ("Content" or "Header")) { diagnostics.Add($"{element.GetType().Name}.{name} takes an element; only Content/Header accept text"); return; }
		try { property.SetValue(element, Convert(property.PropertyType, value)); }
		catch (Exception e) { diagnostics.Add($"{element.GetType().Name}.{name}=\"{value}\": {e.GetBaseException().Message}"); }
	}

	/// <summary>Owner.Property attributes (Grid.Row, DockPanel.Dock, Canvas.Left): MewUI exposes
	/// them as static Owner.SetProperty(Element, value) methods.</summary>
	static void ApplyAttached(Element element, string owner, string name, string value, List<string> diagnostics)
	{
		var setter = controls.GetType("Aprillz.MewUI.Controls." + owner)?.GetMethods(BindingFlags.Public | BindingFlags.Static)
			.FirstOrDefault(m => m.Name == "Set" + name && m.GetParameters() is { Length: 2 } p && p[0].ParameterType.IsInstanceOfType(element));
		if (setter == null) { diagnostics.Add($"Attached property {owner}.{name} is not known to MewUI {version}"); return; }
		try { setter.Invoke(null, new[] { element, Convert(setter.GetParameters()[1].ParameterType, value) }); }
		catch (Exception e) { diagnostics.Add($"{owner}.{name}=\"{value}\": {e.GetBaseException().Message}"); }
	}

	static object? Convert(Type target, string value)
	{
		var t = Nullable.GetUnderlyingType(target) ?? target;
		if (t == typeof(string) || t == typeof(object)) return value;
		if (typeof(Element).IsAssignableFrom(t)) return new Label { Text = value };
		if (t.IsEnum) return Enum.Parse(t, value, true);
		if (t == typeof(bool)) return bool.Parse(value);
		if (t == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
		if (t == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
		if (t == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
		if (t == typeof(Thickness)) {
			var p = value.Split(',', ' ', StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
			return p.Length switch { 1 => new Thickness(p[0]), 2 => new Thickness(p[0], p[1], p[0], p[1]), 4 => new Thickness(p[0], p[1], p[2], p[3]), _ => throw new FormatException("Thickness") };
		}
		var parse = t.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
		if (parse != null) return parse.Invoke(null, new object[] { value });
		throw new NotSupportedException("No conversion to " + t.Name);
	}
}
#endif
