using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ICSharpCode.Translations;

public enum PoSeverity { Info, Warning, Error }

/// <summary>A problem found in a <c>.po</c> text; <see cref="Line"/> is 1-based.</summary>
public sealed record PoProblem(string Id, string Message, PoSeverity Severity, int Line);

/// <summary>One completion candidate; <see cref="Insertion"/> replaces the text from the returned start offset to the caret.</summary>
public sealed record PoCompletion(string Display, string Insertion, string Description);

/// <summary>
/// What an editor needs to know about a <c>.po</c> text, as pure functions of it: the problems a translator would otherwise
/// meet at <c>msgfmt -c</c> or at runtime, the hover for a message, completion for keywords, flags and the source text,
/// the file:line a <c>#:</c> reference points at, and the outline. The language service
/// (<see cref="PoLanguageService"/>) only converts these to the editor's contracts.
/// </summary>
public static class PoAnalyzer
{
	// A printf conversion: %[n$][flags][width][.precision][length]conversion. "%%" is a literal percent, not a placeholder.
	static readonly Regex Printf = new Regex(@"%(?:(?<pos>\d+)\$)?[-+#0']*(?:\*|\d+)?(?:\.(?:\*|\d+))?(?:hh|h|ll|l|L|q|j|z|t)?(?<conv>[diouxXeEfFgGaAcspn%])", RegexOptions.Compiled);
	static readonly Regex Markup = new Regex(@"<(?<close>/)?(?<name>[A-Za-z][A-Za-z0-9]*)(?:\s[^<>]*)?(?<self>/)?>", RegexOptions.Compiled);

	/// <summary>The placeholders of a string as (index, conversion): a numbered one (<c>%2$s</c>) keeps its number, the others
	/// are numbered in order, so a translation may reorder placeholders only by numbering them.</summary>
	public static IReadOnlyList<(int Index, char Conversion)> Placeholders(string text)
	{
		var result = new List<(int, char)>();
		var next = 1;
		foreach (Match match in Printf.Matches(text)) {
			var conversion = match.Groups["conv"].Value[0];
			if (conversion == '%') continue;
			result.Add((match.Groups["pos"].Success ? int.Parse(match.Groups["pos"].Value) : next, conversion));
			if (!match.Groups["pos"].Success) next++;
		}
		return result;
	}

	/// <summary>The markup tags of a string, e.g. <c>b</c> and <c>/b</c> for "&lt;b&gt;x&lt;/b&gt;".</summary>
	public static IReadOnlyList<string> Tags(string text)
		=> Markup.Matches(text).Where(m => !m.Groups["self"].Success).Select(m => (m.Groups["close"].Success ? "/" : "") + m.Groups["name"].Value).ToList();

	static string Describe(IEnumerable<(int Index, char Conversion)> placeholders)
		=> string.Join(", ", placeholders.Select(p => "%" + p.Index + "$" + p.Conversion));

	static bool SamePlaceholders(IReadOnlyList<(int, char)> source, IReadOnlyList<(int, char)> translation)
		=> source.OrderBy(p => p).SequenceEqual(translation.OrderBy(p => p));

	/// <summary>Every problem in <paramref name="text"/>, in line order.</summary>
	public static IReadOnlyList<PoProblem> Analyze(string text)
	{
		var catalog = PoCatalog.Parse(text);
		var problems = new List<PoProblem>();
		var header = catalog.Header;
		var hasHeader = header.Line > 0; // a parsed header entry; a catalogue without one only holds the default
		var live = catalog.Entries.Where(e => !e.Obsolete).ToList();

		var nplurals = 0;
		if (!hasHeader) {
			if (live.Count > 0) problems.Add(new PoProblem("PO1008", "The file has no header entry (msgid \"\"), so its charset and plural forms are unknown.", PoSeverity.Warning, Math.Max(1, live[0].Line)));
		} else {
			var headerLine = Math.Max(1, header.MsgIdLine > 0 ? header.MsgIdLine : header.Line);
			var charset = Regex.Match(header.MsgStr, @"charset\s*=\s*([^\s;\\]+)", RegexOptions.IgnoreCase);
			if (!charset.Success) problems.Add(new PoProblem("PO1008", "The header has no Content-Type charset.", PoSeverity.Warning, headerLine));
			else if (!string.Equals(charset.Groups[1].Value, "UTF-8", StringComparison.OrdinalIgnoreCase))
				problems.Add(new PoProblem("PO1008", $"The header says charset={charset.Groups[1].Value}; the translator saves .po files as UTF-8.", PoSeverity.Warning, headerLine));
			var plurals = Regex.Match(header.MsgStr, @"nplurals\s*=\s*(\d+)");
			if (plurals.Success) nplurals = int.Parse(plurals.Groups[1].Value);
		}

		var seen = new Dictionary<string, PoEntry>(StringComparer.Ordinal);
		foreach (var entry in live) {
			var idLine = Math.Max(1, entry.MsgIdLine > 0 ? entry.MsgIdLine : entry.Line);
			var strLine = Math.Max(1, entry.MsgStrLine > 0 ? entry.MsgStrLine : idLine);
			if (seen.TryGetValue(entry.Key, out var first))
				problems.Add(new PoProblem("PO1007", $"Duplicate message definition: \"{Shorten(entry.MsgId)}\" is already defined at line {Math.Max(1, first.MsgIdLine)}.", PoSeverity.Error, idLine));
			else seen[entry.Key] = entry;

			if (entry.IsFuzzy)
				problems.Add(new PoProblem("PO1004", "Fuzzy: this translation is not used until it is confirmed (typing in the translator, or removing the flag).", PoSeverity.Info, entry.FlagsLine > 0 ? entry.FlagsLine : strLine));

			if (entry.MsgIdPlural != null) {
				if (nplurals == 0 && hasHeader && entry.IsTranslated)
					problems.Add(new PoProblem("PO1006", "A plural message needs 'Plural-Forms: nplurals=N; plural=...;' in the header.", PoSeverity.Warning, idLine));
				else if (nplurals > 0 && entry.IsTranslated && entry.PluralStrings.Count != nplurals)
					problems.Add(new PoProblem("PO1005", $"The header declares nplurals={nplurals}, but this message has {entry.PluralStrings.Count} msgstr form(s).", PoSeverity.Error, strLine));
			}

			if (entry.IsFuzzy) continue; // an unconfirmed translation is unused, so its text is not checked yet
			CheckTranslation(entry, problems, strLine);
		}
		return problems.OrderBy(p => p.Line).ToList();
	}

	static void CheckTranslation(PoEntry entry, List<PoProblem> problems, int line)
	{
		var forms = entry.MsgIdPlural == null
			? new List<string> { entry.MsgStr }
			: entry.PluralStrings.ToList();
		for (var i = 0; i < forms.Count; i++) {
			var form = forms[i];
			if (form.Length == 0) continue;
			var which = entry.MsgIdPlural == null ? "" : $"[{i}]";
			var source = entry.MsgIdPlural ?? entry.MsgId;

			if (!entry.Flags.Contains("no-c-format")) {
				var expected = Placeholders(source);
				var actual = Placeholders(form);
				// A plural form may leave a placeholder out ("one file" for "%d files"), never add or change one.
				var plural = entry.MsgIdPlural != null;
				var ok = plural ? actual.All(a => expected.Contains(a)) : SamePlaceholders(expected, actual);
				if ((expected.Count > 0 || actual.Count > 0 && entry.Flags.Contains("c-format")) && !ok)
					problems.Add(new PoProblem("PO1001", $"msgstr{which} placeholders ({(actual.Count == 0 ? "none" : Describe(actual))}) do not match the source's ({(expected.Count == 0 ? "none" : Describe(expected))}).", PoSeverity.Error, line));
			}
			var sourceTags = Tags(source).OrderBy(t => t, StringComparer.Ordinal);
			var formTags = Tags(form).OrderBy(t => t, StringComparer.Ordinal);
			if (sourceTags.Any() && !sourceTags.SequenceEqual(formTags))
				problems.Add(new PoProblem("PO1002", $"msgstr{which} markup tags differ from the source's ({string.Join(" ", Tags(source).Select(t => "<" + t + ">"))}).", PoSeverity.Warning, line));
			if (source.StartsWith("\n", StringComparison.Ordinal) != form.StartsWith("\n", StringComparison.Ordinal)
				|| source.EndsWith("\n", StringComparison.Ordinal) != form.EndsWith("\n", StringComparison.Ordinal))
				problems.Add(new PoProblem("PO1003", $"msgid and msgstr{which} do not both begin or both end with a line break.", PoSeverity.Warning, line));
		}
	}

	static string Shorten(string text, int length = 40)
	{
		text = text.Replace("\n", " ").Replace("\t", " ");
		return text.Length <= length ? text : text.Substring(0, length - 1) + "\u2026";
	}

	static IEnumerable<string> Lines(string text) => text.Replace("\r", "").Split('\n');

	/// <summary>The entry whose lines contain 1-based <paramref name="line"/> (comments through its msgstr), or null.</summary>
	static (PoEntry Entry, bool IsHeader)? EntryAt(PoCatalog catalog, int line, int lineCount)
	{
		var all = new List<(PoEntry Entry, bool IsHeader)>();
		if (catalog.Header.Line > 0) all.Add((catalog.Header, true));
		all.AddRange(catalog.Entries.Where(e => e.Line > 0).Select(e => (e, false)));
		all = all.OrderBy(x => x.Entry.Line).ToList();
		for (var i = 0; i < all.Count; i++) {
			var end = i + 1 < all.Count ? all[i + 1].Entry.Line - 1 : lineCount;
			if (line >= all[i].Entry.Line && line <= end) return all[i];
		}
		return null;
	}

	/// <summary>The hover text for the message at 1-based <paramref name="line"/>, or null between messages.</summary>
	public static string? Hover(string text, int line)
	{
		var catalog = PoCatalog.Parse(text);
		var allLines = Lines(text).ToList();
		if (line < 1 || line > allLines.Count || allLines[line - 1].Trim().Length == 0) return null;
		if (EntryAt(catalog, line, allLines.Count) is not { } found) return null;
		var (entry, isHeader) = found;
		if (isHeader) return "Header\n" + string.Join("\n", entry.MsgStr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
		var sb = new StringBuilder();
		sb.Append(entry.MsgId.Length == 0 ? "(empty source)" : entry.MsgId);
		if (entry.MsgIdPlural != null) sb.Append("\n| ").Append(entry.MsgIdPlural);
		if (entry.Context != null) sb.Append("\nContext: ").Append(entry.Context);
		var state = entry.Obsolete ? "Obsolete" : !entry.IsTranslated ? "Untranslated" : entry.IsFuzzy ? "Fuzzy" : "Translated";
		var translation = entry.MsgIdPlural == null ? entry.MsgStr : string.Join(" | ", entry.PluralStrings);
		sb.Append("\nTranslation (").Append(state).Append("): ").Append(translation.Length == 0 ? "-" : translation);
		if (entry.ExtractedComments.Count > 0) sb.Append("\nNote: ").Append(string.Join(" ", entry.ExtractedComments));
		if (entry.References.Count > 0) sb.Append("\nSources: ").Append(string.Join("  ", entry.References));
		var placeholders = Placeholders(entry.MsgIdPlural ?? entry.MsgId);
		if (placeholders.Count > 0) sb.Append("\nPlaceholders: ").Append(Describe(placeholders));
		return sb.ToString();
	}

	static readonly (string Name, string Description)[] Flags = {
		("fuzzy", "The translation is unconfirmed and is not used until the flag is removed."),
		("c-format", "The message uses printf placeholders; msgfmt checks them."),
		("no-c-format", "The message only looks like it has printf placeholders."),
		("python-format", "The message uses Python % placeholders."),
		("no-wrap", "Do not wrap long lines."),
	};

	/// <summary>Completion at <paramref name="offset"/>: flags after <c>#,</c>, the source text inside a <c>msgstr "</c>, or the
	/// keywords that may start this line. Null when there is nothing to offer; the start is where the replacement begins.</summary>
	public static (IReadOnlyList<PoCompletion> Items, int Start)? Complete(string text, int offset)
	{
		offset = Math.Max(0, Math.Min(offset, text.Length));
		var lineStart = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
		var prefix = text.Substring(lineStart, offset - lineStart);
		var trimmed = prefix.TrimStart();

		if (trimmed.StartsWith("#,", StringComparison.Ordinal)) {
			var start = offset;
			while (start > lineStart && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] != ',') start--;
			var typed = text.Substring(start, offset - start);
			var items = Flags.Where(f => f.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Select(f => new PoCompletion(f.Name, f.Name, f.Description)).ToList();
			return items.Count == 0 ? null : (items, start);
		}

		var open = Regex.Match(prefix, @"^\s*msgstr(\[\d+\])?\s+""(?:[^""\\]|\\.)*$");
		if (open.Success) {
			var catalog = PoCatalog.Parse(text);
			var lineNumber = text.Take(offset).Count(c => c == '\n') + 1;
			if (EntryAt(catalog, lineNumber, Lines(text).Count()) is { IsHeader: false } found && found.Entry.MsgId.Length > 0) {
				var source = found.Entry.MsgIdPlural != null && open.Groups[1].Success && open.Groups[1].Value != "[0]" ? found.Entry.MsgIdPlural : found.Entry.MsgId;
				var start = lineStart + prefix.LastIndexOf('"') + 1;
				return (new[] { new PoCompletion("Source text", Escape(source), "Starts the translation from the source text.") }, start);
			}
			return null;
		}

		if (!Regex.IsMatch(trimmed, @"^[a-z_\[\]0-9]*$") || prefix.Length != trimmed.Length) return null;
		var previous = PreviousKeyword(text, lineStart);
		string[] keywords = previous switch {
			"msgctxt" => new[] { "msgid" },
			"msgid" => new[] { "msgid_plural", "msgstr" },
			"msgid_plural" => new[] { "msgstr[0]", "msgstr[1]" },
			_ => new[] { "msgid", "msgctxt" }
		};
		var matching = keywords.Where(k => k.StartsWith(trimmed, StringComparison.Ordinal)).Select(k => new PoCompletion(k, k, "msgstr[0]".Equals(k) ? "The singular form." : "gettext keyword")).ToList();
		return matching.Count == 0 ? null : (matching, lineStart);
	}

	/// <summary>The last keyword before the line starting at <paramref name="lineStart"/> within the same entry, or "" at an entry boundary.</summary>
	static string PreviousKeyword(string text, int lineStart)
	{
		var lines = text.Substring(0, lineStart).Replace("\r", "").Split('\n').ToList();
		if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
		for (var i = lines.Count - 1; i >= 0; i--) {
			var line = lines[i].Trim();
			if (line.Length == 0) return "";
			if (line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("\"", StringComparison.Ordinal)) continue;
			var word = line.Split(' ')[0];
			return word.StartsWith("msgstr", StringComparison.Ordinal) ? "msgstr" : word;
		}
		return "";
	}

	static string Escape(string value)
		=> value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r");

	/// <summary>The <c>file:line</c> reference under <paramref name="offset"/> in a <c>#:</c> line, or null.</summary>
	public static (string Path, int Line)? ReferenceAt(string text, int offset)
	{
		offset = Math.Max(0, Math.Min(offset, text.Length));
		var lineStart = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
		var lineEnd = text.IndexOf('\n', offset);
		if (lineEnd < 0) lineEnd = text.Length;
		var line = text.Substring(lineStart, lineEnd - lineStart).TrimEnd('\r');
		if (!line.StartsWith("#:", StringComparison.Ordinal)) return null;
		foreach (Match token in Regex.Matches(line, @"\S+:\d+")) {
			var start = lineStart + token.Index;
			if (offset < start || offset > start + token.Length) continue;
			var colon = token.Value.LastIndexOf(':');
			return (token.Value.Substring(0, colon), int.Parse(token.Value.Substring(colon + 1)));
		}
		return null;
	}

	/// <summary>The outline: the header, then one node per live message named by its source text.</summary>
	public static IReadOnlyList<(string Name, string Kind, int Line)> Outline(string text)
	{
		var catalog = PoCatalog.Parse(text);
		var result = new List<(string, string, int)>();
		if (catalog.Header.Line > 0) result.Add(("Header", "header", Math.Max(1, catalog.Header.MsgIdLine > 0 ? catalog.Header.MsgIdLine : catalog.Header.Line)));
		foreach (var entry in catalog.Entries.Where(e => !e.Obsolete && e.Line > 0)) {
			var name = Shorten(entry.MsgId.Length == 0 ? "(empty)" : entry.MsgId, 60);
			result.Add((entry.Context == null ? name : entry.Context + ": " + name, entry.IsTranslated ? (entry.IsFuzzy ? "fuzzy" : "translated") : "untranslated", Math.Max(1, entry.MsgIdLine > 0 ? entry.MsgIdLine : entry.Line)));
		}
		return result;
	}
}
