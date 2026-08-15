#if !OPENDEVELOP_NO_DEVFLOW
using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using ICSharpCode.SharpDevelop;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.AspNetCore.AddIn
{
	[DevFlowUIThread]
	public static class AspNetCoreDevFlowActions
	{
		static Process process;

		[DevFlowAction("od.aspnetcore.status", Description = "Inspect the current ASP.NET Core project's AddInTree-selected launch behavior and process command")]
		public static string Status(string projectName = null)
		{
			var project = FindProject(projectName);
			if (project == null) return JsonSerializer.Serialize(new { success = false, error = "ASP.NET Core project not found" });
			try {
				var start = project.CreateStartInfo();
				return JsonSerializer.Serialize(new {
					success = true, project = project.Name, startable = project.IsStartable,
					behavior = start.FileName, arguments = start.ArgumentList.ToArray(),
					workingDirectory = start.WorkingDirectory,
					applicationUrls = start.Environment.TryGetValue("ASPNETCORE_URLS", out var urls) ? urls : null,
					blazorWebAssemblyDevServer = start.ArgumentList.Any(a => a.EndsWith("blazor-devserver.dll", StringComparison.OrdinalIgnoreCase)),
					processAlive = process is { HasExited: false }
				});
			} catch (Exception ex) { return JsonSerializer.Serialize(new { success = false, error = ex.Message }); }
		}

		[DevFlowAction("od.aspnetcore.start", Description = "Start the current ASP.NET Core project through IProject.CreateStartInfo for integration verification")]
		public static string Start(string projectName = null)
		{
			StopProcess();
			var project = FindProject(projectName);
			if (project == null) return JsonSerializer.Serialize(new { success = false, error = "ASP.NET Core project not found" });
			try {
				var start = project.CreateStartInfo();
				start.RedirectStandardOutput = true;
				start.RedirectStandardError = true;
				process = Process.Start(start);
				return JsonSerializer.Serialize(new { success = true, processId = process.Id });
			} catch (Exception ex) { return JsonSerializer.Serialize(new { success = false, error = ex.Message }); }
		}

		[DevFlowAction("od.aspnetcore.stop", Description = "Stop the ASP.NET Core process started by od.aspnetcore.start")]
		public static string Stop()
		{
			var stopped = StopProcess();
			return JsonSerializer.Serialize(new { success = true, stopped });
		}

		static ICSharpCode.SharpDevelop.Project.AbstractProject FindProject(string name)
		{
			var projects = SD.ProjectService.CurrentSolution?.Projects;
			if (projects == null) return null;
			var project = string.IsNullOrEmpty(name) ? SD.ProjectService.CurrentProject ?? projects.FirstOrDefault()
				: projects.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
			return project as ICSharpCode.SharpDevelop.Project.AbstractProject;
		}

		static bool StopProcess()
		{
			if (process == null) return false;
			try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
			catch { }
			finally { process.Dispose(); process = null; }
			return true;
		}
	}
}
#endif
