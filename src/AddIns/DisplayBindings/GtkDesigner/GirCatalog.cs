using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ICSharpCode.GtkDesigner;

/// <summary>How a GTK property's value is edited and validated. Maps onto DDP's
/// <c>DesignerPropertyInfo.Kind</c> ("Boolean", "Number", "Enum", "String", "Color",
/// "Reference", "Unsupported"); flags travel as "Enum" with <see cref="GirProperty.IsFlags"/>.</summary>
/// <summary>How a GTK property's value is edited and validated. <see cref="Path"/> is a file/URI
/// that GtkBuilder turns into a GFile or texture (relative paths resolve against the .ui's folder).</summary>
public enum GirValueKind { String, Boolean, Integer, Unsigned, Double, Enum, Flags, Color, Reference, Path, Unsupported }

public sealed record GirEnumMember(string Nick, string Name, string CIdentifier, long Value);

public sealed record GirEnum(string QualifiedName, string TypeName, bool IsFlags, IReadOnlyList<GirEnumMember> Members)
{
	/// <summary>The GtkBuilder spelling of a member given any of its spellings (nick, GIR name or
	/// C identifier, case-insensitive), or null.</summary>
	public string? Normalize(string value)
	{
		var v = value.Trim();
		var member = Members.FirstOrDefault(m => string.Equals(m.Nick, v, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(m.Name, v, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(m.CIdentifier, v, StringComparison.OrdinalIgnoreCase));
		return member?.Nick;
	}
}

public sealed record GirProperty(
	string Name, string DeclaringType, string TypeName, GirValueKind Kind, GirEnum? Enum,
	bool Writable, bool ConstructOnly, bool Deprecated, string? DefaultValue, string Description)
{
	public bool IsFlags => Kind == GirValueKind.Flags;
}

/// <summary>A GObject signal. <paramref name="HasParameters"/> decides whether Gir.Core gives its
/// handler a typed <c>{Declaring}.{Name}SignalArgs</c> or plain <c>System.EventArgs</c>;
/// <paramref name="ReturnType"/> is the GIR type name, "none" for void.</summary>
public sealed record GirSignal(string Name, string DeclaringType, bool Deprecated, bool HasParameters = false, string ReturnType = "none");

public sealed record GirClass(
	string QualifiedName, string TypeName, string? Parent, bool Abstract, bool Deprecated,
	IReadOnlyList<string> Interfaces, IReadOnlyList<GirProperty> OwnProperties, IReadOnlyList<GirSignal> OwnSignals, string Description);

/// <summary>
/// Binding-neutral GTK type catalogue read from installed GObject Introspection XML
/// (doc/technotes/gtk-designer.md, "Binding and metadata baseline"). Pure XML: no GTK, no
/// Gir.Core, so it runs in the design host and in unit tests alike. Covers classes, interfaces
/// (whose properties, e.g. GtkOrientable:orientation, a class inherits), enumerations and
/// bitfields, across the loaded namespaces (Gtk, and its Gdk/Gsk/Pango/Gio/GObject parents).
/// </summary>
public sealed class GirCatalog
{
	static readonly XNamespace Core = "http://www.gtk.org/introspection/core/1.0";
	static readonly XNamespace C = "http://www.gtk.org/introspection/c/1.0";
	static readonly XNamespace GLibNs = "http://www.gtk.org/introspection/glib/1.0";

	readonly Dictionary<string, GirClass> classesByQualifiedName = new(StringComparer.Ordinal);
	readonly Dictionary<string, GirClass> classesByTypeName = new(StringComparer.Ordinal);
	readonly Dictionary<string, GirEnum> enums = new(StringComparer.Ordinal);
	readonly Dictionary<string, (string Qualified, List<XElement> Properties, List<XElement> Signals, string? Parent, string Ns)> pending = new(StringComparer.Ordinal);

	public string GtkVersion { get; private set; } = "";

	/// <summary>Loads <c>Gtk-4.0.gir</c> and the namespaces it includes, from <paramref name="girDirectory"/>.</summary>
	public static GirCatalog Load(string girDirectory, string rootRepository = "Gtk-4.0") => Load(girDirectory, new[] { rootRepository });

	/// <summary>Loads several root repositories (e.g. Gtk-4.0 and, when installed, Adw-1) and
	/// everything they include. The first root that exists sets <see cref="GtkVersion"/>.</summary>
	public static GirCatalog Load(string girDirectory, IEnumerable<string> rootRepositories)
	{
		var catalog = new GirCatalog();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var roots = rootRepositories.ToList();
		var rootRepository = roots.FirstOrDefault() ?? "Gtk-4.0";
		var queue = new Queue<string>(roots);
		var documents = new List<(string Ns, XElement Namespace)>();
		while (queue.Count > 0) {
			var repository = queue.Dequeue();
			if (!seen.Add(repository)) continue;
			var path = Path.Combine(girDirectory, repository + ".gir");
			if (!File.Exists(path)) continue;   // an include we do not need (cairo, HarfBuzz ...) may be absent
			var doc = XDocument.Load(path);
			var ns = doc.Root?.Element(Core + "namespace");
			if (ns == null) continue;
			if (repository == rootRepository) catalog.GtkVersion = (string?)ns.Attribute("version") ?? "";
			documents.Add(((string?)ns.Attribute("name") ?? "", ns));
			foreach (var include in doc.Root!.Elements(Core + "include"))
				queue.Enqueue((string?)include.Attribute("name") + "-" + (string?)include.Attribute("version"));
		}
		// Enums first: properties resolve their type against them.
		foreach (var (nsName, ns) in documents)
			foreach (var e in ns.Elements(Core + "enumeration").Concat(ns.Elements(Core + "bitfield")))
				catalog.AddEnum(nsName, e);
		foreach (var (nsName, ns) in documents)
			foreach (var type in ns.Elements(Core + "class").Concat(ns.Elements(Core + "interface")))
				catalog.AddClass(nsName, type);
		return catalog;
	}

	void AddEnum(string ns, XElement e)
	{
		var qualified = ns + "." + (string?)e.Attribute("name");
		var members = e.Elements(Core + "member").Select(m => new GirEnumMember(
			(string?)m.Attribute(GLibNs + "nick") ?? ((string?)m.Attribute("name") ?? "").Replace('_', '-'),
			(string?)m.Attribute("name") ?? "",
			(string?)m.Attribute(C + "identifier") ?? "",
			long.TryParse((string?)m.Attribute("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0)).ToList();
		enums[qualified] = new GirEnum(qualified, (string?)e.Attribute(GLibNs + "type-name") ?? (string?)e.Attribute(C + "type") ?? qualified,
			e.Name == Core + "bitfield", members);
	}

	void AddClass(string ns, XElement type)
	{
		var qualified = ns + "." + (string?)type.Attribute("name");
		var parent = Qualify(ns, (string?)type.Attribute("parent"));
		var interfaces = type.Elements(Core + "implements").Select(i => Qualify(ns, (string?)i.Attribute("name"))!).Where(i => i != null).ToList();
		// Interfaces' prerequisites are not parents; an interface's properties are its own.
		var properties = type.Elements(Core + "property").Select(p => Property(ns, qualified, p)).ToList();
		var signals = type.Elements(GLibNs + "signal").Select(s => new GirSignal((string?)s.Attribute("name") ?? "", qualified, (string?)s.Attribute("deprecated") == "1",
			s.Element(Core + "parameters")?.Elements(Core + "parameter").Any() == true,
			(string?)s.Element(Core + "return-value")?.Element(Core + "type")?.Attribute("name") ?? "none")).ToList();
		var girClass = new GirClass(qualified, (string?)type.Attribute(GLibNs + "type-name") ?? (string?)type.Attribute(C + "type") ?? qualified,
			parent, (string?)type.Attribute("abstract") == "1" || type.Name == Core + "interface", (string?)type.Attribute("deprecated") == "1",
			interfaces, properties, signals, Doc(type));
		classesByQualifiedName[qualified] = girClass;
		classesByTypeName[girClass.TypeName] = girClass;
	}

	static string? Qualify(string ns, string? name) => string.IsNullOrEmpty(name) ? null : name!.Contains('.') ? name : ns + "." + name;

	static string Doc(XElement e)
	{
		var text = (string?)e.Element(Core + "doc") ?? "";
		var paragraph = text.Split(new[] { "\n\n" }, StringSplitOptions.None)[0];
		return paragraph.Replace('\n', ' ').Trim();
	}

	GirProperty Property(string ns, string declaring, XElement p)
	{
		var typeName = (string?)p.Element(Core + "type")?.Attribute("name") ?? (p.Element(Core + "array") != null ? "array" : "");
		var qualifiedType = typeName.Contains('.') || IsFundamental(typeName) ? typeName : ns + "." + typeName;
		enums.TryGetValue(qualifiedType, out var girEnum);
		var kind = girEnum != null ? (girEnum.IsFlags ? GirValueKind.Flags : GirValueKind.Enum) : KindOf(qualifiedType);
		var defaultValue = (string?)p.Attribute("default-value");
		if (defaultValue != null) defaultValue = NormalizeDefault(kind, girEnum, defaultValue);
		return new GirProperty((string?)p.Attribute("name") ?? "", declaring, qualifiedType, kind, girEnum,
			(string?)p.Attribute("writable") == "1", (string?)p.Attribute("construct-only") == "1",
			(string?)p.Attribute("deprecated") == "1", defaultValue, Doc(p));
	}

	static bool IsFundamental(string name) => name is "gboolean" or "gint" or "guint" or "gint8" or "guint8" or "gint16" or "guint16"
		or "gint32" or "guint32" or "gint64" or "guint64" or "glong" or "gulong" or "gsize" or "gssize" or "gchar" or "guchar"
		or "gunichar" or "gfloat" or "gdouble" or "utf8" or "filename" or "gpointer" or "GType" or "array" or "";

	GirValueKind KindOf(string type) => type switch {
		"gboolean" => GirValueKind.Boolean,
		"gint" or "gint8" or "gint16" or "gint32" or "gint64" or "glong" or "gssize" or "gchar" => GirValueKind.Integer,
		"guint" or "guint8" or "guint16" or "guint32" or "guint64" or "gulong" or "gsize" or "guchar" or "gunichar" => GirValueKind.Unsigned,
		"gfloat" or "gdouble" => GirValueKind.Double,
		"utf8" or "filename" => GirValueKind.String,
		// Boxed types GtkBuilder parses from text (gsk_transform_parse: "translate(10, 20) rotate(45)").
		"Gsk.Transform" => GirValueKind.String,
		// GtkBuilder builds these from a file name, path or URI (gtk_builder_value_from_string).
		"Gio.File" or "Gdk.Paintable" or "Gdk.Texture" => GirValueKind.Path,
		"Gdk.RGBA" => GirValueKind.Color,
		_ when IsObjectType(type) => GirValueKind.Reference,
		_ => GirValueKind.Unsupported
	};

	// Resolved lazily: classes are added after properties are read, so a class-typed property is
	// recognised by name shape here and confirmed against the class table at lookup time.
	static bool IsObjectType(string type) => type.Contains('.') && !type.StartsWith("GLib.", StringComparison.Ordinal)
		&& type is not ("Gdk.RGBA" or "Pango.AttrList" or "Pango.FontDescription" or "Pango.TabArray" or "GObject.Closure" or "GLib.Variant");

	static string NormalizeDefault(GirValueKind kind, GirEnum? girEnum, string value)
	{
		switch (kind) {
			case GirValueKind.Boolean: return value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? "True" : value.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ? "False" : value;
			case GirValueKind.Enum: return girEnum?.Normalize(value) ?? value;
			case GirValueKind.Flags:
				var parts = value.Split('|').Select(part => girEnum?.Normalize(part.Trim())).Where(p => p != null).ToArray();
				return parts.Length == 0 ? "" : string.Join("|", parts);
			case GirValueKind.String: return value == "NULL" ? "" : value.Trim('"');
			case GirValueKind.Reference: return "";
			default: return value;
		}
	}

	public GirClass? FindByTypeName(string builderClass) => classesByTypeName.TryGetValue(builderClass, out var c) ? c : null;
	public GirClass? FindByQualifiedName(string qualified) => classesByQualifiedName.TryGetValue(qualified, out var c) ? c : null;
	public GirEnum? FindEnum(string qualified) => enums.TryGetValue(qualified, out var e) ? e : null;
	public IEnumerable<GirClass> Classes => classesByQualifiedName.Values;

	/// <summary>The GIR namespace of a class ("Gtk", "Adw"), from its qualified name.</summary>
	public static string NamespaceOf(GirClass c) => c.QualifiedName.Substring(0, Math.Max(0, c.QualifiedName.IndexOf('.')));

	/// <summary>The class and its ancestors, most-derived first.</summary>
	public IEnumerable<GirClass> Lineage(string builderClass)
	{
		for (var current = FindByTypeName(builderClass); current != null; current = current.Parent == null ? null : FindByQualifiedName(current.Parent))
			yield return current;
	}

	/// <summary>Every property an instance of <paramref name="builderClass"/> has - own, inherited,
	/// and from implemented interfaces - most-derived first, each name once.</summary>
	public IReadOnlyList<GirProperty> PropertiesOf(string builderClass)
	{
		var result = new List<GirProperty>();
		var names = new HashSet<string>(StringComparer.Ordinal);
		void AddFrom(GirClass c) { foreach (var p in c.OwnProperties) if (names.Add(p.Name)) result.Add(p); }
		foreach (var c in Lineage(builderClass)) {
			AddFrom(c);
			foreach (var i in c.Interfaces) if (FindByQualifiedName(i) is { } iface) AddFrom(iface);
		}
		return result;
	}

	public GirProperty? FindProperty(string builderClass, string propertyName)
	{
		var name = propertyName.Replace('_', '-');
		return PropertiesOf(builderClass).FirstOrDefault(p => p.Name == name);
	}

	/// <summary>Every signal of the class, its ancestors and interfaces, most-derived first.</summary>
	public IReadOnlyList<GirSignal> SignalsOf(string builderClass)
	{
		var result = new List<GirSignal>(); var names = new HashSet<string>(StringComparer.Ordinal);
		foreach (var c in Lineage(builderClass)) {
			foreach (var s in c.OwnSignals) if (names.Add(s.Name)) result.Add(s);
			foreach (var i in c.Interfaces) if (FindByQualifiedName(i) is { } iface) foreach (var s in iface.OwnSignals) if (names.Add(s.Name)) result.Add(s);
		}
		return result;
	}

	/// <summary>Whether <paramref name="builderClass"/> is (or derives from) <paramref name="ancestorTypeName"/>.</summary>
	public bool IsA(string builderClass, string ancestorTypeName) => Lineage(builderClass).Any(c => c.TypeName == ancestorTypeName);

	/// <summary>
	/// Checks and canonicalises a value for GtkBuilder before it enters the document ("values are
	/// parsed by type before source mutation; invalid text never enters the document"). Returns
	/// false with a reason for a value GTK would reject.
	/// </summary>
	public static bool TryNormalizeValue(GirProperty property, string value, out string normalized, out string error)
	{
		normalized = value; error = "";
		var v = value.Trim();
		switch (property.Kind) {
			case GirValueKind.Boolean:
				if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("t", StringComparison.OrdinalIgnoreCase) || v.Equals("y", StringComparison.OrdinalIgnoreCase)) { normalized = "True"; return true; }
				if (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("no", StringComparison.OrdinalIgnoreCase) || v == "0" || v.Equals("f", StringComparison.OrdinalIgnoreCase) || v.Equals("n", StringComparison.OrdinalIgnoreCase)) { normalized = "False"; return true; }
				error = $"'{value}' is not a boolean (True/False)."; return false;
			case GirValueKind.Integer:
				if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) { normalized = i.ToString(CultureInfo.InvariantCulture); return true; }
				error = $"'{value}' is not an integer."; return false;
			case GirValueKind.Unsigned:
				if (ulong.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u)) { normalized = u.ToString(CultureInfo.InvariantCulture); return true; }
				error = $"'{value}' is not a non-negative integer."; return false;
			case GirValueKind.Double:
				if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)) { normalized = d.ToString("R", CultureInfo.InvariantCulture); return true; }
				error = $"'{value}' is not a number."; return false;
			case GirValueKind.Enum:
				if (property.Enum?.Normalize(v) is { } nick) { normalized = nick; return true; }
				error = $"'{value}' is not one of: {string.Join(", ", property.Enum?.Members.Select(m => m.Nick) ?? Array.Empty<string>())}."; return false;
			case GirValueKind.Flags:
				if (v.Length == 0) { normalized = ""; return true; }
				var parts = v.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(part => (part, nick: property.Enum?.Normalize(part))).ToArray();
				if (parts.FirstOrDefault(p => p.nick == null) is { part: { } bad }) { error = $"'{bad}' is not a flag of {property.Enum?.QualifiedName}."; return false; }
				normalized = string.Join("|", parts.Select(p => p.nick)); return true;
			case GirValueKind.Color:
				// GtkBuilder parses Gdk.RGBA through gdk_rgba_parse: names, #rgb/#rrggbb(aa), rgb()/rgba().
				if (v.Length > 0) { normalized = v; return true; }
				error = "A color is required."; return false;
			default:
				return true;
		}
	}
}
