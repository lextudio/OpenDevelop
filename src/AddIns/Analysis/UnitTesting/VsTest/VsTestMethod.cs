using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ICSharpCode.SharpDevelop.Project;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace ICSharpCode.UnitTesting
{
	class VsTestMethod : TestBase, IVsTestTestProvider
	{
		readonly ITestProject project;
		readonly List<TestCase> testCases = new List<TestCase>();
		readonly string displayName;

		public VsTestMethod(ITestProject project, TestCase testCase)
		{
			this.project = project;
			this.testCases.Add(testCase);
			this.displayName = testCase.DisplayName;
		}

		public override ITestProject ParentProject {
			get { return project; }
		}

		public override string DisplayName {
			get { return displayName; }
		}

		public IReadOnlyList<TestCase> TestCases {
			get { return testCases; }
		}

		public IProject Project {
			get { return ((VsTestProject)project).Project; }
		}

		public IEnumerable<TestCase> GetTests()
		{
			return testCases;
		}

		// TestBase.Result's setter is `protected`, so VsTestProject (a sibling subclass, not a
		// base/derived relation to this one) can't assign it directly -- expose a public setter
		// for VsTestProject.UpdateTestResult to apply a completed run's outcome.
		public void SetResult(TestResultType resultType)
		{
			Result = resultType;
		}
	}
}
