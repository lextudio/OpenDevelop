using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LeXtudio.MewUI.Xaml;

/// <summary>
/// An .mxaml document: parse, validate, transform, and serialize the MewUI designer's
/// authoritative document. All mutations validate first; a mutation that would introduce
/// errors leaves the document untouched and reports via <see cref="Diagnostics"/>.
/// </summary>
public sealed class MxamlDocument
{
	const string NamespaceUri = "http://schemas.lextudio.com/mewui/2026";

	readonly List<MxamlDiagnostic> diagnostics = new();

	public string Class { get; private set; } = "";
	public MxamlObject Root { get; private set; } = new() { Type = "Window", Name = "root" };
	public IReadOnlyList<MxamlDiagnostic> Diagnostics => diagnostics;
	public bool HasErrors => diagnostics.Any(d => d.Severity == MxamlDiagnosticSeverity.Error);
	readonly List<string> undo = new();
	readonly List<string> redo = new();
	string rawText = "";

	/// <summary>Creates an empty document (placeholder Window root). Prefer
	/// <see cref="Parse"/> to load existing MXAML text.</summary>
	public MxamlDocument() { }

	/// <summary>Replaces the document text. Clears undo/redo. When parsing fails the document
	/// becomes an empty placeholder and <see cref="Error"/> holds the reason; the original text
	/// stays available via <see cref="LastRawText"/> so a caller's flush echoes it back losslessly.</summary>
	public bool Reset(string text)
	{
		rawText = text ?? "";
		undo.Clear(); redo.Clear();
		var ok = TryLoad(rawText);
		rawText = ToXaml(); // parsed state canonicalizes
		return ok;
	}

	public string Error { get; private set; } = "";
	public bool LastParseSucceeded { get; private set; } = true;
	public string LastRawText => rawText;

	bool TryLoad(string text)
	{
		diagnostics.Clear();
		try {
			var loaded = Parse(text);
			Class = loaded.Class;
			Root = loaded.Root;
			foreach (var d in loaded.Diagnostics) diagnostics.Add(d);
			Error = HasErrors ? string.Join("; ", diagnostics.Where(d => d.Severity == MxamlDiagnosticSeverity.Error).Select(d => d.Message)) : "";
			return !HasErrors;
		} catch (MxamlException ex) {
			Error = ex.Message;
			return false;
		}
	}

	public bool Undo()
	{
		if (undo.Count == 0) return false;
		redo.Add(ToXaml());
		var previous = undo[^1];
		undo.RemoveAt(undo.Count - 1);
		TryLoad(previous);
		rawText = previous;
		return true;
	}

	public bool Redo()
	{
		if (redo.Count == 0) return false;
		undo.Add(ToXaml());
		var next = redo[^1];
		redo.RemoveAt(redo.Count - 1);
		TryLoad(next);
		rawText = next;
		return true;
	}

	/// <summary>Parses MXAML text. Throws <see cref="MxamlException"/> for malformed XML or a
	/// wrong root; structural/semantic problems surface as <see cref="Diagnostics"/> instead.</summary>
	public static MxamlDocument Parse(string text)
	{
		XDocument xdoc;
		try {
			xdoc = XDocument.Parse(text, LoadOptions.SetLineInfo);
		} catch (XmlException ex) {
			throw new MxamlException($"MXAML is not well-formed XML: {ex.Message}", ex);
		}
		if (xdoc.Root == null || xdoc.Root.Name.LocalName != "Window")
			throw new MxamlException("MXAML root must be a <Window> element (it carries the Class attribute, WPF-style).");

		var doc = new MxamlDocument();
		var classAttr = xdoc.Root.Attribute("Class");
		if (classAttr == null || !IsDottedIdentifier(classAttr.Value))
			doc.AddDiagnostic(xdoc.Root, MxamlDiagnosticSeverity.Error, "Root requires a Class attribute naming the partial class (e.g. \"App.MainWindow\").");

		doc.Root = doc.BuildObject(xdoc.Root);

		doc.Class = classAttr?.Value ?? "";

		// Whole-tree uniqueness pass: per-element checks during build miss duplicates against
		// siblings built LATER (measured: last sibling's duplicate went undetected).
		var seenNames = new HashSet<string>(StringComparer.Ordinal);
		foreach (var o in doc.Root.DescendantsAndSelf()) {
			if (!seenNames.Add(o.Name))
				doc.diagnostics.Add(new(MxamlDiagnosticSeverity.Error, $"Duplicate Name '{o.Name}'.", o.Line, o.Column));
		}
		return doc;
	}

	MxamlObject BuildObject(XElement element)
	{
		var type = element.Name.LocalName;
		var lineInfo = (IXmlLineInfo)element;
		var result = new MxamlObject {
			Type = type,
			Name = (string?)element.Attribute("Name") ?? "",
			Line = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0,
			Column = lineInfo.HasLineInfo() ? lineInfo.LinePosition : 0,
		};

		if (!MewUIControlCatalog.IsKnownType(type))
			AddDiagnostic(element, MxamlDiagnosticSeverity.Warning, $"Unknown control type '{type}' - retained for round-trip but not code-generated.");

		foreach (var attribute in element.Attributes()) {
			if (attribute.IsNamespaceDeclaration || attribute.Name.LocalName is "Name" or "Class") continue;
			var name = attribute.Name.LocalName;
			var isEvent = MewUIControlCatalog.IsKnownEvent(name);
			result.Attributes.Add(new MxamlAttribute { Name = name, Value = attribute.Value, IsEvent = isEvent, Line = ((IXmlLineInfo)attribute).HasLineInfo() ? ((IXmlLineInfo)attribute).LineNumber : 0 });

			if (!isEvent && !MewUIControlCatalog.IsSupported(type, name))
				AddDiagnostic(attribute, MxamlDiagnosticSeverity.Warning, $"'{type}.{name}' has no registered kind - generated as a comment.");
			else if (!isEvent && MewUIControlCatalog.KindOf(type, name) == MxamlPropertyKind.Enum
				&& !MewUIControlCatalog.EnumValuesOf(type, name).Contains(attribute.Value, StringComparer.Ordinal))
				AddDiagnostic(attribute, MxamlDiagnosticSeverity.Error, $"{type}.{name}: '{attribute.Value}' is not a supported value.");
			else if (!isEvent && MewUIControlCatalog.KindOf(type, name) == MxamlPropertyKind.Thickness && MewUIControlCatalog.ParseThickness(attribute.Value) == null)
				AddDiagnostic(attribute, MxamlDiagnosticSeverity.Error, $"{type}.{name}: '{attribute.Value}' is not a thickness (one, two or four numbers).");
			else if (!isEvent && MewUIControlCatalog.KindOf(type, name) == MxamlPropertyKind.Color && !MewUIControlCatalog.IsValidColor(attribute.Value))
				AddDiagnostic(attribute, MxamlDiagnosticSeverity.Error, $"{type}.{name}: '{attribute.Value}' is not a colour (#RRGGBB, #AARRGGBB or a MewUI colour name).");
		}

		if (string.IsNullOrEmpty(result.Name))
			AddDiagnostic(element, MxamlDiagnosticSeverity.Error, $"{type} requires a Name attribute.");
		else if (!IsIdentifier(result.Name))
			AddDiagnostic(element, MxamlDiagnosticSeverity.Error, $"Name '{result.Name}' is not a valid C# identifier.");

		var isContainer = MewUIControlCatalog.IsContainer(type);
		var hasItems = MewUIControlCatalog.HasStringItems(type);
		var objectChildren = element.Elements().Count(e => !(hasItems && e.Name.LocalName == "Item"));
		if (MewUIControlCatalog.IsSingleChildHost(type) && objectChildren > 1)
			AddDiagnostic(element, MxamlDiagnosticSeverity.Error, $"'{type}' {result.Name} holds one child; {objectChildren} were given.");
		if (objectChildren > 0 && result.Attributes.Any(a => MewUIControlCatalog.KindOf(type, a.Name) == MxamlPropertyKind.ContentText))
			AddDiagnostic(element, MxamlDiagnosticSeverity.Error, $"'{type}' {result.Name} has both a caption attribute and child content; both set Content.");
		foreach (var childElement in element.Elements()) {
			if (hasItems && childElement.Name.LocalName == "Item") {
				if (childElement.HasElements || childElement.HasAttributes)
					AddDiagnostic(childElement, MxamlDiagnosticSeverity.Error, $"'{type}' items are plain text: <Item>text</Item>.");
				result.Items.Add(childElement.Value);
				continue;
			}
			if (!isContainer)
				AddDiagnostic(childElement, MxamlDiagnosticSeverity.Error, $"'{type}' is not a container and cannot take children.");
			result.Children.Add(BuildObject(childElement));
		}
		return result;
	}

	void AddDiagnostic(XObject node, MxamlDiagnosticSeverity severity, string message)
	{
		var info = (IXmlLineInfo)node;
		diagnostics.Add(new MxamlDiagnostic(severity, message,
			info.HasLineInfo() ? info.LineNumber : 0,
			info.HasLineInfo() ? info.LinePosition : 0));
	}

	void AddDiagnostic(MxamlDiagnostic diagnostic) => diagnostics.Add(diagnostic);

	// ---- transformations ---------------------------------------------------------------

	string Snapshot() => ToXaml();

	bool Commit(Func<bool> mutate)
	{
		var before = ToXaml();
		diagnostics.RemoveAll(d => d.Severity != MxamlDiagnosticSeverity.Info && d.Line == 0);
		if (!mutate()) return false;
		Revalidate();
		if (HasErrors) {
			// transactional: roll back to the pre-mutation snapshot on newly-introduced errors
			var restored = Parse(before);
			Class = restored.Class; Root = restored.Root;
			return false;
		}
		undo.Add(before);
		redo.Clear();
		rawText = before;
		return true;
	}

	void Revalidate()
	{
		diagnostics.Clear();
		if (!IsDottedIdentifier(Class))
			diagnostics.Add(new(MxamlDiagnosticSeverity.Error, $"Class '{Class}' is not a valid dotted C# identifier."));
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var o in Root.DescendantsAndSelf()) {
			if (!IsIdentifier(o.Name)) diagnostics.Add(new(MxamlDiagnosticSeverity.Error, $"Name '{o.Name}' is not a valid C# identifier."));
			else if (!seen.Add(o.Name)) diagnostics.Add(new(MxamlDiagnosticSeverity.Error, $"Duplicate Name '{o.Name}'."));
			if (MewUIControlCatalog.IsSingleChildHost(o.Type) && o.Children.Count > 1)
				diagnostics.Add(new(MxamlDiagnosticSeverity.Error, $"'{o.Type}' {o.Name} holds one child; {o.Children.Count} were given."));
			if (o.Children.Count > 0 && o.Attributes.Any(a => !a.IsEvent && MewUIControlCatalog.KindOf(o.Type, a.Name) == MxamlPropertyKind.ContentText))
				diagnostics.Add(new(MxamlDiagnosticSeverity.Error, $"'{o.Type}' {o.Name} has both a caption attribute and child content; both set Content."));
			foreach (var a in o.Attributes) {
				if (a.IsEvent) {
					if (!IsIdentifier(a.Value)) diagnostics.Add(new(MxamlDiagnosticSeverity.Error, $"Event handler '{a.Value}' is not a valid identifier."));
					continue;
				}
				switch (MewUIControlCatalog.KindOf(o.Type, a.Name)) {
					case MxamlPropertyKind.Double when !double.TryParse(a.Value, System.Globalization.CultureInfo.InvariantCulture, out _):
						diagnostics.Add(new(MxamlDiagnosticSeverity.Error, $"{o.Type}.{a.Name}: '{a.Value}' is not a number."));
						break;
					case MxamlPropertyKind.Int32 when !int.TryParse(a.Value, System.Globalization.CultureInfo.InvariantCulture, out _):
						diagnostics.Add(new(MxamlDiagnosticSeverity.Error, $"{o.Type}.{a.Name}: '{a.Value}' is not an integer."));
						break;
					case MxamlPropertyKind.Boolean when a.Value is not ("true" or "false" or "True" or "False"):
						diagnostics.Add(new(MxamlDiagnosticSeverity.Error, $"{o.Type}.{a.Name}: '{a.Value}' is not a boolean."));
						break;
					case MxamlPropertyKind.Enum when !MewUIControlCatalog.EnumValuesOf(o.Type, a.Name).Contains(a.Value, StringComparer.Ordinal):
						diagnostics.Add(new(MxamlDiagnosticSeverity.Error, $"{o.Type}.{a.Name}: '{a.Value}' is not a supported value."));
						break;
					case MxamlPropertyKind.Thickness when MewUIControlCatalog.ParseThickness(a.Value) == null:
						diagnostics.Add(new(MxamlDiagnosticSeverity.Error, $"{o.Type}.{a.Name}: '{a.Value}' is not a thickness (one, two or four numbers)."));
						break;
					case MxamlPropertyKind.Color when !MewUIControlCatalog.IsValidColor(a.Value):
						diagnostics.Add(new(MxamlDiagnosticSeverity.Error, $"{o.Type}.{a.Name}: '{a.Value}' is not a colour (#RRGGBB, #AARRGGBB or a MewUI colour name)."));
						break;
				}
			}
		}
	}

	/// <summary>Inserts a new control of <paramref name="type"/> under the named container.
	/// The new element's Name is auto-generated (camelCase + counter).</summary>
	public bool Add(string containerName, string type)
	{
		var parent = Find(containerName);
		if (parent == null || !MewUIControlCatalog.IsContainer(parent.Type) || !MewUIControlCatalog.IsKnownType(type))
			return false;
		return Commit(() => {
			var name = UniqueName(type);
			parent.Children.Add(new MxamlObject { Type = type, Name = name });
			return true;
		});
	}

	/// <summary>The named elements as MXAML snippets (each with its subtree), for the clipboard. An
	/// element inside another named element travels with it; the root is not copyable. Null when
	/// any name is unknown or is the root.</summary>
	public IReadOnlyList<string>? CopySnippets(IReadOnlyCollection<string> names)
	{
		var objects = new List<MxamlObject>();
		foreach (var name in names) {
			var obj = Find(name);
			if (obj == null || obj == Root) return null;
			objects.Add(obj);
		}
		return objects.Where(o => !objects.Any(other => other != o && other.DescendantsAndSelf().Skip(1).Contains(o)))
			.Select(o => { var sb = new StringBuilder(); o.WriteTo(sb, ""); return sb.ToString().TrimEnd('\n'); })
			.ToArray();
	}

	/// <summary>Pastes MXAML snippets into <paramref name="targetName"/> or its nearest container
	/// ancestor, after <paramref name="afterName"/> when it is a child there (Duplicate) and
	/// otherwise at the end. Every pasted Name the document (or an earlier pasted element) already
	/// uses is made unique (<c>button1</c> becomes <c>button2</c>); a Canvas position is dropped
	/// unless the target is a Canvas. One transactional, undoable change: snippets that are not
	/// MXAML, or a paste that would introduce errors, change nothing. <paramref name="created"/>
	/// lists the pasted top-level names.</summary>
	public bool Paste(string targetName, IReadOnlyList<string> snippets, out List<string> created, string? afterName = null)
	{
		created = new List<string>();
		var target = Find(targetName);
		while (target != null && !MewUIControlCatalog.IsContainer(target.Type))
			target = FindParent(target.Name);
		if (target == null || snippets.Count == 0) return false;
		MxamlDocument parsed;
		try {
			parsed = Parse($"<Window xmlns=\"{NamespaceUri}\" Name=\"paste\" Class=\"Paste\">{string.Concat(snippets)}</Window>");
		} catch (MxamlException) { return false; }
		if (parsed.HasErrors || parsed.Root.Children.Count == 0) return false;
		var pasted = parsed.Root.Children.ToList();
		var taken = Root.DescendantsAndSelf().Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
		foreach (var obj in pasted.SelectMany(o => o.DescendantsAndSelf())) {
			if (taken.Add(obj.Name)) continue;
			var stem = obj.Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
			var n = 1; while (!taken.Add(stem + n)) n++;
			obj.Name = stem + n;
		}
		if (target.Type != "Canvas")
			foreach (var obj in pasted)
				foreach (var position in obj.Attributes.Where(a => a.Name is "Canvas.Left" or "Canvas.Top").ToList())
					obj.Attributes.Remove(position);
		var names = pasted.Select(o => o.Name).ToList();
		if (!Commit(() => {
			var after = afterName == null ? null : target.Children.FirstOrDefault(c => c.Name == afterName);
			var index = after == null ? target.Children.Count : target.Children.IndexOf(after) + 1;
			foreach (var obj in pasted)
				target.Children.Insert(index++, obj);
			return true;
		})) return false;
		created = names;
		return true;
	}

	/// <summary>Wraps sibling elements in a new <paramref name="containerType"/> placed where the
	/// first of them (in document order) was, keeping their relative order, as the WPF and WinUI
	/// designers' Wrap does. All names must share one parent and none may be the root; a single-child
	/// host (Border) takes exactly one. Attached layout values (dotted attributes such as
	/// <c>Canvas.Left</c> or <c>DockPanel.Dock</c>) describe placement in the OLD parent, so the
	/// wrapper takes over the first element's and the wrapped elements drop theirs. One
	/// transactional, undoable change; <paramref name="created"/> is the wrapper's name.</summary>
	public bool Wrap(IReadOnlyCollection<string> names, string containerType, out string created)
	{
		created = "";
		if (names.Count == 0 || !MewUIControlCatalog.IsContainer(containerType) || !MewUIControlCatalog.IsKnownType(containerType)
			|| containerType == "Window" || !MewUIControlCatalog.IsPanel(containerType) && names.Count != 1)
			return false;
		var parent = FindParent(names.First());
		if (parent == null || names.Any(name => FindParent(name) != parent))
			return false;
		var wrapped = parent.Children.Where(child => names.Contains(child.Name)).ToList();
		if (wrapped.Count != names.Count)
			return false;
		var name = UniqueName(containerType);
		var wrapper = new MxamlObject { Type = containerType, Name = name };
		foreach (var attached in wrapped[0].Attributes.Where(a => a.Name.Contains('.')).ToList())
			wrapper.Attributes.Add(new MxamlAttribute { Name = attached.Name, Value = attached.Value });
		if (!Commit(() => {
			var index = parent.Children.IndexOf(wrapped[0]);
			foreach (var child in wrapped) {
				parent.Children.Remove(child);
				foreach (var attached in child.Attributes.Where(a => a.Name.Contains('.')).ToList())
					child.Attributes.Remove(attached);
				wrapper.Children.Add(child);
			}
			parent.Children.Insert(index, wrapper);
			return true;
		})) return false;
		created = name;
		return true;
	}

	/// <summary>Replaces a container with its children at its position (the WPF/WinUI designers'
	/// Unwrap). The root cannot be unwrapped, nor an empty container, nor several children into a
	/// single-child host. A lone child inherits the container's attached placement (dotted
	/// attributes); with several, the children only drop placement that addressed the removed
	/// container. One transactional, undoable change.</summary>
	public bool Unwrap(string name)
	{
		var container = Find(name);
		var parent = FindParent(name);
		if (container == null || parent == null || !MewUIControlCatalog.IsContainer(container.Type) || container.Children.Count == 0
			|| !MewUIControlCatalog.IsPanel(parent.Type) && container.Children.Count != 1)
			return false;
		return Commit(() => {
			var index = parent.Children.IndexOf(container);
			parent.Children.RemoveAt(index);
			var children = container.Children.ToList();
			foreach (var child in children) {
				foreach (var attached in child.Attributes.Where(a => a.Name.Contains('.')).ToList())
					child.Attributes.Remove(attached);
				if (children.Count == 1)
					foreach (var attached in container.Attributes.Where(a => a.Name.Contains('.')))
						child.Attributes.Add(new MxamlAttribute { Name = attached.Name, Value = attached.Value });
				parent.Children.Insert(index++, child);
			}
			return true;
		});
	}

	/// <summary>Moves a child within its parent by <paramref name="delta"/> positions.</summary>
	public bool Reorder(string name, int delta)
	{
		if (delta == 0 || name == Root.Name) return false;
		var parent = FindParent(name);
		if (parent == null) return false;
		return Commit(() => {
			var index = parent.Children.IndexOf(parent.Children.First(c => c.Name == name));
			if (index < 0) return false;
			var target = Math.Clamp(index + delta, 0, parent.Children.Count - 1);
			if (target == index) return false;
			var item = parent.Children[index];
			parent.Children.RemoveAt(index);
			parent.Children.Insert(target, item);
			return true;
		});
	}

	/// <summary>Removes the named element and its subtree. The design-surface root cannot be removed.</summary>
	public bool Remove(string name)
	{
		if (name == Root.Name) return false;
		return Commit(() => {
			var target = Find(name);
			if (target == null) return false;
			var parent = FindParent(name);
			if (parent == null) return false;
			parent.Children.Remove(parent.Children.First(c => c.Name == name));
			return true;
		});
	}

	/// <summary>Sets a property value (creating the attribute when absent). Values are strings
	/// here; the registry decides how they generate.</summary>
	public bool SetProperty(string name, string property, string value)
	{
		if (property is "Name") return Rename(name, value);
		var obj = Find(name); if (obj == null) return false;
		if (property is "Canvas.Left" or "Canvas.Top" && FindParent(name)?.Type != "Canvas") return false;
		return Commit(() => {
			var existing = obj.FindAttribute(property);
			if (existing != null) {
				if (existing.IsEvent) return false;
				existing.Value = value;
			}
			else {
				obj.Attributes.Add(new MxamlAttribute { Name = property, Value = value });
			}
			return true;
		});
	}

	/// <summary>Replaces a ComboBox/ListBox's string items (its <c>&lt;Item&gt;</c> children) in one
	/// undo step; an empty list removes them.</summary>
	public bool SetItems(string name, IReadOnlyList<string> items)
	{
		var obj = Find(name);
		if (obj == null || !MewUIControlCatalog.HasStringItems(obj.Type)) return false;
		if (obj.Items.SequenceEqual(items ?? Array.Empty<string>())) return true;
		return Commit(() => {
			obj.Items.Clear();
			foreach (var item in items ?? Array.Empty<string>())
				obj.Items.Add(item ?? "");
			return true;
		});
	}

	/// <summary>Sets the coordinates of an immediate Canvas child. Other layouts deliberately
	/// reject this operation: their arranged bounds are not an editable position contract.</summary>
	public bool SetCanvasPosition(string name, double left, double top)
	{
		if (!double.IsFinite(left) || !double.IsFinite(top)) return false;
		var parent = FindParent(name);
		if (parent?.Type != "Canvas") return false;
		return Commit(() => {
			var child = parent.Children.FirstOrDefault(c => c.Name == name);
			if (child == null) return false;
			SetAttribute(child, "Canvas.Left", Coordinate(left));
			SetAttribute(child, "Canvas.Top", Coordinate(top));
			return true;
		});
	}

	/// <summary>Moves a direct Canvas child and, when both dimensions are supplied, gives it an
	/// explicit size. A negative dimension is the bounds-RPC sentinel for a move-only gesture;
	/// zero is never authored because it cannot be reached through the resize handles.</summary>
	public bool SetCanvasBounds(string name, double left, double top, double width, double height)
	{
		var moveOnly = width == -1 && height == -1;
		if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height) || !moveOnly && (width <= 0 || height <= 0)) return false;
		var parent = FindParent(name);
		if (parent?.Type != "Canvas") return false;
		return Commit(() => {
			var child = parent.Children.FirstOrDefault(c => c.Name == name);
			if (child == null) return false;
			SetAttribute(child, "Canvas.Left", Coordinate(left));
			SetAttribute(child, "Canvas.Top", Coordinate(top));
			if (!moveOnly) { SetAttribute(child, "Width", Coordinate(width)); SetAttribute(child, "Height", Coordinate(height)); }
			return true;
		});
	}

	/// <summary>Renames an element. Event handler values are user-method references and are
	/// deliberately NOT rewritten (unlike GTK's value-collision hazard).</summary>
	public bool Rename(string name, string newName)
	{
		if (!IsIdentifier(newName)) return false;
		var obj = Find(name); if (obj == null || obj == Root) return false;
		if (Find(newName) != null) return false;
		return Commit(() => {
			obj.Name = newName;
			return true;
		});
	}

	/// <summary>Adds or removes an event wiring on the named element.</summary>
	public bool SetEvent(string name, string eventName, string? handler)
	{
		var obj = Find(name); if (obj == null || !MewUIControlCatalog.IsKnownEvent(eventName)) return false;
		if (handler != null && !IsIdentifier(handler)) return false;
		return Commit(() => {
			var existing = obj.FindAttribute(eventName);
			if (handler == null) {
				if (existing != null) obj.Attributes.Remove(existing);
				return true;
			}
			if (existing != null) existing.Value = handler;
			else obj.Attributes.Add(new MxamlAttribute { Name = eventName, Value = handler, IsEvent = true });
			return true;
		});
	}

	// ---- lookups -----------------------------------------------------------------------

	public MxamlObject? Find(string name) => Root.Name == name ? Root : Root.DescendantsAndSelf().FirstOrDefault(o => o.Name == name);

	MxamlObject? FindParent(string name)
		=> Root.DescendantsAndSelf().FirstOrDefault(p => p.Children.Any(c => c.Name == name));

	static void SetAttribute(MxamlObject obj, string name, string value)
	{
		var attribute = obj.FindAttribute(name);
		if (attribute == null) obj.Attributes.Add(new MxamlAttribute { Name = name, Value = value });
		else attribute.Value = value;
	}

	static string Coordinate(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

	string UniqueName(string type)
	{
		var prefix = char.ToLowerInvariant(type[0]) + type[1..];
		var taken = Root.DescendantsAndSelf().Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
		for (var i = 1; ; i++)
			if (!taken.Contains(prefix + i)) return prefix + i;
	}

	static bool IsIdentifier(string value)
		=> !string.IsNullOrWhiteSpace(value) && (char.IsLetter(value[0]) || value[0] == '_')
		   && value.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');

	static bool IsDottedIdentifier(string value)
		=> !string.IsNullOrWhiteSpace(value) && value.Split('.').All(IsIdentifier);

	// ---- serialization -----------------------------------------------------------------

	/// <summary>Canonical .mxaml serialization (4-space indent, attributes in insertion order).</summary>
	public string ToXaml()
	{
		var sb = new StringBuilder();
		sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
		sb.Append("<Window xmlns=\"").Append(NamespaceUri).Append('"');
		if (!string.IsNullOrEmpty(Root.Name))
			sb.Append(" Name=\"").Append(Escape(Root.Name)).Append('"');
		sb.Append(" Class=\"").Append(Escape(Class)).Append('"');
		foreach (var a in Root.Attributes.Where(a => !a.IsEvent))
			sb.Append(' ').Append(a.Name).Append("=\"").Append(Escape(a.Value)).Append('"');
		sb.Append(">\n");
		foreach (var child in Root.Children)
			child.WriteTo(sb, "    ");
		sb.Append("</Window>");
		return sb.ToString();
	}

	static string Escape(string value) => value
		.Replace("&", "&amp;")
		.Replace("<", "&lt;")
		.Replace(">", "&gt;")
		.Replace("\"", "&quot;");
}
