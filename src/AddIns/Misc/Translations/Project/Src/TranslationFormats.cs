using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ICSharpCode.Translations;

/// <summary>
/// How one translation file maps to the translator's entries and back: <c>.po</c> is a catalogue already, while a culture
/// <c>.resx</c> is translated against its neutral <c>.resx</c> and keyed by resource name. The translator model and view see
/// only <see cref="PoEntry"/> rows, so both formats share the grid, the filters and the progress line.
/// </summary>
public interface ITranslationFormat
{
	/// <summary>The file text as a catalogue of entries (source, translation, comments, flags).</summary>
	PoCatalog Parse(string text);
	/// <summary>The file text for <paramref name="catalog"/>, which <see cref="Parse"/> produced and the editor changed.</summary>
	string Write(PoCatalog catalog, string originalText);
	/// <summary>Whether the format records a fuzzy state; the view hides the Fuzzy column when it does not.</summary>
	bool SupportsFuzzy { get; }
	/// <summary>What an entry's <see cref="PoEntry.Context"/> is called in the view: "Context" for gettext, "Name" for a resource key.</summary>
	string ContextHeader { get; }
}

/// <summary>gettext: the file is the catalogue.</summary>
public sealed class PoFormat : ITranslationFormat
{
	public PoCatalog Parse(string text) => PoCatalog.Parse(text);
	public string Write(PoCatalog catalog, string originalText) => catalog.Write();
	public bool SupportsFuzzy => true;
	public string ContextHeader => "Context";
}

/// <summary>
/// A culture resource file (<c>Form1.fr.resx</c>) translated against its neutral file (<c>Form1.resx</c>). Only string
/// resources take part: entries with a <c>type</c> or <c>mimetype</c> (a Point, an image, a boolean) and the designer's
/// <c>&gt;&gt;name.Type</c> bookkeeping are not text a translator touches. An entry is keyed by its resource name
/// (the entry's <see cref="PoEntry.Context"/>), shows the neutral value as its source and the neutral
/// <c>&lt;comment&gt;</c> as its note, and is untranslated until the culture file has a value for the name. A name the culture
/// file has but the neutral file no longer does is kept as an obsolete entry. Writing puts the translations back into the
/// culture file's own XML (headers, non-string resources and layout untouched): a translated name gets or updates its
/// <c>&lt;data&gt;</c>, an emptied one loses it.
/// </summary>
public sealed class ResxFormat : ITranslationFormat
{
	readonly string neutralText;

	public ResxFormat(string neutralText) { this.neutralText = neutralText ?? ""; }

	public bool SupportsFuzzy => false;
	public string ContextHeader => "Name";

	/// <summary>The culture code of <c>Form1.fr.resx</c> ("fr", "zh-Hans"), or null when the name is not a culture resource.</summary>
	public static string? CultureOf(string path)
	{
		if (!string.Equals(Path.GetExtension(path), ".resx", StringComparison.OrdinalIgnoreCase)) return null;
		var culture = Path.GetExtension(Path.GetFileNameWithoutExtension(path));
		if (culture.Length < 2) return null;
		culture = culture.Substring(1);
		// predefinedOnly: otherwise any word ("Designer") would be accepted as a custom culture.
		try { return CultureInfo.GetCultureInfo(culture, true).Name.Length > 0 ? culture : null; }
		catch (CultureNotFoundException) { return null; }
	}

	/// <summary>The neutral file of a culture resource: <c>Form1.fr.resx</c> -&gt; <c>Form1.resx</c> in the same folder.</summary>
	public static string NeutralPathOf(string culturePath)
		=> Path.Combine(Path.GetDirectoryName(culturePath) ?? "", Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(culturePath)) + ".resx");

	/// <summary>The translatable strings of a resource file: name -&gt; (value, comment), in file order.</summary>
	public static IReadOnlyList<(string Name, string Value, string Comment)> Strings(string resxText)
	{
		var result = new List<(string, string, string)>();
		XDocument document;
		try { document = XDocument.Parse(resxText); }
		catch (XmlException) { return result; }
		foreach (var data in document.Root?.Elements("data") ?? Enumerable.Empty<XElement>()) {
			var name = (string?)data.Attribute("name");
			if (string.IsNullOrEmpty(name) || name!.StartsWith(">>", StringComparison.Ordinal)) continue;
			if (data.Attribute("type") != null || data.Attribute("mimetype") != null) continue;
			result.Add((name, (string?)data.Element("value") ?? "", (string?)data.Element("comment") ?? ""));
		}
		return result;
	}

	public PoCatalog Parse(string cultureText)
	{
		var catalog = new PoCatalog();
		var translated = Strings(cultureText).ToDictionary(s => s.Name, s => s, StringComparer.Ordinal);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var (name, value, comment) in Strings(neutralText)) {
			if (!seen.Add(name)) continue;
			var entry = new PoEntry { Context = name, MsgId = value };
			if (comment.Length > 0) entry.ExtractedComments.Add(comment);
			if (translated.TryGetValue(name, out var existing)) {
				entry.MsgStr = existing.Value;
				if (existing.Comment.Length > 0) entry.TranslatorComments.AddRange(existing.Comment.Replace("\r", "").Split('\n'));
			}
			catalog.Entries.Add(entry);
		}
		// A translation of a name the neutral file lost: kept, shown as obsolete, never written away.
		foreach (var (name, value, comment) in translated.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
			if (!seen.Contains(name)) catalog.Entries.Add(new PoEntry { Context = name, MsgId = name, MsgStr = value, Obsolete = true });
		return catalog;
	}

	public string Write(PoCatalog catalog, string originalText)
	{
		XDocument document;
		try { document = XDocument.Parse(originalText, LoadOptions.PreserveWhitespace); }
		catch (XmlException) { document = XDocument.Parse(EmptyResx, LoadOptions.PreserveWhitespace); }
		var root = document.Root!;
		var existing = root.Elements("data").Where(d => d.Attribute("type") == null && d.Attribute("mimetype") == null && (string?)d.Attribute("name") is { Length: > 0 })
			.GroupBy(d => (string)d.Attribute("name")!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
		foreach (var entry in catalog.Entries.Where(e => !e.Obsolete)) {
			var name = entry.Context!;
			existing.TryGetValue(name, out var data);
			if (entry.MsgStr.Length == 0) {
				RemoveWithIndent(data);
				continue;
			}
			if (data == null) {
				data = new XElement("data", new XAttribute("name", name), new XAttribute(XNamespace.Xml + "space", "preserve"), new XText("\n    "), new XElement("value"), new XText("\n  "));
				AppendWithIndent(root, data);
			}
			var value = data.Element("value");
			if (value == null) { value = new XElement("value"); data.AddFirst(new XText("\n    "), value); }
			value.Value = entry.MsgStr;
			var comment = string.Join("\n", entry.TranslatorComments);
			var commentElement = data.Element("comment");
			if (comment.Length == 0) commentElement?.Remove();
			else if (commentElement != null) commentElement.Value = comment;
			else value.AddAfterSelf(new XText("\n    "), new XElement("comment", comment));
		}
		var text = root.ToString(SaveOptions.DisableFormatting);
		var declaration = document.Declaration ?? new XDeclaration("1.0", "utf-8", null);
		return declaration + "\n" + text + (originalText.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "");
	}

	const string EmptyResx = "<root>\n  <resheader name=\"resmimetype\">\n    <value>text/microsoft-resx</value>\n  </resheader>\n  <resheader name=\"version\">\n    <value>2.0</value>\n  </resheader>\n  <resheader name=\"reader\">\n    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>\n  </resheader>\n  <resheader name=\"writer\">\n    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>\n  </resheader>\n</root>\n";

	/// <summary>Appends <paramref name="data"/> after the last element, on its own indented line.</summary>
	static void AppendWithIndent(XElement root, XElement data)
	{
		if (root.LastNode is XText { Value: var tail } && tail.Trim().Length == 0) { root.LastNode.Remove(); root.Add(new XText("\n  "), data, new XText("\n")); }
		else root.Add(new XText("\n  "), data, new XText("\n"));
	}

	/// <summary>Removes an element together with the whitespace line that preceded it.</summary>
	static void RemoveWithIndent(XElement? element)
	{
		if (element == null) return;
		if (element.PreviousNode is XText { Value: var lead } previous && lead.Trim().Length == 0) previous.Remove();
		element.Remove();
	}
}
