using System;
using System.Threading;

namespace ICSharpCode.UnitTesting
{
	public enum TestOperationKind
	{
		Run,
		Debug,
		Coverage
	}

	/// <summary>
	/// Exclusive, solution-wide ownership of a test-related operation. Disposing the
	/// lease is the only way an active operation returns to the idle state.
	/// </summary>
	public interface ITestOperation : IDisposable
	{
		TestOperationKind Kind { get; }
		CancellationToken CancellationToken { get; }
	}
}
