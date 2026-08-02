using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.CodeCoverage
{
	public sealed class AltCoverCoverageRunner
	{
		readonly CodeCoverageSettingsFactory settingsFactory;

		public AltCoverCoverageRunner()
			: this(new CodeCoverageSettingsFactory())
		{
		}

		public AltCoverCoverageRunner(CodeCoverageSettingsFactory settingsFactory)
		{
			this.settingsFactory = settingsFactory;
		}

		public async Task<CodeCoverageRunResult> RunAsync(
			IReadOnlyList<IProject> projects,
			Func<IProject, CancellationToken, Task<bool>> buildProjectAsync,
			Func<IProject, IReadOnlyList<string>, CancellationToken, Task> publishTestResultsAsync,
			CancellationToken cancellationToken)
		{
			var log = new List<string>();
			var resultFiles = new List<string>();

			foreach (IProject project in projects) {
				cancellationToken.ThrowIfCancellationRequested();

				string projectPath = project.FileName?.ToString();
				if (string.IsNullOrEmpty(projectPath) || !File.Exists(projectPath)) {
					log.Add("Skipping project without a project file: " + project.Name);
					continue;
				}

				log.Add("Building " + project.Name);
				bool buildSucceeded = await buildProjectAsync(project, cancellationToken);
				if (!buildSucceeded) {
					log.Add("Build failed for " + project.Name);
					continue;
				}

				AltCoverApplication application = CreateAltCoverApplication(project);
				log.Add("AltCover: " + application.FileName);
				RestoreSavedOutput(project, log);

				var prepare = await CodeCoverageProcessRunner.RunAsync(application.GetPrepareProcessStartInfo(), cancellationToken);
				log.AddRange(prepare.OutputLines);
				if (prepare.ExitCode != 0) {
					log.Add("AltCover prepare failed for " + project.Name + " with exit code " + prepare.ExitCode);
					continue;
				}

				// MTP's server mode currently produces zero AltCover visits, so keep the
				// proven one-shot process and request detailed, ANSI-free result lines. These
				// are published into the ITest model without executing the tests a second time.
				var testStartInfo = CodeCoverageProjectOutput.CreateRunStartInfo(project);
				testStartInfo.ArgumentList.Add("--no-ansi");
				testStartInfo.ArgumentList.Add("--no-progress");
				testStartInfo.ArgumentList.Add("--output");
				testStartInfo.ArgumentList.Add("Detailed");
				var testRun = await CodeCoverageProcessRunner.RunAsync(testStartInfo, cancellationToken);
				log.AddRange(testRun.OutputLines);
				await publishTestResultsAsync(project, testRun.OutputLines, cancellationToken);
				if (testRun.ExitCode != 0)
					log.Add("Instrumented test run completed with exit code " + testRun.ExitCode + " for " + project.Name);

				var collect = await CodeCoverageProcessRunner.RunAsync(application.GetCollectProcessStartInfo(), cancellationToken);
				log.AddRange(collect.OutputLines);
				RestoreSavedOutput(project, log);
				if (collect.ExitCode != 0) {
					log.Add("AltCover collect failed for " + project.Name + " with exit code " + collect.ExitCode);
					continue;
				}

				application.PromoteResultsToStableFileName();
				if (File.Exists(application.CodeCoverageResultsFileName))
					resultFiles.Add(application.CodeCoverageResultsFileName);
				else
					log.Add("Coverage report was not created: " + application.CodeCoverageResultsFileName);
			}

			return new CodeCoverageRunResult(resultFiles, log);
		}

		AltCoverApplication CreateAltCoverApplication(IProject project)
		{
			CodeCoverageSettings settings = settingsFactory.CreateCodeCoverageSettings(project);
			var application = new AltCoverApplication(settings, project);
			string directory = Path.GetDirectoryName(application.WorkingResultsFileName);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);
			return application;
		}

		static void RestoreSavedOutput(IProject project, IList<string> log)
		{
			string targetDirectory = Path.GetDirectoryName(CodeCoverageProjectOutput.GetAssembly(project));
			string savedDirectory = Path.Combine(targetDirectory, "__Saved");
			if (!Directory.Exists(savedDirectory))
				return;

			log.Add("Restoring AltCover saved output: " + savedDirectory);
			foreach (string savedFile in Directory.GetFiles(savedDirectory, "*", SearchOption.AllDirectories)) {
				string relativePath = savedFile.Substring(savedDirectory.Length).TrimStart(Path.DirectorySeparatorChar);
				string destination = Path.Combine(targetDirectory, relativePath);
				string destinationDirectory = Path.GetDirectoryName(destination);
				if (!string.IsNullOrEmpty(destinationDirectory))
					Directory.CreateDirectory(destinationDirectory);
				File.Copy(savedFile, destination, overwrite: true);
			}
			Directory.Delete(savedDirectory, recursive: true);
		}
	}
}
