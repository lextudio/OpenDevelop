using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ICSharpCode.SharpDevelop;

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
			return selectedTests.Count();
		}

		protected override ProcessStartInfo GetProcessStartInfo(IEnumerable<ITest> selectedTests)
		{
			var info = new ProcessStartInfo();
			var assembly = testProject.Project.OutputAssemblyFullPath;
			if (assembly != null)
			{
				info.FileName = assembly;
				info.WorkingDirectory = assembly.GetParentDirectory();
			}
			return info;
		}
	}
}
