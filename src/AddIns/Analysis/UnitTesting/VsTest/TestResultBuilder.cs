using System;
using System.Linq;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;

namespace ICSharpCode.UnitTesting
{
	class TestResultBuilder
	{
		bool singleTest;

		public TestResultBuilder(bool singleTest)
		{
			this.singleTest = singleTest;
		}

		public ICSharpCode.UnitTesting.TestResult Convert(Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult result)
		{
			var testResult = new ICSharpCode.UnitTesting.TestResult(result.TestCase.DisplayName) {
				Message = result.ErrorMessage,
				StackTrace = result.ErrorStackTrace,
				ResultType = ToResultType(result.Outcome)
			};

			if (result.Messages != null) {
				var output = string.Join(
					Environment.NewLine,
					result.Messages.Select(m => m.Text));
				testResult.Message = output;
			}

			return testResult;
		}

		static TestResultType ToResultType(TestOutcome outcome)
		{
			switch (outcome) {
				case TestOutcome.Passed:
					return TestResultType.Success;
				case TestOutcome.Failed:
				case TestOutcome.NotFound:
					return TestResultType.Failure;
				case TestOutcome.Skipped:
					return TestResultType.Ignored;
				default:
					return TestResultType.None;
			}
		}
	}
}
