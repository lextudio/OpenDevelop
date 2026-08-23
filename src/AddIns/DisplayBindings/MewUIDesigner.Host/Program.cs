using ICSharpCode.SharpDevelop.Designer.Remote;
namespace ICSharpCode.MewUIDesigner.Host;
static class Program { static int Main(string[] args) => DesignerChildHost.Run(args, "MewUIDesigner.Host", token => new MewUIDesignerHostService(token)); }
