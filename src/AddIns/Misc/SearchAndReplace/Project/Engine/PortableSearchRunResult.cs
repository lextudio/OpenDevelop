using System.Collections.Generic;

namespace ICSharpCode.SearchAndReplace.Portable;

public sealed record PortableSearchRunResult(
	IReadOnlyList<PortableSearchResult> Results,
	int SearchedFileCount)
{
	public string FormatStatus() => $"{Results.Count} result(s) in {SearchedFileCount} file(s).";
}
