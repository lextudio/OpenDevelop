// Where a toolbox item dropped onto markup source goes. Framework-neutral: it knows XML, not GTK or
// MewUI. The caller says which elements are containers; the planner finds the innermost one holding
// the drop point and the position among that container's children - so a drop never lands inside a
// start tag, an attribute value or a leaf's text, and a framework's own editor (GtkBuilder's
// <child><object/></child>, a XAML element) can do the actual insertion.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace ICSharpCode.SharpDevelop.Designer.Shell;

/// <summary>An element of parsed markup, with the character offsets of its tags.</summary>
public sealed class XmlMarkupElement
{
	internal XmlMarkupElement(string name, IReadOnlyDictionary<string, string> attributes, int start, XmlMarkupElement? parent)
	{
		Name = name; Attributes = attributes; Start = start; Parent = parent;
	}

	/// <summary>The local name ("object", "StackPanel").</summary>
	public string Name { get; }
	public IReadOnlyDictionary<string, string> Attributes { get; }
	public XmlMarkupElement? Parent { get; }
	public IReadOnlyList<XmlMarkupElement> Children => children;
	internal readonly List<XmlMarkupElement> children = new();
	/// <summary>Offset of the start tag's '&lt;'.</summary>
	public int Start { get; }
	/// <summary>Offset just past the start tag's '&gt;'.</summary>
	public int StartTagEnd { get; internal set; }
	/// <summary>Offset just past the element - its end tag, or the start tag of an empty element.</summary>
	public int End { get; internal set; }
	public bool IsEmpty { get; internal set; }

	public string? Attribute(string name) => Attributes.TryGetValue(name, out var value) ? value : null;
}

/// <summary>A planned insertion: the new element becomes child <see cref="ChildIndex"/> of
/// <see cref="Container"/>, and as text it goes at <see cref="Offset"/> - always just after the start
/// tag or just after a sibling, never in the middle of one.</summary>
public sealed record XmlInsertionPoint(XmlMarkupElement Container, int ChildIndex, int Offset);

public static class XmlToolboxDropPlanner
{
	/// <summary>The document's root element, or null when the text is not well-formed - a drop is
	/// then refused rather than guessed at.</summary>
	public static XmlMarkupElement? Parse(string text)
	{
		if (string.IsNullOrEmpty(text)) return null;
		var lineStarts = LineStarts(text);
		int OffsetOf(IXmlLineInfo info, int prefix) => lineStarts[info.LineNumber - 1] + info.LinePosition - 1 - prefix;
		XmlMarkupElement? root = null, current = null;
		try {
			using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
			var info = (IXmlLineInfo)reader;
			while (reader.Read()) {
				if (reader.NodeType == XmlNodeType.Element) {
					// LinePosition is the name's column, one past the '<'.
					var start = OffsetOf(info, 1);
					var empty = reader.IsEmptyElement;
					var name = reader.LocalName;
					var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
					if (reader.MoveToFirstAttribute()) {
						do attributes[reader.LocalName] = reader.Value; while (reader.MoveToNextAttribute());
						reader.MoveToElement();
					}
					var element = new XmlMarkupElement(name, attributes, start, current) { StartTagEnd = TagEnd(text, start), IsEmpty = empty };
					if (current == null) root ??= element; else current.children.Add(element);
					if (empty) element.End = element.StartTagEnd; else current = element;
				} else if (reader.NodeType == XmlNodeType.EndElement && current != null) {
					// One past "</".
					current.End = TagEnd(text, OffsetOf(info, 2));
					current = current.Parent;
				}
			}
		} catch (XmlException) {
			return null;
		}
		return root;
	}

	/// <summary>
	/// Where a drop at <paramref name="offset"/> goes: the innermost element holding it for which
	/// <paramref name="accepts"/> is true. A drop inside a child goes after that child, and a drop on a
	/// start tag goes before that element - so dropping onto a line inserts beside what is on it.
	/// </summary>
	public static XmlInsertionPoint? Plan(string text, int offset, Func<XmlMarkupElement, bool> accepts)
	{
		var root = Parse(text);
		if (root == null || accepts == null) return null;
		offset = Math.Max(0, Math.Min(offset, text.Length));
		// The chain of elements whose content holds the offset, outermost first. A start tag is not
		// content: a drop on one belongs to the parent, before that element.
		var chain = new List<XmlMarkupElement>();
		for (var element = Contains(root, offset) ? root : null; element != null;) {
			chain.Add(element);
			element = element.Children.FirstOrDefault(child => Contains(child, offset) || OnStartTag(child, offset));
			if (element != null && OnStartTag(element, offset)) break;
		}
		for (var i = chain.Count - 1; i >= 0; i--) {
			var container = chain[i];
			if (!accepts(container)) continue;
			var children = container.Children;
			int index;
			var onPath = children.FirstOrDefault(child => Contains(child, offset) || OnStartTag(child, offset) || OnEmptyElement(child, offset));
			if (onPath != null) {
				var at = IndexOf(children, onPath);
				index = BeforeChild(onPath, offset) ? at : at + 1;
			} else {
				index = children.Count(child => child.End <= offset);
			}
			return new XmlInsertionPoint(container, index, index == 0 ? container.StartTagEnd : children[index - 1].End);
		}
		return null;
	}

	/// <summary>The text that inserts <paramref name="markup"/> at <paramref name="point"/> on a line of
	/// its own, indented like its siblings (or one step in from its container when it has none).</summary>
	public static (int Offset, string Text) InsertElement(string text, XmlInsertionPoint point, string markup)
	{
		var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var children = point.Container.Children;
		var indent = children.Count > 0 ? IndentAt(text, children[0].Start) : IndentAt(text, point.Container.Start) + IndentUnit(text);
		var insertion = newline + indent + markup;
		// A container with nothing in it yet has its end tag on the same line as its start tag
		// (<StackPanel></StackPanel>); keep the end tag on its own line too.
		if (children.Count == 0 && !point.Container.IsEmpty && LineOf(text, point.Container.StartTagEnd) == LineOf(text, point.Container.End))
			insertion += newline + IndentAt(text, point.Container.Start);
		return (point.Offset, insertion);
	}

	static bool Contains(XmlMarkupElement element, int offset) => !element.IsEmpty && offset >= element.StartTagEnd && offset < element.End;
	/// <summary>On the start tag of an element that has content. An empty element is all start tag,
	/// and a drop on it means "next to this", so it counts as a drop on a child (after it).</summary>
	static bool OnStartTag(XmlMarkupElement element, int offset) => !element.IsEmpty && offset >= element.Start && offset < element.StartTagEnd;
	/// <summary>Whether a drop inside <paramref name="child"/> means "before it": on its start tag, or
	/// on the start tag of what it merely wraps - GtkBuilder's &lt;child&gt;&lt;object ...&gt;, where
	/// the line a user drops on is the &lt;object&gt; one.</summary>
	static bool BeforeChild(XmlMarkupElement child, int offset)
	{
		for (var element = child; element != null; element = element.Children.Count == 1 ? element.Children[0] : null)
			if (OnStartTag(element, offset)) return true;
		return false;
	}
	static bool OnEmptyElement(XmlMarkupElement element, int offset) => element.IsEmpty && offset >= element.Start && offset < element.End;
	static int IndexOf(IReadOnlyList<XmlMarkupElement> list, XmlMarkupElement item) { for (var i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], item)) return i; return -1; }

	/// <summary>Offset just past the '&gt;' of the tag starting at <paramref name="start"/>, ignoring
	/// any '&gt;' inside a quoted attribute value.</summary>
	static int TagEnd(string text, int start)
	{
		var quote = '\0';
		for (var i = start; i < text.Length; i++) {
			var c = text[i];
			if (quote != '\0') { if (c == quote) quote = '\0'; }
			else if (c is '"' or '\'') quote = c;
			else if (c == '>') return i + 1;
		}
		return text.Length;
	}

	static int[] LineStarts(string text)
	{
		var starts = new List<int> { 0 };
		for (var i = 0; i < text.Length; i++) {
			if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { starts.Add(i + 2); i++; }
			else if (text[i] is '\n' or '\r') starts.Add(i + 1);
		}
		return starts.ToArray();
	}

	static int LineOf(string text, int offset) { var line = 0; for (var i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') line++; return line; }

	static string IndentAt(string text, int offset)
	{
		var lineStart = offset;
		while (lineStart > 0 && text[lineStart - 1] is not ('\n' or '\r')) lineStart--;
		var end = lineStart;
		while (end < text.Length && text[end] is ' ' or '\t') end++;
		return text.Substring(lineStart, end - lineStart);
	}

	static string IndentUnit(string text) => text.Contains("\n\t", StringComparison.Ordinal) ? "\t" : "  ";
}
