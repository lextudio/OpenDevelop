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
using System.Linq;
using System.Threading;
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
		AltCoverCoverageRunner coverageRunner = new AltCoverCoverageRunner();
		public static Task CurrentRunTask { get; private set; } = Task.CompletedTask;

		public override void Run()
		{
			RunTestsWithCoverageAsync(GetTests(SD.GetRequiredService<ITestService>()).ToList());
		}

		/// <summary>
		/// Runs the given tests with code coverage. Menu commands, DevFlow and the OpenLens test
		/// lens's "Run with Coverage" menu item all reach this entry point. Do not allow two
		/// AltCover --inplace prepare/collect sequences to rewrite the same output directory
		/// concurrently.
		/// </summary>
		public static Task RunTestsWithCoverageAsync(IReadOnlyList<ITest> tests)
		{
			if (!CurrentRunTask.IsCompleted)
				return CurrentRunTask;
			var task = new RunTestWithCodeCoverageCommand().RunAsync(tests);
			CurrentRunTask = task;
			task.FireAndForget();
			return task;
		}

		async Task RunAsync(IReadOnlyList<ITest> tests)
		{
			ITestService testService = SD.GetRequiredService<ITestService>();
			ITestOperation operation;
			if (!testService.TryBeginOperation(TestOperationKind.Coverage, out operation))
				return;

			using (operation) {
				await RunWithLeaseAsync(testService, tests, operation.CancellationToken);
			}
		}

		async Task RunWithLeaseAsync(ITestService testService, IReadOnlyList<ITest> tests, CancellationToken cancellationToken)
		{
			ClearCodeCoverageResults();

			var coverageResultsReader = new CodeCoverageResultsReader();

			IProject project = FindProject(tests);
			if (project == null)
				return;

			var mtpTestProject = FindMtpTestProject(tests, project);
			if (mtpTestProject != null)
				await mtpTestProject.RefreshAsync(cancellationToken);

			CodeCoverageRunResult run;
			using (mtpTestProject?.SuppressBuildDiscovery()) {
				run = await coverageRunner.RunAsync(
					new[] { project },
					BuildProjectAsync,
					(_, outputLines, cancellationToken) => PublishTestResultsAsync(mtpTestProject, tests, outputLines, cancellationToken),
					cancellationToken);
			}
			foreach (string line in run.LogLines)
				SD.Log.Info("Code coverage: " + line);
			foreach (string fileName in run.ResultFiles)
			{
				coverageResultsReader.AddResultsFile(fileName);
				VSMacCoverageRepositoryAdapter.Save(project, fileName);
			}

			// Creating the pad must happen before DisplayCodeCoverageResults: ShowResults
			// only populates an existing CodeCoveragePad instance. The old condition was
			// inverted and brought the pad forward only when critical errors existed, so a
			// successful run kept its valid results entirely invisible.
			if (run.ResultFiles.Any())
				SD.MainThread.InvokeIfRequired(ShowCodeCoverageResultsPad);
			// Running with coverage is an explicit request to display coverage. Enable the
			// editor overlay before ShowResults so its refresh paints already-open editors;
			// ViewOpened then paints files reached by double-clicking a coverage node.
			if (run.ResultFiles.Any())
				CodeCoverageService.CodeCoverageHighlighted = true;
			DisplayCodeCoverageResults(coverageResultsReader);
		}

		static async Task PublishTestResultsAsync(
			MtpTestProject testProject,
			IReadOnlyList<ITest> tests,
			IReadOnlyList<string> outputLines,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (testProject == null)
				return;

			await SD.MainThread.InvokeAsync(() => {
				foreach (ITest test in tests)
					test.ResetTestResults();
				foreach (string line in outputLines) {
					if (!TryParseTestResultLine(line, out string name, out TestResultType resultType))
						continue;
					var result = new TestResult(name) {
						ResultType = resultType
					};
					testProject.UpdateTestResult(result);
				}
			});
		}

		static bool TryParseTestResultLine(string line, out string name, out TestResultType resultType)
		{
			name = null;
			resultType = TestResultType.None;
			if (line.StartsWith("passed ", StringComparison.Ordinal))
				resultType = TestResultType.Success;
			else if (line.StartsWith("failed ", StringComparison.Ordinal))
				resultType = TestResultType.Failure;
			else if (line.StartsWith("skipped ", StringComparison.Ordinal))
				resultType = TestResultType.Ignored;
			else
				return false;

			int nameStart = line.IndexOf(' ') + 1;
			int durationStart = line.LastIndexOf(" (", StringComparison.Ordinal);
			if (durationStart <= nameStart)
				return false;
			name = line.Substring(nameStart, durationStart - nameStart);
			return true;
		}

		static async Task<bool> BuildProjectAsync(IProject project, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var buildResults = await SD.BuildService.BuildAsync(project, new BuildOptions(BuildTarget.Build));
			cancellationToken.ThrowIfCancellationRequested();
			return buildResults.Result == BuildResultCode.Success;
		}

		protected virtual IEnumerable<ITest> GetTests(ITestService testService)
		{
			return TestableCondition.GetTests(testService.OpenSolution, Owner);
		}

		void ClearCodeCoverageResults()
		{
			SD.MainThread.InvokeIfRequired(() => CodeCoverageService.ClearResults());
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

		static MtpTestProject FindMtpTestProject(IEnumerable<ITest> tests, IProject project)
		{
			foreach (ITest test in tests) {
				if (test is MtpTestProject mtpTestProject && mtpTestProject.Project == project)
					return mtpTestProject;
				var found = FindMtpTestProject(test.NestedTests, project);
				if (found != null)
					return found;
			}
			return null;
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
