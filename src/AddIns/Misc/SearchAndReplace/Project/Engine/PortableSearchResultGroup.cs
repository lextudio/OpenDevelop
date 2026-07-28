using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.SearchAndReplace.Portable;

public sealed record PortableSearchResultGroup(
	string Title,
	IReadOnlyList<PortableSearchResult> Results,
	IReadOnlyList<PortableSearchResultGroup> Children)
{
	public int OccurrenceCount => Results.Count + Children.Sum(child => child.OccurrenceCount);
}
