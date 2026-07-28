using System.Collections.Generic;

namespace ICSharpCode.CodeCoverage
{
	public sealed class CodeCoverageProcessResult
	{
		public CodeCoverageProcessResult(int exitCode, IReadOnlyList<string> outputLines)
		{
			ExitCode = exitCode;
			OutputLines = outputLines;
		}

		public int ExitCode { get; }
		public IReadOnlyList<string> OutputLines { get; }
	}
}
