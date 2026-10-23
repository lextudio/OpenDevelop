using System.Text.RegularExpressions;

namespace OpenDevelop.Terminal
{
	enum TerminalLinkKind
	{
		Url,
		File,
	}

	/// <summary>A link-shaped piece of a row's text: a URL, or a file path with an optional line and column.</summary>
	/// <param name="Start">Index of the first character in the row text.</param>
	/// <param name="Length">Number of characters, including any ":line:col" or "(line,col)" suffix.</param>
	/// <param name="Path">The URL, or the path without the line/column suffix.</param>
	readonly record struct TerminalLinkCandidate(TerminalLinkKind Kind, int Start, int Length, string Path, int Line, int Column);

	/// <summary>Finds URLs and file references (<c>src/a.cs:12:5</c>, <c>a.cs(12,5)</c>, <c>C:\x\a.cs</c>, <c>/x/a.cs</c>) in a line of terminal text. Pure text work: whether a file exists is decided by the caller.</summary>
	static class TerminalLinks
	{
		static readonly Regex Url = new Regex(@"https?://[^\s<>""'`]+", RegexOptions.Compiled);

		// path: a Windows drive path, or a path containing '/' (optionally absolute or ~), or a bare file name with an extension;
		// then an optional ":line[:col]" or "(line[,col])".
		static readonly Regex File = new Regex(
			@"(?<path>[A-Za-z]:[\\/][^\s:*?""<>|()]+|~?/?(?:[\w.@+~-]+/)+[\w.@+~-]*\w|[\w@+~-][\w.@+~-]*\.\w+)"
			+ @"(?:(?::(?<l1>[0-9]{1,6})(?![0-9])(?::(?<c1>[0-9]{1,6})(?![0-9]))?)|(?:\((?<l2>[0-9]{1,6})(?![0-9])(?:,\s*(?<c2>[0-9]{1,6})(?![0-9]))?\)))?",
			RegexOptions.Compiled);

		public static IReadOnlyList<TerminalLinkCandidate> Find(string text)
		{
			var found = new List<TerminalLinkCandidate>();
			if (string.IsNullOrEmpty(text))
				return found;

			foreach (Match m in Url.Matches(text)) {
				string url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
				while (url.EndsWith(")", StringComparison.Ordinal) && url.Count(c => c == ')') > url.Count(c => c == '('))
					url = url.Substring(0, url.Length - 1);
				if (url.Length > "https://".Length)
					found.Add(new TerminalLinkCandidate(TerminalLinkKind.Url, m.Index, url.Length, url, 0, 0));
			}

			foreach (Match m in File.Matches(text)) {
				if (found.Any(u => u.Kind == TerminalLinkKind.Url && m.Index < u.Start + u.Length && u.Start < m.Index + m.Length))
					continue; // part of a URL
				var path = m.Groups["path"];
				string pathText = path.Value.TrimEnd('.', ',', ';');
				bool hasSeparator = pathText.Contains('/') || pathText.Contains('\\');
				bool hasLine = m.Groups["l1"].Success || m.Groups["l2"].Success;
				if (!hasSeparator && !hasLine)
					continue; // a bare "name.ext" is too common in output to be a link without a line number
				if (string.IsNullOrEmpty(System.IO.Path.GetExtension(pathText)))
					continue;
				// The digit groups are 1-6 ASCII digits not followed by another digit, so they always parse; a longer run (or
				// non-ASCII digits) simply leaves the suffix unmatched and the link is the bare path.
				int line = 0, column = 0;
				if (m.Groups["l1"].Success) {
					int.TryParse(m.Groups["l1"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out line);
					if (m.Groups["c1"].Success)
						int.TryParse(m.Groups["c1"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out column);
				} else if (m.Groups["l2"].Success) {
					int.TryParse(m.Groups["l2"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out line);
					if (m.Groups["c2"].Success)
						int.TryParse(m.Groups["c2"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out column);
				}
				int length = hasLine ? m.Length : pathText.Length;
				found.Add(new TerminalLinkCandidate(TerminalLinkKind.File, m.Index, length, pathText, line, column));
			}
			return found.OrderBy(c => c.Start).ToList();
		}
	}
}
