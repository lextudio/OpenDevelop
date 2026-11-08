// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

using System;
using System.IO;
using System.Linq;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Services;

namespace ICSharpCode.PackageManagement
{
	/// <summary>Opens the Updates tab for the package reference selected in Solution Explorer.</summary>
	public sealed class UpdatePackageReferenceCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			if (!(Owner is IProjectBrowserPackageReferenceContext reference)
				|| string.IsNullOrWhiteSpace(reference.PackageId))
				return;

			SelectOwningProject(reference.ProjectFileName);
			PackageManagementServices.OutputMessagesView.Clear();
			using (var view = new ManagePackagesView { Owner = SD.Workbench.MainWindow }) {
				view.ShowUpdatesFor(reference.PackageId);
				view.ShowDialog();
			}
		}

		static void SelectOwningProject(string projectFileName)
		{
			if (string.IsNullOrWhiteSpace(projectFileName) || SD.ProjectService.CurrentSolution == null)
				return;
			try {
				var fullPath = Path.GetFullPath(projectFileName);
				var project = SD.ProjectService.CurrentSolution.Projects.FirstOrDefault(candidate =>
					string.Equals(Path.GetFullPath(candidate.FileName.ToString()), fullPath, StringComparison.OrdinalIgnoreCase));
				if (project != null)
					SD.ProjectService.CurrentProject = project;
			} catch (Exception ex) {
				LoggingService.Warn("Could not select the package reference's project: " + ex.Message);
			}
		}
	}
}
