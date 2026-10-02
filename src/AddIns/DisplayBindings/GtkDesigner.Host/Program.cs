using ICSharpCode.GtkDesigner;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.GtkDesigner.Host;

static class Program
{
	static int Main(string[] args)
	{
		MacBackgroundApplication.Apply();
		Gtk.Module.Initialize();
		if (args.Length >= 2 && args[0] == "--probe") return Probe.Run(args[1]);
		MacBackgroundApplication.Apply();
		return DesignerChildHost.Run(args, "GtkDesigner.Host", token => new GtkDesignerHostService(token));
	}
}

/// <summary>Offline render probe: builds a .ui document exactly as a preview does and reports what GTK
/// actually allocated at each step. The real render happens in a child process behind RPC, so a frame
/// that disagrees with the document's declared default size is otherwise only visible as a stretched
/// preview. This says which of measure, default size or allocate is responsible.</summary>
static class Probe
{
	public static int Run(string fileName)
	{
		// Report the document as the preview pipeline delivers it, not as authored: the same GTK code
		// allocates 800x600 from the raw file and something else from the sanitized one, and the
		// difference is the whole question.
		if (Sanitized(fileName) is { } sanitized) {
			var temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "od-probe-" + Guid.NewGuid().ToString("N") + ".ui");
			File.WriteAllText(temporary, sanitized);
			try { Report(temporary, "SANITIZED"); } finally { File.Delete(temporary); }
			return Report(fileName, "ORIGINAL");
		}
		return Report(fileName, "ORIGINAL");
	}

	/// <summary>The document as PreviewXml produces it, or null when there is no GIR catalogue to
	/// sanitize against.</summary>
	static string? Sanitized(string fileName)
	{
		if (GtkPropertyMetadata.Catalog is not { } catalog) { Console.WriteLine("(no GIR catalogue; sanitizing skipped)"); return null; }
		var document = System.Xml.Linq.XDocument.Parse(File.ReadAllText(fileName), System.Xml.Linq.LoadOptions.PreserveWhitespace);
		foreach (var signal in document.Descendants().Where(e => e.Name.LocalName == "signal").ToList()) signal.Remove();
		var diagnostics = new List<string>();
		return GtkPreviewSanitizer.Sanitize(document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting),
			catalog, diagnostics, GtkPropertyMetadata.LayoutChildClass, _ => null);
	}

	static int Report(string fileName, string label)
	{
		Console.WriteLine("=== " + label + " ===");
		using var builder = Gtk.Builder.New();
		builder.AddFromFile(fileName);
		// The builder hands out objects only by name, so take the ids from the document itself.
		var ids = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(fileName), "id\\s*=\\s*\"([^\"]+)\"")
			.Select(m => m.Groups[1].Value).Distinct().ToList();
		var root = ids.Select(id => builder.GetObject(id)).OfType<Gtk.Widget>().FirstOrDefault();
		if (root == null) { Console.Error.WriteLine("no widget in " + fileName); return 1; }
		Console.WriteLine("root: " + root.GetType().Name + " (" + ids[0] + ")");
		root.Measure(Gtk.Orientation.Horizontal, -1, out _, out var naturalWidth, out _, out _);
		root.Measure(Gtk.Orientation.Vertical, naturalWidth, out _, out var naturalHeight, out _, out _);
		Console.WriteLine($"natural: {naturalWidth}x{naturalHeight}");
		var width = Math.Max(1, naturalWidth);
		var height = Math.Max(1, naturalHeight);
		if (root is Gtk.Window window) {
			window.GetDefaultSize(out var defaultWidth, out var defaultHeight);
			Console.WriteLine($"GetDefaultSize: {defaultWidth}x{defaultHeight}");
			width = Math.Max(width, defaultWidth); height = Math.Max(height, defaultHeight);
		}
		Console.WriteLine($"frame: {width}x{height}");
		root.SetSizeRequest(width, height);
		root.Realize();
		root.Allocate(width, height, -1, null);
		var paintTarget = root is Gtk.Window mapped ? mapped.GetChild() ?? root : root;
		Console.WriteLine("content after allocate: " + paintTarget.GetWidth() + "x" + paintTarget.GetHeight());
		for (var attempt = 0; attempt < 4; attempt++) {
			paintTarget.Allocate(width, height, -1, null);
			paintTarget.QueueDraw();
			var context = GLib.MainContext.Default();
			for (var i = 0; i < 8 && context.Pending(); i++) context.Iteration(false);
			Console.WriteLine($"  settle {attempt}: " + paintTarget.GetWidth() + "x" + paintTarget.GetHeight());
		}
		Console.Write(Tree(paintTarget, 1));
		return 0;
	}

	/// <summary>GTK4 dropped GtkContainer: children come from the first-child / next-sibling chain.</summary>
	static string Tree(Gtk.Widget widget, int depth)
	{
		if (depth > 8) return "";
		var text = new System.Text.StringBuilder();
		text.Append(new string(' ', depth * 2)).Append(widget.GetType().Name)
			.Append(" size=").Append(widget.GetWidth()).Append('x').Append(widget.GetHeight()).Append('\n');
		for (var child = widget.GetFirstChild(); child != null; child = child.GetNextSibling())
			text.Append(Tree(child, depth + 1));
		return text.ToString();
	}
}

static class MacBackgroundApplication
{
	const long AccessoryActivationPolicy = 1;
	[System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")] static extern nint objc_getClass(string name);
	[System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib")] static extern nint sel_registerName(string name);
	[System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] static extern nint Send(nint receiver, nint selector);
	[System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] static extern bool SendPolicy(nint receiver, nint selector, long policy);

	public static void Apply()
	{
		if (!OperatingSystem.IsMacOS()) return;
		try {
			MacProcessPresentation.TransformCurrentProcessToBackground();
			var applicationClass = objc_getClass("NSApplication");
			var application = Send(applicationClass, sel_registerName("sharedApplication"));
			SendPolicy(application, sel_registerName("setActivationPolicy:"), AccessoryActivationPolicy);
		} catch { }
	}
}

static class MacProcessPresentation
{
	const uint BackgroundApplication = 2;
	const uint UIElementApplication = 4;

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	struct ProcessSerialNumber
	{
		public uint HighLongOfPSN;
		public uint LowLongOfPSN;
	}

	[System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
	static extern int GetCurrentProcess(out ProcessSerialNumber psn);
	[System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
	static extern int GetProcessForPID(int pid, out ProcessSerialNumber psn);
	[System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
	static extern int TransformProcessType(ref ProcessSerialNumber psn, uint transformState);

	public static void TransformCurrentProcessToBackground()
	{
		if (!OperatingSystem.IsMacOS()) return;
		if (GetCurrentProcess(out var psn) == 0)
			Transform(ref psn);
	}

	public static void TransformProcessToBackground(int processId)
	{
		if (!OperatingSystem.IsMacOS()) return;
		try {
			if (GetProcessForPID(processId, out var psn) == 0)
				Transform(ref psn);
		} catch { }
	}

	static void Transform(ref ProcessSerialNumber psn)
	{
		if (TransformProcessType(ref psn, UIElementApplication) != 0)
			TransformProcessType(ref psn, BackgroundApplication);
	}
}
