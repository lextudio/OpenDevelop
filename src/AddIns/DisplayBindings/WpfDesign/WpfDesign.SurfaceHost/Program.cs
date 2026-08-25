using System;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WpfDesign.SurfaceHost;

static class Program
{
	[STAThread]
	static int Main(string[] args)
	{
		try
		{
			// GLFW/ProGPU display initialization must happen on the macOS process main thread. A
			// background WPF dispatcher works for small headless fixtures but deadlocks in glfwInit as
			// soon as a real control template queries SystemParameters. Keep WPF on Main and move the
			// socket/RPC wait loop to a worker instead.
			var dispatcher = new WpfHeadlessDispatcher(useCurrentThread: true);
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
}
