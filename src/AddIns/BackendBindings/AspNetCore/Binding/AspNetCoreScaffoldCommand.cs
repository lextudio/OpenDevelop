using System;
using System.Diagnostics;
using ICSharpCode.AspNetCore;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.AspNetCore.AddIn
{
	public sealed class AspNetCoreScaffoldCommand : AbstractMenuCommand
	{
		public override async void Run()
		{
			var project = ProjectService.CurrentProject;
			if (project == null) return;
			try {
				if (!await AspNetCoreScaffolding.IsInstalledAsync()) {
					MessageService.ShowMessage("The modern .NET scaffolding tool is not installed. Install it in a terminal with:\n\n" + AspNetCoreScaffolding.InstallCommand, "ASP.NET Core Scaffolding");
					return;
				}
				ICSharpCode.SharpDevelop.Commands.SaveAllFiles.SaveAll();
				Process.Start(AspNetCoreScaffolding.CreateInteractiveTerminalCommand(project.Directory));
			}
			catch (Exception ex) { MessageService.ShowError("Could not start ASP.NET Core scaffolding: " + ex.Message); }
		}
	}
}
