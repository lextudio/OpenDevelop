using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ICSharpCode.SearchAndReplace.Portable;

public sealed record PortableSearchOptions(
	string Pattern,
	string Replacement,
	string RootDirectory,
	string FileTypes,
	bool MatchCase,
	bool UseRegex,
	bool IncludeSubdirectories);

public sealed record PortableSearchResult(
	string FilePath,
	int Line,
	int Column,
	int Offset,
	int Length,
	string Preview)
{
	public string Location => $"{FilePath}:{Line}:{Column}";
}

public sealed class PortableSearchEngine
{
	static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
	{
		".git",
		".vs",
		"bin",
		"obj",
		"node_modules",
		"packages",
		"artifacts"
	};

	public IReadOnlyList<PortableSearchResult> FindAll(PortableSearchOptions options, out int searchedFileCount)
	{
		if (string.IsNullOrEmpty(options.Pattern))
			throw new ArgumentException("Search pattern cannot be empty.", nameof(options));
		if (string.IsNullOrWhiteSpace(options.RootDirectory) || !Directory.Exists(options.RootDirectory))
			throw new DirectoryNotFoundException("Search directory does not exist: " + options.RootDirectory);

		var matcher = CreateMatcher(options);
		var results = new List<PortableSearchResult>();
		searchedFileCount = 0;

		foreach (var file in EnumerateFiles(options))
		{
			searchedFileCount++;
			AddMatches(file, matcher, results);
		}

		return results;
	}

	public int ReplaceListed(IEnumerable<PortableSearchResult> results, PortableSearchOptions options)
	{
		if (string.IsNullOrEmpty(options.Pattern))
			throw new ArgumentException("Search pattern cannot be empty.", nameof(options));

		var replace = CreateReplacer(options);
		var changed = 0;
		foreach (var file in results.Select(item => item.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
		{
			try
			{
				var original = File.ReadAllText(file);
				var updated = replace(original);
				if (!string.Equals(original, updated, StringComparison.Ordinal))
				{
					File.WriteAllText(file, updated);
					changed++;
				}
			}
			catch
			{
				// Keep replacing other listed files when one file is locked or unreadable.
			}
		}

		return changed;
	}

	static void AddMatches(string file, Func<string, IEnumerable<MatchRange>> matcher, List<PortableSearchResult> results)
	{
		string text;
		try
		{
			var info = new FileInfo(file);
			if (info.Length > 4 * 1024 * 1024)
				return;

			text = File.ReadAllText(file);
		}
		catch
		{
			return;
		}

		var lineStarts = GetLineStarts(text);
		foreach (var match in matcher(text))
		{
			var lineIndex = FindLineIndex(lineStarts, match.Index);
			var lineStart = lineStarts[lineIndex];
			var lineEnd = text.IndexOfAny(new[] { '\r', '\n' }, lineStart);
			if (lineEnd < 0)
				lineEnd = text.Length;

			results.Add(new PortableSearchResult(
				file,
				lineIndex + 1,
				match.Index - lineStart + 1,
				match.Index,
				match.Length,
				text[lineStart..lineEnd].Trim()));
		}
	}

	static Func<string, IEnumerable<MatchRange>> CreateMatcher(PortableSearchOptions options)
	{
		if (options.UseRegex)
		{
			var regex = new Regex(options.Pattern, GetRegexOptions(options));
			return text => regex.Matches(text).Select(match => new MatchRange(match.Index, match.Length));
		}

		var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		return text => FindLiteralMatches(text, options.Pattern, comparison);
	}

	static Func<string, string> CreateReplacer(PortableSearchOptions options)
	{
		if (options.UseRegex)
		{
			var regex = new Regex(options.Pattern, GetRegexOptions(options));
			return text => regex.Replace(text, options.Replacement ?? string.Empty);
		}

		var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		return text => ReplaceLiteral(text, options.Pattern, options.Replacement ?? string.Empty, comparison);
	}

	static RegexOptions GetRegexOptions(PortableSearchOptions options)
	{
		var regexOptions = RegexOptions.Multiline;
		if (!options.MatchCase)
			regexOptions |= RegexOptions.IgnoreCase;
		return regexOptions;
	}

	static IEnumerable<string> EnumerateFiles(PortableSearchOptions options)
	{
		var patterns = options.FileTypes
			.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.DefaultIfEmpty("*.*")
			.ToArray();

		var searchOption = options.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
		foreach (var pattern in patterns)
		{
			IEnumerable<string> files;
			try
			{
				files = Directory.EnumerateFiles(options.RootDirectory, pattern, searchOption);
			}
			catch
			{
				continue;
			}

			foreach (var file in files)
			{
				if (!IsInExcludedDirectory(options.RootDirectory, file))
					yield return file;
			}
		}
	}

	static bool IsInExcludedDirectory(string root, string file)
	{
		var relative = Path.GetRelativePath(root, file);
		var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return parts.Any(part => ExcludedDirectories.Contains(part));
	}

	static IEnumerable<MatchRange> FindLiteralMatches(string text, string pattern, StringComparison comparison)
	{
		var index = 0;
		while (index < text.Length)
		{
			index = text.IndexOf(pattern, index, comparison);
			if (index < 0)
				yield break;

			yield return new MatchRange(index, pattern.Length);
			index += Math.Max(pattern.Length, 1);
		}
	}

	static string ReplaceLiteral(string text, string pattern, string replacement, StringComparison comparison)
	{
		var result = new StringBuilder(text.Length);
		var index = 0;
		while (index < text.Length)
		{
			var match = text.IndexOf(pattern, index, comparison);
			if (match < 0)
			{
				result.Append(text, index, text.Length - index);
				break;
			}

			result.Append(text, index, match - index);
			result.Append(replacement);
			index = match + pattern.Length;
		}

		return result.ToString();
	}

	static int[] GetLineStarts(string text)
	{
		var starts = new List<int> { 0 };
		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] == '\n' && i + 1 < text.Length)
				starts.Add(i + 1);
		}

		return starts.ToArray();
	}

	static int FindLineIndex(int[] lineStarts, int index)
	{
		var position = Array.BinarySearch(lineStarts, index);
		return position >= 0 ? position : Math.Max(0, ~position - 1);
	}

	readonly record struct MatchRange(int Index, int Length);
}
