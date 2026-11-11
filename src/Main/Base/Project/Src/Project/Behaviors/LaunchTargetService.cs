// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// The launch-target drop-down's state for a project and what choosing an entry does. The view
	/// (<c>LaunchTargetComboBox</c>) and the DevFlow actions share it, so the toolbar and automation
	/// make the same changes.
	/// </summary>
	public static class LaunchTargetService
	{
		public sealed record State(bool Visible, string ActiveTargetFramework, IReadOnlyList<LaunchTargetEntry> Entries);

		static readonly State Hidden = new State(false, null, Array.Empty<LaunchTargetEntry>());

		/// <summary>
		/// The entries to show for <paramref name="project"/>, or a hidden state for a project that is not
		/// multi-target and names no platform in its profiles, or has nothing to list or add. A launchSettings.json
		/// that is not valid JSON or cannot be read shows nothing here; starting the project reports the error.
		/// </summary>
		public static State GetState(IProject project)
		{
			if (project == null)
				return Hidden;
			var targetFrameworks = ProjectTargetFrameworkService.GetTargetFrameworks(project);
			if (targetFrameworks.Count == 0)
				return Hidden;
			IReadOnlyList<LaunchSettingsProfile> profiles;
			try {
				profiles = LaunchSettingsProfile.LoadAll(project.Directory);
			} catch (Exception ex) when (ex is ProjectStartException || ex is IOException || ex is UnauthorizedAccessException) {
				return Hidden;
			}
			if (!LaunchTargetModel.ShouldShow(targetFrameworks, profiles))
				return Hidden;
			string active = ProjectTargetFrameworkService.GetActiveTargetFramework(project);
			string selected = project.Preferences.Get(LaunchSettingsProfile.SelectedProfilePreferenceKey, string.Empty);
			var entries = LaunchTargetModel.Build(project.Name, targetFrameworks, profiles, active, selected).ToList();
			var chosen = entries.FirstOrDefault(e => e.Kind == LaunchTargetEntryKind.Profile && e.IsSelected);
			string platform = chosen == null ? null : LaunchSettingsProfile.GetPlatform(chosen.TargetFramework ?? string.Empty);
			var source = LaunchTargetDeviceSources.ForPlatform(platform);
			if (source != null)
				entries.AddRange(LaunchTargetModel.BuildDevices(platform, source.GetDevices(project)));
			// Several frameworks but nothing to choose or add (net10.0;netstandard2.0 without profiles): no empty drop-down.
			return entries.Count == 0 ? Hidden : new State(true, active, entries);
		}

		/// <summary>
		/// The profile Run and Debug apply for <paramref name="project"/>: what the drop-down shows as selected,
		/// so the toolbar and the launch agree. For a project without target frameworks (not MSBuild based) it is
		/// the stored profile, else dotnet run's pick. Throws <see cref="ProjectStartException"/> for invalid JSON.
		/// </summary>
		public static LaunchSettingsProfile GetLaunchProfile(IProject project)
		{
			var targetFrameworks = ProjectTargetFrameworkService.GetTargetFrameworks(project);
			if (targetFrameworks.Count == 0)
				return LaunchSettingsProfile.LoadSelected(project.Directory, project.Preferences);
			return LaunchTargetModel.SelectProfile(targetFrameworks, LaunchSettingsProfile.LoadAll(project.Directory),
				ProjectTargetFrameworkService.GetActiveTargetFramework(project),
				project.Preferences.Get(LaunchSettingsProfile.SelectedProfilePreferenceKey, string.Empty));
		}

		/// <summary>
		/// Chooses <paramref name="entry"/>: an AddProfile entry first writes its profile into
		/// launchSettings.json; then the profile becomes the project's selected one and its framework,
		/// if it names one, the active target framework.
		/// </summary>
		/// <exception cref="LaunchSettingsEditException">The profile could not be added, or a device entry has no source; nothing changed.</exception>
		public static void Apply(IProject project, LaunchTargetEntry entry)
		{
			if (project == null)
				throw new ArgumentNullException(nameof(project));
			if (entry == null)
				throw new ArgumentNullException(nameof(entry));
			if (entry.Kind == LaunchTargetEntryKind.Device) {
				var source = LaunchTargetDeviceSources.ForPlatform(entry.Platform)
					?? throw new LaunchSettingsEditException("No device list is available for the platform '" + entry.Platform + "'.");
				source.Select(project, entry.DeviceId);
				return;
			}
			if (entry.Kind == LaunchTargetEntryKind.AddProfile)
				LaunchSettingsEditor.AddProfileToFile(LaunchSettingsProfile.GetPath(project.Directory), entry.ProfileName, entry.Platform);
			// The profile first: changing the framework raises an event whose handlers read the selection.
			project.Preferences.Set(LaunchSettingsProfile.SelectedProfilePreferenceKey, entry.ProfileName);
			project.SavePreferences();
			if (entry.TargetFramework != null)
				ProjectTargetFrameworkService.SetActiveTargetFramework(project, entry.TargetFramework);
		}
	}
}
