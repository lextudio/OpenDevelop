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
		readonly OpenCoverSettingsFactory settingsFactory;

		public AltCoverCoverageRunner()
			: this(new OpenCoverSettingsFactory())
		{
		}

		public AltCoverCoverageRunner(OpenCoverSettingsFactory settingsFactory)
		{
			this.settingsFactory = settingsFactory;
		}

		public async Task<CodeCoverageRunResult> RunAsync(
			IReadOnlyList<IProject> projects,
			Func<IProject, CancellationToken, Task<bool>> buildProjectAsync,
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
				ClearPreviousSavedOutput(project, log);

				var prepare = await CodeCoverageProcessRunner.RunAsync(application.GetPrepareProcessStartInfo(), cancellationToken);
				log.AddRange(prepare.OutputLines);
				if (prepare.ExitCode != 0) {
					log.Add("AltCover prepare failed for " + project.Name + " with exit code " + prepare.ExitCode);
					continue;
				}

				var testRun = await CodeCoverageProcessRunner.RunAsync(CodeCoverageProjectOutput.CreateRunStartInfo(project), cancellationToken);
				log.AddRange(testRun.OutputLines);
				if (testRun.ExitCode != 0)
					log.Add("Instrumented test run failed for " + project.Name + " with exit code " + testRun.ExitCode);

				var collect = await CodeCoverageProcessRunner.RunAsync(application.GetCollectProcessStartInfo(), cancellationToken);
				log.AddRange(collect.OutputLines);
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
			OpenCoverSettings settings = settingsFactory.CreateOpenCoverSettings(project);
			var application = new AltCoverApplication(settings, project);
			string directory = Path.GetDirectoryName(application.WorkingResultsFileName);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);
			return application;
		}

		static void ClearPreviousSavedOutput(IProject project, IList<string> log)
		{
			string targetDirectory = Path.GetDirectoryName(CodeCoverageProjectOutput.GetAssembly(project));
			string savedDirectory = Path.Combine(targetDirectory, "__Saved");
			if (!Directory.Exists(savedDirectory))
				return;

			log.Add("Removing stale AltCover saved output: " + savedDirectory);
			Directory.Delete(savedDirectory, recursive: true);
		}
	}
}
