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
			ClearCodeCoverageResults();

			var coverageResultsReader = new CodeCoverageResultsReader();

			ITestService testService = SD.GetRequiredService<ITestService>();
			IEnumerable<ITest> allTests = GetTests(testService);

			// AltCover instruments ahead-of-time (unlike OpenCover, which wraps the test process
			// at launch via the CLR profiler API), so the "prepare" step just needs to run to
			// completion before anything reads the target assemblies - it doesn't need to sit
			// between the IDE and a specific spawned test-runner process. This used to run inside
			// TestExecutionOptions.ModifyProcessStartInfoBeforeTestRun, a hook only ever invoked by
			// the old process-spawning test runners (TestProcessRunnerBase) - VsTestRunner (the
			// runner actually used for od.code-coverage.run today) executes VSTest in-process and
			// never builds or reads a ProcessStartInfo, so that hook silently never fired: no
			// instrumentation ever happened, no results ever appeared, and callers polling for
			// results burned their full timeout every time. Running Prepare unconditionally here
			// works for every runner, in-process or not.
			AltCoverApplication app = CreateAltCoverApplication(allTests);
			coverageResultsReader.AddResultsFile(app.CodeCoverageResultsFileName);
			RunToCompletion(app.GetPrepareProcessStartInfo());

			var options = new TestExecutionOptions();
			// Capture `app` in the closure rather than a shared instance field: Run() can be
			// invoked again (e.g. a second od.code-coverage.run) before this fire-and-forgotten
			// continuation gets around to running its own Collect step - a field would have already
			// been overwritten by the second call's own AltCoverApplication (a different working
			// path/GUID - see that class's remarks), so the FIRST run's own Collect step ran against
			// the SECOND run's working file, which didn't exist yet: "FileNotFoundException:
			// coverage.<second-run-guid>.xml", and the first run's real results never got merged.
			testService.RunTestsAsync(allTests, options)
				.ContinueWith(t => AfterTestsRunTask(t, app, coverageResultsReader))
				.FireAndForget();
		}

		protected virtual IEnumerable<ITest> GetTests(ITestService testService)
		{
			return TestableCondition.GetTests(testService.OpenSolution, Owner);
		}

		void ClearCodeCoverageResults()
		{
			SD.MainThread.InvokeIfRequired(() => CodeCoverageService.ClearResults());
		}

		AltCoverApplication CreateAltCoverApplication(IEnumerable<ITest> tests)
		{
			IProject project = FindProject(tests);
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
		// belonging directly to a project, so tests.First().ParentProject.Project threw a
		// NullReferenceException the moment this method actually ran (previously masked entirely:
		// this used to execute inside TestExecutionOptions.ModifyProcessStartInfoBeforeTestRun,
		// a hook VsTestRunner never invokes - see the comment in Run() above). Walk down to the
		// first node that is itself an ITestProject, or already has one as its ParentProject.
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

		Task AfterTestsRunTask(Task task, AltCoverApplication app, CodeCoverageResultsReader coverageResultsReader)
		{
			if (task.Exception != null)
				throw task.Exception;

			// Step 3 (Collect) - must run only after the test-runner process from step 2 (the
			// unwrapped process the "Run" method above let start on its own) has fully exited,
			// which is exactly the point AfterTestsRunTask is invoked at.
			RunToCompletion(app.GetCollectProcessStartInfo());
			// Collect wrote the merged report to this run's unique working path (see
			// AltCoverApplication's remarks) - promote it onto the stable, well-known path
			// coverageResultsReader (below) and SolutionCodeCoverageResults expect, now that
			// collection has genuinely finished and there's nothing left to race with.
			app.PromoteResultsToStableFileName();

			ShowCodeCoverageResultsPadIfNoCriticalTestFailures();
			DisplayCodeCoverageResults(coverageResultsReader);
			return task;
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
