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
		// ... and only those that reference THIS object: other references keep their target.
		foreach (var property in document.Descendants().Where(e => IsIdReference(e) && e.Value.Trim() == id))
			property.Value = newId;
		return Commit();
	}

	static readonly HashSet<string> ContainerClasses = new(StringComparer.Ordinal) { "GtkBox", "GtkGrid", "GtkCenterBox", "GtkPaned", "GtkScrolledWindow", "GtkWindow", "GtkApplicationWindow", "GtkNotebook", "GtkStack", "GtkOverlay", "GtkFrame", "AdwClamp", "AdwPreferencesPage", "AdwPreferencesGroup", "AdwPreferencesWindow", "AdwNavigationView", "AdwNavigationPage", "AdwNavigationSplitView" };

	/// <summary>Adds a new <paramref name="className"/> child. A drop resolved by GtkDropPlanner
	/// gives an insertion <paramref name="index"/> among the existing children (a box) or a
	/// grid <paramref name="cell"/>; with neither, the child is appended (and a grid child goes to
	/// the first free row).</summary>
	public bool Add(string parentId, string className, int? index = null, (int Column, int Row)? cell = null)
	{
		var parent = Find(parentId); if (parent == null || string.IsNullOrWhiteSpace(className)) return false;
		// GtkBuilder <child> is only valid under container widgets; reject leaf parents instead
		// of generating a document libgtk would refuse to load.
		if ((string?)parent.Attribute("class") is not { } cls || !ContainerClasses.Contains(cls)) return false;
		if (cls == "AdwNavigationView" && className != "AdwNavigationPage") return false;
		if (cls == "AdwNavigationSplitView" && className != "AdwNavigationPage") return false;
		if (cls == "AdwNavigationPage" && parent.Elements().Any(e => e.Name.LocalName == "property" && SameName((string?)e.Attribute("name"), "child"))) return false;
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
		if (cls == "AdwNavigationPage") {
			parent.Add(new XElement("property", new XAttribute("name", "child"), created));
			return Commit();
		}
		if (cls == "AdwNavigationSplitView") {
			var existing = parent.Elements().Count(e => e.Name.LocalName == "child" && e.Elements().Any(IsObject));
			if (existing >= 2) return false;
			child.SetAttributeValue("type", existing == 0 ? "sidebar" : "content");
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

	/// <summary>The selected objects as GtkBuilder <c>&lt;child&gt;</c> snippets (the object with its
	/// packing/layout), for the clipboard. An object whose ancestor is also selected travels inside
	/// it; a top-level object has no &lt;child&gt; wrapper and is not copyable.</summary>
	public IReadOnlyList<string>? CopySnippets(IReadOnlyCollection<string> ids)
	{
		var wrappers = new List<XElement>();
		foreach (var id in ids) {
			var element = Find(id);
			if (element?.Parent is not { Name.LocalName: "child" } wrapper || element.Name.LocalName == "template") return null;
			wrappers.Add(wrapper);
		}
		return wrappers.Where(w => !wrappers.Any(other => other != w && w.Ancestors().Contains(other)))
			.Select(w => w.ToString(SaveOptions.DisableFormatting)).ToArray();
	}

	/// <summary>Pastes <c>&lt;child&gt;</c> snippets into <paramref name="targetId"/>, or its nearest
	/// container ancestor when the target is a leaf, after <paramref name="afterId"/>'s child when
	/// given (Duplicate) and otherwise at the end. Every pasted object id that the document already
	/// uses is made unique (<c>button1</c> becomes <c>button2</c>), and id-reference properties
	/// inside the paste follow the rename. All snippets are one undo step; <paramref name="created"/>
	/// lists the pasted top-level object ids.</summary>
	public bool Paste(string targetId, IReadOnlyList<string> snippets, out List<string> created, string? afterId = null)
	{
		created = new List<string>();
		var target = Find(targetId);
		while (target != null && !((string?)target.Attribute("class") is { } cls && ContainerClasses.Contains(cls)))
			target = target.Ancestors().FirstOrDefault(IsObject);
		if (target == null || snippets.Count == 0) return false;
		var parsed = new List<XElement>();
		try {
			foreach (var snippet in snippets) {
				var child = XElement.Parse(snippet, LoadOptions.PreserveWhitespace);
				if (child.Name.LocalName != "child" || child.Elements().Count(IsObject) != 1 || child.Descendants().Any(e => e.Name.LocalName == "template")) return false;
				parsed.Add(child);
			}
		} catch (XmlException) { return false; }
		var used = new HashSet<string>(document.Descendants().Where(e => e.Name.LocalName == "object").Select(e => (string?)e.Attribute("id")).OfType<string>(), StringComparer.Ordinal);
		// One map for the whole paste: a reference from one pasted snippet to an object in another
		// (a label and its mnemonic widget copied together) follows that object's rename too.
		var renames = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var child in parsed) {
			foreach (var obj in child.Descendants().Where(e => e.Name.LocalName == "object")) {
				if ((string?)obj.Attribute("id") is not { } id) continue;
				var unique = id;
				if (!used.Add(id)) {
					var stem = id.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
					var n = 1; while (!used.Add(stem + n)) n++;
					unique = stem + n;
				}
				renames[id] = unique;
				obj.SetAttributeValue("id", unique);
			}
		}
		foreach (var child in parsed) {
			foreach (var property in child.Descendants().Where(IsIdReference))
				if (renames.TryGetValue(property.Value.Trim(), out var renamed)) property.Value = renamed;
			// An id-less object is pasted but has no identity to select.
			if ((string?)child.Elements().First(IsObject).Attribute("id") is { } createdId) created.Add(createdId);
		}
		var anchor = afterId == null ? null : Find(afterId)?.Parent;
		if (anchor?.Name.LocalName == "child" && anchor.Parent == target) anchor.AddAfterSelf(parsed);
		else target.Add(parsed);
		return Commit();
	}

	/// <summary>Wraps sibling objects in a new <paramref name="className"/> placed where the first
	/// of them (in document order) was, keeping their order - the WPF/WinUI designers' Wrap. All ids
	/// must be direct &lt;child&gt; objects of one parent; a single-child container (GtkFrame) takes
	/// exactly one. A child's &lt;layout&gt; is read by the OLD parent's layout manager (a GtkGrid
	/// cell, a GtkFixed position), so the wrapper takes over the first child's and the wrapped
	/// children drop theirs. One undo step; <paramref name="created"/> is the wrapper's id.</summary>
	public bool Wrap(IReadOnlyCollection<string> ids, string className, out string created)
	{
		created = "";
		if (ids.Count == 0 || className is not ("GtkBox" or "GtkFrame") || className == "GtkFrame" && ids.Count != 1)
			return false;
		var wrappers = new List<XElement>();
		foreach (var id in ids) {
			if (Find(id) is not { Parent: { Name.LocalName: "child" } child } || child.Elements().Count(IsObject) != 1)
				return false;
			wrappers.Add(child);
		}
		var parent = wrappers[0].Parent;
		if (parent == null || wrappers.Any(w => w.Parent != parent) || wrappers.Distinct().Count() != wrappers.Count)
			return false;
		var ordered = parent.Elements().Where(wrappers.Contains).ToList();
		var name = UniqueId(className[3..].ToLowerInvariant());
		var container = new XElement("object", new XAttribute("class", className), new XAttribute("id", name));
		if (className == "GtkBox")
			container.Add(new XElement("property", new XAttribute("name", "orientation"), "vertical"));
		if (ordered[0].Elements().First(IsObject).Elements().FirstOrDefault(e => e.Name.LocalName == "layout") is { } layout)
			container.Add(new XElement(layout));
		var replacement = new XElement("child", container);
		ordered[0].AddBeforeSelf(replacement);
		foreach (var child in ordered) {
			child.Remove();
			child.Elements().First(IsObject).Elements().Where(e => e.Name.LocalName == "layout").Remove();
			container.Add(child);
		}
		created = name;
		return Commit();
	}

	static readonly HashSet<string> SingleChildClasses = new(StringComparer.Ordinal) { "GtkFrame", "GtkScrolledWindow", "GtkWindow", "GtkApplicationWindow", "AdwClamp" };

	/// <summary>Replaces a placed container with its &lt;child&gt; objects at its position (the
	/// WPF/WinUI designers' Unwrap). Refused for a root, a container holding objects other than
	/// &lt;child&gt; ones (a property-held child has no &lt;child&gt; slot to move), an empty one, or
	/// several children into a single-child parent. A lone child inherits the container's
	/// &lt;layout&gt;; children drop the layout that addressed the removed container. One undo step.</summary>
	/// <summary>A GtkDropDown's strings, held in its own <c>&lt;items&gt;&lt;item&gt;</c> list (the
	/// GtkBuilder shorthand that creates its GtkStringList model), or null when the object is not a
	/// GtkDropDown or takes its strings from an explicit <c>model</c> property instead.</summary>
	public IReadOnlyList<string>? GetItems(string id)
	{
		var element = Find(id);
		if (element == null || (string?)element.Attribute("class") != "GtkDropDown"
			|| element.Elements().Any(e => e.Name.LocalName == "property" && SameName((string?)e.Attribute("name"), "model")))
			return null;
		return element.Elements().Where(e => e.Name.LocalName == "items").SelectMany(i => i.Elements())
			.Where(e => e.Name.LocalName == "item").Select(e => e.Value).ToList();
	}

	/// <summary>Replaces a GtkDropDown's <c>&lt;items&gt;</c> with <paramref name="items"/>, in order,
	/// as one undo step. An entry with a SourceIndex keeps that item element's attributes
	/// (translatable, context, comments) with the new text; a new entry is <c>translatable="yes"</c>
	/// like other user-visible strings; an empty list removes <c>&lt;items&gt;</c>.</summary>
	public bool SetItems(string id, IReadOnlyList<(int SourceIndex, string Text)> items)
	{
		if (GetItems(id) is not { } current) return false;
		var element = Find(id)!;
		var sources = items.Where(item => item.SourceIndex >= 0).Select(item => item.SourceIndex).ToList();
		if (sources.Any(index => index >= current.Count) || sources.Distinct().Count() != sources.Count) return false;
		var oldItems = element.Elements().Where(e => e.Name.LocalName == "items").SelectMany(i => i.Elements())
			.Where(e => e.Name.LocalName == "item").ToList();
		var rebuilt = items.Select(item => {
			var copy = item.SourceIndex >= 0 ? new XElement(oldItems[item.SourceIndex]) : new XElement("item", new XAttribute("translatable", "yes"));
			copy.Value = item.Text ?? "";
			return copy;
		}).ToList();
		if (rebuilt.Select(e => e.ToString()).SequenceEqual(oldItems.Select(e => e.ToString()))) return true;
		var lists = element.Elements().Where(e => e.Name.LocalName == "items").ToList();
		if (rebuilt.Count == 0) lists.ForEach(list => list.Remove());
		else if (lists.Count == 0) element.Add(new XElement("items", rebuilt));
		else {
			lists.Skip(1).ToList().ForEach(list => list.Remove());
			lists[0].ReplaceNodes(rebuilt);
		}
		return Commit();
	}

	public bool Unwrap(string id)
	{
		if (Find(id) is not { Parent: { Name.LocalName: "child", Parent: { } parent } wrapper } container)
			return false;
		var children = container.Elements().Where(e => e.Name.LocalName == "child" && e.Elements().Any(IsObject)).ToList();
		var heldObjects = container.Elements().Where(e => e.Name.LocalName == "property").SelectMany(p => p.Elements()).Any(IsObject);
		if (children.Count == 0 || heldObjects || children.Any(c => c.Elements().Count(IsObject) != 1)
			|| (string?)parent.Attribute("class") is { } parentClass && SingleChildClasses.Contains(parentClass) && children.Count != 1)
			return false;
		var layout = container.Elements().FirstOrDefault(e => e.Name.LocalName == "layout");
		foreach (var child in children) {
			child.Remove();
			var obj = child.Elements().First(IsObject);
			obj.Elements().Where(e => e.Name.LocalName == "layout").Remove();
			if (children.Count == 1 && layout != null)
				obj.Add(new XElement(layout));
			wrapper.AddBeforeSelf(child);
		}
		wrapper.Remove();
		return Commit();
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
		{ "member-name", "target", "menu-model", "widget", "popover", "action-widget", "mnemonic-widget" };
	static bool IsIdReference(XElement e)
		=> e.Name.LocalName == "property"
		   && IdReferenceProperties.Contains((string?)e.Attribute("name") ?? "");
	string UniqueId(string prefix) { for (var i = 1; ; i++) if (Find(prefix + i) == null) return prefix + i; }
	static bool IsIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && (char.IsLetter(value[0]) || value[0] == '_') && value.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');
	sealed class Utf8StringWriter : StringWriter { public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(false); }
}
