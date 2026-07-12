namespace ICSharpCode.UnitTesting.Mtp
{
	class MtpTargetFramework : TestBase
	{
		readonly ITestProject project;

		public MtpTargetFramework(ITestProject project, string targetFramework)
		{
			this.project = project;
			TargetFramework = targetFramework;
			BindResultToCompositeResultOfNestedTests();
		}

		public string TargetFramework { get; }
		public override string DisplayName => TargetFramework;
		public override ITestProject ParentProject => project;
		public new TestCollection NestedTests => base.NestedTestCollection;
	}
}
