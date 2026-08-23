using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.GtkDesigner.Host;

static class Program
{
	static int Main(string[] args) => DesignerChildHost.Run(args, "GtkDesigner.Host", token => new GtkDesignerHostService(token));
}
