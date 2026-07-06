using System.Text.RegularExpressions;

namespace Plugins.RegExpTk
{
	public class MatchViewModel
	{
		public Match Match { get; }
		public string Value => Match.Value;
		public int Index => Match.Index;
		public int End => Match.Index + Match.Length;
		public int Length => Match.Length;
		public int GroupCount => Match.Groups.Count;

		public MatchViewModel(Match match)
		{
			Match = match;
		}
	}
}
