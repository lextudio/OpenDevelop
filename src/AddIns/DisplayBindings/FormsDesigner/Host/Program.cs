using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.FormsDesigner.Host;

static class Program
{
	static int Main(string[] args) =>
		DesignerChildHost.Run(args, "FormsDesigner.Host", token => new DesignerHostService(token));
}
