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
			/// <summary>Write time of each referenced project's output when this build was recorded.</summary>
			public Dictionary<string, long> ReferenceOutputTicks { get; set; } = new Dictionary<string, long>();
		}

		sealed class Inputs
		{
			public string Key;
			public string Hash;
			public List<string> Files;
			public List<string> ReferenceOutputs;
			public List<string> Outputs;
		}

		// One build asks about each project up to three times (the up-front restore and the
		// one-process build decide on it before the project's own build does), and each answer
		// walks the project tree. Answers are kept per build (per options object) until anything
		// is recorded or forgotten, since a rebuilt reference changes its dependents' answer.
		static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ProjectBuildOptions, Dictionary<IProject, (int Generation, bool UpToDate, string Reason)>> answers
			= new System.Runtime.CompilerServices.ConditionalWeakTable<ProjectBuildOptions, Dictionary<IProject, (int, bool, string)>>();
		static int generation;

		/// <summary>True if the build can be skipped; <paramref name="reason"/> says why not otherwise.</summary>
		public static bool IsUpToDate(IProject project, ProjectBuildOptions options, out string reason)
		{
			var perBuild = answers.GetValue(options, _ => new Dictionary<IProject, (int, bool, string)>());
			int current = System.Threading.Volatile.Read(ref generation);
			lock (perBuild) {
				if (perBuild.TryGetValue(project, out var known) && known.Generation == current) {
					reason = known.Reason;
					return known.UpToDate;
				}
			}
			bool upToDate = Check(project, options, out reason);
			lock (perBuild)
				perBuild[project] = (current, upToDate, reason);
			return upToDate;
		}

		static bool Check(IProject project, ProjectBuildOptions options, out string reason)
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
			if (!inputs.Outputs.All(File.Exists)) { reason = "output missing"; return false; }
			var started = new DateTime(record.BuildStartedUtcTicks, DateTimeKind.Utc);
			foreach (var file in inputs.Files) {
				if (File.GetLastWriteTimeUtc(file) >= started) {
					reason = Path.GetFileName(file) + " changed";
					return false;
				}
			}
			// A referenced output is compared with the write time recorded after this build, not
			// with its start: in a one-process build the references are rebuilt during it.
			foreach (var file in inputs.ReferenceOutputs) {
				if (record.ReferenceOutputTicks == null || !record.ReferenceOutputTicks.TryGetValue(file, out var ticks)
				    || File.GetLastWriteTimeUtc(file).Ticks != ticks) {
					reason = Path.GetFileName(file) + " changed";
					return false;
				}
			}
			return true;
		}

		/// <summary>Records a successful build that started at <paramref name="buildStartedUtc"/>.</summary>
		public static void Succeeded(IProject project, ProjectBuildOptions options, DateTime buildStartedUtc)
		{
			System.Threading.Interlocked.Increment(ref generation);
			if (!Enabled || options.Target == BuildTarget.Clean)
				return;
			(project as MSBuildBasedProject)?.RefreshEvaluation();
			var inputs = Collect(project, options, out _);
			var path = RecordPath(project);
			if (inputs == null || path == null)
				return;
			try {
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllText(path, JsonSerializer.Serialize(new Record {
					Key = inputs.Key, InputsHash = inputs.Hash, BuildStartedUtcTicks = buildStartedUtc.Ticks,
					ReferenceOutputTicks = inputs.ReferenceOutputs.ToDictionary(file => file, file => File.GetLastWriteTimeUtc(file).Ticks)
				}));
			} catch (Exception ex) {
				LoggingService.Warn("Fast up-to-date check: cannot record the build of " + project.Name + ": " + ex.Message);
			}
		}

		/// <summary>Forgets a project's record (a clean, or a failed build).</summary>
		public static void Forget(IProject project)
		{
			System.Threading.Interlocked.Increment(ref generation);
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
				// The record describes the active configuration's evaluation; any other one builds.
				var active = msbuildProject.ActiveConfiguration;
				if (!string.IsNullOrEmpty(options.Configuration) && !string.Equals(options.Configuration, active.Configuration, StringComparison.OrdinalIgnoreCase)
				    || !string.IsNullOrEmpty(options.Platform) && !string.Equals(NormalizePlatform(options.Platform), NormalizePlatform(active.Platform), StringComparison.OrdinalIgnoreCase)) {
					reason = "not the active configuration";
					return null;
				}
				// TargetPath is what MSBuild writes; OutputAssemblyFullPath says X.exe for an SDK Exe
				// project, whose output is X.dll, so such a project was never up to date.
				// A multi-targeted build writes one assembly per TFM; all of them must exist. Its
				// inputs are the active TFM's items plus the whole project tree, which covers the
				// other TFMs' items too unless they link files from outside it.
				var targetFrameworks = LanguageServiceProjectSnapshotFactory.GetTargetFrameworks(project);
				var outputs = new List<string>();
				if (targetFrameworks.Count > 1) {
					foreach (var targetFramework in targetFrameworks)
						outputs.Add(msbuildProject.GetEvaluatedProperty("TargetPath", targetFramework));
				} else {
					// TargetPath is what MSBuild writes; OutputAssemblyFullPath says X.exe for an SDK
					// Exe project, whose output is X.dll, so such a project was never up to date.
					var targetPath = msbuildProject.GetEvaluatedProperty("TargetPath");
					outputs.Add(!string.IsNullOrEmpty(targetPath) ? targetPath : msbuildProject.OutputAssemblyFullPath?.ToString());
				}
				if (outputs.Any(string.IsNullOrEmpty)) {
					reason = "no output assembly";
					return null;
				}
				string projectDirectory = project.Directory.ToString();
				var excluded = new[] {
					Path.Combine(projectDirectory, "bin"),
					Path.Combine(projectDirectory, "obj")
				}.Concat(outputs.Select(Path.GetDirectoryName)).Concat(new[] { "BaseIntermediateOutputPath", "IntermediateOutputPath" }
					// Written by the build itself (AspNetCore's *.Up2Date marker lives outside obj).
					.Select(name => msbuildProject.GetEvaluatedProperty(name))
					.Where(value => !string.IsNullOrEmpty(value))
					.Select(value => Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(projectDirectory, value))))
				.Select(EnsureTrailingSeparator).ToArray();

				var files = new HashSet<string>(StringComparer.Ordinal);
				var referenceOutputs = new HashSet<string>(StringComparer.Ordinal);
				foreach (var file in msbuildProject.GetEvaluationInputFiles())
					files.Add(file);
				foreach (var item in msbuildProject.GetEvaluatedProjectItems()) {
					if (string.IsNullOrEmpty(item.EvaluatedInclude) || item.EvaluatedInclude.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
						continue;
					string path = Path.IsPathRooted(item.EvaluatedInclude) ? item.EvaluatedInclude : Path.Combine(projectDirectory, item.EvaluatedInclude);
					path = Path.GetFullPath(path);
					if (excluded.Any(directory => path.StartsWith(directory, StringComparison.Ordinal)) || IsUnderBuildOutput(path) || !File.Exists(path))
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
					var referencedProject = reference.ReferencedProject as CompilableProject;
					var referenced = referencedProject?.GetEvaluatedProperty("TargetPath");
					if (string.IsNullOrEmpty(referenced))
						referenced = referencedProject?.OutputAssemblyFullPath?.ToString();
					if (!string.IsNullOrEmpty(referenced)) {
						referenceOutputs.Add(referenced);
						continue;
					}
					// Outside the solution (OpenDevelop.Mvp.slnx leaves out Widgets, DesignerCanvas, ...):
					// MSBuild still builds it as part of this project, so its sources are this build's
					// inputs. Without an evaluation its output is unknown; its tree, its own references'
					// trees and the Directory.Build files above them stand in for it.
					if (!AddOutOfSolutionReference(reference.FileName.ToString(), files, new HashSet<string>(StringComparer.Ordinal))) {
						reason = "missing project reference " + reference.Include;
						return null;
					}
				}
				files.ExceptWith(referenceOutputs);
				var sorted = files.OrderBy(file => file, StringComparer.Ordinal).ToList();
				var sortedReferences = referenceOutputs.OrderBy(file => file, StringComparer.Ordinal).ToList();
				var sdk = Sdk.DotNetSdkService.ResolveEffectiveSdk();
				return new Inputs {
					Key = "v3|" + sdk.DotnetExecutablePath + "|" + sdk.HighestSdkVersion + "|" + MinimalMSBuildEngine.GlobalPropertiesKey(options),
					Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", sorted) + "\n|\n" + string.Join("\n", sortedReferences)))),
					Files = sorted,
					ReferenceOutputs = sortedReferences,
					Outputs = outputs
				};
			} catch (Exception ex) {
				reason = "cannot evaluate: " + ex.Message;
				return null;
			}
		}

		static bool AddOutOfSolutionReference(string projectFile, HashSet<string> files, HashSet<string> visited)
		{
			projectFile = Path.GetFullPath(projectFile);
			if (!visited.Add(projectFile))
				return true;
			if (!File.Exists(projectFile))
				return true; // a conditioned-out reference (AvalonEdit's TextCore.Uno one); a real one fails the build itself
			string directory = Path.GetDirectoryName(projectFile);
			var excluded = new[] { Path.Combine(directory, "bin"), Path.Combine(directory, "obj") }.Select(EnsureTrailingSeparator).ToArray();
			foreach (var file in EnumerateProjectTree(directory, excluded))
				files.Add(file);
			for (var parent = directory; !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent)) {
				foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json" }) {
					var candidate = Path.Combine(parent, name);
					if (File.Exists(candidate))
						files.Add(candidate);
				}
			}
			// Unevaluated, so conditions are ignored: a superset of its real references, never fewer.
			foreach (var element in System.Xml.Linq.XDocument.Load(projectFile).Descendants().Where(e => e.Name.LocalName == "ProjectReference")) {
				var include = (string)element.Attribute("Include");
				if (string.IsNullOrEmpty(include) || include.Contains("$("))
					continue;
				if (!AddOutOfSolutionReference(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar)), files, visited))
					return false;
			}
			return true;
		}

		/// <summary>True for a file in some bin/obj directory (a nested project's, which the
		/// parent's globs can pick up: AspNetCore/Binding/obj/*.Up2Date).</summary>
		static bool IsUnderBuildOutput(string path)
		{
			char separator = Path.DirectorySeparatorChar;
			return path.Contains(separator + "obj" + separator, StringComparison.Ordinal)
				|| path.Contains(separator + "bin" + separator, StringComparison.Ordinal);
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
					if (name == "bin" || name == "obj")
						continue; // a nested project's build output (AspNetCore/Tests/obj)
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
