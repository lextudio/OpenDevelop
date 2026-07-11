using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ICSharpCode.SharpDevelop.Debugging;
using ICSharpCode.UnitTesting.Mtp;

namespace ICSharpCode.UnitTesting
{
	public class MtpTestDebugger : TestDebuggerBase
	{
		readonly MtpTestProject testProject;

		public MtpTestDebugger(MtpTestProject testProject, TestExecutionOptions options)
		{
			this.testProject = testProject;
		}

		public override int GetExpectedNumberOfTestResults(IEnumerable<ITest> selectedTests)
		{
			return testProject.GetTestNodesForSelectedTests(selectedTests).Count;
		}

		protected override ProcessStartInfo GetProcessStartInfo(IEnumerable<ITest> selectedTests)
		{
			var fullyQualifiedNames = CollectFullyQualifiedNames(selectedTests);
			var assembly = testProject.Project.OutputAssemblyFullPath;

			var info = new ProcessStartInfo {
				WorkingDirectory = testProject.Project.Directory ?? Environment.CurrentDirectory
			};

			if (assembly != null && File.Exists(assembly)) {
				// MTP test projects build to a self-contained apphost exe - run it directly (no
				// vstest.console wrapper) with xUnit v3's own filter switches, one --filter-method
				// per selected test, rather than a single vstest-style "--TestCases:a,b,c" argument.
				info.FileName = assembly;
				info.Arguments = string.Join(" ",
					fullyQualifiedNames.Select(name => "--filter-method \"" + name + "\""));
				info.WorkingDirectory = Path.GetDirectoryName(assembly);
			} else {
				info.FileName = "dotnet";
				info.Arguments = "exec \"" + testProject.Project.AssemblyName + ".dll\" "
					+ string.Join(" ", fullyQualifiedNames.Select(name => "--filter-method \"" + name + "\""));
			}

			return info;
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
