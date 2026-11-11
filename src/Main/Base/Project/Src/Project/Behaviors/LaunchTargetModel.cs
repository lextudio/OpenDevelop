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
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.SharpDevelop.Project
{
	public enum LaunchTargetEntryKind
	{
		/// <summary>An existing launchSettings.json profile; choosing it selects its target framework too.</summary>
		Profile,
		/// <summary>A target framework without a profile: choosing it writes one.</summary>
		AddProfile
	}

	/// <param name="TargetFramework">The framework a <see cref="LaunchTargetEntryKind.Profile"/> selects (null for a
	/// profile that fits every framework and so leaves the active one alone), or the one an AddProfile entry is for.</param>
	/// <param name="ProfileName">The profile's name, or the name an AddProfile entry would give the new one.</param>
	/// <param name="Platform">The <c>compatibleTargetFramework</c> an AddProfile entry would write.</param>
	public sealed record LaunchTargetEntry(LaunchTargetEntryKind Kind, string Label, string TargetFramework, string ProfileName, string Platform, bool IsSelected);

	/// <summary>
	/// What the launch-target drop-down shows, the way Visual Studio lists an Uno project's launch
	/// profiles: one entry per launchSettings.json profile that belongs to one of the project's
	/// target frameworks (a profile of a framework the project does not have is hidden, and so is a
	/// framework without a profile), then one "add a profile" entry per framework that has none.
	/// </summary>
	public static class LaunchTargetModel
	{
		/// <summary>
		/// The drop-down is only worth showing for a project that has several target frameworks or whose
		/// profiles name one; a plain single-target project keeps its toolbar as it is.
		/// </summary>
		public static bool ShouldShow(IReadOnlyList<string> targetFrameworks, IReadOnlyList<LaunchSettingsProfile> profiles)
		{
			return targetFrameworks.Count > 1 || profiles.Any(p => !string.IsNullOrWhiteSpace(p.CompatibleTargetFramework));
		}

		/// <summary>
		/// The profile Run and Debug apply, the one <see cref="Build"/> marks as selected: among the profiles that
		/// fit the active target framework the stored one, else the first; null when none fits. Showing one
		/// profile while starting another would be the worst kind of drop-down, so both use this.
		/// </summary>
		public static LaunchSettingsProfile SelectProfile(
			IReadOnlyList<string> targetFrameworks,
			IReadOnlyList<LaunchSettingsProfile> profiles,
			string activeTargetFramework,
			string selectedProfileName)
		{
			var entries = ResolveEntries(targetFrameworks, profiles);
			int selected = FindSelected(entries, activeTargetFramework, selectedProfileName);
			return selected < 0 ? null : entries[selected].Profile;
		}

		static List<(LaunchSettingsProfile Profile, string Tfm)> ResolveEntries(
			IReadOnlyList<string> targetFrameworks, IReadOnlyList<LaunchSettingsProfile> profiles)
		{
			var entries = new List<(LaunchSettingsProfile Profile, string Tfm)>();
			foreach (var profile in profiles) {
				if (string.IsNullOrWhiteSpace(profile.CompatibleTargetFramework)) {
					entries.Add((profile, null));
					continue;
				}
				string tfm = targetFrameworks.FirstOrDefault(profile.IsCompatibleWith);
				if (tfm != null)
					entries.Add((profile, tfm));
			}
			return entries;
		}

		// Among the entries that fit the active framework: the stored selection, else the first.
		static int FindSelected(List<(LaunchSettingsProfile Profile, string Tfm)> entries, string activeTargetFramework, string selectedProfileName)
		{
			int selected = -1;
			for (int i = 0; i < entries.Count; i++) {
				bool fits = entries[i].Tfm == null
					|| string.Equals(entries[i].Tfm, activeTargetFramework, StringComparison.OrdinalIgnoreCase);
				if (!fits)
					continue;
				if (selected < 0)
					selected = i;
				if (!string.IsNullOrEmpty(selectedProfileName) && string.Equals(entries[i].Profile.Name, selectedProfileName, StringComparison.Ordinal))
					return i;
			}
			return selected;
		}

		public static IReadOnlyList<LaunchTargetEntry> Build(
			string projectName,
			IReadOnlyList<string> targetFrameworks,
			IReadOnlyList<LaunchSettingsProfile> profiles,
			string activeTargetFramework,
			string selectedProfileName)
		{
			var entries = ResolveEntries(targetFrameworks, profiles);
			int selected = FindSelected(entries, activeTargetFramework, selectedProfileName);

			var result = new List<LaunchTargetEntry>();
			for (int i = 0; i < entries.Count; i++) {
				var (profile, tfm) = entries[i];
				result.Add(new LaunchTargetEntry(LaunchTargetEntryKind.Profile, profile.Name, tfm, profile.Name,
					profile.CompatibleTargetFramework, i == selected));
			}

			var existingNames = profiles.Select(p => p.Name).ToList();
			var offeredPlatforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string tfm in targetFrameworks) {
				string platform = LaunchSettingsProfile.GetPlatform(tfm);
				if (platform.Length == 0 || profiles.Any(p => p.IsCompatibleWith(tfm) && !string.IsNullOrWhiteSpace(p.CompatibleTargetFramework)))
					continue;
				if (!offeredPlatforms.Add(platform))
					continue;
				string name = SuggestProfileName(projectName, platform, existingNames);
				result.Add(new LaunchTargetEntry(LaunchTargetEntryKind.AddProfile, tfm, tfm, name, platform, false));
			}
			return result;
		}

		/// <summary>Uno's naming, <c>App (Desktop)</c>; a name already in use gets <c>(2)</c>, <c>(3)</c>, ... after it.</summary>
		public static string SuggestProfileName(string projectName, string platform, IEnumerable<string> existingNames)
		{
			string baseName = (string.IsNullOrWhiteSpace(projectName) ? "App" : projectName) + " (" + DisplayPlatform(platform) + ")";
			var taken = new HashSet<string>(existingNames ?? Array.Empty<string>(), StringComparer.Ordinal);
			if (!taken.Contains(baseName))
				return baseName;
			for (int n = 2; ; n++) {
				string candidate = baseName + " (" + n + ")";
				if (!taken.Contains(candidate))
					return candidate;
			}
		}

		static string DisplayPlatform(string platform)
		{
			switch (platform.ToLowerInvariant()) {
				case "desktop": return "Desktop";
				case "ios": return "iOS";
				case "android": return "Android";
				case "windows": return "Windows";
				case "maccatalyst": return "Mac Catalyst";
				case "macos": return "macOS";
				case "browserwasm": return "WebAssembly";
				default: return platform;
			}
		}
	}
}
