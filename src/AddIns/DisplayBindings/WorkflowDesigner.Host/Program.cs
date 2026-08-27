using ICSharpCode.SharpDevelop.Designer.Remote;
namespace ICSharpCode.WorkflowDesigner.Host;
static class Program { static int Main(string[] args) => DesignerChildHost.Run(args, "WorkflowDesigner.Host", token => new WorkflowDesignerHostService(token)); }
