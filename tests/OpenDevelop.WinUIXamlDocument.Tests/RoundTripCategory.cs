using System.Text.RegularExpressions;
using ICSharpCode.WinUIXamlDesigner;

namespace OpenDevelop.WinUIXamlDocument.Tests;

/// <summary>
/// What an edit-free parse and save of a XAML file loses, apart from line endings (which are reported separately, see
/// <see cref="Analysis.LineEndingsChanged"/>). Each file is put in the first category that fully explains the difference.
/// </summary>
public enum RoundTripCategory
{
	/// <summary>The saved text is the source (ignoring line endings and a leading XML declaration).</summary>
	Identical,
	/// <summary>Attributes that were on separate lines, or separated by several spaces, are joined with single spaces.</summary>
	AttributeWhitespace,
	/// <summary><c>&lt;a/&gt;</c> is written <c>&lt;a /&gt;</c> (a start tag followed at once by its end tag is kept as written).</summary>
	EmptyElementForm,
	/// <summary>Single-quoted attribute values are written double-quoted.</summary>
	QuoteStyle,
	/// <summary>Something else differs (character references, text, comments ...).</summary>
	Other,
	/// <summary>The text is not well-formed XML, so the editor cannot load it at all.</summary>
	NotLoadable,
}

/// <summary>The result of <see cref="RoundTrip.Analyze"/>.</summary>
/// <param name="Category">What differs, ignoring line endings and the XML declaration.</param>
/// <param name="LineEndingsChanged">The saved text uses a different line ending than the source (CRLF to LF or LF to CRLF).</param>
/// <param name="DeclarationDropped">The source began with an XML declaration and the saved text does not.</param>
public sealed record Analysis(RoundTripCategory Category, bool LineEndingsChanged, bool DeclarationDropped);

public static class RoundTrip
{
	/// <summary>The text the editor holds after loading <paramref name="text"/> and saving it with no edit (its own Reset and Serialize).</summary>
	public static string? SaveWithoutEdit(string text)
	{
		var editor = new WinUIXamlDocumentEditor();
		return editor.Reset(text, out _) ? WinUIXamlDocumentEditor.Serialize(editor.Document) : null;
	}

	public static Analysis Analyze(string source)
	{
		var saved = SaveWithoutEdit(source);
		if (saved == null)
			return new Analysis(RoundTripCategory.NotLoadable, false, false);
		bool crlfSource = source.Contains("\r\n", StringComparison.Ordinal);
		bool crlfSaved = saved.Contains("\r\n", StringComparison.Ordinal);
		bool lineEndings = (source.Contains('\n') || saved.Contains('\n')) && crlfSource != crlfSaved;
		bool declaration = Declaration.IsMatch(source.TrimStart('﻿')) && !Declaration.IsMatch(saved);

		// Both sides are normalized the same way, cumulatively, so a file lands in the first category that fully explains it.
		var category = RoundTripCategory.Other;
		if (string.Equals(Normalize(saved, 1), Normalize(source, 1), StringComparison.Ordinal))
			category = RoundTripCategory.Identical;
		else
			for (int level = 2; level <= 4; level++)
				if (string.Equals(Normalize(saved, level), Normalize(source, level), StringComparison.Ordinal)) {
					category = (RoundTripCategory)(level - 1);
					break;
				}
		return new Analysis(category, lineEndings, declaration);
	}

	public static RoundTripCategory Classify(string source) => Analyze(source).Category;

	/// <summary>The text with line endings made LF and a leading declaration removed, for comparisons that must not depend on the platform.</summary>
	public static string Lf(string text) => Normalize(text, 1);

	// A tag, treating a quoted attribute value as opaque (it may contain '>' or whitespace).
	const string Quoted = "(?:[^<>\"']|\"[^\"]*\"|'[^']*')*";
	static readonly Regex Tag = new("<[A-Za-z_]" + Quoted + ">", RegexOptions.Compiled);
	static readonly Regex Declaration = new(@"^\s*<\?xml[^>]*\?>\s*", RegexOptions.Compiled);

	static string Normalize(string text, int level)
	{
		text = text.TrimStart('﻿').Replace("\r\n", "\n").Replace("\r", "\n");
		text = Declaration.Replace(text, "");
		if (level == 1)
			return text;
		return Tag.Replace(text, match => NormalizeTag(match.Value, level));
	}

	static string NormalizeTag(string tag, int level)
	{
		var sb = new System.Text.StringBuilder(tag.Length);
		char quote = '\0';
		bool previousWhitespace = false;
		foreach (char c in tag) {
			if (quote != '\0') {
				sb.Append(c == quote && level >= 4 && quote == '\'' ? '"' : c);
				if (c == quote) quote = '\0';
				previousWhitespace = false;
			} else if (c is '"' or '\'') {
				quote = c;
				sb.Append(c == '\'' && level >= 4 ? '"' : c);
				previousWhitespace = false;
			} else if (char.IsWhiteSpace(c)) {
				if (!previousWhitespace) sb.Append(' ');
				previousWhitespace = true;
			} else {
				sb.Append(c);
				previousWhitespace = false;
			}
		}
		var result = sb.ToString();
		return level >= 3 ? Regex.Replace(result, @"\s*/>$", "/>") : result;
	}
}
