// DevFlow actions used by tests/OpenDevelop.IntegrationTests to drive the app without a native
// file-open dialog (which the WPF-embedded DevFlow agent can't see/control - see
// doc/technotes/csharp-roslyn.md session notes). Static methods on a [DevFlowUIThread]-annotated
// class are auto-discovered by LeXtudio.DevFlow.Agent.Core and dispatched to the UI thread.

using System;
using System.Linq;
using System.Text.Json;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Project;
using LeXtudio.DevFlow.Agent.Core;

namespace ICSharpCode.SharpDevelop.DevFlow
{
	[DevFlowUIThread]
	public static class OpenDevelopDevFlowActions
	{
		[DevFlowAction("od.open-solution", Description = "Open a solution or project file by path (bypasses the native Open dialog)")]
		public static string OpenSolution(string path)
		{
			bool ok = SD.ProjectService.OpenSolutionOrProject(FileName.Create(path));
			return JsonSerializer.Serialize(new { success = ok, currentSolution = SD.ProjectService.CurrentSolution?.FileName?.ToString() });
		}

		[DevFlowAction("od.solution-tree", Description = "Get the current solution's project/file tree, as seen by Solution Explorer")]
		public static string GetSolutionTree()
		{
			var solution = SD.ProjectService.CurrentSolution;
			if (solution == null)
				return JsonSerializer.Serialize(new { solutionFile = (string)null, projects = Array.Empty<object>() });

			var projects = solution.Projects.Select(p => new {
				name = p.Name,
				fileName = p.FileName != null ? p.FileName.ToString() : null,
				files = p.Items.OfType<FileProjectItem>()
					.Where(i => i.ItemType == ItemType.Compile)
					.Select(i => i.FileName.ToString())
					.ToArray()
			}).ToArray();

			return JsonSerializer.Serialize(new {
				solutionFile = solution.FileName != null ? solution.FileName.ToString() : null,
				projects
			});
		}

		[DevFlowAction("od.open-file", Description = "Open a file in the editor by path (bypasses the native Open dialog)")]
		public static string OpenFile(string path)
		{
			var viewContent = SD.FileService.OpenFile(FileName.Create(path));
			return JsonSerializer.Serialize(new {
				opened = viewContent != null,
				viewContentType = viewContent != null ? viewContent.GetType().FullName : null
			});
		}

		[DevFlowAction("od.active-view", Description = "Inspect the active view content - used to confirm AvalonEdit rendered an opened file")]
		public static string GetActiveView()
		{
			var viewContent = SD.Workbench.ActiveViewContent;
			if (viewContent == null)
				return JsonSerializer.Serialize(new { active = false });

			string typeName = viewContent.GetType().FullName;
			var editor = viewContent.GetService<ITextEditor>();
			string text = editor != null ? editor.Document.Text : null;

			return JsonSerializer.Serialize(new {
				active = true,
				typeName,
				isAvalonEdit = typeName == "ICSharpCode.AvalonEdit.AddIn.AvalonEditViewContent",
				fileName = viewContent.PrimaryFileName != null ? viewContent.PrimaryFileName.ToString() : null,
				textLength = text?.Length,
				textPreview = text != null ? text.Substring(0, Math.Min(200, text.Length)) : null
			});
		}
	}
}
