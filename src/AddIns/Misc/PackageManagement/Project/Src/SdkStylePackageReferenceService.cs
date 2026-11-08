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
		
		public bool IsInstalled(string packageId)
		{
			return FindPackageReference(packageId) != null;
		}
		
		public void InstallPackage(IPackage package, IPackageRepository sourceRepository, ILogger logger)
		{
			InstallPackage(package.Id, package.Version.ToString(), sourceRepository?.Source, logger);
		}

		public void InstallPackage(string packageId, string version, string source, ILogger logger)
		{
			AddPackageReference(packageId, version);
			Restore(source, logger);
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
					existing.Parent.RemoveChild(existing);
				}

				ProjectItemGroupElement itemGroup = FindPackageReferenceItemGroup()
					?? project.MSBuildProjectFile.AddItemGroup();
				if (centralVersionsFile != null) {
					itemGroup.AddItem("PackageReference", packageId);
				} else {
					itemGroup.AddItem(
						"PackageReference",
						packageId,
						new [] { new KeyValuePair<string, string>("Version", version) });
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
		public string Restore(string source, ILogger logger)
		{
			try {
				var startInfo = new ProcessStartInfo("dotnet") {
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
				};
				startInfo.ArgumentList.Add("restore");
				startInfo.ArgumentList.Add(project.FileName.ToString());
				if (!string.IsNullOrWhiteSpace(source) && !source.StartsWith("Aggregate", StringComparison.OrdinalIgnoreCase)) {
					startInfo.ArgumentList.Add("--source");
					startInfo.ArgumentList.Add(source);
				}
				
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
