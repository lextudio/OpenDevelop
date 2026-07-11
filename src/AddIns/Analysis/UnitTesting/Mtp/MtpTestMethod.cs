using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.UnitTesting.Mtp
{
	class MtpTestMethod : TestBase
	{
		readonly ITestProject project;
		readonly MtpTestNode node;

		public MtpTestMethod(ITestProject project, MtpTestNode node)
		{
			this.project = project;
			this.node = node;
		}

		public override ITestProject ParentProject {
			get { return project; }
		}

		public override string DisplayName {
			get { return node.DisplayName; }
		}

		public string Uid {
			get { return node.Uid; }
		}

		public MtpTestNode Node {
			get { return node; }
		}

		// Used to build a "--filter-method" argument for a one-off debug launch of the built test
		// exe (MtpTestDebugger) - MTP has no separate "fully qualified name" concept the way
		// TestCase.FullyQualifiedName did, so reconstruct the closest equivalent from location.*.
		public string FullyQualifiedName {
			get {
				var type = node.LocationType;
				var method = node.LocationMethodName;
				return !string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(method)
					? type + "." + method
					: DisplayName;
			}
		}

		public IProject Project {
			get { return ((MtpTestProject)project).Project; }
		}

		// TestBase.Result's setter is `protected`, so MtpTestProject (a sibling subclass, not a
		// base/derived relation to this one) can't assign it directly - expose a public setter
		// for MtpTestProject.UpdateTestResult to apply a completed run's outcome.
		public void SetResult(TestResultType resultType)
		{
			Result = resultType;
		}
	}
}
