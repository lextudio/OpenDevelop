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
	sealed class MinimalMSBuildEngine : IMSBuildEngine
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
		static readonly Regex DiagnosticLine = new Regex(
			@"^(?<file>.*?)\((?<line>\d+),(?<column>\d+)\):\s*(?<severity>error|warning)\s+(?<code>[A-Za-z0-9]+)\s*:\s*(?<text>.*?)(\s*\[.*\])?$",
			RegexOptions.Compiled);

		// Which dotnet host/SDK to run builds with is no longer hardcoded here - it's resolved
		// fresh on every build from DotNetSdkService (Options > .NET SDK), the single place this
		// choice is made across build/debug/test. Not necessarily the same SDK hosting this
		// process itself (see the type-level comment) - just needs its own consistent SDK/MSBuild
		// toolset, which any selected installed SDK provides.

		public IList<ReferenceProjectItem> ResolveAssemblyReferences(
			MSBuildBasedProject baseProject,
			ReferenceProjectItem[] additionalReferences = null, bool resolveOnlyAdditionalReferences = false,
			bool logErrorsToOutputPad = true)
		{
			var results = new List<ReferenceProjectItem>();
			if (additionalReferences != null)
				results.AddRange(additionalReferences);
			return results;
		}

		public async Task<bool> BuildAsync(IProject project, ProjectBuildOptions options, IBuildFeedbackSink feedbackSink, CancellationToken cancellationToken, IEnumerable<string> additionalTargetFiles = null)
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
				"MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET", "MSBUILD_NUGET_PATH", "MSBUILD_EXE_PATH"
			}) {
				psi.EnvironmentVariables.Remove(name);
			}
			foreach (var kv in DotNetSdkService.GetEnvironmentVariablesFor(sdk))
				psi.EnvironmentVariables[kv.Key] = kv.Value;
			// This app's own process may be running under a non-invariant/non-English OS locale
			// (LANG/LC_ALL inherited from the desktop session), and some MSBuild tasks parse
			// culture-sensitive content internally; forcing invariant globalization for the build
			// child avoids locale-dependent parsing surprises regardless of the app's own locale.
			psi.EnvironmentVariables["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
			psi.EnvironmentVariables["LANG"] = "en_US.UTF-8";
			psi.EnvironmentVariables["LC_ALL"] = "en_US.UTF-8";
			psi.ArgumentList.Add(TargetToVerb(options.Target));
			psi.ArgumentList.Add(project.FileName.ToString());
			psi.ArgumentList.Add("--nologo");
			// We always build a single .csproj directly (never a .sln), so the CurrentVersion.targets
			// logic that synthesizes solution-dependency ProjectReferences and resolves their
			// per-solution-configuration via AssignProjectConfiguration is pure overhead we don't
			// need - and passing BuildingInsideVisualStudio=true (as any IDE driving single-project
			// builds does) skips it, matching how this project is actually being built here.
			psi.ArgumentList.Add("-p:BuildingInsideVisualStudio=true");
			if (!string.IsNullOrEmpty(options.Configuration)) {
				psi.ArgumentList.Add("-c");
				psi.ArgumentList.Add(options.Configuration);
			}
			if (!string.IsNullOrEmpty(options.Platform) && options.Platform != "AnyCPU") {
				psi.ArgumentList.Add("-p:Platform=" + options.Platform);
			}
			if (options.Properties != null) {
				foreach (var kv in options.Properties) {
					// MSBuildBasedProject.CreateProjectBuildOptions sets this to an XML blob
					// describing the whole solution's project/configuration mapping, so that
					// ProjectReferences resolve their configuration correctly when building a
					// .sln directly. We always build one .csproj at a time here, so it's both
					// unneeded and unsafe to forward: arbitrary XML can't be round-tripped through
					// a single `-p:Name=Value` CLI token (no escaping for '{', ';', quotes, etc.),
					// which is exactly what caused MSB3108 "unexpected token '{'" failures here.
					if (kv.Key == "CurrentSolutionConfigurationContents")
						continue;
					psi.ArgumentList.Add($"-p:{kv.Key}={kv.Value}");
				}
			}

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

					process.Start();
					process.BeginOutputReadLine();
					process.BeginErrorReadLine();

					await process.WaitForExitAsync(cancellationToken);
					success = process.ExitCode == 0;
				}
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
