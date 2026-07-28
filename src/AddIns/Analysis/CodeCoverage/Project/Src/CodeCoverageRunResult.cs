using System.Collections.Generic;

namespace ICSharpCode.CodeCoverage
{
	public sealed class CodeCoverageRunResult
	{
		public CodeCoverageRunResult(IReadOnlyList<string> resultFiles, IReadOnlyList<string> logLines)
		{
			ResultFiles = resultFiles;
			LogLines = logLines;
		}

		public IReadOnlyList<string> ResultFiles { get; }
		public IReadOnlyList<string> LogLines { get; }
	}
}
