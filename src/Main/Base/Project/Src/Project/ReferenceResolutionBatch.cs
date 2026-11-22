// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// The pure parts of batched reference resolution (IBatchAssemblyReferenceResolver,
	/// doc/technotes/fast-mode.md): grouping projects by the global.json that governs them, the
	/// traversal project one batch runs, and reading its per-project output back. Kept apart from
	/// the engine so they can be tested without MSBuild or an IDE.
	/// </summary>
	public static class ReferenceResolutionBatch
	{
		/// <summary>The nearest directory above <paramref name="projectFile"/> holding a global.json, or null.</summary>
		public static string GlobalJsonDirectory(string projectFile)
		{
			for (var directory = Path.GetDirectoryName(projectFile); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory)) {
				if (File.Exists(Path.Combine(directory, "global.json")))
					return directory;
			}
			return null;
		}

		/// <summary>
		/// Resolves <paramref name="projects"/> one batch per global.json directory (projects without one
		/// share a batch), the batches side by side, and merges what they return. A batch that returns
		/// null failed as a whole: its projects are simply missing from the result, so the caller
		/// resolves them one by one - other batches are not affected.
		/// </summary>
		public static Dictionary<TProject, IReadOnlyList<string>> ResolveInGlobalJsonBatches<TProject>(
			IReadOnlyList<TProject> projects, Func<TProject, string> projectFile,
			Func<string, IReadOnlyList<TProject>, IReadOnlyDictionary<TProject, IReadOnlyList<string>>> resolveBatch)
		{
			var result = new Dictionary<TProject, IReadOnlyList<string>>();
			var batches = GroupByGlobalJson(projects, projectFile)
				.Select(group => Task.Run(() => resolveBatch(group.Key, group.Value)))
				.ToArray();
			foreach (var batch in batches) {
				IReadOnlyDictionary<TProject, IReadOnlyList<string>> resolved;
				try {
					resolved = batch.Result;
				} catch (AggregateException) {
					continue; // like a null: this batch's projects fall back to single resolution
				}
				if (resolved == null)
					continue;
				foreach (var pair in resolved)
					result[pair.Key] = pair.Value;
			}
			return result;
		}

		/// <summary>Projects grouped by <see cref="GlobalJsonDirectory"/>; the key is null for projects with none.</summary>
		public static List<KeyValuePair<string, IReadOnlyList<TProject>>> GroupByGlobalJson<TProject>(IReadOnlyList<TProject> projects, Func<TProject, string> projectFile)
		{
			return projects
				.GroupBy(project => GlobalJsonDirectory(projectFile(project)) ?? "", StringComparer.Ordinal)
				.Select(group => new KeyValuePair<string, IReadOnlyList<TProject>>(group.Key.Length == 0 ? null : group.Key, group.ToList()))
				.ToList();
		}

		public static string CreateResolveReferencesTraversal(IReadOnlyList<(string File, string TargetFramework)> projects)
			=> CreateResolveReferencesTraversal(projects.Select(project => (project.File, project.TargetFramework, (string)null)).ToList(), null);

		/// <summary>
		/// The target file <see cref="CreateResolveReferencesTraversal(IReadOnlyList{ValueTuple{string, string, string}}, string)"/>
		/// injects through <c>CustomAfterMicrosoftCommonTargets</c> to read each project's resolved
		/// <c>Analyzer</c> items (package analyzers and source generators only exist after
		/// ResolvePackageAssets/ResolveTargetingPackAssets, and no SDK target returns them). It imports the
		/// project's own CustomAfterMicrosoftCommonTargets first, so overriding the property loses nothing.
		/// </summary>
		public const string AnalyzersTargetsContent = @"<Project>
  <Import Project=""$(ODOriginalCustomAfterMicrosoftCommonTargets)"" Condition=""'$(ODOriginalCustomAfterMicrosoftCommonTargets)' != '' And Exists('$(ODOriginalCustomAfterMicrosoftCommonTargets)')"" />
  <Target Name=""ODResolveAnalyzers"" DependsOnTargets=""ResolveReferences"" Returns=""@(Analyzer)"" />
</Project>
";

		/// <summary>
		/// The traversal, also collecting each project's <c>Analyzer</c> items as <c>ODAnalyzerPath</c> when
		/// <paramref name="analyzersTargetsFile"/> (a file holding <see cref="AnalyzersTargetsContent"/>) is
		/// given. <c>OriginalCustomAfter</c> is the project's evaluated CustomAfterMicrosoftCommonTargets.
		/// </summary>
		public static string CreateResolveReferencesTraversal(IReadOnlyList<(string File, string TargetFramework, string OriginalCustomAfter)> projects,
			string analyzersTargetsFile)
		{
			string Escape(string value) => System.Security.SecurityElement.Escape(value);
			string PropertiesFor(string targetFramework, string originalCustomAfter) =>
				"BuildingInsideVisualStudio=true" + (string.IsNullOrEmpty(targetFramework) ? "" : ";TargetFramework=" + targetFramework)
				+ (analyzersTargetsFile == null ? "" : ";CustomAfterMicrosoftCommonTargets=" + analyzersTargetsFile
					+ ";ODOriginalCustomAfterMicrosoftCommonTargets=" + originalCustomAfter);
			var text = new StringBuilder();
			text.AppendLine("<Project>");
			text.AppendLine("  <ItemGroup>");
			foreach (var project in projects)
				text.AppendLine("    <ODProject Include=\"" + Escape(project.File) + "\" Properties=\"" + Escape(PropertiesFor(project.TargetFramework, project.OriginalCustomAfter)) + "\" />");
			text.AppendLine("  </ItemGroup>");
			var calls = new List<string>();
			for (int i = 0; i < projects.Count; i++) {
				// Same global properties as the parallel pass, so this is a results-cache hit.
				text.AppendLine("  <Target Name=\"R" + i + "\">");
				var properties = Escape(PropertiesFor(projects[i].TargetFramework, projects[i].OriginalCustomAfter));
				text.AppendLine("    <MSBuild Projects=\"" + Escape(projects[i].File) + "\" Targets=\"ResolveReferences\" Properties=\"" + properties
					+ "\" SkipNonexistentTargets=\"true\" ContinueOnError=\"WarnAndContinue\"><Output TaskParameter=\"TargetOutputs\" ItemName=\"_R" + i + "\" /></MSBuild>");
				text.AppendLine("    <ItemGroup><ODReferencePath Include=\"@(_R" + i + ")\" ODProject=\"" + Escape(projects[i].File) + "\" /></ItemGroup>");
				if (analyzersTargetsFile != null) {
					text.AppendLine("    <MSBuild Projects=\"" + Escape(projects[i].File) + "\" Targets=\"ODResolveAnalyzers\" Properties=\"" + properties
						+ "\" SkipNonexistentTargets=\"true\" ContinueOnError=\"WarnAndContinue\"><Output TaskParameter=\"TargetOutputs\" ItemName=\"_A" + i + "\" /></MSBuild>");
					text.AppendLine("    <ItemGroup><ODAnalyzerPath Include=\"@(_A" + i + ")\" ODProject=\"" + Escape(projects[i].File) + "\" /></ItemGroup>");
				}
				text.AppendLine("  </Target>");
				calls.Add("R" + i);
			}
			text.AppendLine("  <Target Name=\"Resolve\">");
			// Batched per distinct global-property set, since the MSBuild task's Properties apply to all.
			text.AppendLine("    <MSBuild Projects=\"@(ODProject)\" Targets=\"ResolveReferences\" Properties=\"%(ODProject.Properties)\" BuildInParallel=\"true\" SkipNonexistentTargets=\"true\" ContinueOnError=\"WarnAndContinue\" />");
			text.AppendLine("    <CallTarget Targets=\"" + string.Join(";", calls) + "\" />");
			text.AppendLine("  </Target>");
			text.AppendLine("</Project>");
			return text.ToString();
		}

		/// <summary>The -getItem JSON's ODReferencePath items grouped by ODProject, or null if absent.</summary>
		public static Dictionary<string, List<string>> ParseLabelledReferencePaths(string stdout)
			=> ParseLabelledItems(stdout, "ODReferencePath");

		/// <summary>The -getItem JSON's <paramref name="itemName"/> items grouped by ODProject, or null if absent.</summary>
		public static Dictionary<string, List<string>> ParseLabelledItems(string stdout, string itemName)
		{
			int start = stdout.IndexOf('{');
			if (start < 0)
				return null;
			try {
				using var document = System.Text.Json.JsonDocument.Parse(stdout.Substring(start));
				if (!document.RootElement.TryGetProperty("Items", out var itemsElement)
				    || !itemsElement.TryGetProperty(itemName, out var list))
					return null;
				var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
				foreach (var item in list.EnumerateArray()) {
					if (!item.TryGetProperty("Identity", out var identity) || !item.TryGetProperty("ODProject", out var owner))
						continue;
					var key = owner.GetString();
					if (!grouped.TryGetValue(key, out var paths))
						grouped[key] = paths = new List<string>();
					paths.Add(identity.GetString());
				}
				return grouped;
			} catch (System.Text.Json.JsonException) {
				return null;
			}
		}

	}
}
