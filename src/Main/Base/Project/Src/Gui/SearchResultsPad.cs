using System.Collections.Generic;
using ICSharpCode.SharpDevelop.Editor.Search;

namespace ICSharpCode.SharpDevelop.Gui;

public static class SearchResultsPad
{
	public static void AddSearchResult(string title, IList<SearchResultMatch> results)
	{
#if !HAS_UNO
		Editor.Search.SearchResultsPad.Instance.ShowSearchResults(title, results);
#endif
	}
	
	public static void ClearSearchResults()
	{
#if !HAS_UNO
		Editor.Search.SearchResultsPad.Instance.ClearLastSearchesList();
#endif
	}
}
