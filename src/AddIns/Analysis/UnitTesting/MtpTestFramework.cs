using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.UnitTesting
{
	public class MtpTestFramework : ITestFramework
	{
		public bool IsTestProject(IProject project)
		{
			return true;
		}

		public ITestProject CreateTestProject(ITestSolution parentSolution, IProject project)
		{
			return new MtpTestProject(project);
		}
	}
}
