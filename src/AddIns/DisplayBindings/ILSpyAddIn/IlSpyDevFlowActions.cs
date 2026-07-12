// DevFlow actions used by tests/OpenDevelop.IntegrationTests to drive the hosted ILSpy addin
// without a native file-open dialog (which the WPF-embedded DevFlow agent can't see/control -
// same reasoning as OpenDevelopDevFlowActions.cs/WpfDesignDevFlowActions.cs). Static methods on a
// [DevFlowUIThread]-annotated class are auto-discovered by LeXtudio.DevFlow.Agent.Core and
// dispatched to the UI thread.
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using LeXtudio.DevFlow.Agent.Core;

namespace ICSharpCode.ILSpyAddIn
{
	[DevFlowUIThread]
	public static class IlSpyDevFlowActions
	{
		[DevFlowAction("od.ilspy.open-assembly", Description = "Open an assembly (.dll/.exe) into the hosted ILSpy AssemblyTreeModel, bypassing the native file dialog")]
		public static async Task<string> OpenAssembly(string path)
		{
			await IlSpyWorkspaceHost.OpenAssembly(path);
			return JsonSerializer.Serialize(new { opened = true, path });
		}

		[DevFlowAction("od.ilspy.status", Description = "Inspect the hosted ILSpy pads (Assemblies/Search/Analyzer/Decompiled Code): whether they're registered/visible, the assembly tree's loaded assemblies, and a snippet of the decompiled code pane")]
		public static string GetStatus()
		{
			var panes = IlSpyWorkspaceHost.Panes
				.Select(p => new { title = p.Title, contentId = p.ContentId, isVisible = p.IsVisible, isActive = p.IsActive })
				.ToArray();

			var assemblyTreeModel = IlSpyWorkspaceHost.AssemblyTreeModel;
			var loadedAssemblies = assemblyTreeModel.AssemblyList.GetAssemblies()
				.Select(a => a.ShortName)
				.ToArray();

			string decompiledText = IlSpyWorkspaceHost.DecompilerTextView.textEditor.Text;

			return JsonSerializer.Serialize(new {
				panes,
				loadedAssemblies,
				decompiledTextLength = decompiledText?.Length ?? 0,
				decompiledTextSnippet = decompiledText?.Length > 2000 ? decompiledText[..2000] : decompiledText
			});
		}
	}
}
