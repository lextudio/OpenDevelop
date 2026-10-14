using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
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

	/// <summary>Returns a document-local element path made only from sibling element indices.
	/// Unlike x:Name this is stable for unnamed source objects and never refers to a runtime node.</summary>
	public string GetElementPath(XElement element)
	{
		if (element == null || element.Document != Document || Document?.Root == null)
			return null;
		var indices = new Stack<int>();
		for (var current = element; current != Document.Root; current = current.Parent)
		{
			if (current.Parent == null)
				return null;
			indices.Push(current.Parent.Elements().TakeWhile(sibling => sibling != current).Count());
		}
		return string.Join("/", indices);
	}

	/// <summary>Finds an element previously identified by <see cref="GetElementPath"/>.</summary>
	public XElement FindElementAtPath(string path)
	{
		var current = Document?.Root;
		if (current == null || path == null)
			return null;
		foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			if (!int.TryParse(part, out var index) || index < 0)
				return null;
			current = current.Elements().ElementAtOrDefault(index);
			if (current == null)
				return null;
		}
		return current;
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

	/// <summary>Adds a named MenuFlyout through a source control's ContextFlyout property.
	/// This is source-owned because the component has no visual-tree identity in the preview.</summary>
	public XElement InsertContextFlyout(XElement target)
	{
		if (Document?.Root == null)
			throw new InvalidOperationException("The document has no root to add a component to.");
		if (target == null || target.Document != Document || target.Name.LocalName.Contains('.', StringComparison.Ordinal))
			throw new ArgumentException("A source control is required.", nameof(target));
		if (string.IsNullOrEmpty((string)target.Attribute(NameDirective)))
			throw new InvalidOperationException("The target control needs an x:Name before a ContextFlyout can be added.");
		var propertyName = target.Name.LocalName + ".ContextFlyout";
		if (target.Attribute("ContextFlyout") != null
			|| target.Elements().Any(element => string.Equals(element.Name.LocalName, propertyName, StringComparison.Ordinal)))
			throw new InvalidOperationException("The target control already has a ContextFlyout.");

		BeginChange();
		var flyout = new XElement(Document.Root.GetDefaultNamespace() + "MenuFlyout");
		flyout.SetAttributeValue(NameDirective, UniqueName("MenuFlyout"));
		flyout.Add(new XElement(Document.Root.GetDefaultNamespace() + "MenuFlyoutItem",
			new XAttribute("Text", "Menu item")));
		target.Add(new XElement(target.Name.Namespace + propertyName, flyout));
		CommitChange();
		return flyout;
	}

	/// <summary>Adds a named <c>Flyout</c> through a control's Flyout property. Like a
	/// ContextFlyout, this popup is source-owned and has no preview-tree identity.</summary>
	public XElement InsertFlyout(XElement target)
	{
		return InsertFlyoutBase(target, "Flyout", flyout => flyout.Add(
			new XElement(Document.Root.GetDefaultNamespace() + "TextBlock", new XAttribute("Text", "Flyout content"))));
	}

	/// <summary>Adds a named <c>CommandBarFlyout</c> through a control's Flyout property.
	/// The popup and its command bar are source-owned rather than preview-tree children.</summary>
	public XElement InsertCommandBarFlyout(XElement target)
	{
		return InsertFlyoutBase(target, "CommandBarFlyout", flyout => flyout.Add(
			new XElement(Document.Root.GetDefaultNamespace() + "AppBarButton", new XAttribute("Label", "Command"))));
	}

	XElement InsertFlyoutBase(XElement target, string flyoutType, Action<XElement> addDefaultContent)
	{
		if (Document?.Root == null)
			throw new InvalidOperationException("The document has no root to add a component to.");
		if (target == null || target.Document != Document || target.Name.LocalName.Contains('.', StringComparison.Ordinal))
			throw new ArgumentException("A source control is required.", nameof(target));
		if (!IsFlyoutTarget(target.Name.LocalName))
			throw new ArgumentException(flyoutType + " can only be added to a Button or AppBarButton.", nameof(target));
		if (string.IsNullOrEmpty((string)target.Attribute(NameDirective)))
			throw new InvalidOperationException("The target control needs an x:Name before a " + flyoutType + " can be added.");
		var propertyName = target.Name.LocalName + ".Flyout";
		if (target.Attribute("Flyout") != null
			|| target.Elements().Any(element => string.Equals(element.Name.LocalName, propertyName, StringComparison.Ordinal)))
			throw new InvalidOperationException("The target control already has a Flyout.");

		BeginChange();
		var flyout = new XElement(Document.Root.GetDefaultNamespace() + flyoutType);
		flyout.SetAttributeValue(NameDirective, UniqueName(flyoutType));
		addDefaultContent(flyout);
		target.Add(new XElement(target.Name.Namespace + propertyName, flyout));
		CommitChange();
		return flyout;
	}

	static bool IsFlyoutTarget(string typeName)
	{
		return string.Equals(typeName, "Button", StringComparison.Ordinal)
			|| string.Equals(typeName, "AppBarButton", StringComparison.Ordinal);
	}

	/// <summary>Adds a named item to a source-owned menu container. Like the flyout itself,
	/// the item is selected through the component tray rather than a preview-tree identity.</summary>
	public XElement InsertMenuFlyoutItem(XElement menu)
	{
		if (Document?.Root == null)
			throw new InvalidOperationException("The document has no root to add a MenuFlyoutItem to.");
		if (!IsMenuFlyoutContainer(menu))
			throw new ArgumentException("A source MenuFlyout or MenuFlyoutSubItem is required.", nameof(menu));

		BeginChange();
		var item = new XElement(Document.Root.GetDefaultNamespace() + "MenuFlyoutItem");
		item.SetAttributeValue(NameDirective, UniqueName("MenuFlyoutItem"));
		item.SetAttributeValue("Text", "Menu item");
		menu.Add(item);
		CommitChange();
		return item;
	}

	/// <summary>Adds a named checkable item to a source-owned menu container.</summary>
	public XElement InsertToggleMenuFlyoutItem(XElement menu)
	{
		if (Document?.Root == null)
			throw new InvalidOperationException("The document has no root to add a ToggleMenuFlyoutItem to.");
		if (!IsMenuFlyoutContainer(menu))
			throw new ArgumentException("A source MenuFlyout or MenuFlyoutSubItem is required.", nameof(menu));

		BeginChange();
		var item = new XElement(Document.Root.GetDefaultNamespace() + "ToggleMenuFlyoutItem");
		item.SetAttributeValue(NameDirective, UniqueName("ToggleMenuFlyoutItem"));
		item.SetAttributeValue("Text", "Toggle item");
		menu.Add(item);
		CommitChange();
		return item;
	}

	/// <summary>Adds a named submenu to a source-owned menu container.</summary>
	public XElement InsertMenuFlyoutSubItem(XElement menu)
	{
		if (Document?.Root == null)
			throw new InvalidOperationException("The document has no root to add a MenuFlyoutSubItem to.");
		if (!IsMenuFlyoutContainer(menu))
			throw new ArgumentException("A source MenuFlyout or MenuFlyoutSubItem is required.", nameof(menu));

		BeginChange();
		var item = new XElement(Document.Root.GetDefaultNamespace() + "MenuFlyoutSubItem");
		item.SetAttributeValue(NameDirective, UniqueName("MenuFlyoutSubItem"));
		item.SetAttributeValue("Text", "Submenu");
		menu.Add(item);
		CommitChange();
		return item;
	}

	bool IsMenuFlyoutContainer(XElement element)
	{
		return element != null && element.Document == Document
			&& (string.Equals(element.Name.LocalName, "MenuFlyout", StringComparison.Ordinal)
				|| string.Equals(element.Name.LocalName, "MenuFlyoutSubItem", StringComparison.Ordinal));
	}

	/// <summary>Inserts a copied XAML element as a source edit, after <paramref name="after"/> when
	/// it is a child of <paramref name="container"/> (Duplicate) and otherwise at the end. The returned
	/// element receives a fresh document-local name so the caller can select it after the render
	/// refresh, and every named descendant whose x:Name the document already uses is renamed too: a
	/// duplicate x:Name makes the whole page fail to load.</summary>
	public XElement Paste(string xaml, XElement container, XElement? after = null)
	{
		if (Document?.Root == null)
			throw new InvalidOperationException("The document has no root to paste into.");
		if (container == null || container.Document != Document)
			throw new ArgumentException("A container from this document is required.", nameof(container));
		var pasted = XElement.Parse(xaml ?? "", LoadOptions.PreserveWhitespace);
		var typeName = pasted.Name.LocalName;
		var name = UniqueName(typeName);
		pasted.Name = Document.Root.GetDefaultNamespace() + typeName;
		pasted.SetAttributeValue(NameDirective, name);
		var taken = new HashSet<string>(ElementNames(), StringComparer.Ordinal) { name };
		foreach (var descendant in pasted.Descendants()) {
			if ((string)descendant.Attribute(NameDirective) is not { Length: > 0 } inner || taken.Add(inner))
				continue;
			var stem = inner.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
			var index = 1;
			while (!taken.Add(stem + index))
				index++;
			descendant.SetAttributeValue(NameDirective, stem + index);
		}

		BeginChange();
		if (after != null && after.Parent == container)
			after.AddAfterSelf(pasted);
		else
			container.Add(pasted);
		CommitChange();
		return pasted;
	}

	/// <summary>Replaces the direct text content of a leaf element in one source-history entry.
	/// This deliberately refuses mixed/object content so an inline edit cannot discard a child
	/// control, property element, comment, or markup extension.</summary>
	public void SetTextContent(XElement element, string text)
	{
		if (element == null || element.Document != Document)
			throw new ArgumentException("An element from this document is required.", nameof(element));
		if (element.Elements().Any() || element.Nodes().Any(node => node is not XText))
			throw new InvalidOperationException("Only direct text content can be edited inline.");
		BeginChange();
		element.RemoveNodes();
		element.Add(new XText(text ?? ""));
		CommitChange();
	}

	/// <summary>Wraps a non-root source element in a named container, preserving the original
	/// sibling position and recording the complete structural change as one undo entry.</summary>
	public XElement WrapInContainer(XElement element, string containerType)
	{
		if (element == null)
			throw new ArgumentNullException(nameof(element));
		if (element.Document != Document)
			throw new ArgumentException("An element from this document is required.", nameof(element));
		if (element == Document?.Root || element.Parent is not XElement parent)
			return null;
		if (string.IsNullOrWhiteSpace(containerType))
			throw new ArgumentException("A container type is required.", nameof(containerType));

		var wrapper = new XElement(Document.Root.GetDefaultNamespace() + containerType);
		wrapper.SetAttributeValue(NameDirective, UniqueName(containerType));
		BeginChange();
		element.ReplaceWith(wrapper);
		wrapper.Add(element);
		CommitChange();
		return wrapper;
	}

	static readonly HashSet<string> UnwrappablePanels = new(StringComparer.Ordinal) { "Grid", "StackPanel", "Canvas", "RelativePanel" };

	/// <summary>The inverse of <see cref="WrapInContainer"/>: replaces a non-root Grid, StackPanel,
	/// Canvas, RelativePanel or Border with its child elements, in one undo step. Layout attached to
	/// the removed container (its own <c>Grid.Row</c>, <c>Canvas.Left</c> etc.) moves to each child
	/// that does not set it, and the children's attached placement inside the removed container
	/// (<c>Grid.*</c>, <c>Canvas.*</c>, <c>RelativePanel.*</c>) is dropped. Refused when the
	/// container is empty, or when several children would land in a parent that is not a panel
	/// (a content property holds only one element).</summary>
	public bool Unwrap(XElement element)
	{
		if (element == null)
			throw new ArgumentNullException(nameof(element));
		if (element.Document != Document)
			throw new ArgumentException("An element from this document is required.", nameof(element));
		var kind = element.Name.LocalName;
		if (element == Document?.Root || element.Parent is not XElement parent
			|| !(UnwrappablePanels.Contains(kind) || kind == "Border"))
			return false;
		var children = element.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList();
		if (children.Count == 0)
			return false;
		var parentIsPanel = UnwrappablePanels.Contains(parent.Name.LocalName)
			|| parent.Name.LocalName.EndsWith(".Children", StringComparison.Ordinal);
		if (children.Count > 1 && !parentIsPanel)
			return false;

		var inherited = element.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None
			&& a.Name.LocalName.Contains('.')).ToList();
		BeginChange();
		foreach (var child in children)
		{
			child.Remove();
			child.Attributes().Where(a => a.Name.Namespace == XNamespace.None
				&& a.Name.LocalName.StartsWith(kind + ".", StringComparison.Ordinal)).Remove();
			foreach (var attribute in inherited)
				if (child.Attribute(attribute.Name) == null)
					child.SetAttributeValue(attribute.Name, attribute.Value);
		}
		element.ReplaceWith(children);
		CommitChange();
		return true;
	}

	/// <summary>Moves an existing source element under a different source parent. Cycles, self
	/// moves and root moves are rejected without changing the document or its history.</summary>
	public bool Reparent(XElement element, XElement target)
	{
		if (element == null)
			throw new ArgumentNullException(nameof(element));
		if (target == null)
			throw new ArgumentNullException(nameof(target));
		if (element.Document != Document || target.Document != Document)
			throw new ArgumentException("Both elements must belong to this document.");
		if (element == Document?.Root || element == target || element.Descendants().Contains(target))
			return false;

		BeginChange();
		element.Remove();
		target.Add(element);
		CommitChange();
		return true;
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

	/// <summary>Commits a structural source mutation as one undoable document edit. The callback
	/// must leave the document untouched when it returns <see langword="false"/>.</summary>
	public bool TryMutate(Func<bool> mutation)
	{
		if (mutation == null)
			throw new ArgumentNullException(nameof(mutation));
		if (!mutation())
			return false;
		BeginChange();
		CommitChange();
		return true;
	}

	public void Remove(XElement element)
	{
		if (element == null)
			throw new ArgumentNullException(nameof(element));
		if (element == Document?.Root)
			throw new InvalidOperationException("The root element cannot be deleted.");
		BeginChange();
		var property = element.Parent as XElement;
		element.Remove();
		// A ContextFlyout is a single-value property, not a collection. Deleting its selected
		// source component must remove the now-empty property element as part of the same history
		// entry; otherwise a tray Delete leaves redundant markup behind. Preserve comments or other
		// meaningful nodes, but do not let pretty-printing whitespace keep an empty property alive.
		if ((property?.Name.LocalName.EndsWith(".ContextFlyout", StringComparison.Ordinal) == true
			|| property?.Name.LocalName.EndsWith(".Flyout", StringComparison.Ordinal) == true)
			&& property.Nodes().All(node => node is XText text && string.IsNullOrWhiteSpace(text.Value)))
			property.Remove();
		CommitChange();
	}

	/// <summary>Moves a source element to either edge of its direct source sibling collection.
	/// The structural mutation and its serialization are one document-history entry.</summary>
	public bool MoveToEdge(XElement element, bool front)
	{
		if (element == null)
			throw new ArgumentNullException(nameof(element));
		if (element.Document != Document)
			throw new ArgumentException("An element from this document is required.", nameof(element));
		// A named root is selectable from Document Outline, but it has no sibling order to edit.
		// Treat its edge command as an inert UI request instead of surfacing an exception there.
		if (element == Document?.Root)
			return false;
		if (element.Parent is not XElement parent)
			return false;
		var edge = front ? parent.Elements().LastOrDefault() : parent.Elements().FirstOrDefault();
		if (edge == element)
			return false;

		BeginChange();
		element.Remove();
		if (front)
			parent.Add(element);
		else
			parent.AddFirst(element);
		CommitChange();
		return true;
	}

	/// <summary>Renames an existing <c>x:Name</c> while preserving the editor's one-operation
	/// undo boundary. The caller remains responsible for refreshing the isolated runtime.</summary>
	public bool Rename(string oldName, string newName, out string error)
	{
		error = "";
		newName = newName?.Trim() ?? "";
		if (string.IsNullOrEmpty(newName)) { error = "A name is required."; return false; }
		try { XmlConvert.VerifyNCName(newName); }
		catch (Exception) { error = $"'{newName}' is not a valid XAML name."; return false; }
		var element = FindElement(oldName);
		if (element == null) { error = $"Element '{oldName}' was not found."; return false; }
		if (!string.Equals(oldName, newName, StringComparison.Ordinal) && FindElement(newName) != null) {
			error = $"An element named '{newName}' already exists.";
			return false;
		}
		if (string.Equals(oldName, newName, StringComparison.Ordinal))
			return true;
		BeginChange();
		element.SetAttributeValue(NameDirective, newName);
		CommitChange();
		return true;
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

	public string UniqueName(string controlName)
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
