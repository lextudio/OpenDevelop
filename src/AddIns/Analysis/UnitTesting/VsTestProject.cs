using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.TypeSystem;

namespace ICSharpCode.UnitTesting
{
	public class VsTestProject : TestProjectBase
	{
		public VsTestProject(IProject project) : base(project)
		{
		}

		public override ITestRunner CreateTestRunner(TestExecutionOptions options)
		{
			return new VsTestRunner(this, options);
		}

		public override IEnumerable<ITest> GetTestsForEntity(IEntity entity)
		{
			return Enumerable.Empty<ITest>();
		}

		public override void UpdateTestResult(TestResult result)
		{
		}

		protected override bool IsTestClass(ITypeDefinition typeDefinition)
		{
			return false;
		}

		protected override ITest CreateTestClass(ITypeDefinition typeDefinition)
		{
			return null;
		}

		protected override void UpdateTestClass(ITest test, ITypeDefinition typeDefinition)
		{
		}
	}
}
