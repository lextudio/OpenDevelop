using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.GtkDesigner.Host;

/// <summary>
/// Typed GTK property and signal metadata for the Properties pad, from the installed GIR
/// (<see cref="GirCatalog"/>). Replaces a hand-written list of eleven string properties: every
/// writable property of the class, its ancestors and interfaces is offered with its real type,
/// enum choices, default and documentation, grouped by the class that declares it.
/// </summary>
static class GtkPropertyMetadata
{
	static readonly Lazy<(GirCatalog? Catalog, string Diagnostic)> catalog = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

	/// <summary>Starts loading off the RPC path; the GIR set is ~20 MB of XML.</summary>
	public static void Preload() => ThreadPool.QueueUserWorkItem(_ => _ = catalog.Value);

	public static GirCatalog? Catalog => catalog.Value.Catalog;
	public static string Diagnostic => catalog.Value.Diagnostic;

	static (GirCatalog?, string) Load()
	{
		var directory = FindGirDirectory();
		if (directory == null)
			return (null, "GTK introspection data (Gtk-4.0.gir) was not found, so the Properties pad shows only the properties written in the file. Install the GTK 4 introspection files (MSYS2: they ship with the gtk4 package; Homebrew: gtk4; Debian/Ubuntu: gir1.2-gtk-4.0) or set GTK4_GIR_DIR.");
		try {
			// Libadwaita's catalogue joins when its GIR is installed; documents still opt in per file.
			var roots = File.Exists(Path.Combine(directory, "Adw-1.gir")) ? new[] { "Gtk-4.0", "Adw-1" } : new[] { "Gtk-4.0" };
			return (GirCatalog.Load(directory, roots), "");
		} catch (Exception ex) {
			return (null, "GTK introspection data in " + directory + " could not be read: " + ex.Message);
		}
	}

	/// <summary>GTK4_GIR_DIR, else share/gir-1.0 beside the libgtk this process loads (found on
	/// PATH on Windows: the IDE puts the located GTK bin folder first), else the OS locations.</summary>
	static string? FindGirDirectory()
	{
		static bool HasGtkGir(string? dir) => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "Gtk-4.0.gir"));
		var explicitDir = Environment.GetEnvironmentVariable("GTK4_GIR_DIR");
		if (HasGtkGir(explicitDir)) return explicitDir;
		if (OperatingSystem.IsWindows()) {
			foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
				if (!File.Exists(Path.Combine(entry, "libgtk-4-1.dll"))) continue;
				var candidate = Path.GetFullPath(Path.Combine(entry, "..", "share", "gir-1.0"));
				if (HasGtkGir(candidate)) return candidate;
			}
		}
		foreach (var candidate in new[] { "/opt/homebrew/share/gir-1.0", "/usr/local/share/gir-1.0", "/usr/share/gir-1.0", "/usr/lib64/gir-1.0" })
			if (HasGtkGir(candidate)) return candidate;
		return null;
	}

	/// <summary>User-visible text GtkBuilder should mark translatable when first written - the
	/// same judgement Stetic's objects.xml encoded with its translatable="yes" attribute.</summary>
	static readonly HashSet<string> TranslatableProperties = new(StringComparer.Ordinal) {
		"label", "title", "text", "tooltip-text", "tooltip-markup", "placeholder-text", "secondary-text",
		"primary-icon-tooltip-text", "secondary-icon-tooltip-text", "subtitle", "description", "heading", "body"
	};

	public static bool IsTranslatable(string builderClass, string propertyName)
	{
		var name = propertyName.Replace('_', '-');
		if (!TranslatableProperties.Contains(name)) return false;
		return Catalog?.FindProperty(builderClass, name) is not { } p || p.Kind == GirValueKind.String;
	}

	/// <summary>The prefix that marks a child's &lt;layout&gt; property in DDP property names.</summary>
	public const string LayoutPrefix = "layout:";

	/// <summary>Which LayoutChild class a container's layout manager uses. GIR describes the
	/// LayoutChild classes but not which widget uses which manager, so this is the override
	/// table the technote calls for; subclasses (IsA) inherit their parent's entry.</summary>
	static readonly (string Container, string LayoutChild)[] LayoutChildren = {
		("GtkGrid", "GtkGridLayoutChild"),
		("GtkOverlay", "GtkOverlayLayoutChild"),
		("GtkFixed", "GtkFixedLayoutChild"),
	};

	public static string? LayoutChildClass(string? containerClass)
	{
		if (string.IsNullOrEmpty(containerClass) || Catalog is not { } cat) return null;
		foreach (var (container, layoutChild) in LayoutChildren)
			if (cat.IsA(containerClass, container)) return layoutChild;
		return null;
	}

	/// <summary>Every property a node offers: each one written in the document (so nothing in the
	/// file is hidden), plus every writable, non-deprecated property the catalogue knows for the
	/// class. Kinds without an editor stay visible only when the document already sets them.
	/// A child of a layout-managed container also gets that manager's &lt;layout&gt; properties.</summary>
	public static List<DesignerPropertyInfo> PropertiesFor(GtkUiNode node, string? parentClass)
	{
		var result = OwnPropertiesFor(node);
		var layoutClass = LayoutChildClass(parentClass);
		var layout = node.Layout ?? new Dictionary<string, string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		if (layoutClass != null && Catalog is { } cat && cat.FindByTypeName(layoutClass) is { } layoutChild) {
			// Only the LayoutChild subclass's own properties: GtkLayoutChild's layout-manager and
			// child-widget are construct-only plumbing, not something a .ui file sets.
			foreach (var p in layoutChild.OwnProperties.Where(p => p.Writable && !p.Deprecated)) {
				seen.Add(p.Name);
				var written = layout.FirstOrDefault(kv => kv.Key.Replace('_', '-') == p.Name);
				var info = Info(p, written.Key != null ? written.Value : null, cat, layoutChild.QualifiedName);
				info.Name = LayoutPrefix + p.Name;
				info.Category = "Layout (" + parentClass + ")";
				result.Add(info);
			}
		}
		foreach (var (name, value) in layout)
			if (seen.Add(name.Replace('_', '-')))
				result.Add(new DesignerPropertyInfo { Name = LayoutPrefix + name, DisplayName = name, Category = "Layout" + (parentClass == null ? "" : " (" + parentClass + ")"), TypeName = "utf8", Kind = "String", Value = value, ShouldSerialize = true });
		return result;
	}

	/// <summary>The everyday properties of a class, grouped under "Common" so they lead the grid -
	/// the role Stetic's hand-written objects.xml &lt;itemgroup&gt;s played. A class inherits its
	/// ancestors' entries; everything else stays under the class that declares it.</summary>
	static readonly Dictionary<string, string[]> CommonProperties = new(StringComparer.Ordinal) {
		["GtkWidget"] = new[] { "visible", "sensitive", "halign", "valign", "hexpand", "vexpand", "margin-start", "margin-end", "margin-top", "margin-bottom", "tooltip-text", "css-classes", "width-request", "height-request" },
		["GtkWindow"] = new[] { "title", "default-width", "default-height", "resizable", "modal", "decorated" },
		["GtkButton"] = new[] { "label", "icon-name", "has-frame", "use-underline" },
		["GtkLabel"] = new[] { "label", "use-markup", "wrap", "justify", "xalign", "ellipsize", "selectable" },
		["GtkEntry"] = new[] { "placeholder-text", "visibility", "max-length", "input-purpose" },
		["GtkEditable"] = new[] { "text", "editable" },
		["GtkCheckButton"] = new[] { "label", "active", "group" },
		["GtkToggleButton"] = new[] { "active" },
		["GtkBox"] = new[] { "spacing", "homogeneous" },
		["GtkOrientable"] = new[] { "orientation" },
		["GtkGrid"] = new[] { "row-spacing", "column-spacing", "row-homogeneous", "column-homogeneous" },
		["GtkImage"] = new[] { "icon-name", "file", "pixel-size" },
		["GtkScale"] = new[] { "digits", "draw-value" },
		["GtkRange"] = new[] { "adjustment", "inverted" },
		["GtkSpinButton"] = new[] { "adjustment", "digits", "numeric" },
		["GtkScrolledWindow"] = new[] { "hscrollbar-policy", "vscrollbar-policy", "has-frame" },
		["GtkPaned"] = new[] { "position", "wide-handle" },
		["GtkStack"] = new[] { "transition-type", "hhomogeneous", "vhomogeneous" },
		["GtkTextView"] = new[] { "editable", "wrap-mode", "monospace" },
		["GtkSwitch"] = new[] { "active" },
	};

	/// <summary>Properties that only take effect while another has a given value - Stetic's
	/// objects.xml disabled-if/invisible-if. They stay listed (discoverable) but read-only, with
	/// the condition in the description, until the controlling property enables them; one the file
	/// already sets stays editable.</summary>
	static readonly (string Class, string Property, string Controller, string EnabledWhen)[] Conditional = {
		("GtkLabel", "wrap-mode", "wrap", "True"),
		("GtkLabel", "natural-wrap-mode", "wrap", "True"),
		("GtkLabel", "lines", "wrap", "True"),
		("GtkEntry", "invisible-char", "visibility", "False"),
		("GtkEntry", "invisible-char-set", "visibility", "False"),
		("GtkScale", "value-pos", "draw-value", "True"),
		("GtkScrolledWindow", "max-content-width", "propagate-natural-width", "True"),
		("GtkScrolledWindow", "max-content-height", "propagate-natural-height", "True"),
	};

	static void ApplyConditions(GirCatalog cat, string builderClass, List<DesignerPropertyInfo> properties)
	{
		foreach (var (cls, property, controller, enabledWhen) in Conditional) {
			if (!cat.IsA(builderClass, cls)) continue;
			var target = properties.FirstOrDefault(p => p.Name == property);
			var control = properties.FirstOrDefault(p => p.Name == controller);
			if (target == null || control == null || target.ShouldSerialize || target.IsReadOnly) continue;
			if (string.Equals(control.Value, enabledWhen, StringComparison.OrdinalIgnoreCase)) continue;
			target.IsReadOnly = true;
			target.Description = (string.IsNullOrEmpty(target.Description) ? "" : target.Description + " ") + $"Applies when {controller} is {enabledWhen}.";
		}
	}

	static bool IsCommon(GirCatalog cat, string builderClass, string propertyName)
	{
		foreach (var c in cat.Lineage(builderClass)) {
			if (CommonProperties.TryGetValue(c.TypeName, out var names) && names.Contains(propertyName)) return true;
			foreach (var i in c.Interfaces)
				if (cat.FindByQualifiedName(i) is { } iface && CommonProperties.TryGetValue(iface.TypeName, out var inames) && inames.Contains(propertyName)) return true;
		}
		return false;
	}

	static List<DesignerPropertyInfo> OwnPropertiesFor(GtkUiNode node)
	{
		var result = new List<DesignerPropertyInfo>();
		var cat = Catalog;
		var known = cat?.PropertiesOf(node.ClassName) ?? Array.Empty<GirProperty>();
		var owner = cat?.FindByTypeName(node.ClassName)?.QualifiedName;
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var p in known) {
			var written = node.Properties.FirstOrDefault(kv => kv.Key.Replace('_', '-') == p.Name);
			var isWritten = written.Key != null;
			if (!isWritten && (!p.Writable || p.Deprecated || p.Kind == GirValueKind.Unsupported)) continue;
			seen.Add(p.Name);
			var info = Info(p, isWritten ? written.Value : null, cat!, owner);
			if (IsCommon(cat!, node.ClassName, p.Name)) info.Category = "Common";
			result.Add(info);
		}
		foreach (var (name, value) in node.Properties) {
			if (!seen.Add(name.Replace('_', '-'))) continue;
			// In the file but unknown to the catalogue (no GIR, a custom widget, a typo): editable text.
			result.Add(new DesignerPropertyInfo { Name = name, DisplayName = name, Category = cat == null ? "GTK" : "Other", TypeName = "utf8", Kind = "String", Value = value, ShouldSerialize = true });
		}
		if (cat != null) ApplyConditions(cat, node.ClassName, result);
		return result;
	}

	/// <param name="owner">The concrete class (GIR name) whose GParamSpec gives a numeric range:
	/// interface properties are only findable through a class that implements them.</param>
	static DesignerPropertyInfo Info(GirProperty p, string? written, GirCatalog cat, string? owner)
	{
		var declaring = cat.FindByQualifiedName(p.DeclaringType)?.TypeName ?? p.DeclaringType;
		var info = new DesignerPropertyInfo {
			Name = p.Name,
			DisplayName = p.Name,
			Description = p.Description,
			Category = declaring,
			TypeName = p.TypeName,
			Kind = p.Kind switch {
				GirValueKind.Boolean => "Boolean",
				GirValueKind.Integer or GirValueKind.Unsigned or GirValueKind.Double => "Number",
				GirValueKind.Enum or GirValueKind.Flags => "Enum",
				GirValueKind.Color => "Color",
				GirValueKind.Reference => "Reference",
				GirValueKind.Path => "String",
				GirValueKind.Unsupported => "Unsupported",
				_ => "String"
			},
			// Flags carry their members in AllowedValues too, but combine with '|': only a plain
			// enum is a single exclusive choice.
			IsEnum = p.Kind == GirValueKind.Enum,
			// Unwritten properties show GTK's default, so the grid never invents a value.
			Value = written ?? p.DefaultValue ?? "",
			ShouldSerialize = written != null,
			IsReadOnly = !p.Writable || p.Kind == GirValueKind.Unsupported
		};
		if (p.Enum != null) info.AllowedValues.AddRange(p.Enum.Members.Select(m => m.Nick).Distinct(StringComparer.Ordinal));
		if (info.Kind == "Number" && owner != null && GtkParamRanges.RangeOf(owner, p.Name) is { } range && GtkParamRanges.Describe(range) is { } text)
			info.Description = (string.IsNullOrEmpty(info.Description) ? "" : info.Description + " ") + "Range: " + text + ".";
		return info;
	}

	/// <summary>Signals for the Properties pad's events: the class's, its ancestors' and its
	/// interfaces', from the catalogue; the small built-in list when there is no GIR.</summary>
	public static IEnumerable<string> SignalsFor(string builderClass, Func<string, IEnumerable<string>> fallback)
	{
		var cat = Catalog;
		if (cat?.FindByTypeName(builderClass) == null) return fallback(builderClass);
		return cat.SignalsOf(builderClass).Where(s => !s.Deprecated).Select(s => s.Name);
	}

	/// <summary>Type-checks an edit and returns the canonical GtkBuilder text; null means reset.
	/// Throws with a readable reason for a value GTK would reject, before the document changes.</summary>
	public static string? Validate(GtkUiNode? node, string propertyName, string? value, string? parentClass = null)
	{
		if (value == null || node == null) return value;
		var isLayout = propertyName.StartsWith(LayoutPrefix, StringComparison.Ordinal);
		var ownerClass = isLayout ? LayoutChildClass(parentClass) : node.ClassName;
		var name = isLayout ? propertyName.Substring(LayoutPrefix.Length) : propertyName;
		if (ownerClass == null || Catalog?.FindProperty(ownerClass, name) is not { } property)
			return value;
		if (!property.Writable)
			throw new InvalidOperationException($"{node.ClassName}:{property.Name} is read-only.");
		if (!GirCatalog.TryNormalizeValue(property, value, out var normalized, out var error))
			throw new InvalidOperationException($"{node.ClassName}:{property.Name}: {error}");
		// GTK's own limits (GParamSpec), which GIR does not carry: a value outside them would be
		// clamped or rejected by GtkBuilder at runtime, so it never enters the document.
		if (property.Kind is GirValueKind.Integer or GirValueKind.Unsigned or GirValueKind.Double
			&& Catalog?.FindByTypeName(ownerClass)?.QualifiedName is { } owner && GtkParamRanges.RangeOf(owner, property.Name) is { } range
			&& double.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
			&& (number < range.Min || number > range.Max))
			throw new InvalidOperationException($"{node.ClassName}:{property.Name}: {normalized} is outside {GtkParamRanges.Describe(range) ?? (range.Min + " to " + range.Max)}.");
		return normalized;
	}
}
