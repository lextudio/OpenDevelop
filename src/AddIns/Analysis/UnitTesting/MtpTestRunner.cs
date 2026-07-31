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
			var testMethods = testProject.GetTestMethodsForSelectedTests(selectedTests);
			if (testMethods.Count == 0) {
				output.WriteLine("No tests to run.");
				return;
			}

			foreach (var group in testMethods.GroupBy(method => method.TargetFramework, StringComparer.OrdinalIgnoreCase)) {
				await RunTargetFrameworkAsync(group.Key, group.ToList(), output, cancellationToken);
			}
		}

		async Task RunTargetFrameworkAsync(string targetFramework, IReadOnlyList<MtpTestMethod> testMethods, TextWriter output, CancellationToken cancellationToken)
		{
			var testNodes = testMethods.Select(method => method.Node).ToList();
			var assemblyPath = MtpTestProject.ResolveAssemblyDll(testProject.Project, targetFramework);
			if (assemblyPath == null || !File.Exists(assemblyPath)) {
				output.WriteLine("Test assembly not found: " + assemblyPath);
				return;
			}
			output.WriteLine("Target framework: " + targetFramework);

			// A fresh MtpServerProcess per run (rather than a long-lived, IDE-session singleton
			// like the old VsTestRunAdapter.Instance) - this test host process is started, run to
			// completion, and torn down within this one RunAsync call, matching how a one-shot
			// `dotnet exec`/`dotnet run` invocation behaves. See doc/technotes/altcover.md for why
			// a persistent host was the leading suspect behind the AltCover zero-visits bug.
			IReadOnlyList<MtpTestNode> results;
			var liveReportedNames = new HashSet<string>(StringComparer.Ordinal);
			try {
				await using var server = await MtpServerProcess.StartAsync(assemblyPath, Path.GetDirectoryName(assemblyPath), cancellationToken);
				await server.InitializeAsync(cancellationToken);

				// A test still showing its Roslyn-approximate (pre-MTP-confirmation) node has an empty
				// Uid (see MtpTestProject.BuildApproxNode) - it can never appear in a real discovered
				// set, so filtering by it would silently run nothing instead of the test the user
				// actually asked for. Fall back to running everything in this target framework instead
				// of skipping it: a safe over-approximation, matching Simple.TestService's same
				// fallback for its own unconfirmed entries.
				var hasUnconfirmedSelection = testNodes.Any(n => string.IsNullOrEmpty(n.Uid));
				var allTestsSelected = hasUnconfirmedSelection
					|| testNodes.Count == CountAllMethodsForTargetFramework(testProject.NestedTests, targetFramework);
				HashSet<string> selectedUids = allTestsSelected
					? null
					: new HashSet<string>(testNodes.Select(n => n.Uid), StringComparer.Ordinal);
				server.TestNodeUpdated += node => {
					if (!IsFinalTestResultNode(node))
						return;
					if (selectedUids != null && !selectedUids.Contains(node.Uid))
						return;
					if (!liveReportedNames.Add(node.DisplayName))
						return;
					ReportTestNodeResult(targetFramework, node, output);
				};
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
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception ex) {
				output.WriteLine(ex.Message);
				foreach (var method in testMethods) {
					OnTestFinished(new TestFinishedEventArgs(new MtpTestResult(targetFramework + "\0" + method.DisplayName) {
						Message = ex.Message,
						ResultType = TestResultType.Failure
					}));
				}
				return;
			}

			var reportedNames = new HashSet<string>(liveReportedNames, StringComparer.Ordinal);
			foreach (var node in results.Where(n => n.NodeType == "action")) {
				if (liveReportedNames.Contains(node.DisplayName))
					continue;
				reportedNames.Add(node.DisplayName);
				ReportTestNodeResult(targetFramework, node, output);
			}

			foreach (var method in testMethods.Where(method => !reportedNames.Contains(method.DisplayName))) {
				const string message = "The MTP test host did not report a result for this selected test.";
				output.WriteLine("{0} {1}", method.DisplayName, TestResultType.Failure);
				output.WriteLine(message);
				OnTestFinished(new TestFinishedEventArgs(new MtpTestResult(targetFramework + "\0" + method.DisplayName) {
					Message = message,
					ResultType = TestResultType.Failure
				}));
			}
		}

		static int CountAllMethodsForTargetFramework(IEnumerable<ITest> tests, string targetFramework)
		{
			return tests.SelectMany(test => EnumerateMethods(new[] { test }))
				.Count(method => string.Equals(method.TargetFramework, targetFramework, StringComparison.OrdinalIgnoreCase));
		}

		static IEnumerable<MtpTestMethod> EnumerateMethods(IEnumerable<ITest> tests)
		{
			foreach (var test in tests) {
				if (test is MtpTestMethod method)
					yield return method;
				else if (test.NestedTests != null)
					foreach (var nested in EnumerateMethods(test.NestedTests))
						yield return nested;
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
		
		static bool IsFinalTestResultNode(MtpTestNode node)
		{
			if (node.NodeType != "action")
				return false;
			switch (node.ExecutionState) {
				case "passed":
				case "failed":
				case "timed-out":
				case "error":
				case "canceled":
				case "skipped":
					return true;
				default:
					return false;
			}
		}
		
		void ReportTestNodeResult(string targetFramework, MtpTestNode node, TextWriter output)
		{
			var converted = new MtpTestResult(targetFramework + "\0" + node.DisplayName) {
				Message = node.ErrorMessage,
				ResultType = ToResultType(node.ExecutionState)
			};
			
			// Echo each result to the run's output writer (the UnitTesting output pad) - without
			// this the pad stayed completely empty after a run, with no textual record of what
			// ran or how it went.
			output.WriteLine("{0} {1}", node.DisplayName, converted.ResultType);
			if (!string.IsNullOrEmpty(converted.Message))
				output.WriteLine(converted.Message);
			
			OnTestFinished(new TestFinishedEventArgs(converted));
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
