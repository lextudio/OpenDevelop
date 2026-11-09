// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

#nullable enable

using System;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.PackageManagement
{
	/// <summary>Where NuGet's configuration puts installed packages for the open solution.</summary>
	static class GlobalPackagesFolder
	{
		/// <summary>The global packages folder (null when the configuration cannot be read). Reads the solution model: UI thread.</summary>
		public static string? ForOpenSolution()
		{
			try {
				var settings = NuGet.Configuration.Settings.LoadDefaultSettings(SD.ProjectService.CurrentSolution?.Directory.ToString());
				return NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder(settings);
			} catch (Exception ex) {
				LoggingService.Debug("Could not determine the global packages folder: " + ex.Message);
				return null;
			}
		}
	}
}
