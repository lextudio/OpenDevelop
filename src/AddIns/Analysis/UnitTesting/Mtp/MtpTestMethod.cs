using System;
using ICSharpCode.SharpDevelop.Project;
using System.Windows.Input;

namespace ICSharpCode.UnitTesting.Mtp
{
	class MtpTestMethod : TestBase
	{
		readonly ITestProject project;
		readonly MtpTestNode node;

		public MtpTestMethod(ITestProject project, MtpTestNode node, string targetFramework)
		{
			this.project = project;
			this.node = node;
			TargetFramework = targetFramework;
		}

		public string TargetFramework { get; }

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
		
		public override ICommand GoToDefinition {
			get { return new MtpGoToDefinitionCommand(node, Project); }
		}

		// TestBase.Result's setter is `protected`, so MtpTestProject (a sibling subclass, not a
		// base/derived relation to this one) can't assign it directly - expose a public setter
		// for MtpTestProject.UpdateTestResult to apply a completed run's outcome.
		public void SetResult(TestResultType resultType)
		{
			Result = resultType;
		}

		/// <summary>How long the most recent run of this test took, or <see langword="null"/> if
		/// never run or not reported. Set by <see cref="MtpTestProject.UpdateTestResult"/>.</summary>
		public TimeSpan? Duration { get; private set; }

		public void SetDuration(TimeSpan? duration)
		{
			Duration = duration;
		}
	}
}
