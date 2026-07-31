namespace ICSharpCode.UnitTesting.Mtp
{
	sealed class MtpTestResult : TestResult
	{
		public MtpTestResult(string name)
			: base(name)
		{
			FullName = name;
		}

		public string FullName { get; }
	}
}
