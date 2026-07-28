using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.SearchAndReplace.Portable;

public sealed record PortableReplacePlan(IReadOnlyList<PortableReplaceFilePlan> Files)
{
	public int ChangedFileCount => Files.Count(file => file.HasChanges);
	public int MatchCount => Files.Sum(file => file.MatchCount);
	public bool HasChanges => ChangedFileCount > 0;
	public string FormatStatus() => $"{MatchCount} replacement(s) planned in {ChangedFileCount} file(s).";
}
