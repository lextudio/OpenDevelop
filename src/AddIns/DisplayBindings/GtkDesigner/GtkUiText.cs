using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.GtkDesigner;

/// <summary>One attribute of a GtkBuilder element, with the exact spans the editor needs for
/// completion, hover and rename. <see cref="ValueStart"/>/<see cref="ValueEnd"/> exclude the
/// quotes; a value that is still being typed has <see cref="ValueEnd"/> at the end of the text
/// (or at the caret) and <see cref="Unterminated"/> set.</summary>
public sealed class GtkUiAttribute
{
	public string Name { get; set; } = "";
	public int NameStart { get; set; }
	public int NameEnd { get; set; }
	public string Value { get; set; } = "";
	public int ValueStart { get; set; }
	public int ValueEnd { get; set; }
	/// <summary>False for a bare attribute name with no '=' yet.</summary>
	public bool HasValue { get; set; }
	public bool Unterminated { get; set; }
}

/// <summary>One element of a GtkBuilder document, with its tag and content spans. The parser is
/// deliberately forgiving: while a tag is being typed the last element is incomplete rather than
/// absent, which is what makes completion inside a half-written tag possible.</summary>
public sealed class GtkUiElement
{
	public string Name { get; set; } = "";
	public int NameStart { get; set; }
	public int NameEnd { get; set; }

	/// <summary>Offset of the '&lt;' that opens the start tag.</summary>
	public int Start { get; set; }
	/// <summary>Offset just after the start tag's '&gt;' (or the end of the text when incomplete).</summary>
	public int OpenEnd { get; set; }
	/// <summary>Offset of the '&lt;' that opens the end tag (== <see cref="OpenEnd"/> when
	/// self-closing or incomplete).</summary>
	public int ContentEnd { get; set; }
	/// <summary>Offset just after the end tag's '&gt;' (== <see cref="OpenEnd"/> when self-closing).</summary>
	public int End { get; set; }
	public bool SelfClosing { get; set; }
	/// <summary>The start tag runs into the end of the text (the document is being typed).</summary>
	public bool Incomplete { get; set; }
	public int Depth { get; set; }
	public GtkUiElement? Parent { get; set; }
	public List<GtkUiElement> Children { get; } = new();
	public List<GtkUiAttribute> Attributes { get; } = new();

	public GtkUiAttribute? Attribute(string name)
		=> Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

	public string? Value(string name) => Attribute(name)?.Value;

	/// <summary>The element's own text, without child markup.</summary>
	public string Content(string text)
		=> ContentEnd > OpenEnd ? text.Substring(OpenEnd, ContentEnd - OpenEnd) : "";

	public bool HasElementChildren => Children.Count > 0;

	public IEnumerable<GtkUiElement> SelfAndDescendants()
	{
		yield return this;
		foreach (var child in Children)
			foreach (var descendant in child.SelfAndDescendants())
				yield return descendant;
	}

	/// <summary>The nearest ancestor (or this element) with the given tag name.</summary>
	public GtkUiElement? Nearest(string name)
	{
		for (var element = this; element != null; element = element.Parent)
			if (string.Equals(element.Name, name, StringComparison.Ordinal))
				return element;
		return null;
	}

	/// <summary>The nearest ancestor (or this element) that declares a class: an &lt;object&gt; or a
	/// template.</summary>
	public GtkUiElement? NearestObject()
	{
		for (var element = this; element != null; element = element.Parent)
			if (element.Name is "object" or "template")
				return element;
		return null;
	}

	public override string ToString() => "<" + Name + ">";
}

public readonly record struct GtkUiSyntaxError(int Offset, string Message);

/// <summary>
/// A GtkBuilder (.ui) document parsed with exact character offsets, for the IDE-side language
/// service. Unlike <see cref="GtkUiDocumentEditor"/>'s XDocument this keeps the source positions
/// completion, hover and rename need, and it never throws: malformed or half-typed XML still
/// yields the elements and attributes that could be read, plus a list of errors.
/// </summary>
public sealed class GtkUiDocument
{
	public string Text { get; }
	public List<GtkUiElement> Roots { get; } = new();
	public List<GtkUiElement> All { get; } = new();
	public List<GtkUiSyntaxError> Errors { get; } = new();

	GtkUiDocument(string text) => Text = text;

	public static GtkUiDocument Parse(string text)
	{
		var document = new GtkUiDocument(text ?? "");
		var source = document.Text;
		var stack = new List<GtkUiElement>();
		var position = 0;

		while (position < source.Length)
		{
			var open = source.IndexOf('<', position);
			if (open < 0)
				break;

			if (StartsWith(source, open, "<!--")) {
				var close = source.IndexOf("-->", open + 4, StringComparison.Ordinal);
				if (close < 0) { document.Errors.Add(new GtkUiSyntaxError(open, "The comment is not closed.")); break; }
				position = close + 3;
				continue;
			}
			if (StartsWith(source, open, "<![CDATA[")) {
				var close = source.IndexOf("]]>", open + 9, StringComparison.Ordinal);
				if (close < 0) { document.Errors.Add(new GtkUiSyntaxError(open, "The CDATA section is not closed.")); break; }
				position = close + 3;
				continue;
			}
			if (StartsWith(source, open, "<?")) {
				var close = source.IndexOf("?>", open + 2, StringComparison.Ordinal);
				if (close < 0) { document.Errors.Add(new GtkUiSyntaxError(open, "The processing instruction is not closed.")); break; }
				position = close + 2;
				continue;
			}
			if (StartsWith(source, open, "<!")) {
				var close = source.IndexOf('>', open + 2);
				if (close < 0) { document.Errors.Add(new GtkUiSyntaxError(open, "The declaration is not closed.")); break; }
				position = close + 1;
				continue;
			}
			if (StartsWith(source, open, "</")) {
				position = document.ParseEndTag(source, open, stack);
				continue;
			}

			var element = document.ParseStartTag(source, open, stack);
			document.All.Add(element);
			if (stack.Count > 0) {
				element.Parent = stack[^1];
				element.Parent.Children.Add(element);
			} else {
				document.Roots.Add(element);
			}
			if (!element.SelfClosing && !element.Incomplete)
				stack.Add(element);
			position = element.OpenEnd;
		}

		foreach (var element in stack) {
			element.ContentEnd = source.Length;
			element.End = source.Length;
			// A tag that runs into the end of the text is being typed; only a completed start tag
			// that never closes is an error.
			if (!element.Incomplete)
				document.Errors.Add(new GtkUiSyntaxError(element.Start, "The element <" + element.Name + "> is not closed."));
		}

		document.Errors.Sort((left, right) => left.Offset.CompareTo(right.Offset));
		return document;
	}

	static bool StartsWith(string text, int offset, string value)
		=> offset + value.Length <= text.Length && string.CompareOrdinal(text, offset, value, 0, value.Length) == 0;

	int ParseEndTag(string text, int open, List<GtkUiElement> stack)
	{
		var position = open + 2;
		var nameStart = position;
		while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '>')
			position++;
		var name = text.Substring(nameStart, position - nameStart);
		var close = text.IndexOf('>', position);
		if (close < 0) {
			Errors.Add(new GtkUiSyntaxError(open, "The end tag </" + name + "> is not closed."));
			return text.Length;
		}

		var match = -1;
		for (var index = stack.Count - 1; index >= 0; index--) {
			if (string.Equals(stack[index].Name, name, StringComparison.Ordinal)) { match = index; break; }
		}
		if (match < 0) {
			Errors.Add(new GtkUiSyntaxError(open, "The end tag </" + name + "> has no matching start tag."));
		} else {
			for (var index = stack.Count - 1; index > match; index--) {
				var unclosed = stack[index];
				unclosed.ContentEnd = open;
				unclosed.End = open;
				Errors.Add(new GtkUiSyntaxError(unclosed.Start, "The element <" + unclosed.Name + "> is not closed before </" + name + ">."));
			}
			var element = stack[match];
			element.ContentEnd = open;
			element.End = close + 1;
			stack.RemoveRange(match, stack.Count - match);
		}
		return close + 1;
	}

	GtkUiElement ParseStartTag(string text, int open, List<GtkUiElement> stack)
	{
		var element = new GtkUiElement { Start = open, Depth = stack.Count };
		var position = open + 1;
		var nameStart = position;
		while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '>' && text[position] != '/')
			position++;
		element.NameStart = nameStart;
		element.NameEnd = position;
		element.Name = text.Substring(nameStart, position - nameStart);
		if (element.Name.Length == 0)
			Errors.Add(new GtkUiSyntaxError(open, "Expected an element name after '<'."));

		while (true) {
			while (position < text.Length && char.IsWhiteSpace(text[position]))
				position++;
			if (position >= text.Length) {
				element.Incomplete = true;
				element.OpenEnd = element.ContentEnd = element.End = text.Length;
				return element;
			}
			if (text[position] == '>') {
				element.OpenEnd = element.ContentEnd = position + 1;
				element.End = position + 1;
				return element;
			}
			if (text[position] == '/') {
				if (position + 1 < text.Length && text[position + 1] == '>') {
					element.SelfClosing = true;
					element.OpenEnd = element.ContentEnd = element.End = position + 2;
					return element;
				}
				position++;
				continue;
			}

			var attribute = new GtkUiAttribute();
			var attributeStart = position;
			while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '=' && text[position] != '>' && text[position] != '/')
				position++;
			attribute.NameStart = attributeStart;
			attribute.NameEnd = position;
			attribute.Name = text.Substring(attributeStart, position - attributeStart);
			attribute.ValueStart = attribute.ValueEnd = position;

			var afterName = position;
			while (afterName < text.Length && char.IsWhiteSpace(text[afterName]))
				afterName++;
			if (afterName < text.Length && text[afterName] == '=') {
				attribute.HasValue = true;
				position = afterName + 1;
				while (position < text.Length && char.IsWhiteSpace(text[position]))
					position++;
				if (position < text.Length && (text[position] == '"' || text[position] == '\'')) {
					var quote = text[position];
					position++;
					attribute.ValueStart = position;
					var close = text.IndexOf(quote, position);
					if (close < 0) {
						attribute.ValueEnd = text.Length;
						attribute.Unterminated = true;
						position = text.Length;
					} else {
						attribute.ValueEnd = close;
						position = close + 1;
					}
					attribute.Value = Decode(text.Substring(attribute.ValueStart, attribute.ValueEnd - attribute.ValueStart));
				} else {
					// Unquoted values are not valid XML but are common while a value is being typed.
					attribute.ValueStart = position;
					while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '>')
						position++;
					attribute.ValueEnd = position;
					attribute.Value = text.Substring(attribute.ValueStart, attribute.ValueEnd - attribute.ValueStart);
				}
			}
			if (attribute.Name.Length > 0)
				element.Attributes.Add(attribute);
			if (position >= text.Length) {
				element.Incomplete = true;
				element.OpenEnd = element.ContentEnd = element.End = text.Length;
				return element;
			}
		}
	}

	/// <summary>The XML entity subset a GtkBuilder file uses in attribute values.</summary>
	public static string Decode(string value)
	{
		if (value.IndexOf('&') < 0)
			return value;
		return value
			.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
			.Replace("&apos;", "'").Replace("&amp;", "&");
	}

	/// <summary>The deepest element whose whole tag (start tag through end tag) contains
	/// <paramref name="offset"/>.</summary>
	public GtkUiElement? ElementAt(int offset)
	{
		GtkUiElement? best = null;
		foreach (var element in All) {
			if (offset < element.Start || offset > element.End)
				continue;
			if (best == null || element.Depth >= best.Depth)
				best = element;
		}
		return best;
	}

	/// <summary>The deepest element whose start tag contains <paramref name="offset"/>. The offset
	/// just after a completed tag's '&gt;' is content, not tag, but an unfinished tag at the end of
	/// the text still owns the caret so completion can run inside it.</summary>
	public GtkUiElement? TagAt(int offset)
	{
		GtkUiElement? best = null;
		foreach (var element in All) {
			if (offset < element.Start || offset > element.OpenEnd)
				continue;
			if (!element.Incomplete && offset == element.OpenEnd)
				continue;
			if (best == null || element.Depth >= best.Depth)
				best = element;
		}
		return best;
	}

	/// <summary>The deepest element whose content (between the tags) contains
	/// <paramref name="offset"/>.</summary>
	public GtkUiElement? ContentAt(int offset)
	{
		GtkUiElement? best = null;
		foreach (var element in All) {
			if (element.SelfClosing || offset < element.OpenEnd || offset > element.ContentEnd)
				continue;
			if (best == null || element.Depth >= best.Depth)
				best = element;
		}
		return best;
	}

	public GtkUiElement? FindById(string id)
		=> All.FirstOrDefault(e => string.Equals(e.Value("id"), id, StringComparison.Ordinal));

	public IEnumerable<string> Ids()
		=> All.Select(e => e.Value("id")).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!);

	/// <summary>Every attribute that holds an object id, in document order: the <c>id</c>
	/// declarations themselves plus <c>&lt;lookup name&gt;</c>. Reference-valued property text is
	/// added by the language service, which alone knows the property's GIR type.</summary>
	public IEnumerable<(GtkUiElement Element, GtkUiAttribute Attribute, bool Declaration)> IdOccurrences()
	{
		foreach (var element in All) {
			foreach (var attribute in element.Attributes) {
				if (string.Equals(attribute.Name, "id", StringComparison.Ordinal)) {
					if (attribute.Value.Length > 0)
						yield return (element, attribute, true);
				} else if (attribute.Value.Length > 0 && string.Equals(attribute.Name, "name", StringComparison.Ordinal) && element.Name == "lookup") {
					// <lookup name="other-object"> refers to an id; <link name="section"> does not
					// (it names a GMenu link, not an object).
					yield return (element, attribute, false);
				}
			}
		}
	}
}
