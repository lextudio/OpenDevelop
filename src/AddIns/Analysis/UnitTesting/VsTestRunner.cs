using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.UnitTesting
{
	public class VsTestRunner : ITestRunner
	{
		public event EventHandler<TestFinishedEventArgs> TestFinished;

		public VsTestRunner(VsTestProject project, TestExecutionOptions options)
		{
		}

		public Task RunAsync(IEnumerable<ITest> selectedTests, IProgress<double> progress, TextWriter output, CancellationToken cancellationToken)
		{
			return Task.CompletedTask;
		}

		public void Dispose()
		{
		}
	}
}
