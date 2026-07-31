using System;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.UnitTesting
{
	public class MtpTestFramework : ITestFramework
	{
		public bool IsTestProject(IProject project)
		{
			if (project is not MSBuildBasedProject msbuildProject)
				return false;

			return string.Equals(msbuildProject.GetEvaluatedProperty("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(msbuildProject.GetEvaluatedProperty("IsTestingPlatformApplication"), "true", StringComparison.OrdinalIgnoreCase);
		}

		public ITestProject CreateTestProject(ITestSolution parentSolution, IProject project)
		{
			return new MtpTestProject(project);
		}
	}
}
