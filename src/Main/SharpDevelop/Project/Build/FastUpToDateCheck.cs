// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// Skips starting `dotnet build` for a project nothing has changed in since its last successful
	/// build, as Visual Studio's fast up-to-date check does (and C# Dev Kit 11 adopted: a no-op build
	/// of 407 projects went from 34 s to 0.9 s). Each `dotnet build` here is a process start, an
	/// evaluation and MSBuild's own incremental checks: about a second per project even when it
	/// does nothing (doc/technotes/fast-mode.md).
	///
	/// A project is up to date when, against the record of its last successful build: the global
	/// properties and SDK are the same; the list of input files is the same (catches files added or
	/// removed by a glob); every input was last written before that build started (so a file edited
	/// during the build is caught too); and its output assembly exists. Inputs are the project file,
	/// every import its evaluation read, every evaluated item that names an existing file outside
	/// the build's own bin/obj/output directories, project.assets.json, and the output assemblies of
	/// referenced projects (a rebuilt dependency rebuilds its dependents). Anything this cannot judge
	/// (multi-targeted projects, a configuration other than the active one) simply builds.
	/// OD_FAST_UP_TO_DATE=0 disables it.
	/// </summary>
	static class FastUpToDateCheck
	{
		static readonly bool Enabled = Environment.GetEnvironmentVariable("OD_FAST_UP_TO_DATE") != "0";

		sealed class Record
		{
			public string Key { get; set; } = "";
			public string InputsHash { get; set; } = "";
			public long BuildStartedUtcTicks { get; set; }
		}

		sealed class Inputs
		{
			public string Key;
			public string Hash;
			public List<string> Files;
			public string Output;
		}

		/// <summary>True if the build can be skipped; <paramref name="reason"/> says why not otherwise.</summary>
		public static bool IsUpToDate(IProject project, ProjectBuildOptions options, out string reason)
		{
			reason = null;
			if (!Enabled || options.Target != BuildTarget.Build) {
				reason = "not a plain build";
				return false;
			}
			var inputs = Collect(project, options, out reason);
			if (inputs == null)
				return false;
			var record = Load(project);
			if (record == null) { reason = "never built here"; return false; }
			if (record.Key != inputs.Key) { reason = "build properties changed"; return false; }
			if (record.InputsHash != inputs.Hash) { reason = "input files added or removed"; return false; }
			if (!File.Exists(inputs.Output)) { reason = "output missing"; return false; }
			var started = new DateTime(record.BuildStartedUtcTicks, DateTimeKind.Utc);
			foreach (var file in inputs.Files) {
				if (File.GetLastWriteTimeUtc(file) >= started) {
					reason = Path.GetFileName(file) + " changed";
					return false;
				}
			}
			return true;
		}

		/// <summary>Records a successful build that started at <paramref name="buildStartedUtc"/>.</summary>
		public static void Succeeded(IProject project, ProjectBuildOptions options, DateTime buildStartedUtc)
		{
			if (!Enabled || options.Target == BuildTarget.Clean)
				return;
			var inputs = Collect(project, options, out _);
			var path = RecordPath(project);
			if (inputs == null || path == null)
				return;
			try {
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllText(path, JsonSerializer.Serialize(new Record {
					Key = inputs.Key, InputsHash = inputs.Hash, BuildStartedUtcTicks = buildStartedUtc.Ticks
				}));
			} catch (Exception ex) {
				LoggingService.Warn("Fast up-to-date check: cannot record the build of " + project.Name + ": " + ex.Message);
			}
		}

		/// <summary>Forgets a project's record (a clean, or a failed build).</summary>
		public static void Forget(IProject project)
		{
			var path = RecordPath(project);
			try {
				if (path != null && File.Exists(path))
					File.Delete(path);
			} catch (Exception ex) {
				LoggingService.Warn("Fast up-to-date check: cannot forget " + project.Name + ": " + ex.Message);
			}
		}

		static Inputs Collect(IProject project, ProjectBuildOptions options, out string reason)
		{
			reason = null;
			try {
				if (!(project is CompilableProject msbuildProject)) {
					reason = "not an MSBuild project";
					return null;
				}
				if (LanguageServiceProjectSnapshotFactory.GetTargetFrameworks(project).Count > 1) {
					reason = "multi-targeted";
					return null;
				}
				// The record describes the active configuration's evaluation; any other one builds.
				var active = msbuildProject.ActiveConfiguration;
				if (!string.IsNullOrEmpty(options.Configuration) && !string.Equals(options.Configuration, active.Configuration, StringComparison.OrdinalIgnoreCase)
				    || !string.IsNullOrEmpty(options.Platform) && !string.Equals(NormalizePlatform(options.Platform), NormalizePlatform(active.Platform), StringComparison.OrdinalIgnoreCase)) {
					reason = "not the active configuration";
					return null;
				}
				var output = msbuildProject.OutputAssemblyFullPath?.ToString();
				if (string.IsNullOrEmpty(output)) {
					reason = "no output assembly";
					return null;
				}
				string projectDirectory = project.Directory.ToString();
				var excluded = new[] {
					Path.Combine(projectDirectory, "bin"),
					Path.Combine(projectDirectory, "obj"),
					Path.GetDirectoryName(output)
				}.Select(EnsureTrailingSeparator).ToArray();

				var files = new HashSet<string>(StringComparer.Ordinal);
				foreach (var file in msbuildProject.GetEvaluationInputFiles())
					files.Add(file);
				foreach (var item in msbuildProject.GetEvaluatedProjectItems()) {
					if (string.IsNullOrEmpty(item.EvaluatedInclude) || item.EvaluatedInclude.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
						continue;
					string path = Path.IsPathRooted(item.EvaluatedInclude) ? item.EvaluatedInclude : Path.Combine(projectDirectory, item.EvaluatedInclude);
					path = Path.GetFullPath(path);
					if (excluded.Any(directory => path.StartsWith(directory, StringComparison.Ordinal)) || !File.Exists(path))
						continue;
					files.Add(path);
				}
				// Every file under the project directory too, not only what the evaluation lists: a kept
				// MSBuild evaluation does not re-expand wildcards, so a file added since it was made
				// would not be among its items (found the hard way: an added .cs was "up to date").
				// Conservative - editing a README also rebuilds - but never stale.
				foreach (var file in EnumerateProjectTree(projectDirectory, excluded))
					files.Add(file);
				var assets = msbuildProject.GetEvaluatedProperty("ProjectAssetsFile");
				if (!string.IsNullOrEmpty(assets) && File.Exists(assets))
					files.Add(Path.GetFullPath(assets));
				foreach (var reference in project.GetItemsOfType(ItemType.ProjectReference).OfType<ProjectReferenceProjectItem>()) {
					var referenced = (reference.ReferencedProject as CompilableProject)?.OutputAssemblyFullPath?.ToString();
					if (string.IsNullOrEmpty(referenced)) {
						reason = "unresolved project reference " + reference.Include;
						return null;
					}
					files.Add(referenced);
				}
				var sorted = files.OrderBy(file => file, StringComparer.Ordinal).ToList();
				var sdk = Sdk.DotNetSdkService.ResolveEffectiveSdk();
				return new Inputs {
					Key = "v1|" + sdk.DotnetExecutablePath + "|" + sdk.HighestSdkVersion + "|" + MinimalMSBuildEngine.GlobalPropertiesKey(options),
					Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", sorted)))),
					Files = sorted,
					Output = output
				};
			} catch (Exception ex) {
				reason = "cannot evaluate: " + ex.Message;
				return null;
			}
		}

		static IEnumerable<string> EnumerateProjectTree(string root, string[] excluded)
		{
			var pending = new Stack<string>();
			pending.Push(root);
			while (pending.Count > 0) {
				var directory = pending.Pop();
				foreach (var file in Directory.EnumerateFiles(directory))
					yield return Path.GetFullPath(file);
				foreach (var child in Directory.EnumerateDirectories(directory)) {
					var name = Path.GetFileName(child);
					if (name.StartsWith(".", StringComparison.Ordinal))
						continue; // .git, .vs, .od, ...
					if (excluded.Any(path => EnsureTrailingSeparator(child).StartsWith(path, StringComparison.Ordinal)))
						continue;
					pending.Push(child);
				}
			}
		}

		static string NormalizePlatform(string platform) =>
			string.Equals(platform, "Any CPU", StringComparison.OrdinalIgnoreCase) ? "AnyCPU" : platform;

		static string EnsureTrailingSeparator(string directory) =>
			directory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? directory : directory + Path.DirectorySeparatorChar;

		static Record Load(IProject project)
		{
			var path = RecordPath(project);
			try {
				return path != null && File.Exists(path) ? JsonSerializer.Deserialize<Record>(File.ReadAllText(path)) : null;
			} catch {
				return null;
			}
		}

		static string RecordPath(IProject project)
		{
			var solutionDirectory = project.ParentSolution?.Directory.ToString();
			if (solutionDirectory == null)
				return null;
			var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project.FileName.ToString().ToUpperInvariant())));
			return Path.Combine(solutionDirectory, ".od", "build-up-to-date", name + ".json");
		}
	}
}
