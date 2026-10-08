// Real (not mocked) IMSBuildEngine. The original MSBuildEngine (Project/Build/MSBuildEngine/*.cs,
// excluded from this MVP build - see SharpDevelop.csproj's <Compile Remove>) drives builds through
// a separate out-of-process worker (BuildWorkerManager/WorkerProcess/
// ICSharpCode.SharpDevelop.BuildWorker), which is itself a WinForms console project out of MVP
// scope. This class also runs the build out-of-process, but via a plain `dotnet build` child
// process instead of that bespoke IPC worker.
//
// That's not a shortcut - it's necessary. An earlier version of this class used
// Microsoft.Build.Execution.BuildManager in-process, which failed with MSB4062
// ("Could not load type 'Microsoft.Build.Framework.IMultiThreadableTask'") the moment an SDK task
// actually ran. Root cause: this app's hosting SDK (librewpf's local net10.0 preview install)
// bundles a Microsoft.NET.Build.Tasks.dll built against a newer Microsoft.Build.Framework API than
// whatever copy of that assembly is already loaded in this process (evaluation-only MSBuild work,
// like Solution Explorer's project-item listing, never hits this because it never executes a
// task). Neither clearing MSBuildSDKsPath-style environment variables nor passing an explicit
// MSBuildSDKsPath global property changed the outcome - .NET's SDK/task resolution for a hosted
// BuildManager is tied to the current process's own runtime location, not something a
// ProjectCollection call site can override. A separate `dotnet build` process gets its own clean
// MSBuild host and entirely sidesteps the shared-assembly-identity conflict - which is exactly why
// the original SharpDevelop authors used a separate worker process for this in the first place.
//
// Not implemented: CompileTaskNames/AdditionalTargetFiles/AdditionalMSBuildLoggers/
// MSBuildLoggerFilters are the extension points the (excluded) real engine used for its logger
// pipeline; nothing in this MVP build populates or reads them, so they're empty/no-op here.
// ResolveAssemblyReferences returns only additionalReferences (real resolution would need the same
// out-of-process approach as BuildAsync; not needed yet - RoslynWorkspaceHelper.GetMetadataReferences
// already falls back to the host runtime's trusted platform assemblies when this comes back empty).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project.Sdk;

namespace ICSharpCode.SharpDevelop.Project
{
	sealed class MinimalMSBuildEngine : IMSBuildEngine, IBatchAssemblyReferenceResolver
	{
		public ISet<string> CompileTaskNames { get; } = new HashSet<string> { "Csc", "Vbc", "CoreCompile" };

		public IEnumerable<KeyValuePair<string, string>> GlobalBuildProperties {
			get { yield break; }
		}

		public IList<FileName> AdditionalTargetFiles { get; } = new List<FileName>();
		public IList<IMSBuildAdditionalLogger> AdditionalMSBuildLoggers { get; } = new List<IMSBuildAdditionalLogger>();
		public IList<IMSBuildLoggerFilter> MSBuildLoggerFilters { get; } = new List<IMSBuildLoggerFilter>();

		// Standard MSBuild diagnostic line shape:
		//   /path/File.cs(12,34): error CS1002: ; expected [/path/Project.csproj]
		//   /path/File.cs(12,34): warning CS0168: The variable 'x' is declared but never used [/path/Project.csproj]
		// Roslyn also emits a 4-number span variant for multi-position diagnostics -
		// (startLine,startColumn,endLine,endColumn) - e.g.:
		//   /path/File.cs(3,50,3,56): error CS1002: ; expected [/path/Project.csproj]
		// The trailing ",endLine,endColumn)" group is optional so both shapes match; only the
		// start position is reported (matching the plain 2-number shape's granularity).
		static readonly Regex DiagnosticLine = new Regex(
			@"^(?<file>.*?)\((?<line>\d+),(?<column>\d+)(?:,\d+,\d+)?\):\s*(?<severity>error|warning)\s+(?<code>[A-Za-z0-9]+)\s*:\s*(?<text>.*?)(\s*\[.*\])?$",
			RegexOptions.Compiled);

		// Which dotnet host/SDK to run builds with is no longer hardcoded here - it's resolved
		// fresh on every build from DotNetSdkService (Options > .NET SDK), the single place this
		// choice is made across build/debug/test. Not necessarily the same SDK hosting this
		// process itself (see the type-level comment) - just needs its own consistent SDK/MSBuild
		// toolset, which any selected installed SDK provides.

		/// <summary>
		/// The child-process setup shared by <see cref="BuildAsync"/> and
		/// <see cref="ResolveAssemblyReferences"/>: selected SDK, scrubbed environment, pinned
		/// locale, and a working directory outside this repo. Both paths need exactly the same
		/// treatment - see the comments inside - and having two copies of it is how they drift.
		/// </summary>
		static ProcessStartInfo CreateDotnetChildStartInfo()
		{
			var sdk = DotNetSdkService.ResolveEffectiveSdk();
			var psi = new ProcessStartInfo(sdk.DotnetExecutablePath) {
				// Deliberately NOT project.Directory: dotnet's SDK resolution walks up from the
				// current working directory looking for global.json, and OpenDevelop's own repo
				// root pins an SDK version (librewpf's local preview install) that this build
				// process is specifically trying to avoid (see the type-level comment). Running
				// from outside the repo tree - with an absolute project path as the build target -
				// sidesteps that pin entirely.
				WorkingDirectory = Path.GetTempPath(),
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};
			// This app's own process is launched with DOTNET_ROOT/DOTNET_HOST_PATH/MSBuildSDKsPath-
			// family variables pointing at whatever SDK launch.sh pinned it to; a child process
			// inherits them by default, which would make the dotnet muxer resolve right back to
			// that SDK regardless of which dotnet binary is actually invoked here. Clear them, then
			// set them explicitly to match the selected SDK - deterministic either way, instead of
			// clearing-and-hoping the child's own global.json/PATH resolution lands on the right one.
			foreach (string name in new[] {
				"DOTNET_ROOT", "DOTNET_ROOT(x86)", "DOTNET_HOST_PATH", "DOTNET_MULTILEVEL_LOOKUP",
				"MSBuildSDKsPath", "MSBuildExtensionsPath", "MSBuildExtensionsPath32", "MSBuildExtensionsPath64",
				"MSBuildToolsPath", "MSBuildToolsVersion", "MSBuildEnableWorkloadResolver",
				"MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET", "MSBUILD_NUGET_PATH", "MSBUILD_EXE_PATH"
			}) {
				psi.EnvironmentVariables.Remove(name);
			}
			foreach (var kv in DotNetSdkService.GetEnvironmentVariablesFor(sdk)) {
				// Only propagate variables the dotnet muxer itself requires. The remaining
				// entries are in-process MSBuild settings. In particular, this application's
				// bundled SdkResolvers folder contains the ARM64 NuGet resolver; exposing it
				// to an x64 child makes WinUI builds fail with MSB4244.
				if (string.Equals(kv.Key, "DOTNET_ROOT", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(kv.Key, "DOTNET_HOST_PATH", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(kv.Key, "MSBuildSDKsPath", StringComparison.OrdinalIgnoreCase))
					psi.EnvironmentVariables[kv.Key] = kv.Value;
			}
			// This app's own process may be running under a non-invariant/non-English OS locale
			// (LANG/LC_ALL inherited from the desktop session), and some MSBuild tasks parse
			// culture-sensitive content internally; pinning the build child to en_US.UTF-8 makes
			// that parsing deterministic regardless of the app's own locale.
			//
			// Do NOT also set DOTNET_SYSTEM_GLOBALIZATION_INVARIANT here. It contradicts the two
			// lines below - in globalization-invariant mode the only valid culture is the invariant
			// one, so "en-US" becomes an invalid culture identifier - and any compiler that
			// constructs a CultureInfo for its own diagnostics then fails outright. The F# compiler
			// does exactly that and aborts every build with:
			//   error FS0193 : internal error : Only the invariant culture is supported in
			//   globalization-invariant mode ... 'en-US' is an invalid culture identifier
			// Determinism is already achieved by the explicit locale, without disabling ICU.
			psi.EnvironmentVariables["LANG"] = "en_US.UTF-8";
			psi.EnvironmentVariables["LC_ALL"] = "en_US.UTF-8";
			// The LibreWPF SDK compiles a tiny native Win32-compat shim with `cc` on macOS. With no
			// SDKROOT, clang falls back to its own SDK search order and picks the CommandLineTools
			// SDK, which on this toolchain carries architectures the installed linker rejects
			// ("MacOSX27.0.sdk ... error: unknown architecture") - so every LibreWPF.Sdk project's
			// build failed at that final shim step (the assembly itself is written earlier, which is
			// why the failure was easy to miss). Point the build child at Xcode's macOS SDK, the
			// same one `xcrun --sdk macosx --show-sdk-path` reports, so an in-IDE build matches a
			// shell `dotnet build` with SDKROOT exported. Only when the caller hasn't chosen one.
			if (OperatingSystem.IsMacOS()
				&& !(psi.Environment.TryGetValue("SDKROOT", out var existingSdkRoot) && !string.IsNullOrEmpty(existingSdkRoot))) {
				var xcodeSdkRoot = MacOsSdkRoot.Value;
				if (!string.IsNullOrEmpty(xcodeSdkRoot))
					psi.Environment["SDKROOT"] = xcodeSdkRoot;
			}
			return psi;
		}

		static readonly Lazy<string> MacOsSdkRoot = new(TryGetMacOsSdkRoot);

		/// <summary>Xcode's current macOS SDK path, or null when xcrun/SDK are unavailable.</summary>
		static string TryGetMacOsSdkRoot()
		{
			try {
				var psi = new ProcessStartInfo("xcrun") {
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				};
				psi.ArgumentList.Add("--sdk");
				psi.ArgumentList.Add("macosx");
				psi.ArgumentList.Add("--show-sdk-path");
				using var process = Process.Start(psi);
				if (process == null)
					return null;
				var output = process.StandardOutput.ReadToEnd().Trim();
				process.StandardError.ReadToEnd();
				process.WaitForExit(5000);
				return process.ExitCode == 0 && Directory.Exists(output) ? output : null;
			} catch {
				return null;
			}
		}

		/// <summary>
		/// Resolves a project's assembly references by running MSBuild's own `ResolveReferences`
		/// target in a child process and reading back the `ReferencePath` items.
		///
		/// Out of process for the same load-bearing reason as <see cref="BuildAsync"/>: an
		/// in-process MSBuild cannot run SDK tasks, because a hosted engine resolves SDKs and tasks
		/// against the CURRENT process's runtime location, and this app's own
		/// Microsoft.Build.Framework is older than the tasks the selected SDK ships
		/// (MSB4062 - see doc/technotes/msbuild.md). Plain evaluation never executes a task, which
		/// is why Solution Explorer's in-process evaluation has always worked and this has not.
		///
		/// This used to return only <paramref name="additionalReferences"/>, i.e. nothing, and the
		/// consequences were not confined to the project browser: RoslynWorkspaceHelper falls back
		/// to the host runtime's trusted platform assemblies when this comes back empty, so every
		/// C#/VB project compiled against roughly three references. Measured on
		/// tests/fixtures/SampleTestProject - 3 references instead of 179, every xunit type
		/// reported as CS0246, and the resulting snapshot persisted to .od/roslyn-tfm-cache with
		/// `References: []`, which then looked like a cache bug rather than a missing target.
		/// </summary>
		public IList<ReferenceProjectItem> ResolveAssemblyReferences(
			MSBuildBasedProject baseProject,
			ReferenceProjectItem[] additionalReferences = null, bool resolveOnlyAdditionalReferences = false,
			bool logErrorsToOutputPad = true)
		{
			var results = new List<ReferenceProjectItem>();
			if (additionalReferences != null)
				results.AddRange(additionalReferences);
			if (resolveOnlyAdditionalReferences || baseProject == null)
				return results;

			try {
				foreach (var path in RunResolveReferences(baseProject.FileName.ToString(), InnerBuildTargetFramework(baseProject), logErrorsToOutputPad)) {
					// Include is the assembly's simple name, matching what a hand-written
					// <Reference Include="..."/> would carry; the resolved path goes in HintPath,
					// which is what RoslynWorkspaceHelper.GetMetadataReferences reads.
					var item = new ReferenceProjectItem(baseProject, Path.GetFileNameWithoutExtension(path)) {
						HintPath = path
					};
					results.Add(item);
				}
			} catch (Exception ex) {
				// Never throw out of reference resolution: an unresolvable project must degrade to
				// "no references" (the previous behaviour) rather than break project loading.
				LoggingService.Warn("ResolveAssemblyReferences failed for "
					+ baseProject.FileName + ": " + ex.Message);
			}
			return results;
		}

		/// <summary>
		/// The TargetFramework to pin when a project declares <c>TargetFrameworks</c> (plural) with a
		/// single entry. Its unpinned evaluation is the multi-targeting outer build, which has no
		/// ResolveReferences target: such a project failed with MSB4057 on every solution open (3 of
		/// OpenDevelop.Mvp's projects) and reached Roslyn with no resolved references at all. Null
		/// when the project already evaluates to a single TargetFramework.
		/// </summary>
		static string InnerBuildTargetFramework(MSBuildBasedProject project)
		{
			if (!string.IsNullOrWhiteSpace(project.GetEvaluatedProperty("TargetFramework")))
				return null;
			var frameworks = project.GetEvaluatedProperty("TargetFrameworks");
			if (string.IsNullOrWhiteSpace(frameworks))
				return null;
			return frameworks.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(framework => framework.Trim())
				.FirstOrDefault(framework => framework.Length > 0);
		}

		/// <summary>
		/// One `dotnet msbuild` for many projects. A generated traversal project first runs every
		/// project's ResolveReferences in parallel (-m), then asks each project again: those calls
		/// are answered from MSBuild's results cache, and only they can label their outputs with the
		/// requesting project - an item that came from a ProjectReference carries the REFERENCED
		/// project in MSBuildSourceProjectFile, so grouping by that metadata misattributes it.
		/// Measured on 12 OpenDevelop projects: 14.1 s one process each, 1.1 s batched, with
		/// identical reference sets per project.
		/// </summary>
		public IReadOnlyDictionary<MSBuildBasedProject, IReadOnlyList<string>> ResolveAssemblyReferencePaths(IReadOnlyList<MSBuildBasedProject> projects)
		{
			var result = new Dictionary<MSBuildBasedProject, IReadOnlyList<string>>();
			if (projects == null || projects.Count == 0)
				return result;
			// MSBuild's NuGet SDK resolver reads the msbuild-sdks pins of the global.json above the
			// ENTRY project, for every project in the build. A traversal in the temp directory
			// therefore resolved Sdk="LibreWPF.Sdk" to a version none of these projects pins (and
			// brought back NETSDK1047 for 53 of them). One batch per global.json directory, each
			// started from that directory, resolves exactly what each project's own build does.
			// The batches are independent processes, so they run side by side.
			var batches = projects.GroupBy(project => GlobalJsonDirectory(project.FileName.ToString()), StringComparer.Ordinal)
				.Select(group => Task.Run(() => ResolveAssemblyReferencePathsFrom(group.Key, group.ToList())))
				.ToArray();
			foreach (var batch in batches) {
				var resolved = batch.Result;
				if (resolved == null)
					continue; // that batch failed; its projects fall back to single resolution
				foreach (var pair in resolved)
					result[pair.Key] = pair.Value;
			}
			return result;
		}

		/// <summary>The nearest directory above <paramref name="projectFile"/> holding a global.json, or null.</summary>
		static string GlobalJsonDirectory(string projectFile)
		{
			for (var directory = Path.GetDirectoryName(projectFile); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory)) {
				if (File.Exists(Path.Combine(directory, "global.json")))
					return directory;
			}
			return null;
		}

		Dictionary<MSBuildBasedProject, IReadOnlyList<string>> ResolveAssemblyReferencePathsFrom(string globalJsonDirectory, IReadOnlyList<MSBuildBasedProject> projects)
		{
			var result = new Dictionary<MSBuildBasedProject, IReadOnlyList<string>>();
			// Under the global.json's directory so the SDK resolver sees it; .od is OpenDevelop's own
			// scratch folder (gitignored in this repository). Removed again afterwards, together with
			// .od itself when this created it, so nothing is left in a repository that does not
			// ignore it.
			string scratch = globalJsonDirectory != null ? Path.Combine(globalJsonDirectory, ".od") : Path.GetTempPath();
			bool createdScratch = !Directory.Exists(scratch);
			string traversal = Path.Combine(scratch, "resolve-references-" + Guid.NewGuid().ToString("N") + ".proj");
			try {
				Directory.CreateDirectory(scratch);
				var requests = projects.Select(project => (Project: project, File: project.FileName.ToString(), TargetFramework: InnerBuildTargetFramework(project))).ToList();
				File.WriteAllText(traversal, CreateResolveReferencesTraversal(requests.Select(r => (r.File, r.TargetFramework)).ToList()));

				var psi = CreateDotnetChildStartInfo();
				psi.ArgumentList.Add("msbuild");
				psi.ArgumentList.Add(traversal);
				psi.ArgumentList.Add("--nologo");
				psi.ArgumentList.Add("-m:" + ResolveNodeCount());
				psi.ArgumentList.Add("-t:Resolve");
				psi.ArgumentList.Add("-getItem:ODReferencePath");
				using var process = Process.Start(psi);
				if (process == null)
					return null;
				var stdoutTask = process.StandardOutput.ReadToEndAsync();
				var stderrTask = process.StandardError.ReadToEndAsync();
				int timeout = Math.Min(600_000, ResolveReferencesTimeoutMilliseconds + 2_000 * projects.Count);
				if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, timeout) || !process.WaitForExit(timeout)) {
					try { process.Kill(entireProcessTree: true); } catch { }
					LoggingService.Warn("Batched ResolveReferences timed out for " + projects.Count + " projects; resolving them one by one.");
					return null;
				}
				string stdout = stdoutTask.Result, stderr = stderrTask.Result;
				var items = ParseLabelledReferencePaths(stdout);
				if (items == null) {
					LoggingService.Warn("Batched ResolveReferences produced no item list (exit " + process.ExitCode + "); resolving one by one. "
						+ FirstLine(stderr.Length > 0 ? stderr : stdout));
					return null;
				}
				string diagnostics = stdout + "\n" + stderr;
				foreach (var request in requests) {
					items.TryGetValue(request.File, out var paths);
					var errors = ErrorLinesFor(diagnostics, request.File);
					if ((paths == null || paths.Count == 0) && errors.Length > 0) {
						LoggingService.Warn("ResolveAssemblyReferences (batched) failed for " + request.File + ". " + FirstLine(errors));
						ReferenceResolutionDiagnostics.Failed(request.File, errors);
						result[request.Project] = Array.Empty<string>();
					} else {
						ReferenceResolutionDiagnostics.Succeeded(request.File);
						result[request.Project] = (IReadOnlyList<string>)paths ?? Array.Empty<string>();
					}
				}
				return result;
			} catch (Exception ex) {
				LoggingService.Warn("Batched ResolveReferences failed: " + ex.Message + "; resolving one by one.");
				return null;
			} finally {
				try {
					File.Delete(traversal);
					if (createdScratch && !Directory.EnumerateFileSystemEntries(scratch).Any())
						Directory.Delete(scratch);
				} catch { }
			}
		}

		static int ResolveNodeCount() =>
			int.TryParse(Environment.GetEnvironmentVariable("OD_RESOLVE_NODES"), out var nodes) && nodes > 0
				? nodes : Math.Max(1, Math.Min(4, Environment.ProcessorCount));

		static string CreateResolveReferencesTraversal(IReadOnlyList<(string File, string TargetFramework)> projects)
		{
			string Escape(string value) => System.Security.SecurityElement.Escape(value);
			string PropertiesFor(string targetFramework) =>
				"BuildingInsideVisualStudio=true" + (string.IsNullOrEmpty(targetFramework) ? "" : ";TargetFramework=" + targetFramework);
			var text = new StringBuilder();
			text.AppendLine("<Project>");
			text.AppendLine("  <ItemGroup>");
			foreach (var project in projects)
				text.AppendLine("    <ODProject Include=\"" + Escape(project.File) + "\" Properties=\"" + Escape(PropertiesFor(project.TargetFramework)) + "\" />");
			text.AppendLine("  </ItemGroup>");
			var calls = new List<string>();
			for (int i = 0; i < projects.Count; i++) {
				// Same global properties as the parallel pass, so this is a results-cache hit.
				text.AppendLine("  <Target Name=\"R" + i + "\">");
				text.AppendLine("    <MSBuild Projects=\"" + Escape(projects[i].File) + "\" Targets=\"ResolveReferences\" Properties=\"" + Escape(PropertiesFor(projects[i].TargetFramework))
					+ "\" SkipNonexistentTargets=\"true\" ContinueOnError=\"WarnAndContinue\"><Output TaskParameter=\"TargetOutputs\" ItemName=\"_R" + i + "\" /></MSBuild>");
				text.AppendLine("    <ItemGroup><ODReferencePath Include=\"@(_R" + i + ")\" ODProject=\"" + Escape(projects[i].File) + "\" /></ItemGroup>");
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
		static Dictionary<string, List<string>> ParseLabelledReferencePaths(string stdout)
		{
			int start = stdout.IndexOf('{');
			if (start < 0)
				return null;
			try {
				using var document = System.Text.Json.JsonDocument.Parse(stdout.Substring(start));
				if (!document.RootElement.TryGetProperty("Items", out var itemsElement)
				    || !itemsElement.TryGetProperty("ODReferencePath", out var list))
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

		/// <summary>MSBuild error lines attributed to <paramref name="projectFile"/>: either starting
		/// with its path or carrying it as the trailing [project] marker.</summary>
		static string ErrorLinesFor(string output, string projectFile)
		{
			var lines = output.Split('\n')
				.Where(line => line.Contains(": error ", StringComparison.Ordinal)
				               && (line.TrimStart().StartsWith(projectFile, StringComparison.OrdinalIgnoreCase)
				                   || line.Contains("[" + projectFile + "]", StringComparison.OrdinalIgnoreCase)))
				.Select(line => line.Trim());
			return string.Join("\n", lines);
		}

		static IEnumerable<string> RunResolveReferences(string projectFileName, string targetFramework, bool logErrorsToOutputPad)
		{
			var psi = CreateDotnetChildStartInfo();
			psi.ArgumentList.Add("msbuild");
			psi.ArgumentList.Add(projectFileName);
			psi.ArgumentList.Add("--nologo");
			psi.ArgumentList.Add("-m:1");
			psi.ArgumentList.Add("-p:BuildingInsideVisualStudio=true");
			psi.ArgumentList.Add("-t:ResolveReferences");
			if (!string.IsNullOrEmpty(targetFramework))
				psi.ArgumentList.Add("-p:TargetFramework=" + targetFramework);
			// -getItem makes MSBuild print the requested item list as JSON on stdout and suppresses
			// normal build output, so no log parsing is involved.
			psi.ArgumentList.Add("-getItem:ReferencePath");

			using var process = Process.Start(psi);
			if (process == null)
				return Array.Empty<string>();

			// Both redirected pipes have to be drained CONCURRENTLY, and the timeout has to cover
			// the draining - not just the exit.
			//
			// This used to read stdout to EOF, then stderr to EOF, and only then call
			// WaitForExit(timeout). Both halves of that are wrong:
			//
			//  - Sequential ReadToEnd() on two pipes is the textbook child-process deadlock. The
			//    pipe buffer is finite (64 KB), so a child that writes more than that to stderr
			//    while this side is still blocked draining stdout gets stuck in write(); this side
			//    stays stuck in read(); neither ever moves. A failing SDK target is exactly the
			//    case that produces a large stderr - see doc/technotes/msbuild.md.
			//  - The timeout could never fire, because WaitForExit was only reached AFTER both
			//    reads had already returned. Whatever it was protecting against, it was protecting
			//    against it only once the danger had passed.
			//
			// ReadToEndAsync starts both reads on the thread pool. Task.WaitAll - rather than
			// awaiting each task in turn with GetAwaiter().GetResult() - is what makes the timeout
			// bound the reads. That is safe to block on even from the UI thread despite the warning
			// in CLAUDE.md, because StreamReader's async path uses ConfigureAwait(false) internally
			// and so posts no continuation back to the dispatcher; it is still slow, which is why
			// callers on the UI thread are a bug in their own right (see the doc technote).
			// One deadline covers both waits, so the total stays the documented timeout rather than
			// twice it. Once both pipes reach EOF the child has all but exited, so the second wait
			// normally returns immediately; the remaining budget is only there to keep it bounded.
			var deadline = Stopwatch.StartNew();
			int Remaining() => Math.Max(0, ResolveReferencesTimeoutMilliseconds - (int)deadline.ElapsedMilliseconds);

			var stdoutTask = process.StandardOutput.ReadToEndAsync();
			var stderrTask = process.StandardError.ReadToEndAsync();
			if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, Remaining())
				|| !process.WaitForExit(Remaining())) {
				try { process.Kill(entireProcessTree: true); } catch { }
				LoggingService.Warn("ResolveAssemblyReferences timed out for " + projectFileName);
				ReferenceResolutionDiagnostics.Failed(projectFileName, "error MSB0000: timed out after " + ResolveReferencesTimeoutMilliseconds / 1000 + " s");
				return Array.Empty<string>();
			}
			string stdout = stdoutTask.Result;
			string stderr = stderrTask.Result;
			if (process.ExitCode != 0) {
				// A project that has never been restored legitimately fails here. Warn rather than
				// throw, and keep the message in the log where a "why are there no references"
				// investigation will find it.
				LoggingService.Warn("ResolveAssemblyReferences exited " + process.ExitCode + " for "
					+ projectFileName + ". " + FirstLine(stderr.Length > 0 ? stderr : stdout));
				ReferenceResolutionDiagnostics.Failed(projectFileName, stdout + "\n" + stderr);
				return Array.Empty<string>();
			}
			ReferenceResolutionDiagnostics.Succeeded(projectFileName);
			return MSBuildGetItemOutput.ParseItemIdentities(stdout, "ReferencePath");
		}

		static string FirstLine(string text)
		{
			if (string.IsNullOrEmpty(text))
				return string.Empty;
			int newline = text.IndexOf('\n');
			return (newline < 0 ? text : text.Substring(0, newline)).Trim();
		}

		const int ResolveReferencesTimeoutMilliseconds = 120_000;

		/// <summary>
		/// Set by <see cref="BuildEngine"/> on a project's options once a single up-front
		/// <see cref="RestoreAsync"/> has restored it with the same global properties; the build then
		/// passes --no-restore instead of restoring again. Never forwarded to MSBuild.
		/// </summary>
		internal const string RestoredUpFrontProperty = "_OpenDevelopRestoredUpFront";

		/// <summary>
		/// The global properties a restore/build of <paramref name="options"/> passes, as one string.
		/// Two option sets with the same key restore identically.
		/// </summary>
		internal static string GlobalPropertiesKey(ProjectBuildOptions options)
		{
			var psi = new ProcessStartInfo();
			AddGlobalProperties(psi, options);
			return string.Join("\n", psi.ArgumentList);
		}

		/// <summary>
		/// Restores <paramref name="solutionOrProject"/> (and, as dotnet restore always does, everything
		/// it references) once, with the global properties of <paramref name="options"/>. Output goes to
		/// the build log as plain messages; a failure is not a build error, because every project
		/// then simply restores itself as part of its own build.
		/// </summary>
		internal async Task<bool> RestoreAsync(string solutionOrProject, ProjectBuildOptions options, IBuildFeedbackSink feedbackSink, CancellationToken cancellationToken)
		{
			var psi = CreateDotnetChildStartInfo();
			psi.ArgumentList.Add("restore");
			psi.ArgumentList.Add(solutionOrProject);
			psi.ArgumentList.Add("--nologo");
			AddGlobalProperties(psi, options);
			var output = new List<string>();
			try {
				using var process = new Process { StartInfo = psi };
				process.OutputDataReceived += (sender, e) => { if (e.Data != null) lock (output) output.Add(e.Data); };
				process.ErrorDataReceived += (sender, e) => { if (e.Data != null) lock (output) output.Add(e.Data); };
				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				await process.WaitForExitAsync(cancellationToken);
				lock (output) {
					foreach (string line in output)
						feedbackSink?.ReportMessage(new RichText(line));
				}
				return process.ExitCode == 0;
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception ex) {
				LoggingService.Warn("Up-front restore of " + solutionOrProject + " failed to run: " + ex.Message);
				return false;
			}
		}

		static void AddGlobalProperties(ProcessStartInfo psi, ProjectBuildOptions options)
		{
			// We always build a single .csproj directly (never a .sln), so the CurrentVersion.targets
			// logic that synthesizes solution-dependency ProjectReferences and resolves their
			// per-solution-configuration via AssignProjectConfiguration is pure overhead we don't
			// need - and passing BuildingInsideVisualStudio=true (as any IDE driving single-project
			// builds does) skips it, matching how this project is actually being built here.
			psi.ArgumentList.Add("-p:BuildingInsideVisualStudio=true");
			// ...but BuildingInsideVisualStudio also makes _ComputeNonExistentFileProperty add a
			// nonexistent output to CoreCompile (Visual Studio's host compiler does its own change
			// detection), so every build recompiled and rewrote the assembly, and every dependent
			// then rebuilt too. The legacy in-process engine overrode that target with an empty one
			// (MSBuildEngineWorker); its condition also requires UseHostCompilerIfAvailable, so
			// turning that off restores MSBuild's incremental compile the same way.
			psi.ArgumentList.Add("-p:UseHostCompilerIfAvailable=false");
			// -p:Configuration rather than -c: `dotnet restore` rejects -c, and both verbs must pass
			// identical global properties for the up-front restore to stand in for a build's own.
			if (!string.IsNullOrEmpty(options.Configuration)) {
				psi.ArgumentList.Add("-p:Configuration=" + options.Configuration);
			}
			if (!string.IsNullOrEmpty(options.Platform) && options.Platform != "AnyCPU") {
				psi.ArgumentList.Add("-p:Platform=" + options.Platform);
			}
			if (options.Properties != null) {
				foreach (var kv in options.Properties.OrderBy(kv => kv.Key, StringComparer.Ordinal)) {
					// MSBuildBasedProject.CreateProjectBuildOptions sets this to an XML blob
					// describing the whole solution's project/configuration mapping, so that
					// ProjectReferences resolve their configuration correctly when building a
					// .sln directly. We always build one .csproj at a time here, so it's both
					// unneeded and unsafe to forward: arbitrary XML can't be round-tripped through
					// a single `-p:Name=Value` CLI token (no escaping for '{', ';', quotes, etc.),
					// which is exactly what caused MSB3108 "unexpected token '{'" failures here.
					if (kv.Key == "CurrentSolutionConfigurationContents" || kv.Key == RestoredUpFrontProperty)
						continue;
					psi.ArgumentList.Add($"-p:{kv.Key}={kv.Value}");
				}
			}
		}

		/// <summary>
		/// Builds <paramref name="projects"/> in ONE MSBuild process (`-m`), as `dotnet build` of a
		/// solution does, and records each one that succeeded with the fast up-to-date check, so
		/// the per-project builds that follow skip it. Ten `dotnet build` processes side by side
		/// each repeat evaluation and reference resolution and contend for the CPU: a project
		/// that builds in 3-5 s alone took 15-45 s that way (doc/technotes/fast-mode.md).
		///
		/// The projects come in dependency waves and are built with exactly the global properties of
		/// a per-project build (BuildingInsideVisualStudio included, so MSBuild does not build
		/// references itself): each wave in parallel, after the one before it. Letting MSBuild
		/// follow ProjectReferences instead built a project referenced with different global
		/// properties twice at once into the same output (MC1000 on ICSharpCode.Core.Presentation).
		/// Output lines reach <paramref name="reportLine"/> and diagnostics <paramref name="reportError"/>
		/// while the build runs, attributed by the project MSBuild appends to each one. Returns the
		/// projects that built and those that failed with an error of their own; any other failed
		/// one is built again on its own. Never throws.
		/// </summary>
		public async Task<(ISet<IProject> Built, ISet<IProject> FailedWithErrors)> BuildInOneProcessAsync(string solutionFile, IReadOnlyList<IReadOnlyList<IProject>> waves, ProjectBuildOptions options,
			Action<string> reportLine, Action<IProject, BuildError> reportError, CancellationToken cancellationToken)
		{
			var built = new HashSet<IProject>();
			var failedWithErrors = new HashSet<IProject>();
			var nothing = ((ISet<IProject>)built, (ISet<IProject>)failedWithErrors);
			// The traversal must sit under the global.json its projects use (msbuild-sdks pins are
			// read from the entry project's directory); others keep the per-project path.
			string globalJsonDirectory = GlobalJsonDirectory(solutionFile);
			bool SameGlobalJson(IProject project) => string.Equals(GlobalJsonDirectory(project.FileName.ToString()), globalJsonDirectory, StringComparison.Ordinal);
			var includedWaves = waves.Select(wave => wave.Where(SameGlobalJson).ToList()).Where(wave => wave.Count > 0).ToList();
			var included = includedWaves.SelectMany(wave => wave).ToList();
			if (included.Count == 0)
				return nothing;
			var byFile = included.ToDictionary(project => project.FileName.ToString(), StringComparer.OrdinalIgnoreCase);
			var withErrors = new HashSet<IProject>();
			var seenDiagnostics = new HashSet<string>(StringComparer.Ordinal);
			string scratch = globalJsonDirectory != null ? Path.Combine(globalJsonDirectory, ".od") : Path.GetTempPath();
			bool createdScratch = !Directory.Exists(scratch);
			string id = Guid.NewGuid().ToString("N");
			string traversal = Path.Combine(scratch, "build-" + id + ".proj");
			// Results go to a file, not -getItem: that switches the console log off, and with it
			// the diagnostics streamed below.
			string resultFile = Path.Combine(scratch, "build-" + id + ".results");
			var buildStartedUtc = DateTime.UtcNow;
			try {
				Directory.CreateDirectory(scratch);
				File.WriteAllText(traversal, CreateBuildTraversal(includedWaves.Select(wave => (IReadOnlyList<string>)wave.Select(project => project.FileName.ToString()).ToList()).ToList()));
				var psi = CreateDotnetChildStartInfo();
				psi.ArgumentList.Add("msbuild");
				psi.ArgumentList.Add(traversal);
				psi.ArgumentList.Add("--nologo");
				psi.ArgumentList.Add("-m:" + Environment.ProcessorCount);
				psi.ArgumentList.Add("-t:BuildAll");
				psi.ArgumentList.Add("-v:m");
				psi.ArgumentList.Add("-clp:NoSummary");
				psi.ArgumentList.Add("-p:ODResultFile=" + resultFile);
				// Restore is a separate step here (BuildEngine restores up front, or each project
				// restores in its own build); a traversal has nothing of its own to restore.
				AddGlobalProperties(psi, options);
				var started = Stopwatch.StartNew();
				var stdout = new StringBuilder();
				var stderr = new StringBuilder();
				// The output threads only queue lines; they are reported on the caller's thread (see the
				// loop below). Reporting them straight from the output threads, a few hundred at a
				// time, hung the IDE: the UI thread blocked for good on a lock in Visual.GetDpi.
				var pending = new System.Collections.Concurrent.ConcurrentQueue<(string Line, StringBuilder Buffer)>();
				void OnLine(string line, StringBuilder buffer)
				{
					lock (buffer)
						buffer.AppendLine(line);
					reportLine(line);
					var trimmed = line.Trim();
					var match = DiagnosticLine.Match(trimmed);
					if (!match.Success)
						return;
					var owner = DiagnosticProject.Match(trimmed);
					if (!owner.Success || !byFile.TryGetValue(owner.Groups["project"].Value, out var project))
						return;
					lock (seenDiagnostics) {
						// MSBuild repeats every diagnostic in its closing summary, and a multi-targeted
						// project reports it once per TFM.
						if (!seenDiagnostics.Add(owner.Groups["project"].Value + "|" + match.Groups["file"].Value + "|" + match.Groups["line"].Value
						    + "|" + match.Groups["column"].Value + "|" + match.Groups["code"].Value))
							return;
						bool isWarning = match.Groups["severity"].Value == "warning";
						// A restore problem is not final: the traversal restores nothing, and the
						// project's own build (which restores) may well succeed.
						if (!isWarning && !IsRestoreError(match.Groups["code"].Value))
							withErrors.Add(project);
						reportError(project, new BuildError(match.Groups["file"].Value,
							int.Parse(match.Groups["line"].Value), int.Parse(match.Groups["column"].Value),
							match.Groups["code"].Value, match.Groups["text"].Value.Trim()) { IsWarning = isWarning });
					}
				}
				using var process = new Process { StartInfo = psi };
				process.OutputDataReceived += (sender, e) => { if (e.Data != null) pending.Enqueue((e.Data, stdout)); };
				process.ErrorDataReceived += (sender, e) => { if (e.Data != null) pending.Enqueue((e.Data, stderr)); };
				void Drain()
				{
					while (pending.TryDequeue(out var item))
						OnLine(item.Line, item.Buffer);
				}
				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				var exited = process.WaitForExitAsync(cancellationToken);
				while (await Task.WhenAny(exited, Task.Delay(200, cancellationToken)) != exited)
					Drain();
				await exited;
				Drain();
				var results = ParseBuildResults(resultFile);
				var trace = Environment.GetEnvironmentVariable("OD_ONE_PROCESS_TRACE");
				if (!string.IsNullOrEmpty(trace)) {
					File.AppendAllText(trace, "== " + DateTime.Now + " exit " + process.ExitCode + " stdout lines " + stdout.ToString().Split('\n').Length
						+ "\nresults:\n" + (File.Exists(resultFile) ? File.ReadAllText(resultFile) : "(none)")
						+ "\nwithErrors: " + string.Join(", ", withErrors.Select(p => p.Name))
						+ "\nincluded: " + string.Join(", ", included.Select(p => p.FileName.ToString())) + "\n"
						+ "stdout head:\n" + string.Join("\n", stdout.ToString().Split('\n').Take(40)) + "\n");
				}
				if (results == null) {
					LoggingService.Warn("One-process build produced no result list (exit " + process.ExitCode + "); building one by one. " + FirstLine(stderr.Length > 0 ? stderr.ToString() : stdout.ToString()));
					return nothing;
				}
				foreach (var project in included) {
					if (results.TryGetValue(project.FileName.ToString(), out var ok) && ok) {
						FastUpToDateCheck.Succeeded(project, options, buildStartedUtc);
						built.Add(project);
					} else {
						FastUpToDateCheck.Forget(project);
						if (withErrors.Contains(project))
							failedWithErrors.Add(project);
						reportLine(project.Name + " failed in the one-process build ("
							+ (results.ContainsKey(project.FileName.ToString()) ? "result false" : "no result")
							+ (withErrors.Contains(project) ? ", errors reported" : ", no errors of its own") + ")");
					}
				}
				reportLine("Built " + built.Count + "/" + included.Count + " projects in one MSBuild process in " + started.ElapsedMilliseconds + " ms.");
				PerfTimeline.Mark(PerfTimeline.Build, "built-in-one-process", built.Count + "/" + included.Count + " in " + started.ElapsedMilliseconds + "ms");
				return (built, failedWithErrors);
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception ex) {
				LoggingService.Warn("One-process build failed: " + ex.Message + "; building one by one.");
				return nothing;
			} finally {
				try {
					File.Delete(traversal);
					File.Delete(resultFile);
					if (createdScratch && !Directory.EnumerateFileSystemEntries(scratch).Any())
						Directory.Delete(scratch);
				} catch { }
			}
		}

		static bool IsRestoreError(string code) =>
			code.StartsWith("NU", StringComparison.OrdinalIgnoreCase)
			|| code is "NETSDK1004" or "NETSDK1005" or "NETSDK1047";

		/// <summary>The project MSBuild appends to a diagnostic: "... [/path/x.csproj::TargetFramework=net10.0]".</summary>
		static readonly Regex DiagnosticProject = new Regex(@"\[(?<project>[^\[\]]+?)(::[^\]]*)?\]$", RegexOptions.Compiled);

		static string CreateBuildTraversal(IReadOnlyList<IReadOnlyList<string>> waves)
		{
			string Escape(string value) => System.Security.SecurityElement.Escape(value);
			var text = new StringBuilder();
			text.AppendLine("<Project>");
			var calls = new List<string>();
			int index = 0;
			for (int w = 0; w < waves.Count; w++) {
				foreach (var file in waves[w]) {
					// Results-cache hits after the waves; MSBuildLastTaskResult says whether it built.
					// ErrorAndContinue, not true (= WarnAndContinue): that turns the errors into
					// warnings and MSBuildLastTaskResult into true, so a failed project was recorded
					// as built. A project of a wave that never ran gets no line, and so is built on
					// its own afterwards (or skipped there, when one of its dependencies failed).
					text.AppendLine("  <Target Name=\"B" + index + "\" Condition=\"'$(ODWave" + w + "Ran)' == 'true'\">");
					text.AppendLine("    <MSBuild Projects=\"" + Escape(file) + "\" Targets=\"Build\" ContinueOnError=\"ErrorAndContinue\" />");
					text.AppendLine("    <WriteLinesToFile File=\"$(ODResultFile)\" Lines=\"$(MSBuildLastTaskResult)|" + Escape(file) + "\" />");
					text.AppendLine("  </Target>");
					calls.Add("B" + index++);
				}
			}
			// The waves are their own target and the result targets follow it through
			// DependsOnTargets: a target run by CallTarget does not see properties its caller set.
			text.AppendLine("  <Target Name=\"BuildAll\" DependsOnTargets=\"Waves;" + string.Join(";", calls) + "\" />");
			text.AppendLine("  <Target Name=\"Waves\">");
			for (int w = 0; w < waves.Count; w++) {
				// After a failure the later waves are not started: they hold its dependents (or would
				// be built against outputs that are about to be rebuilt).
				string condition = w == 0 ? "" : " Condition=\"'$(ODFailed)' != 'true'\"";
				text.AppendLine("    <MSBuild Projects=\"" + string.Join(";", waves[w].Select(Escape)) + "\" Targets=\"Build\" BuildInParallel=\"true\" ContinueOnError=\"ErrorAndContinue\"" + condition + " />");
				text.AppendLine("    <PropertyGroup" + condition + "><ODWave" + w + "Ran>true</ODWave" + w + "Ran><ODFailed Condition=\"'$(MSBuildLastTaskResult)' == 'false'\">true</ODFailed></PropertyGroup>");
			}
			text.AppendLine("  </Target>");
			text.AppendLine("</Project>");
			return text.ToString();
		}

		/// <summary>The traversal's "succeeded|project file" lines, or null if it wrote none.</summary>
		static Dictionary<string, bool> ParseBuildResults(string resultFile)
		{
			if (!File.Exists(resultFile))
				return null;
			var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			foreach (var line in File.ReadAllLines(resultFile)) {
				int bar = line.IndexOf('|');
				if (bar > 0)
					results[line.Substring(bar + 1)] = string.Equals(line.Substring(0, bar), "true", StringComparison.OrdinalIgnoreCase);
			}
			return results;
		}

		public async Task<bool> BuildAsync(IProject project, ProjectBuildOptions options, IBuildFeedbackSink feedbackSink, CancellationToken cancellationToken, IEnumerable<string> additionalTargetFiles = null)
		{
			if (FastUpToDateCheck.IsUpToDate(project, options, out var notUpToDate)) {
				feedbackSink.ReportMessage(new RichText(project.Name + " is up to date; skipped (fast up-to-date check)."));
				PerfTimeline.Mark(PerfTimeline.Build, "project-up-to-date", project.Name);
				return true;
			}
			if (options.Target == BuildTarget.Build)
				LoggingService.Debug("Fast up-to-date check: building " + project.Name + " (" + notUpToDate + ")");
			var buildStartedUtc = DateTime.UtcNow;
			var psi = CreateDotnetChildStartInfo();
			psi.ArgumentList.Add(TargetToVerb(options.Target));
			psi.ArgumentList.Add(project.FileName.ToString());
			psi.ArgumentList.Add("--nologo");
			// Keep the child MSBuild graph inside a single node. The IDE already orchestrates builds
			// at the project level, and .NET SDK 10.0.301 on macOS can crash ResolvePackageFileConflicts
			// while parallelizing multi-target SDK projects that include net462.
			psi.ArgumentList.Add("-m:1");
			if (options.Properties != null && options.Properties.ContainsKey(RestoredUpFrontProperty)
			    && options.Target != BuildTarget.Clean)
				psi.ArgumentList.Add("--no-restore");
			AddGlobalProperties(psi, options);

			var outputLines = new List<string>();
			bool success;
			try {
				using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true }) {
					process.OutputDataReceived += (sender, e) => {
						if (e.Data == null)
							return;
						lock (outputLines)
							outputLines.Add(e.Data);
					};
					process.ErrorDataReceived += (sender, e) => {
						if (e.Data == null)
							return;
						lock (outputLines)
							outputLines.Add(e.Data);
					};

					var started = Stopwatch.StartNew();
					process.Start();
					process.BeginOutputReadLine();
					process.BeginErrorReadLine();

					await process.WaitForExitAsync(cancellationToken);
					success = process.ExitCode == 0;
					PerfTimeline.Mark(PerfTimeline.Build, "project-built",
						project.Name + " " + started.ElapsedMilliseconds + "ms " + (success ? "ok" : "failed"));
				}
				if (success && options.Target != BuildTarget.Clean)
					FastUpToDateCheck.Succeeded(project, options, buildStartedUtc);
				else
					FastUpToDateCheck.Forget(project);
			} catch (Exception ex) {
				feedbackSink.ReportError(new BuildError(project.FileName.ToString(), ex.Message));
				return false;
			}

			List<string> lines;
			lock (outputLines)
				lines = outputLines;

			bool reportedAnyDiagnostic = false;
			foreach (string line in lines) {
				feedbackSink.ReportMessage(new RichText(line));
				Match match = DiagnosticLine.Match(line.Trim());
				if (!match.Success)
					continue;
				reportedAnyDiagnostic = true;
				feedbackSink.ReportError(new BuildError(
					match.Groups["file"].Value,
					int.Parse(match.Groups["line"].Value),
					int.Parse(match.Groups["column"].Value),
					match.Groups["code"].Value,
					match.Groups["text"].Value.Trim()
					) { IsWarning = match.Groups["severity"].Value == "warning" });
			}

			if (!success && !reportedAnyDiagnostic) {
				// Build failed but nothing matched the diagnostic-line regex (e.g. the dotnet host
				// itself couldn't be started, or output uses a format the regex doesn't cover) -
				// still surface something instead of a silent, unexplained failure.
				feedbackSink.ReportError(new BuildError(project.FileName.ToString(),
					"Build failed (exit code non-zero); see build output for details."));
			}

			return success;
		}

		static string TargetToVerb(BuildTarget target)
		{
			if (target == BuildTarget.Clean)
				return "clean";
			if (target == BuildTarget.Rebuild) {
				// `dotnet build` has no single "rebuild" verb; callers that need a true rebuild
				// should issue Clean then Build. Building still produces correct output either way.
				return "build";
			}
			return "build";
		}
	}
}
