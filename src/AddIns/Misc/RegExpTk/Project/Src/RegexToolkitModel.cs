using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ICSharpCode.RegExpTk.Portable;

public sealed record RegexToolkitOptions(bool IgnoreCase, bool Multiline, bool Singleline);

public sealed record RegexToolkitResult(
	IReadOnlyList<RegexToolkitMatch> Matches,
	string ReplacementResult,
	string? ErrorMessage);

public sealed record RegexToolkitMatch(string Value, int Index, int End, int Length, IReadOnlyList<RegexToolkitGroup> Groups);

public sealed record RegexToolkitGroup(int GroupIndex, string Value, int Index, int Length);

public sealed record RegexQuickInsert(string Name, string Text)
{
	public static IReadOnlyList<RegexQuickInsert> Items { get; } = new[]
	{
		new RegexQuickInsert("Ungreedy star", "*?"),
		new RegexQuickInsert("Word character", "\\w"),
		new RegexQuickInsert("Non-word character", "\\W"),
		new RegexQuickInsert("Whitespace", "\\s"),
		new RegexQuickInsert("Non-whitespace", "\\S"),
		new RegexQuickInsert("Digit", "\\d"),
		new RegexQuickInsert("Non-digit", "\\D"),
		new RegexQuickInsert("Word boundary", "\\b")
	};
}

public static class RegexToolkitEvaluator
{
	public static RegexToolkitResult Evaluate(string pattern, string input, string replacement, RegexToolkitOptions options)
	{
		try
		{
			var regex = new Regex(pattern, ToRegexOptions(options));
			var matches = regex.Matches(input)
				.Select(match => new RegexToolkitMatch(
					match.Value,
					match.Index,
					match.Index + match.Length,
					match.Length,
					match.Groups
						.Cast<Group>()
						.Select((group, index) => new RegexToolkitGroup(
							index,
							group.Success ? group.Value : string.Empty,
							group.Success ? group.Index : -1,
							group.Success ? group.Length : 0))
						.ToArray()))
				.ToArray();

			return new RegexToolkitResult(matches, regex.Replace(input, replacement), null);
		}
		catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
		{
			return new RegexToolkitResult(Array.Empty<RegexToolkitMatch>(), string.Empty, ex.Message);
		}
	}

	static RegexOptions ToRegexOptions(RegexToolkitOptions options)
	{
		var regexOptions = RegexOptions.None;
		if (options.IgnoreCase)
			regexOptions |= RegexOptions.IgnoreCase;
		if (options.Multiline)
			regexOptions |= RegexOptions.Multiline;
		if (options.Singleline)
			regexOptions |= RegexOptions.Singleline;
		return regexOptions;
	}
}
