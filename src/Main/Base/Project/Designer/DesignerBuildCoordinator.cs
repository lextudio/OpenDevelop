using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// Shared build gate for out-of-process designers. XAML is sent to a design host as source and
	/// does not by itself require a build; compiled controls and the application's dependency graph
	/// do. This class keeps that distinction out of individual WPF/WinUI front ends.
	/// </summary>
	public static class DesignerBuildCoordinator
	{
		static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
		static readonly Dictionary<string, BuildStamp> successfulBuilds = new Dictionary<string, BuildStamp>(StringComparer.OrdinalIgnoreCase);
		static readonly Dictionary<string, BuildStamp> failedBuilds = new Dictionary<string, BuildStamp>(StringComparer.OrdinalIgnoreCase);

		public static async Task<DesignerBuildResult> EnsureBuiltAsync(IProject project, bool requireRuntimeGraph = false, Action<string> report = null)
		{
			if (project == null)
				return new DesignerBuildResult(false, false, "No containing project.");
			var before = CreateStamp(project, requireRuntimeGraph);
			if (!before.HasOutput)
				return await BuildAsync(project, before, requireRuntimeGraph, report).ConfigureAwait(true);

			lock (successfulBuilds)
			{
				if (successfulBuilds.TryGetValue(before.Identity, out var previous) && previous.Equals(before))
					return new DesignerBuildResult(false, true, null);
				// A failed build for unchanged inputs is not retried on every design/source switch.
				if (failedBuilds.TryGetValue(before.Identity, out var failed) && failed.Equals(before))
					return new DesignerBuildResult(false, false, "The current design-build inputs failed previously.");
			}

			// The output can predate source/import/reference inputs even when this view has never
			// asked for a build. MSBuild remains the authority when we do build; this inexpensive
			// gate only avoids starting it for a known-current target graph.
			if (before.OutputUtc >= before.InputUtc)
			{
				lock (successfulBuilds) successfulBuilds[before.Identity] = before;
				return new DesignerBuildResult(false, true, null);
			}
			return await BuildAsync(project, before, requireRuntimeGraph, report).ConfigureAwait(true);
		}

		static async Task<DesignerBuildResult> BuildAsync(IProject project, BuildStamp requested, bool requireRuntimeGraph, Action<string> report)
		{
			await gate.WaitAsync().ConfigureAwait(true);
			try
			{
				// A waiting designer may now observe the output a previous designer built.
				var current = CreateStamp(project, requireRuntimeGraph);
				if (current.HasOutput && current.OutputUtc >= current.InputUtc)
				{
					lock (successfulBuilds) successfulBuilds[current.Identity] = current;
					return new DesignerBuildResult(false, true, null);
				}
				// BuildOptions.BuildOnExecute is normally read only by Commands.BuildBeforeExecute
				// (the F5/Run build-before-launch gate). An automated journey that sets it to
				// DoNotBuild - e.g. an integration test isolating keyboard/debugger/Hot Reload
				// coverage from build-before-run responsiveness - expects NO build to happen at
				// all for the duration, but opening a designer-backed document used to start this
				// coordinator's own build regardless, silently consuming the run's time budget.
				// Honor the same override here: report the existing (possibly stale) output as
				// unusable rather than starting a real build behind the caller's back.
				if (BuildOptions.BuildOnExecute == BuildDetection.DoNotBuild)
					return new DesignerBuildResult(false, current.HasOutput, current.HasOutput ? null : "Designer build skipped (BuildOnExecute=DoNotBuild) and no prior output exists.");
				report?.Invoke("Building " + project.Name + " for the designer…");
				var results = await SD.BuildService.BuildAsync(project, new BuildOptions(BuildTarget.Build)).ConfigureAwait(true);
				var after = CreateStamp(project, requireRuntimeGraph);
				if (results.Result == BuildResultCode.Success && after.HasOutput)
				{
					lock (successfulBuilds) {
						successfulBuilds[after.Identity] = after;
						failedBuilds.Remove(after.Identity);
					}
					return new DesignerBuildResult(true, true, null);
				}
				lock (successfulBuilds) failedBuilds[requested.Identity] = requested;
				return new DesignerBuildResult(true, false, "Build returned " + results.Result + ".");
			}
			finally { gate.Release(); }
		}

		static BuildStamp CreateStamp(IProject project, bool requireRuntimeGraph)
		{
			// The remote Roslyn workspace is the authority for unsaved editor-buffer changes. Its
			// revision invalidates only our cheap decision cache; it does NOT by itself demand a
			// build, because an unsaved C#/VB buffer cannot safely become an assembly yet.
			long roslynRevision;
			try { roslynRevision = SD.GetService<ICSharpCode.SharpDevelop.LanguageServices.ILanguageService>()?.GetWorkspaceRevision() ?? 0; }
			catch { roslynRevision = 0; }
			var output = project.OutputAssemblyFullPath ?? "";
			var configuration = project.ParentSolution?.ActiveConfiguration.ToString() ?? "";
			var targetFramework = (project as MSBuildBasedProject)?.GetEvaluatedProperty("TargetFramework") ?? "";
			var runtimeIdentifier = (project as MSBuildBasedProject)?.GetEvaluatedProperty("RuntimeIdentifier") ?? "";
			var identity = project.FileName + "|" + configuration + "|" + targetFramework + "|" + runtimeIdentifier + "|" + output + "|" + requireRuntimeGraph;
			var inputs = new List<string>();
			CollectInputs(project, inputs, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
			var inputUtc = inputs.Where(path => !string.IsNullOrEmpty(path)).Select(LastWriteUtc).DefaultIfEmpty(DateTime.MinValue).Max();
			var artifacts = new[] { output, Path.ChangeExtension(output, ".deps.json"), Path.ChangeExtension(output, ".runtimeconfig.json") };
			var requiredArtifacts = requireRuntimeGraph ? artifacts : artifacts.Take(1);
			var hasOutput = !string.IsNullOrEmpty(output) && requiredArtifacts.All(File.Exists);
			var outputUtc = artifacts.Where(File.Exists).Select(LastWriteUtc).DefaultIfEmpty(DateTime.MinValue).Min();
			return new BuildStamp(identity, inputUtc, outputUtc, hasOutput, roslynRevision);
		}

		static void CollectInputs(IProject project, List<string> inputs, HashSet<string> visited)
		{
			var projectFile = project.FileName?.ToString();
			if (string.IsNullOrEmpty(projectFile) || !visited.Add(projectFile))
				return;
			inputs.Add(projectFile);
			var msbuild = project as MSBuildBasedProject;
			if (msbuild == null)
				return;
			inputs.AddRange(SplitPaths(msbuild.GetEvaluatedProperty("MSBuildAllProjects")));
			inputs.Add(msbuild.GetEvaluatedProperty("ProjectAssetsFile"));
			inputs.AddRange(project.GetItemsOfType(ItemType.Compile).Select(item => item.FileName?.ToString()));
			foreach (var reference in project.GetItemsOfType(ItemType.ProjectReference))
			{
				var referenceFile = reference.FileName?.ToString();
				inputs.Add(referenceFile);
				if (!string.IsNullOrEmpty(referenceFile))
				{
					var referencedProject = SD.ProjectService.FindProjectContainingFile(FileName.Create(referenceFile));
					if (referencedProject != null)
						CollectInputs(referencedProject, inputs, visited);
				}
			}
		}

		static IEnumerable<string> SplitPaths(string value) => (value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
		static DateTime LastWriteUtc(string path) { try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; } }
		readonly record struct BuildStamp(string Identity, DateTime InputUtc, DateTime OutputUtc, bool HasOutput, long RoslynRevision);
	}

	public readonly record struct DesignerBuildResult(bool BuildStarted, bool IsUsable, string Error);
}
