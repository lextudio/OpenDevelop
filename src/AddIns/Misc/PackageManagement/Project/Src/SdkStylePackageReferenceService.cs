// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.

using System;
using System.IO;
using System.Xml.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;
using Microsoft.Build.Construction;
using NuGet;

namespace ICSharpCode.PackageManagement
{
	sealed class SdkStylePackageReferenceService
	{
		readonly MSBuildBasedProject project;
		readonly IPackageManagementProjectService projectService;
		
		public SdkStylePackageReferenceService(MSBuildBasedProject project)
			: this(project, new PackageManagementProjectService())
		{
		}
		
		public SdkStylePackageReferenceService(
			MSBuildBasedProject project,
			IPackageManagementProjectService projectService)
		{
			this.project = project;
			this.projectService = projectService;
		}
		
		/// <summary>
		/// What the project references, as the legacy package objects the Installed tab shows. Safe from any thread:
		/// the project model is read on the UI thread, the nuspecs in the packages folder on the calling one.
		/// </summary>
		public IList<IPackage> GetInstalledPackages()
		{
			var (references, packagesFolder) = SD.MainThread.InvokeIfRequired(() => (
				SolutionPackages.Collect(new[] { project }),
				GlobalPackagesFolder.ForOpenSolution()));
			return InstalledSdkPackages.Read(references, packagesFolder);
		}

		/// <summary>Moves each package to the given version (a changed <c>Version</c>, or the central <c>PackageVersion</c>) and restores once.</summary>
		public void UpdatePackages(IEnumerable<IPackage> packages, ILogger logger)
		{
			foreach (IPackage package in packages) {
				AddPackageReference(package.Id, package.Version.ToString());
			}
			Restore(logger);
		}

		/// <summary>
		/// Removes the package's <c>PackageReference</c> items, saves the project, refreshes the project browser and
		/// restores. Under Central Package Management the <c>PackageVersion</c> entry stays: other projects may use it.
		/// </summary>
		public void UninstallPackage(string packageId, ILogger logger)
		{
			if (RemovePackageReference(packageId)) {
				Restore(logger);
			}
		}

		/// <summary>Removes every <c>PackageReference</c> to the package from the project, saves it and refreshes the project browser. UI thread. False when there was none.</summary>
		public bool RemovePackageReference(string packageId)
		{
			bool removed = false;
			project.PerformUpdateOnProjectFile(() => {
				List<ProjectItemElement> references = project.MSBuildProjectFile.Items
					.Where(item => string.Equals(item.ItemType, "PackageReference", StringComparison.OrdinalIgnoreCase)
						&& string.Equals(item.Include, packageId, StringComparison.OrdinalIgnoreCase))
					.ToList();
				foreach (ProjectItemElement reference in references) {
					ProjectElementContainer group = reference.Parent;
					group.RemoveChild(reference);
					if (group.Count == 0) {
						group.Parent?.RemoveChild(group);
					}
				}
				removed = references.Count > 0;
			});
			if (removed) {
				projectService.Save(project);
				projectService.RefreshProjectBrowser();
			}
			return removed;
		}

		public bool IsInstalled(string packageId)
		{
			return FindPackageReference(packageId) != null;
		}
		
		public void InstallPackage(IPackage package, IPackageRepository sourceRepository, ILogger logger)
		{
			InstallPackage(package.Id, package.Version.ToString(), logger);
		}

		public void InstallPackage(string packageId, string version, ILogger logger)
		{
			AddPackageReference(packageId, version);
			Restore(logger);
		}

		/// <summary>
		/// Adds (or replaces) the PackageReference, saves the project and refreshes the project
		/// browser. Touches the project model, so call it on the UI thread.
		/// </summary>
		public void AddPackageReference(string packageId, string version)
		{
			AddOrUpdatePackageReference(packageId, version);
			projectService.Save(project);
			projectService.RefreshProjectBrowser();
		}
		
		void AddOrUpdatePackageReference(string packageId, string version)
		{
			// Under Central Package Management NuGet rejects a Version on a PackageReference (NU1008):
			// the version belongs in a PackageVersion item of Directory.Packages.props.
			string centralVersionsFile = FindCentralPackageVersionsFile();
			if (centralVersionsFile != null) {
				// First, so that a props file that cannot be written (read-only, locked, malformed)
				// fails before the in-memory project is changed and a later save persists a reference
				// without any version. An unused PackageVersion left behind by a later failure is harmless.
				CentralPackageVersionsFile.Set(centralVersionsFile, packageId, version);
			}
			project.PerformUpdateOnProjectFile(() => {
				ProjectItemElement existing = FindPackageReference(packageId);
				if (existing != null) {
					// An update changes the version of the reference that is there, in place: replacing the item
					// would leave an empty ItemGroup behind and drop whatever else the reference carries
					// (PrivateAssets, IncludeAssets, ...).
					ProjectMetadataElement versionMetadata = existing.Metadata.FirstOrDefault(metadata => metadata.Name == "Version");
					if (centralVersionsFile != null) {
						versionMetadata?.Parent.RemoveChild(versionMetadata); // a Version here would be NU1008
					} else if (versionMetadata != null) {
						versionMetadata.Value = version;
					} else {
						existing.AddMetadata("Version", version, expressAsAttribute: true);
					}
					return;
				}

				ProjectItemGroupElement itemGroup = FindPackageReferenceItemGroup()
					?? project.MSBuildProjectFile.AddItemGroup();
				ProjectItemElement added = itemGroup.AddItem("PackageReference", packageId);
				if (centralVersionsFile == null) {
					added.AddMetadata("Version", version, expressAsAttribute: true);
				}
			});
		}

		/// <summary>The Directory.Packages.props that holds the versions when the project opts into Central Package Management, otherwise null.</summary>
		string FindCentralPackageVersionsFile()
		{
			if (!string.Equals(project.GetEvaluatedProperty("ManagePackageVersionsCentrally"), "true", StringComparison.OrdinalIgnoreCase)) {
				return null;
			}
			string path = project.GetEvaluatedProperty("DirectoryPackagesPropsPath");
			return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
		}

		ProjectItemElement FindPackageReference(string packageId)
		{
			return project.MSBuildProjectFile.Items
				.FirstOrDefault(item =>
					string.Equals(item.ItemType, "PackageReference", StringComparison.OrdinalIgnoreCase) &&
					string.Equals(item.Include, packageId, StringComparison.OrdinalIgnoreCase));
		}
		
		ProjectItemGroupElement FindPackageReferenceItemGroup()
		{
			return project.MSBuildProjectFile.ItemGroups
				.FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Condition) &&
					group.Items.Any(item => string.Equals(item.ItemType, "PackageReference", StringComparison.OrdinalIgnoreCase)));
		}
		
		/// <summary>
		/// Runs <c>dotnet restore</c> for the project and waits for it, so call it off the UI thread.
		/// Returns null on success, otherwise why it failed (also sent to <paramref name="logger"/> when given).
		/// </summary>
		public string Restore(ILogger logger)
		{
			try {
				var startInfo = new ProcessStartInfo("dotnet") {
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
				};
				startInfo.ArgumentList.Add("restore");
				startInfo.ArgumentList.Add(project.FileName.ToString());
				// No --source: dotnet restore uses the sources NuGet defines for this project (nuget.config in
				// the solution's directory and above, the user's and the machine's), the ones the dialog lists.
				using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)) {
					string output = process.StandardOutput.ReadToEnd();
					string error = process.StandardError.ReadToEnd();
					process.WaitForExit();
					if (!string.IsNullOrWhiteSpace(output)) {
						logger?.Log(MessageLevel.Info, output);
					}
					if (process.ExitCode != 0) {
						logger?.Log(MessageLevel.Warning, error);
						return DescribeRestoreFailure(process.ExitCode, error, output);
					}
				}
				return null;
			} catch (Exception ex) {
				logger?.Log(MessageLevel.Warning, "Unable to restore packages after installing '{0}': {1}", project.Name, ex.Message);
				return ex.Message;
			}
		}

		/// <summary>dotnet restore reports problems on stdout as often as on stderr; prefer an error line.</summary>
		static string DescribeRestoreFailure(int exitCode, string error, string output)
		{
			string text = string.IsNullOrWhiteSpace(error) ? output : error;
			string line = (text ?? string.Empty)
				.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
				?? (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
			return "dotnet restore exited with code " + exitCode + (string.IsNullOrEmpty(line) ? "" : ": " + line);
		}
	}
}
