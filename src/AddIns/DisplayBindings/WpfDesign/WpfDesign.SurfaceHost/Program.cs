using System;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WpfDesign.SurfaceHost;

static class Program
{
	[STAThread]
	static int Main(string[] args)
	{
		WpfHeadlessDispatcher? dispatcher = null;
		return DesignerChildHost.Run(args, "WpfDesign.SurfaceHost",
			token => new WpfSurfaceHostService(token, dispatcher = new WpfHeadlessDispatcher()),
			afterShutdown: () => dispatcher?.Shutdown());
	}
}
