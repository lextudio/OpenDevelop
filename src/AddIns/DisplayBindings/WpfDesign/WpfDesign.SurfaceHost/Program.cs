using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WpfDesign.SurfaceHost;

static class Program
{
	[STAThread]
	static int Main(string[] args)
	{
		try
		{
			// The portable LibreWPF package describes System.Windows.Extensions as a Windows
			// runtime asset. On macOS the child host's deps graph consequently has no selected
			// candidate, although the host payload contains the portable DLL beside this entry
			// assembly. Resolve such addin-local dependencies from the host payload; do not use
			// a machine-wide probing path or a RID-specific asset.
			AssemblyLoadContext.Default.Resolving += ResolvePayloadAssembly;
			#if MICROSOFT_WPF
			Console.Error.WriteLine("WpfDesign.SurfaceHost: runtime=MicrosoftWpf (isolated MicrosoftHost payload).");
			#else
			Console.Error.WriteLine("WpfDesign.SurfaceHost: runtime=LibreWPF (isolated Host payload).");
			#endif
			// GLFW/ProGPU display initialization must happen on the macOS process main thread. A
			// background WPF dispatcher works for small headless fixtures but deadlocks in glfwInit as
			// soon as a real control template queries SystemParameters. Keep WPF on Main and move the
			// socket/RPC wait loop to a worker instead.
			var dispatcher = new WpfHeadlessDispatcher(useCurrentThread: true);
			// A live Application instance registers WPF's "pack" URI scheme handler, which
			// Application.GetResourceStream (and therefore a custom control's implicit
			// themes/generic.xaml default-style lookup) relies on even outside a GUI app. No
			// MainWindow, no Run() - this process pumps its own dispatcher loop below.
			if (Application.Current == null)
				new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
			var host = Task.Run(() => DesignerChildHost.Run(args, "WpfDesign.SurfaceHost",
				token => new MultiDocumentWpfSurfaceHostService(token, dispatcher),
				afterShutdown: dispatcher.Shutdown));
			dispatcher.Run();
			return host.GetAwaiter().GetResult();
		}
		catch (Exception exception)
		{
			// Dispatcher shutdown itself runs on the macOS process main thread and is outside
			// DesignerChildHost's worker-task boundary. Never let that disposable child exception
			// escape Main: CoreCLR turns it into abort(), producing a system crash dialog.
			Console.Error.WriteLine($"WpfDesign.SurfaceHost: fatal dispatcher error: {exception}");
			return 1;
		}
	}

	static Assembly? ResolvePayloadAssembly(AssemblyLoadContext _, AssemblyName name)
	{
		if (string.IsNullOrEmpty(name.Name))
			return null;
		var candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
		return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
	}
}
