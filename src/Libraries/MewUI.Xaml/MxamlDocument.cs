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
		}

		if (string.IsNullOrEmpty(result.Name))
			AddDiagnostic(element, MxamlDiagnosticSeverity.Error, $"{type} requires a Name attribute.");
		else if (!IsIdentifier(result.Name))
			AddDiagnostic(element, MxamlDiagnosticSeverity.Error, $"Name '{result.Name}' is not a valid C# identifier.");

		var isContainer = MewUIControlCatalog.IsContainer(type);
		foreach (var childElement in element.Elements()) {
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
