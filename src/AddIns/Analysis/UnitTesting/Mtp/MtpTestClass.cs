namespace ICSharpCode.UnitTesting.Mtp
{
	class MtpTestClass : TestBase
	{
		readonly ITestProject project;
		readonly string className;

		public MtpTestClass(ITestProject project, string className)
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
