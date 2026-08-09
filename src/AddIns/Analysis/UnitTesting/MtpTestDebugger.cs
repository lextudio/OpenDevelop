using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Debugging;
using ICSharpCode.UnitTesting.Mtp;

namespace ICSharpCode.UnitTesting
{
	public class MtpTestDebugger : TestDebuggerBase
	{
		readonly MtpTestProject testProject;
		IReadOnlyList<MtpTestMethod> debuggedMethods = new List<MtpTestMethod>();
		StringBuilder debugOutput = new StringBuilder();

		public MtpTestDebugger(MtpTestProject testProject, TestExecutionOptions options)
		{
			this.testProject = testProject;
		}

		public override int GetExpectedNumberOfTestResults(IEnumerable<ITest> selectedTests)
		{
			return testProject.GetTestNodesForSelectedTests(selectedTests).Count;
		}

		public override async Task RunAsync(
			IEnumerable<ITest> selectedTests,
			IProgress<double> progress,
			TextWriter output,
			CancellationToken cancellationToken)
		{
			var selectedSnapshot = selectedTests.ToList();
			var requested = testProject.GetTestMethodsForSelectedTests(selectedSnapshot);
			var confirmed = await testProject.ConfirmTestMethodsAsync(selectedSnapshot, cancellationToken);
			if (confirmed.Count != requested.Count) {
				var names = string.Join(", ", requested.Select(method => method.FullyQualifiedName));
				var message = "The selected test was found in source but is not present in the built test assembly"
					+ (string.IsNullOrEmpty(names) ? "." : ":\n" + names)
					+ "\n\nCheck that the source file is included as a Compile item and rebuild the project.";
				output.WriteLine(message);
				await SD.MainThread.InvokeAsync(() => SD.MessageService.ShowError(message));
				return;
			}

			await base.RunAsync(confirmed, progress, output, cancellationToken);
		}

		protected override ProcessStartInfo GetProcessStartInfo(IEnumerable<ITest> selectedTests)
		{
			var methods = testProject.GetTestMethodsForSelectedTests(selectedTests);
			var targetFramework = methods.Select(method => method.TargetFramework).Distinct(StringComparer.OrdinalIgnoreCase).Single();
			var fullyQualifiedNames = methods.Select(method => method.FullyQualifiedName).ToList();
			var assembly = MtpTestProject.ResolveAssemblyDll(testProject.Project, targetFramework);

			if (assembly == null || !File.Exists(assembly)) {
				throw new InvalidOperationException("Test assembly not found for target framework '" + targetFramework + "': " + assembly);
			}

			var startInfo = new ProcessStartInfo {
				FileName = assembly,
				WorkingDirectory = Path.GetDirectoryName(assembly) ?? testProject.Project.Directory ?? Environment.CurrentDirectory
			};
			foreach (var name in fullyQualifiedNames) {
				startInfo.ArgumentList.Add("--filter-method");
				startInfo.ArgumentList.Add(name);
			}

			return startInfo;
		}

		protected override void OnBeforeDebugStart(IEnumerable<ITest> selectedTests)
		{
			debuggedMethods = testProject.GetTestMethodsForSelectedTests(selectedTests);
			debugOutput = new StringBuilder();
			BaseDebuggerService.DebugMessagePrinted += OnDebugMessagePrinted;
		}

		protected override void OnDebugStopped()
		{
			BaseDebuggerService.DebugMessagePrinted -= OnDebugMessagePrinted;
			var resultType = ParseSummaryResult(debugOutput.ToString(), out var message);
			if (resultType == TestResultType.None)
				return;

			foreach (var method in debuggedMethods) {
				OnTestFinished(this, new TestFinishedEventArgs(new MtpTestResult(method.TargetFramework + "\0" + method.DisplayName) {
					Message = message,
					ResultType = resultType
				}));
			}
		}

		void OnDebugMessagePrinted(string message)
		{
			debugOutput.Append(message);
		}

		public override void Dispose()
		{
			BaseDebuggerService.DebugMessagePrinted -= OnDebugMessagePrinted;
			base.Dispose();
		}

		static TestResultType ParseSummaryResult(string outputText, out string message)
		{
			message = null;
			if (string.IsNullOrEmpty(outputText))
				return TestResultType.None;

			var lines = outputText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
			var summary = lines.FirstOrDefault(line => line.TrimStart().StartsWith("Test run summary:", StringComparison.Ordinal));
			if (summary == null)
				return TestResultType.None;

			message = summary.Trim();
			if (summary.IndexOf("Passed!", StringComparison.OrdinalIgnoreCase) >= 0)
				return TestResultType.Success;
			if (summary.IndexOf("Skipped!", StringComparison.OrdinalIgnoreCase) >= 0)
				return TestResultType.Ignored;
			if (summary.IndexOf("Failed!", StringComparison.OrdinalIgnoreCase) >= 0)
				return TestResultType.Failure;
			return TestResultType.None;
		}

		static List<string> CollectFullyQualifiedNames(IEnumerable<ITest> tests)
		{
			var names = new List<string>();
			CollectFullyQualifiedNames(tests, names);
			return names;
		}

		static void CollectFullyQualifiedNames(IEnumerable<ITest> tests, List<string> results)
		{
			foreach (var test in tests) {
				if (test is MtpTestMethod method) {
					results.Add(method.FullyQualifiedName);
				} else if (test.NestedTests != null) {
					CollectFullyQualifiedNames(test.NestedTests, results);
				}
			}
		}
	}
}
