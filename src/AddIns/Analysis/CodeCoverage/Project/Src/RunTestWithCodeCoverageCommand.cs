// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.UnitTesting;

namespace ICSharpCode.CodeCoverage
{
	/// <summary>
	/// Menu command selected after right clicking a test method in the text editor
	/// to run tests with code coverage.
	/// </summary>
	public class RunTestWithCodeCoverageCommand : AbstractMenuCommand
	{
		OpenCoverSettingsFactory settingsFactory = new OpenCoverSettingsFactory();
		IFileSystem fileSystem = SD.FileSystem;

		public override void Run()
		{
			RunAsync().FireAndForget();
		}

		async Task RunAsync()
		{
			ClearCodeCoverageResults();

			var coverageResultsReader = new CodeCoverageResultsReader();

			ITestService testService = SD.GetRequiredService<ITestService>();
			IEnumerable<ITest> allTests = GetTests(testService);

			IProject project = FindProject(allTests);
			if (project == null)
				return;

			var buildResults = await SD.BuildService.BuildAsync(project, new BuildOptions(BuildTarget.Build));
			if (buildResults.Result != BuildResultCode.Success)
				return;

			// AltCover instruments ahead-of-time (unlike OpenCover, which wraps the test process
			// at launch via the CLR profiler API), so the "prepare" step just needs to run to
			// completion before anything reads the target assemblies.
			AltCoverApplication app = CreateAltCoverApplication(project);
			coverageResultsReader.AddResultsFile(app.CodeCoverageResultsFileName);
			RunToCompletion(app.GetPrepareProcessStartInfo());

			// Run the (now-instrumented) test project directly as a plain one-shot process -
			// deliberately NOT through ITestService/MtpTestRunner's server-mode JSON-RPC session.
			// AltCover's recorder flushes recorded visits to disk on the instrumented process's own
			// exit (see externals/altcover/AltCover.Recorder/Recorder.fs, FlushFinish/ProcessExit),
			// so the test process needs to be a single process that runs to completion and exits
			// normally - exactly what a bare `dotnet exec`/apphost invocation is, and exactly what
			// the JSON-RPC server-mode session (a longer-lived host talked to over a persistent
			// connection, matching how VsTestRunAdapter's singleton vstest.console process behaved
			// before this addin moved off VSTest) is not. This mirrors both
			// tests/OpenDevelop.IntegrationTests/AltCover.Mtp.targets's own "Coverage" MSBuild target
			// (build, instrument in place, `<Exec>` the built exe directly, collect) and UnoDevelop's
			// own CoverletCoverageRunner, which bypasses its own MTP JSON-RPC client the same way for
			// exactly this reason. See doc/technotes/altcover.md.
			await RunInstrumentedProcessToCompletionAsync(project);

			// Step 3 (Collect) - must run only after the test process above has fully exited.
			RunToCompletion(app.GetCollectProcessStartInfo());
			app.PromoteResultsToStableFileName();

			ShowCodeCoverageResultsPadIfNoCriticalTestFailures();
			DisplayCodeCoverageResults(coverageResultsReader);
		}

		static async Task RunInstrumentedProcessToCompletionAsync(IProject project)
		{
			var assembly = project.OutputAssemblyFullPath;
			var psi = new ProcessStartInfo { UseShellExecute = false };

			if (assembly != null && File.Exists(assembly)) {
				// MTP test projects build to a self-contained apphost exe - run it directly.
				psi.FileName = assembly;
				psi.WorkingDirectory = Path.GetDirectoryName(assembly);
			} else {
				// No apphost for this TFM/platform - fall back to running the managed dll via the
				// dotnet host (same "<AssemblyName>.dll next to the exe" resolution MtpTestProject
				// uses for discovery/execution).
				var dir = Path.GetDirectoryName(assembly);
				psi.FileName = "dotnet";
				psi.Arguments = "exec \"" + Path.Combine(dir ?? string.Empty, project.AssemblyName + ".dll") + "\"";
				psi.WorkingDirectory = dir;
			}

			using (Process process = Process.Start(psi)) {
				await process.WaitForExitAsync();
			}
		}

		protected virtual IEnumerable<ITest> GetTests(ITestService testService)
		{
			return TestableCondition.GetTests(testService.OpenSolution, Owner);
		}

		void ClearCodeCoverageResults()
		{
			SD.MainThread.InvokeIfRequired(() => CodeCoverageService.ClearResults());
		}

		AltCoverApplication CreateAltCoverApplication(IProject project)
		{
			OpenCoverSettings settings = settingsFactory.CreateOpenCoverSettings(project);
			var application = new AltCoverApplication(settings, project);
			// Each AltCoverApplication instance writes to its own unique working path (see that
			// class's remarks) rather than the shared stable CodeCoverageResultsFileName, so there
			// is no old file at that unique path to remove - just make sure the directory exists.
			CreateDirectoryForCodeCoverageResultsFile(application.WorkingResultsFileName);
			return application;
		}

		// RunAllTestsWithCodeCoverageCommand.GetTests() passes the *solution* root node (whose
		// ParentProject is always null - see TestSolution.ParentProject) rather than a test
		// belonging directly to a project, so tests.First().ParentProject.Project would throw a
		// NullReferenceException. Walk down to the first node that is itself an ITestProject, or
		// already has one as its ParentProject.
		static IProject FindProject(IEnumerable<ITest> tests)
		{
			foreach (ITest test in tests) {
				if (test is ITestProject testProject)
					return testProject.Project;
				if (test.ParentProject != null)
					return test.ParentProject.Project;
				IProject found = FindProject(test.NestedTests);
				if (found != null)
					return found;
			}
			return null;
		}


		void CreateDirectoryForCodeCoverageResultsFile(string fileName)
		{
			string directory = Path.GetDirectoryName(fileName);
			fileSystem.CreateDirectory(DirectoryName.Create(directory));
		}

		static void RunToCompletion(ProcessStartInfo processStartInfo)
		{
			processStartInfo.UseShellExecute = false;
			using (Process process = Process.Start(processStartInfo)) {
				process.WaitForExit();
			}
		}

		void ShowCodeCoverageResultsPadIfNoCriticalTestFailures()
		{
			if (TaskService.HasCriticalErrors(false)) {
				SD.MainThread.InvokeIfRequired(() => ShowCodeCoverageResultsPad());
			}
		}
		
		void ShowCodeCoverageResultsPad()
		{
			SD.Workbench.GetPad(typeof(CodeCoveragePad)).BringPadToFront();
		}
		
		void DisplayCodeCoverageResults(CodeCoverageResultsReader coverageResultsReader)
		{
			foreach (CodeCoverageResults result in GetResults(coverageResultsReader)) {
				DisplayCodeCoverageResults(result);
			}
			foreach (string missingFile in coverageResultsReader.GetMissingResultsFiles()) {
				DisplayNoCodeCoverageResultsGeneratedMessage(missingFile);
			}
		}

		IEnumerable<CodeCoverageResults> GetResults(CodeCoverageResultsReader coverageResultsReader)
		{
			return SD.MainThread.InvokeIfRequired(() => coverageResultsReader.GetResults().ToList());
		}
		
		void DisplayCodeCoverageResults(CodeCoverageResults results)
		{
			SD.MainThread.InvokeIfRequired(() => CodeCoverageService.ShowResults(results));
		}
		
		void DisplayNoCodeCoverageResultsGeneratedMessage(string fileName)
		{
			SDTask task = CreateNoCodeCoverageResultsGeneratedTask(fileName);
			TaskService.Add(task);
		}
		
		SDTask CreateNoCodeCoverageResultsGeneratedTask(string fileName)
		{
			string description = GetNoCodeCoverageResultsGeneratedTaskDescription(fileName);
			return new SDTask(null, description, 1, 1, TaskType.Error);
		}
		
		string GetNoCodeCoverageResultsGeneratedTaskDescription(string fileName)
		{
			string message = StringParser.Parse("${res:ICSharpCode.CodeCoverage.NoCodeCoverageResultsGenerated}");
			return String.Format("{0} {1}", message, fileName);
		}
	}
}
