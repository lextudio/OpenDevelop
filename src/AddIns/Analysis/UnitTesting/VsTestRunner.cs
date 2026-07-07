using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace ICSharpCode.UnitTesting
{
	public class VsTestRunner : ITestRunner
	{
		readonly VsTestProject testProject;
		TestResultBuilder resultBuilder;

		public event EventHandler<TestFinishedEventArgs> TestFinished;

		public VsTestRunner(VsTestProject project, TestExecutionOptions options)
		{
			this.testProject = project;
		}

		public async Task RunAsync(
			IEnumerable<ITest> selectedTests,
			IProgress<double> progress,
			TextWriter output,
			CancellationToken cancellationToken)
		{
			var testCases = testProject.GetTestCasesForSelectedTests(selectedTests);
			if (testCases.Count == 0) {
				output.WriteLine("No tests to run.");
				return;
			}

			var singleTest = selectedTests.Count() == 1;
			resultBuilder = new TestResultBuilder(singleTest);

			var tcs = new TaskCompletionSource<object>();

			await VsTestRunAdapter.Instance.RunTestsAsync(
				testProject.Project,
				testCases,
				result => {
					var converted = resultBuilder.Convert(result);
					OnTestFinished(new TestFinishedEventArgs(converted));
				},
				() => {
					tcs.TrySetResult(null);
				},
				cancellationToken);

			await tcs.Task;
		}

		public void Dispose()
		{
		}

		void OnTestFinished(TestFinishedEventArgs e)
		{
			TestFinished?.Invoke(this, e);
		}
	}
}
