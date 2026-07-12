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
			AddOrUpdatePackageReference(package.Id, package.Version.ToString());
			projectService.Save(project);
			projectService.RefreshProjectBrowser();
			Restore(sourceRepository, logger);
		}
		
		void AddOrUpdatePackageReference(string packageId, string version)
		{
			project.PerformUpdateOnProjectFile(() => {
				ProjectItemElement existing = FindPackageReference(packageId);
				if (existing != null) {
					existing.Parent.RemoveChild(existing);
				}
				
				ProjectItemGroupElement itemGroup = FindPackageReferenceItemGroup()
					?? project.MSBuildProjectFile.AddItemGroup();
				itemGroup.AddItem(
					"PackageReference",
					packageId,
					new [] { new KeyValuePair<string, string>("Version", version) });
			});
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
		
		void Restore(IPackageRepository sourceRepository, ILogger logger)
		{
			try {
				var startInfo = new ProcessStartInfo("dotnet") {
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
				};
				startInfo.ArgumentList.Add("restore");
				startInfo.ArgumentList.Add(project.FileName.ToString());
				string source = sourceRepository?.Source;
				if (!string.IsNullOrWhiteSpace(source) && !sourceRepository.Source.StartsWith("Aggregate", StringComparison.OrdinalIgnoreCase)) {
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
					}
				}
			} catch (Exception ex) {
				logger?.Log(MessageLevel.Warning, "Unable to restore packages after installing '{0}': {1}", project.Name, ex.Message);
			}
		}
	}
}
