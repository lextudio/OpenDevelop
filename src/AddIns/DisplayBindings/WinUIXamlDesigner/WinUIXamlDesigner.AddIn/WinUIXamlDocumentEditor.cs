using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>
/// The designer's edit model. Every operation is expressed as a mutation of the XAML *source*
/// document and then re-serialized, per the technote's rule that the runtime visual tree is never
/// the document model: an edit must survive re-parsing, be undoable, and be reproducible by an
/// out-of-process renderer. Nothing here touches ProGPU or <c>Microsoft.UI.Xaml</c>.
/// </summary>
sealed class WinUIXamlDocumentEditor
{
	/// <summary>x:Name lives in the XAML language namespace, not the presentation namespace.</summary>
	public static readonly XName NameDirective =
		XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");

	readonly List<string> undoStack = new();
	readonly List<string> redoStack = new();

	public XDocument Document { get; private set; }
	public string Text { get; private set; } = "";

	public bool CanUndo => undoStack.Count > 0;
	public bool CanRedo => redoStack.Count > 0;

	/// <summary>Replaces the document wholesale (file load / external edit) and drops the history.</summary>
	public bool Reset(string text, out string error)
	{
		error = null;
		try {
			Document = XDocument.Parse(text ?? "", LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
			Text = text ?? "";
			undoStack.Clear();
			redoStack.Clear();
			return true;
		} catch (Exception exception) {
			Document = null;
			Text = text ?? "";
			error = exception.Message;
			return false;
		}
	}

	public XElement FindElement(string name)
	{
		if (Document?.Root == null || string.IsNullOrEmpty(name))
			return null;
		return Document.Root.DescendantsAndSelf()
			.FirstOrDefault(e => string.Equals((string)e.Attribute(NameDirective), name, StringComparison.Ordinal));
	}

	public IReadOnlyList<string> ElementNames() =>
		Document?.Root == null
			? Array.Empty<string>()
			: Document.Root.DescendantsAndSelf()
				.Select(e => (string)e.Attribute(NameDirective))
				.Where(n => !string.IsNullOrEmpty(n))
				.ToList();

	/// <summary>
	/// Inserts a new standard control as the last child of <paramref name="container"/>, giving it
	/// a document-unique x:Name so that selection, Properties and tests have a stable handle on it.
	/// </summary>
	public XElement Insert(string controlName, XElement container)
	{
		if (Document?.Root == null)
			throw new InvalidOperationException("The document has no root to insert into.");
		container ??= Document.Root;

		BeginChange();
		// The new element inherits the root's default namespace, so it serializes as <Button .../>
		// rather than carrying its own xmlns - the same shape a hand-written page has.
		var element = new XElement(Document.Root.GetDefaultNamespace() + controlName);
		element.SetAttributeValue(NameDirective, UniqueName(controlName));
		AddDefaultContent(element, controlName);
		container.Add(element);
		CommitChange();
		return element;
	}

	/// <summary>
	/// Gives an inserted control a meaningful default so the design surface shows something
	/// real instead of an empty shell: Content for button-like controls, Text for
	/// text-bearing ones. Panels and media controls stay empty on purpose.
	/// </summary>
	static void AddDefaultContent(XElement element, string controlName)
	{
		switch (controlName)
		{
			case "TextBlock":
			case "TextBox":
				element.SetAttributeValue("Text", controlName);
				break;
			case "Button":
			case "CheckBox":
			case "HyperlinkButton":
			case "RadioButton":
			case "ToggleSwitch":
				element.SetAttributeValue("Content", controlName);
				break;
		}
	}

	public void SetAttribute(XElement element, XName attribute, string value)
	{
		if (element == null)
			throw new ArgumentNullException(nameof(element));
		BeginChange();
		if (string.IsNullOrEmpty(value))
			element.Attribute(attribute)?.Remove();
		else
			element.SetAttributeValue(attribute, value);
		CommitChange();
	}

	public void Remove(XElement element)
	{
		if (element == null)
			throw new ArgumentNullException(nameof(element));
		if (element == Document?.Root)
			throw new InvalidOperationException("The root element cannot be deleted.");
		BeginChange();
		element.Remove();
		CommitChange();
	}

	public bool Undo() => Move(undoStack, redoStack);
	public bool Redo() => Move(redoStack, undoStack);

	bool Move(List<string> from, List<string> to)
	{
		if (from.Count == 0)
			return false;
		var target = from[^1];
		from.RemoveAt(from.Count - 1);
		to.Add(Text);
		// Undo restores text, so the document has to be re-parsed from it rather than kept as a
		// live object graph - that is what guarantees an undone state is a state the parser accepts.
		Document = XDocument.Parse(target, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
		Text = target;
		return true;
	}

	void BeginChange()
	{
		undoStack.Add(Text);
		redoStack.Clear();
	}

	void CommitChange() => Text = Serialize(Document);

	string UniqueName(string controlName)
	{
		var existing = new HashSet<string>(ElementNames(), StringComparer.Ordinal);
		for (var index = 1; ; index++) {
			var candidate = controlName + index;
			if (existing.Add(candidate))
				return candidate;
		}
	}

	/// <summary>Matches the serialization the ProGPU render path already round-trips through.</summary>
	public static string Serialize(XDocument document)
	{
		using var writer = new System.IO.StringWriter();
		document.Save(writer, SaveOptions.DisableFormatting);
		var text = writer.ToString();
		var start = text.IndexOf('<', 1);
		return start < 0 ? text : text.Substring(start);
	}
}
