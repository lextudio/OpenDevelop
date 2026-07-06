// DevFlow actions used by tests/OpenDevelop.IntegrationTests to drive the app without a native
// file-open dialog (which the WPF-embedded DevFlow agent can't see/control - see
// doc/technotes/csharp-roslyn.md session notes). Static methods on a [DevFlowUIThread]-annotated
// class are auto-discovered by LeXtudio.DevFlow.Agent.Core and dispatched to the UI thread.

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Workbench;
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

		[DevFlowAction("od.build-solution", Description = "Build the current solution (or a single project by name) and return error/warning counts plus the individual diagnostics")]
		public static async Task<string> BuildSolution(string projectName = null)
		{
			var solution = SD.ProjectService.CurrentSolution;
			if (solution == null)
				return JsonSerializer.Serialize(new { success = false, error = "No solution is open." });

			var options = new BuildOptions(BuildTarget.Build);

			BuildResults results;
			if (string.IsNullOrEmpty(projectName)) {
				results = await SD.BuildService.BuildAsync(solution, options);
			} else {
				var project = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
				if (project == null)
					return JsonSerializer.Serialize(new { success = false, error = $"No project named '{projectName}' in the current solution." });
				results = await SD.BuildService.BuildAsync(project, options);
			}

			return JsonSerializer.Serialize(new {
				success = true,
				result = results.Result.ToString(),
				errorCount = results.ErrorCount,
				warningCount = results.WarningCount,
				messageCount = results.MessageCount,
				diagnostics = results.Errors.Select(e => new {
					isWarning = e.IsWarning,
					isMessage = e.IsMessage,
					fileName = e.FileName,
					line = e.Line,
					column = e.Column,
					errorCode = e.ErrorCode,
					errorText = e.ErrorText
				}).ToArray()
			});
		}

		[DevFlowAction("od.output-text", Description = "Get the full accumulated text of an Output pad category (default: 'Build')")]
		public static string GetOutputText(string category = "Build")
		{
			IOutputCategory outputCategory = string.Equals(category, "Build", StringComparison.OrdinalIgnoreCase)
				? SD.OutputPad.BuildCategory
				: SD.OutputPad.GetOrCreateCategory(category);

			string text = (outputCategory as MessageViewCategory)?.Text;
			return JsonSerializer.Serialize(new {
				category,
				text = text ?? string.Empty
			});
		}
	}
}
