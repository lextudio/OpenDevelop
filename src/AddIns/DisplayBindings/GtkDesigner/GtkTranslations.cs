using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using ICSharpCode.Translations;

namespace ICSharpCode.GtkDesigner;

/// <summary>
/// What the GTK designer contributes to the Translations addin (doc/technotes/gtk-designer.md, "Translations"): the
/// strings a GtkBuilder file marks <c>translatable="yes"</c>. The catalogue, the merge and the commands belong to
/// that addin; this only knows the .ui format. Pure, so the unit tests drive it without the IDE.
/// </summary>
public sealed class GtkTranslatableSource : ITranslatableStringSource
{
	public string Name => "GtkBuilder";
	public bool Handles(string filePath) => filePath.EndsWith(".ui", StringComparison.OrdinalIgnoreCase);
	IReadOnlyList<PoEntry> ITranslatableStringSource.Extract(string text, string referencePath) => GtkTranslations.Extract(text, referencePath);
}

/// <summary>The extraction itself, kept apart so tests and other callers need no source instance.</summary>
public static class GtkTranslations
{
	/// <summary>The translatable strings of one .ui: <c>&lt;property&gt;</c>, menu <c>&lt;attribute&gt;</c> and
	/// string-list <c>&lt;item&gt;</c> elements with <c>translatable="yes"</c> (or "true"), each with its
	/// <c>context</c> as msgctxt, its <c>comments</c> as an extracted comment, and <c>file:line</c> as the reference.</summary>
	public static IReadOnlyList<PoEntry> Extract(string uiText, string referencePath)
	{
		var result = new List<PoEntry>();
		XDocument document;
		try { document = XDocument.Parse(uiText, LoadOptions.SetLineInfo); }
		catch (XmlException) { return result; }
		foreach (var element in document.Descendants()) {
			if (!IsYes((string?)element.Attribute("translatable"))) continue;
			var text = element.Value;
			if (text.Length == 0) continue;
			var entry = new PoEntry { MsgId = text, Context = (string?)element.Attribute("context") };
			var line = ((IXmlLineInfo)element).HasLineInfo() ? ((IXmlLineInfo)element).LineNumber : 0;
			entry.References.Add(line > 0 ? referencePath + ":" + line : referencePath);
			if ((string?)element.Attribute("comments") is { Length: > 0 } comments) entry.ExtractedComments.Add(comments);
			result.Add(entry);
		}
		return result;
	}

	static bool IsYes(string? value) => value is "yes" or "true" or "True" or "1";
}
