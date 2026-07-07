using System;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.TypeSystem;

namespace ICSharpCode.UnitTesting
{
	public class VsTestFramework : ITestFramework
	{
		public bool IsTestProject(IProject project)
		{
			return true;
		}

		public ITestProject CreateTestProject(ITestSolution parentSolution, IProject project)
		{
			return new VsTestProject(project);
		}
	}
}
