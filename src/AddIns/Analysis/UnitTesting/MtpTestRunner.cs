using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.UnitTesting.Mtp;

namespace ICSharpCode.UnitTesting
{
	public class MtpTestRunner : ITestRunner
	{
		readonly MtpTestProject testProject;

		public event EventHandler<TestFinishedEventArgs> TestFinished;

		public MtpTestRunner(MtpTestProject project, TestExecutionOptions options)
		{
			this.testProject = project;
		}

		public async Task RunAsync(
			IEnumerable<ITest> selectedTests,
			IProgress<double> progress,
			TextWriter output,
			CancellationToken cancellationToken)
		{
			var testNodes = testProject.GetTestNodesForSelectedTests(selectedTests);
			if (testNodes.Count == 0) {
				output.WriteLine("No tests to run.");
				return;
			}

			var assemblyPath = MtpTestProject.ResolveAssemblyDll(testProject.Project);
			if (assemblyPath == null || !File.Exists(assemblyPath)) {
				output.WriteLine("Test assembly not found: " + assemblyPath);
				return;
			}

			// A fresh MtpServerProcess per run (rather than a long-lived, IDE-session singleton
			// like the old VsTestRunAdapter.Instance) - this test host process is started, run to
			// completion, and torn down within this one RunAsync call, matching how a one-shot
			// `dotnet exec`/`dotnet run` invocation behaves. See doc/technotes/altcover.md for why
			// a persistent host was the leading suspect behind the AltCover zero-visits bug.
			await using var server = await MtpServerProcess.StartAsync(assemblyPath, Path.GetDirectoryName(assemblyPath), cancellationToken);
			await server.InitializeAsync(cancellationToken);

			IReadOnlyList<MtpTestNode> results;
			var allTestsSelected = testNodes.Count == CountAllMethods(testProject.NestedTests);
			if (allTestsSelected) {
				results = await server.RunTestsAsync(cancellationToken);
			} else {
				// Re-discover on this same live host instance right before running so the filter
				// nodes are guaranteed consistent with it, rather than reusing possibly-stale nodes
				// from an earlier discovery call/process (mirrors DotNetTestRunner.RunTestsAsync).
				var discovered = await server.DiscoverTestsAsync(cancellationToken);
				var uidSet = new HashSet<string>(testNodes.Select(n => n.Uid), StringComparer.Ordinal);
				var filter = discovered.Where(n => uidSet.Contains(n.Uid)).ToList();
				results = filter.Count > 0
					? await server.RunTestsAsync(filter, cancellationToken)
					: Array.Empty<MtpTestNode>();
			}

			foreach (var node in results.Where(n => n.NodeType == "action")) {
				var converted = new TestResult(node.DisplayName) {
					Message = node.ErrorMessage,
					ResultType = ToResultType(node.ExecutionState)
				};

				// Echo each result to the run's output writer (the UnitTesting output pad) - without
				// this the pad stayed completely empty after a run, with no textual record of what
				// ran or how it went.
				output.WriteLine("{0} {1}", converted.Name, converted.ResultType);
				if (!string.IsNullOrEmpty(converted.Message))
					output.WriteLine(converted.Message);

				OnTestFinished(new TestFinishedEventArgs(converted));
			}
		}

		static int CountAllMethods(IEnumerable<ITest> tests)
		{
			int count = 0;
			foreach (var test in tests) {
				if (test is MtpTestMethod)
					count++;
				else if (test.NestedTests != null)
					count += CountAllMethods(test.NestedTests);
			}
			return count;
		}

		static TestResultType ToResultType(string executionState)
		{
			switch (executionState) {
				case "passed":
					return TestResultType.Success;
				case "failed":
				case "timed-out":
				case "error":
				case "canceled":
					return TestResultType.Failure;
				case "skipped":
					return TestResultType.Ignored;
				default:
					return TestResultType.None;
			}
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
