using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ICSharpCode.UnitTesting
{
	class VsTestClass : TestBase
	{
		readonly ITestProject project;
		readonly string className;

		public VsTestClass(ITestProject project, string className)
		{
			this.project = project;
			this.className = className;
			BindResultToCompositeResultOfNestedTests();
		}

		public override ITestProject ParentProject {
			get { return project; }
		}

		public override string DisplayName {
			get { return className; }
		}

		public new TestCollection NestedTests {
			get { return base.NestedTestCollection; }
		}
	}
}
