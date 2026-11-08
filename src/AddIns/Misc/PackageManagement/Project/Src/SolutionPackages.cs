// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.SharpDevelop.Project;
using Microsoft.Build.Construction;
using NuGet.Versioning;

namespace ICSharpCode.PackageManagement
{
	/// <summary>
	/// The NuGet packages the SDK-style projects of a solution already reference, with the version each
	/// uses. This is the whole universe the missing-package light bulb draws from: a package that no project
	/// of the solution has used yet is added through Manage Packages, not suggested.
	/// </summary>
	public static class SolutionPackages
	{
		/// <summary>
		/// Package id to the highest version any of <paramref name="projects"/> uses (ids compare ignoring
		/// case). Reads the project model, so call it on the UI thread.
		/// </summary>
		public static Dictionary<string, string> Collect(IEnumerable<MSBuildBasedProject> projects)
		{
			var versionsById = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			var centralFiles = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
			foreach (MSBuildBasedProject project in projects.Where(p => p.IsSdkStyleProject)) {
				Dictionary<string, string>? central = null;
				foreach (ProjectItemElement item in project.MSBuildProjectFile.Items
					.Where(i => string.Equals(i.ItemType, "PackageReference", StringComparison.OrdinalIgnoreCase))) {
					string id = item.Include;
					if (string.IsNullOrWhiteSpace(id) || id.Contains('$')) {
						continue; // an unevaluated property: not a package id we can compare
					}
					string? version = item.Metadata.FirstOrDefault(m => m.Name == "Version")?.Value;
					if (string.IsNullOrEmpty(version)) {
						central ??= ReadCentralVersions(project, centralFiles);
						central.TryGetValue(id, out version);
					}
					if (string.IsNullOrEmpty(version) || version.Contains('$')) {
						continue;
					}
					if (!versionsById.TryGetValue(id, out List<string>? versions)) {
						versionsById[id] = versions = new List<string>();
					}
					versions.Add(version);
				}
			}
			return versionsById.ToDictionary(pair => pair.Key, pair => HighestVersion(pair.Value), StringComparer.OrdinalIgnoreCase);
		}

		/// <summary>The ids of the packages <paramref name="project"/> itself references (ignoring case).</summary>
		public static HashSet<string> ReferencedBy(MSBuildBasedProject project)
		{
			return new HashSet<string>(
				project.MSBuildProjectFile.Items
					.Where(i => string.Equals(i.ItemType, "PackageReference", StringComparison.OrdinalIgnoreCase))
					.Select(i => i.Include),
				StringComparer.OrdinalIgnoreCase);
		}

		/// <summary>
		/// The highest of the version strings, comparing as NuGet versions; a string that is not a plain
		/// version (a range or a floating version) only wins when nothing parses.
		/// </summary>
		public static string HighestVersion(IEnumerable<string> versions)
		{
			List<string> all = versions.ToList();
			NuGetVersion? best = null;
			string? bestText = null;
			foreach (string text in all) {
				if (NuGetVersion.TryParse(text, out NuGetVersion? parsed) && (best == null || parsed > best)) {
					best = parsed;
					bestText = text;
				}
			}
			return bestText ?? all[0];
		}

		static Dictionary<string, string> ReadCentralVersions(MSBuildBasedProject project, Dictionary<string, Dictionary<string, string>> cache)
		{
			if (!string.Equals(project.GetEvaluatedProperty("ManagePackageVersionsCentrally"), "true", StringComparison.OrdinalIgnoreCase)) {
				return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			}
			string? path = project.GetEvaluatedProperty("DirectoryPackagesPropsPath");
			if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) {
				return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			}
			if (!cache.TryGetValue(path, out Dictionary<string, string>? versions)) {
				cache[path] = versions = CentralPackageVersionsFile.Read(path);
			}
			return versions;
		}
	}
}
