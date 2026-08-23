using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.GtkDesigner.Host;

static class Program
{
	static int Main(string[] args)
	{
		MacBackgroundApplication.Apply();
		Gtk.Module.Initialize();
		MacBackgroundApplication.Apply();
		return DesignerChildHost.Run(args, "GtkDesigner.Host", token => new GtkDesignerHostService(token));
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
