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
			token => {
				dispatcher = new WpfHeadlessDispatcher();
				return new MultiDocumentWpfSurfaceHostService(token, dispatcher);
			},
			afterShutdown: () => dispatcher?.Shutdown());
	}
}
