using System;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WpfDesign.SurfaceHost;

static class Program
{
	[STAThread]
	static int Main(string[] args)
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
}
