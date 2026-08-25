using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace ICSharpCode.GtkDesigner;

public sealed record GtkUiNode(string Id, string ClassName, IReadOnlyDictionary<string, string> Properties,
	IReadOnlyList<GtkUiNode> Children, bool IsRoot);

public sealed class GtkUiDocumentEditor
{
	readonly List<string> undo = new(), redo = new();
	XDocument document = new();
	public string Text { get; private set; } = "";
	public string Error { get; private set; } = "";
	public IReadOnlyList<GtkUiNode> Roots { get; private set; } = Array.Empty<GtkUiNode>();
	public bool CanUndo => undo.Count != 0;
	public bool CanRedo => redo.Count != 0;

	public bool Reset(string text) { Text = text ?? ""; undo.Clear(); redo.Clear(); return Parse(); }

	bool Parse()
	{
		try {
			using var reader = XmlReader.Create(new StringReader(Text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
			document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
			if (document.Root?.Name.LocalName != "interface") throw new XmlException("GtkBuilder root must be <interface>.");
			var requires = document.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "requires" && (string?)e.Attribute("lib") == "gtk");
			if (requires == null || !((string?)requires.Attribute("version") ?? "").StartsWith("4", StringComparison.Ordinal))
				throw new XmlException("GTK 4 designer requires <requires lib=\"gtk\" version=\"4.x\" />.");
			var objects = document.Root.Elements().Where(IsObject).ToArray();
			Roots = objects.Select((e, i) => Build(e, true, $"root{i + 1}")).ToArray();
			if (Roots.Count == 0) throw new XmlException("GtkBuilder document contains no top-level object.");
			Error = ""; return true;
		} catch (Exception ex) when (ex is XmlException or InvalidOperationException) {
			Error = ex.Message; Roots = Array.Empty<GtkUiNode>(); return false;
		}
	}

	static GtkUiNode Build(XElement element, bool root, string fallback)
	{
		var id = (string?)element.Attribute("id") ?? "$" + fallback;
		var properties = element.Elements().Where(e => e.Name.LocalName == "property")
			.Where(e => e.Attribute("name") != null).GroupBy(e => (string)e.Attribute("name")!, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
		var children = element.Elements().Where(e => e.Name.LocalName == "child")
			.SelectMany(c => c.Elements().Where(IsObject)).Select((e, i) => Build(e, false, id.TrimStart('$') + "_" + (i + 1))).ToArray();
		return new(id, (string?)element.Attribute("class") ?? "GObject", properties, children, root);
	}

	public bool SetProperty(string id, string name, string value)
	{
		var element = Find(id); if (element == null || string.IsNullOrWhiteSpace(name)) return false;
		var property = element.Elements().FirstOrDefault(e => e.Name.LocalName == "property" && (string?)e.Attribute("name") == name);
		if (property == null) { property = new XElement("property", new XAttribute("name", name), value ?? ""); element.Add(property); }
		else property.Value = value ?? "";
		return Commit();
	}

	public bool Rename(string id, string newId)
	{
		if (id.StartsWith("$", StringComparison.Ordinal) || !IsIdentifier(newId) || Find(newId) != null) return false;
		var element = Find(id); if (element == null) return false;
		element.SetAttributeValue("id", newId);
		// Rewrite only properties that REFERENCE an object id. Rewriting every property whose
		// value happened to equal the old id collaterally edited display text (measured: a label
		// reading "runButton" got renamed along with the button).
		foreach (var property in document.Descendants().Where(IsIdReference))
			property.Value = newId;
		return Commit();
	}

	static readonly HashSet<string> ContainerClasses = new(StringComparer.Ordinal) { "GtkBox", "GtkGrid", "GtkCenterBox", "GtkPaned", "GtkScrolledWindow", "GtkWindow", "GtkApplicationWindow", "GtkNotebook", "GtkStack", "GtkOverlay", "GtkFrame" };

	public bool Add(string parentId, string className)
	{
		var parent = Find(parentId); if (parent == null || string.IsNullOrWhiteSpace(className)) return false;
		// GtkBuilder <child> is only valid under container widgets; reject leaf parents instead
		// of generating a document libgtk would refuse to load.
		if ((string?)parent.Attribute("class") is { } cls && !ContainerClasses.Contains(cls)) return false;
		className = className.Trim(); var baseName = className.StartsWith("Gtk", StringComparison.Ordinal) ? className[3..] : className;
		baseName = char.ToLowerInvariant(baseName[0]) + baseName[1..];
		var id = UniqueId(baseName);
		var child = new XElement("child", new XElement("object", new XAttribute("class", className), new XAttribute("id", id)));
		var created = child.Element("object")!;
		if (className is "GtkButton" or "GtkLabel") created.Add(new XElement("property", new XAttribute("name", "label"), className[3..]));
		else if (className is "GtkEntry") created.Add(new XElement("property", new XAttribute("name", "placeholder-text"), "Entry"));
		parent.Add(child); return Commit();
	}

	public bool Remove(string id)
	{
		var element = Find(id); if (element == null || Roots.Any(r => r.Id == id)) return false;
		var child = element.Parent; if (child?.Name.LocalName != "child") return false;
		child.Remove(); return Commit();
	}

	public bool SetSignal(string id, string signalName, string handlerName)
	{
		var element = Find(id);
		if (element == null || string.IsNullOrWhiteSpace(signalName) || !IsIdentifier(handlerName)) return false;
		var signal = element.Elements().FirstOrDefault(e => e.Name.LocalName == "signal" && (string?)e.Attribute("name") == signalName);
		if (signal == null) element.Add(new XElement("signal", new XAttribute("name", signalName), new XAttribute("handler", handlerName)));
		else signal.SetAttributeValue("handler", handlerName);
		return Commit();
	}

	public IReadOnlyDictionary<string, string> GetSignals(string id)
	{
		var element = Find(id);
		return element == null ? new Dictionary<string, string>() : element.Elements()
			.Where(e => e.Name.LocalName == "signal" && e.Attribute("name") != null)
			.GroupBy(e => (string)e.Attribute("name")!, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => (string?)g.Last().Attribute("handler") ?? "", StringComparer.Ordinal);
	}

	public bool Reorder(string id, int delta)
	{
		var element = Find(id); var wrapper = element?.Parent; var parent = wrapper?.Parent;
		if (wrapper?.Name.LocalName != "child" || parent == null || delta == 0) return false;
		var siblings = parent.Elements().Where(e => e.Name.LocalName == "child" && e.Elements().Any(IsObject)).ToList();
		var oldIndex = siblings.IndexOf(wrapper); var newIndex = Math.Clamp(oldIndex + delta, 0, siblings.Count - 1);
		if (oldIndex < 0 || newIndex == oldIndex) return false;
		wrapper.Remove();
		if (newIndex >= siblings.Count - 1) parent.Add(wrapper); else siblings[newIndex].AddBeforeSelf(wrapper);
		return Commit();
	}

	public bool Undo() => Move(undo, redo);
	public bool Redo() => Move(redo, undo);
	bool Move(List<string> from, List<string> to) { if (from.Count == 0) return false; to.Add(Text); Text = from[^1]; from.RemoveAt(from.Count - 1); return Parse(); }
	bool Commit() { undo.Add(Text); redo.Clear(); Text = Serialize(); return Parse(); }
	string Serialize() { using var writer = new Utf8StringWriter(); document.Save(writer, SaveOptions.DisableFormatting); return writer.ToString(); }
	XElement? Find(string id) => document.Descendants().FirstOrDefault(IsObjectWithId(id));
	static Func<XElement, bool> IsObjectWithId(string id) => e => IsObject(e) && (string?)e.Attribute("id") == id;
	static bool IsObject(XElement e) => e.Name.LocalName is "object" or "template";
	// GtkBuilder properties whose value is a reference to ANOTHER object's id (as opposed to
	// display text that may coincidentally equal one). Extend as new referencing properties are
	// supported by the designer.
	static readonly HashSet<string> IdReferenceProperties = new(StringComparer.Ordinal)
		{ "member-name", "target", "menu-model", "widget", "popover", "action-widget" };
	static bool IsIdReference(XElement e)
		=> e.Name.LocalName == "property"
		   && IdReferenceProperties.Contains((string?)e.Attribute("name") ?? "");
	string UniqueId(string prefix) { for (var i = 1; ; i++) if (Find(prefix + i) == null) return prefix + i; }
	static bool IsIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && (char.IsLetter(value[0]) || value[0] == '_') && value.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');
	sealed class Utf8StringWriter : StringWriter { public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(false); }
}
