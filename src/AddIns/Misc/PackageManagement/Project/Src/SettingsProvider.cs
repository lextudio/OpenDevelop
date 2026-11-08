// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using NuGet;

namespace ICSharpCode.PackageManagement
{
	public class SettingsProvider : ISettingsProvider
	{
		public static Func<string, ISettings> LoadDefaultSettings
			= directory => new PortableNuGetSettings(directory);
		
		IPackageManagementProjectService projectService;
		
		public SettingsProvider()
			: this(PackageManagementServices.ProjectService)
		{
		}
		
		public SettingsProvider(IPackageManagementProjectService projectService)
		{
			this.projectService = projectService;
			projectService.SolutionOpened += OnSettingsChanged;
			projectService.SolutionClosed += OnSettingsChanged;
		}
		
		public event EventHandler SettingsChanged;
		
		void OnSettingsChanged(object sender, SolutionEventArgs e)
		{
			if (SettingsChanged != null) {
				SettingsChanged(this, new EventArgs());
			}
		}
		
		public ISettings LoadSettings()
		{
			try {
				return LoadSettings(GetSolutionDirectory());
			} catch (Exception ex) {
				LoggingService.Error("Unable to load NuGet.Config file.", ex);
			}
			return NullSettings.Instance;
		}
		
		string GetSolutionDirectory()
		{
			ISolution solution = projectService.OpenSolution;
			if (solution != null) {
				// NuGet reads <solution>/NuGet.Config (and the ones above it), not the NuGet 2.x
				// <solution>/.nuget/NuGet.Config this used to point at: dotnet restore ignores that file
				// (measured), so a source saved there was never used by a restore without --source.
				return solution.Directory;
			}
			return null;
		}
		
		ISettings LoadSettings(string directory)
		{
			return LoadDefaultSettings(directory);
		}
	}
}
