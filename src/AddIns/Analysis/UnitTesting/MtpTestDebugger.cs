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
			var methods = testProject.GetTestMethodsForSelectedTests(selectedTests);
			var targetFramework = methods.Select(method => method.TargetFramework).Distinct(StringComparer.OrdinalIgnoreCase).Single();
			var fullyQualifiedNames = methods.Select(method => method.FullyQualifiedName).ToList();
			var assembly = MtpTestProject.ResolveAssemblyDll(testProject.Project, targetFramework);

			var info = new ProcessStartInfo {
				WorkingDirectory = testProject.Project.Directory ?? Environment.CurrentDirectory
			};

			if (assembly != null && File.Exists(assembly)) {
				info.FileName = "dotnet";
				info.Arguments = "exec \"" + assembly + "\" " + string.Join(" ",
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
