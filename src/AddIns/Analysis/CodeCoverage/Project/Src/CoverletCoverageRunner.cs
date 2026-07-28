using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.CodeCoverage
{
	public sealed class CoverletCoverageRunner
	{
		public async Task<CodeCoverageRunResult> RunAsync(
			IReadOnlyList<IProject> projects,
			Func<IProject, CancellationToken, Task<bool>> buildProjectAsync,
			CancellationToken cancellationToken)
		{
			var log = new List<string>();
			var resultFiles = new List<string>();
			string coverletDll = GetCoverletDll();
			log.Add("Coverlet: " + coverletDll);

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

				string outputAssembly = CodeCoverageProjectOutput.GetAssembly(project);
				string outputDirectory = Path.GetDirectoryName(outputAssembly);
				string coverageRoot = Path.Combine(Path.GetTempPath(), "OpenDevelopCoverage", Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(coverageRoot);
				// Coverlet appends the format extension itself, so pass the report path without it.
				string reportBase = Path.Combine(coverageRoot, project.Name);
				string reportFile = reportBase + ".opencover.xml";

				log.Add("Running MTP test assembly with Coverlet: " + outputAssembly);
				var run = await RunProcessAsync(
					CodeCoverageDotNetHost.Resolve(),
					new[] {
						coverletDll,
						outputDirectory,
						"--target", CodeCoverageDotNetHost.Resolve(),
						"--targetargs", "exec \"" + outputAssembly + "\"",
						"--output", reportBase,
						"--format", "opencover",
						"--exclude-by-attribute", "Xunit.FactAttribute",
						"--exclude-by-attribute", "Xunit.TheoryAttribute",
						"--exclude-by-attribute", "NUnit.Framework.TestAttribute",
						"--exclude-by-attribute", "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute",
						"--exclude-by-attribute", "TUnit.Core.TestAttribute",
						"--exclude", "[xunit.*]*",
						"--exclude", "[nunit.*]*",
						"--exclude", "[Microsoft.TestPlatform.*]*",
						"--exclude", "[Microsoft.VisualStudio.TestPlatform.*]*",
						"--exclude", "[Microsoft.Testing.*]*",
						"--exclude", "[testhost*]*"
					},
					outputDirectory,
					cancellationToken);
				log.AddRange(run.OutputLines);
				if (run.ExitCode != 0)
					log.Add("MTP coverage run failed for " + project.Name + " with exit code " + run.ExitCode);

				if (File.Exists(reportFile))
					resultFiles.Add(reportFile);
				else
					log.Add("Coverage report was not created: " + reportFile);
			}

			return new CodeCoverageRunResult(resultFiles, log);
		}

		static string GetCoverletDll()
		{
			string baseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
			string toolsRoot = Path.Combine(baseDirectory, "bin", "Tools", "Coverlet");
			string fileName = FindCoverletDll(toolsRoot);
			if (fileName == null)
				fileName = FindCoverletDll(Path.Combine(baseDirectory, "Coverlet"));

			if (fileName != null)
				return fileName;

			throw new FileNotFoundException("coverlet.console.dll was not found. Ensure the CodeCoverage addin copied the coverlet.console tool files.", Path.Combine(toolsRoot, "coverlet.console.dll"));
		}

		static string FindCoverletDll(string root)
		{
			return Directory.Exists(root)
				? Directory.EnumerateFiles(root, "coverlet.console.dll", SearchOption.AllDirectories).FirstOrDefault()
				: null;
		}

		static async Task<CodeCoverageProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
		{
			return await CodeCoverageProcessRunner.RunAsync(
				CodeCoverageProcessRunner.CreateStartInfo(fileName, arguments, workingDirectory),
				cancellationToken);
		}
	}
}
