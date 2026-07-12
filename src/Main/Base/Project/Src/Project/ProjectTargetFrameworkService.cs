using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.SharpDevelop.Project
{
	public static class ProjectTargetFrameworkService
	{
		const string PreferenceKey = "ActiveTargetFramework";

		public static event EventHandler<ProjectTargetFrameworkChangedEventArgs> ActiveTargetFrameworkChanged = delegate { };

		public static IReadOnlyList<string> GetTargetFrameworks(IProject project)
		{
			if (project is not MSBuildBasedProject msbuildProject)
				return Array.Empty<string>();

			var targetFrameworks = Split(msbuildProject.GetEvaluatedProperty("TargetFrameworks"));
			if (targetFrameworks.Length > 0)
				return targetFrameworks;

			var targetFramework = msbuildProject.GetEvaluatedProperty("TargetFramework")?.Trim();
			return string.IsNullOrEmpty(targetFramework) ? Array.Empty<string>() : new[] { targetFramework };
		}

		public static string GetActiveTargetFramework(IProject project)
		{
			var targetFrameworks = GetTargetFrameworks(project);
			if (targetFrameworks.Count == 0)
				return null;

			var stored = project.Preferences.Get(PreferenceKey, string.Empty);
			return targetFrameworks.FirstOrDefault(tfm => string.Equals(tfm, stored, StringComparison.OrdinalIgnoreCase))
				?? targetFrameworks[0];
		}

		public static void SetActiveTargetFramework(IProject project, string targetFramework)
		{
			if (project == null)
				throw new ArgumentNullException(nameof(project));

			var selected = GetTargetFrameworks(project)
				.FirstOrDefault(tfm => string.Equals(tfm, targetFramework, StringComparison.OrdinalIgnoreCase));
			if (selected == null)
				throw new ArgumentException("The target framework is not declared by the project.", nameof(targetFramework));

			var previous = GetActiveTargetFramework(project);
			if (string.Equals(previous, selected, StringComparison.OrdinalIgnoreCase))
				return;

			project.Preferences.Set(PreferenceKey, selected);
			project.SavePreferences();
			ActiveTargetFrameworkChanged(null, new ProjectTargetFrameworkChangedEventArgs(project, previous, selected));
		}

		static string[] Split(string value)
		{
			return (value ?? string.Empty)
				.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(valuePart => valuePart.Trim())
				.Where(valuePart => valuePart.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}
	}

	public sealed class ProjectTargetFrameworkChangedEventArgs : EventArgs
	{
		public ProjectTargetFrameworkChangedEventArgs(IProject project, string oldTargetFramework, string newTargetFramework)
		{
			Project = project;
			OldTargetFramework = oldTargetFramework;
			NewTargetFramework = newTargetFramework;
		}

		public IProject Project { get; }
		public string OldTargetFramework { get; }
		public string NewTargetFramework { get; }
	}
}
