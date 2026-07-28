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

			CodeCoverageRunResult run = await coverageRunner.RunAsync(
				new[] { project },
				BuildProjectAsync,
				CancellationToken.None);
			foreach (string fileName in run.ResultFiles)
				coverageResultsReader.AddResultsFile(fileName);

			ShowCodeCoverageResultsPadIfNoCriticalTestFailures();
			DisplayCodeCoverageResults(coverageResultsReader);
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
