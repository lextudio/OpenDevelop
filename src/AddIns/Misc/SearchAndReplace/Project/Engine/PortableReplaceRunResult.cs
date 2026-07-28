#nullable enable

using System.Collections.Generic;

namespace ICSharpCode.SearchAndReplace.Portable;

public sealed record PortableReplaceRunResult(
	int ChangedFileCount,
	IReadOnlyList<string>? ChangedFilePaths = null)
{
	public string FormatStatus() => $"Updated {ChangedFileCount} file(s).";
}
