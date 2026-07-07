using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Debugging;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.UnitTesting
{
	public class VsTestDebugger : TestDebuggerBase
	{
		readonly VsTestProject testProject;

		public VsTestDebugger(VsTestProject testProject, TestExecutionOptions options)
		{
			this.testProject = testProject;
		}

		public override int GetExpectedNumberOfTestResults(IEnumerable<ITest> selectedTests)
		{
			return testProject.GetTestCasesForSelectedTests(selectedTests).Count;
		}

		protected override ProcessStartInfo GetProcessStartInfo(IEnumerable<ITest> selectedTests)
		{
			var testCases = testProject.GetTestCasesForSelectedTests(selectedTests);
			var assembly = testProject.Project.OutputAssemblyFullPath;

			var info = new ProcessStartInfo {
				FileName = "dotnet",
				Arguments = $"vstest \"{assembly}\" --TestCases:{string.Join(",", testCases.Select(tc => tc.FullyQualifiedName))}",
				WorkingDirectory = testProject.Project.Directory ?? Environment.CurrentDirectory
			};

			if (assembly != null && File.Exists(assembly)) {
				info.FileName = assembly;
				info.Arguments = string.Empty;
				info.WorkingDirectory = Path.GetDirectoryName(assembly);
			}

			return info;
		}
	}
}
