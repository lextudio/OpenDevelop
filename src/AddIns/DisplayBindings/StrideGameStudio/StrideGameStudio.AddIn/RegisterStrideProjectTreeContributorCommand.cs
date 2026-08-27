// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Autostart command that registers the Stride .sdpkg tree contribution. Declared on
// /SharpDevelop/Autostart (see ICSharpCode.StrideGameStudio.addin) so this addin's assembly loads
// at OpenDevelop startup and the contributor is in place before any solution tree is built -
// same pattern as XamlBinding/ILSpyAddIn's RegisterDevFlowActionsCommand.

using System;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Services;

namespace ICSharpCode.StrideGameStudio
{
	/// <summary>
	/// Registers <see cref="StrideProjectTreeContributor"/> so Solution Explorer shows the
	/// .sdpkg-backed Assets subtree under every Stride game project.
	/// </summary>
	public sealed class RegisterStrideProjectTreeContributorCommand : AbstractCommand
	{
		public override void Run()
		{
			ProjectTreeContributorRegistry.Register(new StrideProjectTreeContributor());

			// A Stride game project is a class library, so a solution opened straight from it has
			// nothing startable and Run/Debug is dead. Resolve (or generate) the launcher up front
			// and make it the startup project, quietly - the Output pad carries the record, since
			// a modal prompt on every solution open would be noise for a one-time, reversible
			// scaffold the user can just delete.
			SD.ProjectService.SolutionOpened += SolutionOpened;
		}

		static void SolutionOpened(object sender, SolutionEventArgs e)
		{
			try {
				var result = StrideLauncherService.EnsureLauncher(e.Solution);
				if (result.Status == "not-a-stride-solution")
					return;

				var output = SD.OutputPad.GetOrCreateCategory("Stride");
				if (!result.Success) {
					output.AppendLine("Stride: could not prepare a launcher project - " + result.Error);
					LoggingService.Warn("Stride: launcher preparation failed - " + result.Error);
					return;
				}

				switch (result.Status) {
					case "generated":
						output.AppendLine($"Stride: generated launcher project '{result.LauncherProjectName}' ({result.LauncherProjectPath}) for game '{result.GameProjectName}' and made it the startup project - '{result.GameProjectName}' is a class library and cannot be started directly.");
						break;
					case "added-existing":
						output.AppendLine($"Stride: added the existing launcher project '{result.LauncherProjectName}' to the solution and made it the startup project.");
						break;
					default:
						output.AppendLine($"Stride: using launcher project '{result.LauncherProjectName}' as the startup project.");
						break;
				}
			} catch (Exception ex) {
				// Never let launcher scaffolding break opening a solution.
				LoggingService.Warn("Stride: launcher preparation threw - " + ex);
			}
		}
	}
}