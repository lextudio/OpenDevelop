using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace ICSharpCode.GtkDesigner;

/// <summary>One GtkBuilder object. <paramref name="Layout"/> holds its &lt;layout&gt; properties - the
/// GTK 4 replacement for GTK 2's &lt;packing&gt;, read by the PARENT's layout manager (GtkGrid's
/// column/row/spans, GtkOverlay's measure/clip-overlay, GtkFixed's transform).</summary>
public sealed record GtkUiNode(string Id, string ClassName, IReadOnlyDictionary<string, string> Properties,
	IReadOnlyList<GtkUiNode> Children, bool IsRoot, IReadOnlyDictionary<string, string>? Layout = null);

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
		// A composite <template class="MyWindow" parent="GtkWindow"> is identified by its class (the
		// user's C# type) and is, for GTK, an instance of its parent type.
		var isTemplate = element.Name.LocalName == "template";
		var id = isTemplate ? (string?)element.Attribute("class") ?? "$" + fallback : (string?)element.Attribute("id") ?? "$" + fallback;
		// A property may hold an object instead of text (<property name="content"><object .../>,
		// common in GTK 4 and libadwaita): that object is a child in the tree, not a text value.
		static bool HoldsObject(XElement p) => p.Elements().Any(IsObject);
		var properties = element.Elements().Where(e => e.Name.LocalName == "property")
			.Where(e => e.Attribute("name") != null && !HoldsObject(e)).GroupBy(e => (string)e.Attribute("name")!, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
		var children = element.Elements().Where(e => e.Name.LocalName == "child" || e.Name.LocalName == "property" && HoldsObject(e))
			.SelectMany(c => c.Elements().Where(IsObject)).Select((e, i) => Build(e, false, id.TrimStart('$') + "_" + (i + 1))).ToArray();
		var layout = element.Elements().Where(e => e.Name.LocalName == "layout").SelectMany(l => l.Elements()).Where(e => e.Name.LocalName == "property" && e.Attribute("name") != null)
			.GroupBy(e => (string)e.Attribute("name")!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
		var className = isTemplate ? (string?)element.Attribute("parent") ?? "GtkWidget" : (string?)element.Attribute("class") ?? "GObject";
		return new(id, className, properties, children, root, layout);
	}

	/// <summary>Sets, or with a null <paramref name="value"/> resets, a property. Reset removes the
	/// <c>&lt;property&gt;</c> so GTK's own default applies (no default value is ever written).
	/// GtkBuilder treats '-' and '_' in property names alike, so either spelling finds an existing
	/// entry. A NEW user-visible string gets <c>translatable="yes"</c>, as Stetic and Glade do;
	/// an existing entry keeps its translatable/context/comments attributes.</summary>
	public bool SetProperty(string id, string name, string? value, bool translatable = false)
	{
		var element = Find(id); if (element == null || string.IsNullOrWhiteSpace(name)) return false;
		var property = element.Elements().FirstOrDefault(e => e.Name.LocalName == "property" && SameName((string?)e.Attribute("name"), name));
		// An edit that changes nothing (reset of a default, same value) succeeds without an undo step.
		if (value == null) {
			if (property == null) return true;
			property.Remove();
			return Commit();
		}
		if (property == null) {
			property = new XElement("property", new XAttribute("name", name), value);
			if (translatable) property.Add(new XAttribute("translatable", "yes"));
			element.Add(property);
		}
		else if (property.Value == value) return true;
		else property.Value = value;
		return Commit();
	}

	/// <summary>Sets, or with null resets, a property in the object's &lt;layout&gt; element, creating
	/// the element on first use and removing it when its last property goes.</summary>
	public bool SetLayoutProperty(string id, string name, string? value)
	{
		var element = Find(id); if (element == null || string.IsNullOrWhiteSpace(name)) return false;
		var layout = element.Elements().FirstOrDefault(e => e.Name.LocalName == "layout");
		var property = layout?.Elements().FirstOrDefault(e => e.Name.LocalName == "property" && SameName((string?)e.Attribute("name"), name));
		if (value == null) {
			if (property == null) return true;
			property.Remove();
			if (!layout!.Elements().Any()) layout.Remove();
			return Commit();
		}
		if (property != null) {
			if (property.Value == value) return true;
			property.Value = value;
			return Commit();
		}
		if (layout == null) { layout = new XElement("layout"); element.Add(layout); }
		layout.Add(new XElement("property", new XAttribute("name", name), value));
		return Commit();
	}

	static bool SameName(string? a, string b) => a != null && string.Equals(a.Replace('_', '-'), b.Replace('_', '-'), StringComparison.Ordinal);

	public bool Rename(string id, string newId)
	{
		if (id.StartsWith("$", StringComparison.Ordinal) || !IsIdentifier(newId) || Find(newId) != null) return false;
		var element = Find(id); if (element == null) return false;
		// A template's identity is its C# class name: renaming it is a code refactoring, not a .ui edit.
		if (element.Name.LocalName == "template") return false;
		element.SetAttributeValue("id", newId);
		// Rewrite only properties that REFERENCE an object id. Rewriting every property whose
		// value happened to equal the old id collaterally edited display text (measured: a label
		// reading "runButton" got renamed along with the button).
		foreach (var property in document.Descendants().Where(IsIdReference))
			property.Value = newId;
		return Commit();
	}

	static readonly HashSet<string> ContainerClasses = new(StringComparer.Ordinal) { "GtkBox", "GtkGrid", "GtkCenterBox", "GtkPaned", "GtkScrolledWindow", "GtkWindow", "GtkApplicationWindow", "GtkNotebook", "GtkStack", "GtkOverlay", "GtkFrame", "AdwClamp", "AdwPreferencesPage", "AdwPreferencesGroup" };

	/// <summary>Adds a new <paramref name="className"/> child. A drop resolved by GtkDropPlanner
	/// gives an insertion <paramref name="index"/> among the existing children (a box) or a
	/// grid <paramref name="cell"/>; with neither, the child is appended (and a grid child goes to
	/// the first free row).</summary>
	public bool Add(string parentId, string className, int? index = null, (int Column, int Row)? cell = null)
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
		// Starter text is user-visible, so it is translatable from the start (the Properties pad
		// marks new label/title/placeholder text the same way).
		if (className is "GtkButton" or "GtkLabel") created.Add(new XElement("property", new XAttribute("name", "label"), new XAttribute("translatable", "yes"), className[3..]));
		else if (className is "GtkEntry") created.Add(new XElement("property", new XAttribute("name", "placeholder-text"), new XAttribute("translatable", "yes"), "Entry"));
		// A GtkGrid places a child by its <layout>; without one every new child lands on (0, 0)
		// over whatever is there. Append it in column 0 of the first free row instead.
		if ((string?)parent.Attribute("class") == "GtkGrid") {
			var (column, row) = cell ?? (0, NextGridRow(parent));
			created.Add(new XElement("layout",
				new XElement("property", new XAttribute("name", "column"), column.ToString(System.Globalization.CultureInfo.InvariantCulture)),
				new XElement("property", new XAttribute("name", "row"), row.ToString(System.Globalization.CultureInfo.InvariantCulture))));
		}
		var siblings = parent.Elements().Where(e => e.Name.LocalName == "child" && e.Elements().Any(IsObject)).ToList();
		if (index is { } at && at >= 0 && at < siblings.Count) siblings[at].AddBeforeSelf(child);
		else parent.Add(child);
		return Commit();
	}

	/// <summary>Adds a new <paramref name="className"/> child where a toolbox drop onto the source text
	/// at <paramref name="offset"/> puts it: into the innermost container holding that point, beside the
	/// child the point is on (see XmlToolboxDropPlanner). False when no container holds the point.</summary>
	public bool AddAt(int offset, string className)
	{
		var point = ICSharpCode.SharpDevelop.Designer.Shell.XmlToolboxDropPlanner.Plan(Text, offset,
			e => e.Name == "object" && e.Attribute("id") != null && e.Attribute("class") is { } cls && ContainerClasses.Contains(cls));
		if (point == null) return false;
		// The planner counts every child element; Add counts only the <child> elements that hold an
		// object (not <property>, <layout>, <style>, ...).
		var index = point.Container.Children.Take(point.ChildIndex).Count(c => c.Name == "child" && c.Children.Any(o => o.Name is "object" or "template"));
		return Add(point.Container.Attribute("id")!, className, index);
	}

	/// <summary>The first row below every existing child (row + row-span; GTK's defaults 0 and 1).</summary>
	static int NextGridRow(XElement grid)
	{
		static int Read(XElement? layout, string name, int fallback)
			=> int.TryParse(layout?.Elements().FirstOrDefault(e => e.Name.LocalName == "property" && SameName((string?)e.Attribute("name"), name))?.Value,
				System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
		var next = 0;
		foreach (var obj in grid.Elements().Where(e => e.Name.LocalName == "child").SelectMany(c => c.Elements().Where(IsObject))) {
			var layout = obj.Elements().FirstOrDefault(e => e.Name.LocalName == "layout");
			next = Math.Max(next, Read(layout, "row", 0) + Math.Max(1, Read(layout, "row-span", 1)));
		}
		return next;
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
	static Func<XElement, bool> IsObjectWithId(string id) => e => e.Name.LocalName == "object" ? (string?)e.Attribute("id") == id
		: e.Name.LocalName == "template" && (string?)e.Attribute("class") == id;
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
