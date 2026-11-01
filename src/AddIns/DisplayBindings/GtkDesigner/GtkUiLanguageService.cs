using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace ICSharpCode.GtkDesigner;

/// <summary>
/// The in-process language service for GtkBuilder <c>.ui</c> files, registered on
/// <see cref="LanguageServiceRegistry"/> for the ".ui" extension.
///
/// A dedicated GtkBuilder language server does not exist: GNOME Builder and the VS Code XML
/// extension both use a generic XML server with GTK's <c>gtk4builder.rng</c>, which checks element
/// nesting only - <c>class=</c> and <c>property name=</c> are free text to RELAX NG. OpenDevelop
/// already holds what a real one needs, so this service answers from it directly: the installed
/// GIR (<see cref="GirCatalog"/>, through <see cref="GtkGirCatalog"/>) supplies classes,
/// properties, signals and enumerations, and <see cref="GtkUiDocument"/> supplies the parsed
/// document with exact spans. Completion, hover, diagnostics, outline and id navigation/rename
/// therefore know GTK and libadwaita, not just XML. The document is plain text: a template or an
/// embedded resource works the same way.
/// </summary>
public sealed class GtkUiLanguageService : ILanguageService, ILanguageServiceDocumentLifecycle
{
	readonly ConcurrentDictionary<DocumentId, string> documents = new();
	long revision;

	/// <summary>Element children per parent element, from GTK's own <c>gtk4builder.rng</c>.</summary>
	static readonly Dictionary<string, string[]> ElementChildren = new(StringComparer.Ordinal) {
		["interface"] = new[] { "requires", "object", "template", "menu" },
		["object"] = new[] { "property", "signal", "child", "constraints" },
		["template"] = new[] { "property", "signal", "child" },
		["child"] = new[] { "object" },
		["property"] = new[] { "object", "constant", "lookup", "closure", "menu" },
		["menu"] = new[] { "item", "section", "submenu" },
		["item"] = new[] { "attribute", "link" },
		["section"] = new[] { "attribute", "item", "section", "submenu" },
		["submenu"] = new[] { "attribute", "item", "section", "submenu" },
		["link"] = new[] { "item" },
		["lookup"] = new[] { "constant" },
		["closure"] = new[] { "constant", "lookup" },
		["constraints"] = new[] { "guide", "constraint" },
	};

	/// <summary>Attributes per element, from <c>gtk4builder.rng</c>.</summary>
	static readonly Dictionary<string, string[]> ElementAttributes = new(StringComparer.Ordinal) {
		["interface"] = new[] { "domain" },
		["requires"] = new[] { "lib", "version" },
		["object"] = new[] { "class", "id", "type-func", "constructor" },
		["template"] = new[] { "class", "parent" },
		["property"] = new[] { "name", "translatable", "comments", "context", "bind-source", "bind-property", "bind-flags" },
		["signal"] = new[] { "name", "handler", "after", "swapped", "object" },
		["child"] = new[] { "type", "internal-child" },
		["menu"] = new[] { "id", "domain" },
		["item"] = new[] { "id" },
		["attribute"] = new[] { "name", "type", "translatable", "context", "comments" },
		["link"] = new[] { "id", "name" },
		["section"] = new[] { "id" },
		["submenu"] = new[] { "id" },
		["lookup"] = new[] { "name", "type" },
		["constant"] = new[] { "type", "translatable", "context", "comments" },
		["closure"] = new[] { "type", "function" },
		["guide"] = new[] { "name", "min-width", "min-height", "nat-width", "nat-height", "max-width", "max-height", "strength" },
		["constraint"] = new[] { "target", "target-attribute", "source", "source-attribute", "relation", "constant", "multiplier", "strength" },
	};

	/// <summary>The GtkBuildable child slots each container accepts, from GTK's own
	/// <c>*_buildable_add_child</c> implementations.</summary>
	static readonly Dictionary<string, string[]> ChildSlots = new(StringComparer.Ordinal) {
		["GtkHeaderBar"] = new[] { "start", "end", "title" },
		["AdwHeaderBar"] = new[] { "start", "end" },
		["GtkNotebook"] = new[] { "tab", "action-start", "action-end" },
		["GtkOverlay"] = new[] { "overlay" },
		["GtkWindow"] = new[] { "titlebar" },
		["GtkApplicationWindow"] = new[] { "titlebar" },
		["GtkDialog"] = new[] { "titlebar" },
		["AdwWindow"] = new[] { "titlebar" },
		["AdwApplicationWindow"] = new[] { "titlebar" },
		["AdwToolbarView"] = new[] { "top", "bottom" },
		["GtkFrame"] = new[] { "label" },
	};

	static readonly Dictionary<string, string> SlotDescriptions = new(StringComparer.Ordinal) {
		["start"] = "Packs the child at the container's start.",
		["end"] = "Packs the child at the container's end.",
		["title"] = "Uses the child as the header bar's title widget (deprecated: set title-widget instead).",
		["tab"] = "Uses the child as the notebook page's tab label.",
		["action-start"] = "Packs the child as a notebook action widget at the start.",
		["action-end"] = "Packs the child as a notebook action widget at the end.",
		["overlay"] = "Adds the child as an overlay above the main child.",
		["titlebar"] = "Uses the child as the window's custom title bar.",
		["label"] = "Uses the child as the frame's label widget.",
		["top"] = "Adds the child to the toolbar view's top bar.",
		["bottom"] = "Adds the child to the toolbar view's bottom bar.",
	};

	static readonly Dictionary<string, string> ElementDescriptions = new(StringComparer.Ordinal) {
		["interface"] = "The root of a GtkBuilder file.",
		["requires"] = "Declares a library and version the file needs.",
		["object"] = "Instantiates a class.",
		["template"] = "Declares a composite widget template.",
		["property"] = "Sets a property; the text is parsed as the property's type.",
		["signal"] = "Connects a GObject signal to a handler in code.",
		["child"] = "Adds a child; type selects a GtkBuildable slot.",
		["menu"] = "A GMenu model.",
		["item"] = "A menu item.",
		["attribute"] = "An attribute of a menu item or link.",
		["link"] = "A link of a menu item.",
		["section"] = "A menu section.",
		["submenu"] = "A submenu.",
		["lookup"] = "Looks a value up at run time.",
		["constant"] = "A constant value.",
		["closure"] = "A closure expression.",
		["constraints"] = "Layout constraints for a GtkConstraintLayout.",
		["guide"] = "A size guide for a constraint layout.",
		["constraint"] = "A single constraint.",
	};

	static readonly Dictionary<string, string> AttributeDescriptions = new(StringComparer.Ordinal) {
		["class"] = "The GObject class to instantiate.",
		["id"] = "The id other elements refer to.",
		["name"] = "The property, signal, attribute or lookup this element names.",
		["handler"] = "The handler method or function name in code.",
		["type"] = "The GtkBuildable child slot, or the attribute's value type.",
		["translatable"] = "Marks the value for translation (yes/no).",
		["context"] = "Translation context for the value.",
		["comments"] = "Translator comment for the value.",
		["lib"] = "The library the file requires (gtk, libadwaita).",
		["version"] = "The library version the file requires.",
		["bind-source"] = "The object a property binding reads from.",
		["bind-property"] = "The property the binding reads.",
		["bind-flags"] = "The binding's flags.",
		["after"] = "Runs the handler after the default handler.",
		["swapped"] = "Swaps the handler's instance and data arguments.",
		["object"] = "The object the signal is connected to instead of the instance.",
		["internal-child"] = "Marks a template's internal child.",
		["type-func"] = "The function that returns the object's GType.",
		["constructor"] = "The function that constructs the object.",
		["domain"] = "The translation domain.",
	};

	public DocumentReadiness GetDocumentReadiness(DocumentId documentId)
		=> documents.ContainsKey(documentId) ? DocumentReadiness.Ready : DocumentReadiness.Unknown;

	public long GetWorkspaceRevision() => Interlocked.Read(ref revision);

	public WorkspaceDocumentInfo? GetWorkspaceDocumentInfo(DocumentId documentId)
		=> documents.ContainsKey(documentId)
			? new WorkspaceDocumentInfo(DocumentReadiness.Ready, null, Array.Empty<string>())
			: null;

	public Task<LensDocumentResult> GetLensDocumentAsync(DocumentId documentId, CancellationToken cancellationToken)
		=> Task.FromResult(new LensDocumentResult(GetDocumentReadiness(documentId), Array.Empty<LensAnchorResult>()));

	public Task UpsertDocumentAsync(DocumentId documentId, string text, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var changed = !documents.TryGetValue(documentId, out var existing) || !string.Equals(existing, text, StringComparison.Ordinal);
		documents[documentId] = text ?? "";
		if (changed)
			Interlocked.Increment(ref revision);
		// A .ui document is now in the workspace, so the first completion or hover will want the
		// GIR (~20 MB of XML). Start loading it off this path instead of at IDE startup, where a
		// user who never opens a GTK file would pay for it.
		GtkGirCatalog.Preload();
		return Task.CompletedTask;
	}

	/// <summary>The registry relays the IDE's document-closed message here, so a closed file's text is not kept.</summary>
	public Task CloseDocumentAsync(string fileName)
	{
		foreach (var id in documents.Keys.Where(key => string.Equals(key.FileName, fileName, StringComparison.OrdinalIgnoreCase)).ToList())
			documents.TryRemove(id, out _);
		return Task.CompletedTask;
	}

	public void OnTextChanged(DocumentId documentId, TextChange change)
	{
		if (change == null || !documents.TryGetValue(documentId, out var text))
			return;
		var start = OffsetOf(text, change.Span.Start);
		var end = OffsetOf(text, change.Span.End);
		if (start > end)
			(start, end) = (end, start);
		// TryUpdate, not an assignment: a document closed since the read above must stay gone.
		if (documents.TryUpdate(documentId, text.Substring(0, start) + change.NewText + text.Substring(Math.Min(end, text.Length)), text))
			Interlocked.Increment(ref revision);
	}

	bool TryGet(DocumentId documentId, out string text) => documents.TryGetValue(documentId, out text!);

	public Task<CompletionResult> GetCompletionsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryGet(documentId, out var text))
			return Task.FromResult(CompletionResult.Empty);
		var document = GtkUiDocument.Parse(text);
		var (items, start) = ComputeCompletions(document, offset);
		if (items.Count == 0)
			return Task.FromResult(CompletionResult.Empty);
		return Task.FromResult(new CompletionResult(items, SpanOf(text, start, offset)));
	}

	#region completion

	static (List<CompletionItem> Items, int Start) ComputeCompletions(GtkUiDocument document, int offset)
	{
		var catalog = GtkGirCatalog.Catalog;
		var tag = document.TagAt(offset);
		if (tag != null) {
			var attribute = AttributeAt(tag, offset);
			if (attribute is { HasValue: true })
				return AttributeValue(tag, attribute, document, offset, catalog);
			if (offset <= tag.NameEnd)
				return ElementNames(tag.Parent, offset);
			return AttributeNames(tag, offset);
		}

		var content = document.ContentAt(offset);
		if (content != null && content.Name == "property" && !content.HasElementChildren)
			return PropertyValue(content, document, offset, catalog);
		return (new List<CompletionItem>(), offset);
	}

	static (List<CompletionItem> Items, int Start) ElementNames(GtkUiElement? parent, int offset)
	{
		var items = new List<CompletionItem>();
		var names = parent == null ? Array.Empty<string>() : ElementChildren.TryGetValue(parent.Name, out var children) ? children : Array.Empty<string>();
		foreach (var name in names)
			items.Add(new CompletionItem(name, name, ElementDescriptions.TryGetValue(name, out var description) ? description : null, "Element"));
		return (items, offset);
	}

	static (List<CompletionItem> Items, int Start) AttributeNames(GtkUiElement element, int offset)
	{
		var items = new List<CompletionItem>();
		var names = ElementAttributes.TryGetValue(element.Name, out var attributes) ? attributes : Array.Empty<string>();
		var present = new HashSet<string>(element.Attributes.Select(a => a.Name), StringComparer.Ordinal);
		foreach (var name in names) {
			if (present.Contains(name))
				continue;
			items.Add(new CompletionItem(name, name, AttributeDescriptions.TryGetValue(name, out var description) ? description : null, "Field"));
		}
		return (items, offset);
	}

	static (List<CompletionItem> Items, int Start) AttributeValue(GtkUiElement element, GtkUiAttribute attribute,
		GtkUiDocument document, int offset, GirCatalog? catalog)
	{
		var text = document.Text;
		var prefix = PrefixOf(attribute, offset, text);
		var wordStart = offset - TrailingWordLength(prefix);
		var objectClass = element.Name is "object" or "template" ? element.Value("class") : element.NearestObject()?.Value("class");

		switch (element.Name + "/" + attribute.Name) {
			case "object/class":
			case "template/class":
				return (ClassItems(catalog, prefix, wordStart), wordStart);
			case "property/name":
				return (PropertyItems(catalog, objectClass, prefix, wordStart), wordStart);
			case "signal/name":
				return (SignalItems(catalog, objectClass, prefix, wordStart), wordStart);
			case "child/type":
				return (SlotItems(objectClass, prefix, wordStart), wordStart);
			case "requires/lib":
				return (Items(new[] { ("gtk", "GTK"), ("libadwaita", "libadwaita") }, prefix, wordStart, "Namespace"), wordStart);
			case "property/translatable":
			case "attribute/translatable":
				return (Items(new[] { ("yes", "Translatable"), ("no", "Not translatable") }, prefix, wordStart, "Value"), wordStart);
			case "signal/after":
			case "signal/swapped":
				return (Items(new[] { ("true", "True"), ("false", "False") }, prefix, wordStart, "Value"), wordStart);
			case "attribute/name":
				return (Items(new[] { ("label", "The item's text"), ("action", "The action to activate"), ("target", "The action's target"), ("icon", "The item's icon") }, prefix, wordStart, "Field"), wordStart);
			case "constant/type":
				return (Items(new[] { ("gchararray", "String"), ("gboolean", "Boolean"), ("gint", "Integer"), ("guint", "Unsigned integer"), ("gdouble", "Double") }, prefix, wordStart, "Type"), wordStart);
		}
		return (new List<CompletionItem>(), wordStart);
	}

	static string PrefixOf(GtkUiAttribute attribute, int offset, string text)
	{
		var start = Math.Min(attribute.ValueStart, text.Length);
		var end = Math.Min(offset, text.Length);
		return end > start ? text.Substring(start, end - start) : "";
	}

	static (List<CompletionItem> Items, int Start) PropertyValue(GtkUiElement property, GtkUiDocument document, int offset, GirCatalog? catalog)
	{
		var propertyInfo = PropertyOf(property, catalog);
		if (propertyInfo == null)
			return (new List<CompletionItem>(), offset);
		var text = document.Text;
		var raw = property.Content(text);
		var valueStart = property.OpenEnd + (raw.Length - raw.TrimStart().Length);
		var end = Math.Min(offset, text.Length);
		var prefix = end > valueStart ? text.Substring(valueStart, end - valueStart) : "";
		var wordStart = offset - TrailingWordLength(prefix);
		var items = new List<CompletionItem>();
		switch (propertyInfo.Kind) {
			case GirValueKind.Enum:
			case GirValueKind.Flags:
				if (propertyInfo.Enum != null) {
					foreach (var member in propertyInfo.Enum.Members) {
						if (!StartsWithIgnoreCase(member.Nick, prefix))
							continue;
						items.Add(new CompletionItem(member.Nick, InsertionFor(member.Nick, prefix),
							member.Name + " (" + member.Value + ")", "EnumMember"));
					}
				}
				break;
			case GirValueKind.Boolean:
				foreach (var (value, description) in new[] { ("True", "True"), ("False", "False") }) {
					if (StartsWithIgnoreCase(value, prefix))
						items.Add(new CompletionItem(value, InsertionFor(value, prefix), description, "Value"));
				}
				break;
			case GirValueKind.Reference:
				var own = property.NearestObject()?.Value("id");
				foreach (var id in document.Ids().Distinct(StringComparer.Ordinal)) {
					if (id == own || !StartsWithIgnoreCase(id, prefix))
						continue;
					var target = document.FindById(id);
					var description = target?.Value("class");
					items.Add(new CompletionItem(id, InsertionFor(id, prefix), description, "Field"));
				}
				break;
		}
		return (items, wordStart);
	}

	static List<CompletionItem> ClassItems(GirCatalog? catalog, string prefix, int wordStart)
	{
		var items = new List<CompletionItem>();
		if (catalog == null)
			return items;
		foreach (var cls in catalog.Classes
			.Where(c => !c.Abstract && (GirCatalog.NamespaceOf(c) is "Gtk" or "Adw"))
			.OrderBy(c => c.TypeName, StringComparer.Ordinal)) {
			if (!StartsWithIgnoreCase(cls.TypeName, prefix))
				continue;
			var description = Summary(cls.Description);
			if (cls.Deprecated)
				description = "[deprecated] " + description;
			items.Add(new CompletionItem(cls.TypeName, InsertionFor(cls.TypeName, prefix), description, "Class"));
		}
		return items;
	}

	static List<CompletionItem> PropertyItems(GirCatalog? catalog, string? builderClass, string prefix, int wordStart)
	{
		var items = new List<CompletionItem>();
		if (catalog == null || builderClass == null || catalog.FindByTypeName(builderClass) == null)
			return items;
		foreach (var property in catalog.PropertiesOf(builderClass)) {
			if (!property.Writable || !StartsWithIgnoreCase(property.Name, prefix))
				continue;
			var description = BuilderName(property.TypeName) + (property.DefaultValue is { Length: > 0 } value ? " = " + value : "")
				+ (property.Deprecated ? "  [deprecated]" : "");
			items.Add(new CompletionItem(property.Name, InsertionFor(property.Name, prefix), description, "Property"));
		}
		return items;
	}

	static List<CompletionItem> SignalItems(GirCatalog? catalog, string? builderClass, string prefix, int wordStart)
	{
		var items = new List<CompletionItem>();
		if (catalog == null || builderClass == null || catalog.FindByTypeName(builderClass) == null)
			return items;
		foreach (var signal in catalog.SignalsOf(builderClass)) {
			if (!StartsWithIgnoreCase(signal.Name, prefix))
				continue;
			var description = (signal.ReturnType == "none" ? "void" : signal.ReturnType) + (signal.Deprecated ? "  [deprecated]" : "");
			items.Add(new CompletionItem(signal.Name, InsertionFor(signal.Name, prefix), description, "Event"));
		}
		return items;
	}

	static List<CompletionItem> SlotItems(string? builderClass, string prefix, int wordStart)
	{
		var items = new List<CompletionItem>();
		if (builderClass == null || !ChildSlots.TryGetValue(builderClass, out var slots))
			return items;
		foreach (var slot in slots) {
			if (!StartsWithIgnoreCase(slot, prefix))
				continue;
			items.Add(new CompletionItem(slot, InsertionFor(slot, prefix), SlotDescriptions.TryGetValue(slot, out var description) ? description : null, "Field"));
		}
		return items;
	}

	static List<CompletionItem> Items(IEnumerable<(string Name, string Description)> values, string prefix, int wordStart, string glyph)
		=> values.Where(value => StartsWithIgnoreCase(value.Name, prefix))
			.Select(value => new CompletionItem(value.Name, InsertionFor(value.Name, prefix), value.Description, glyph))
			.ToList();

	/// <summary>The text a completion inserts when the editor replaces the word before the caret
	/// (letters, digits and '_'): the part of the candidate after the already-typed head. That keeps
	/// hyphenated GtkBuilder names working - typing <c>default-wid</c> in a property value replaces
	/// only <c>wid</c>, so inserting the whole name would duplicate the head.</summary>
	static string InsertionFor(string name, string prefix)
	{
		var head = prefix.Length - TrailingWordLength(prefix);
		if (head > 0 && head <= name.Length && StartsWithIgnoreCase(name, prefix.Substring(0, head)))
			return name.Substring(head);
		return name;
	}

	static int TrailingWordLength(string prefix)
	{
		var length = 0;
		for (var index = prefix.Length - 1; index >= 0 && (char.IsLetterOrDigit(prefix[index]) || prefix[index] == '_'); index--)
			length++;
		return length;
	}

	static bool StartsWithIgnoreCase(string value, string prefix)
		=> value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

	#endregion

	#region quick info

	public Task<QuickInfo?> GetQuickInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryGet(documentId, out var text))
			return Task.FromResult<QuickInfo?>(null);
		var document = GtkUiDocument.Parse(text);
		var catalog = GtkGirCatalog.Catalog;
		var tag = document.TagAt(offset);
		if (tag != null) {
			if (offset <= tag.NameEnd)
				return Task.FromResult(Info(text, tag.NameStart, tag.NameEnd, ElementInfo(tag.Name)));
			var attribute = AttributeAt(tag, offset);
			if (attribute != null && attribute.HasValue) {
				var info = AttributeInfo(tag, attribute, document, catalog);
				if (info != null)
					return Task.FromResult(Info(text, attribute.ValueStart, attribute.ValueEnd, info));
			}
			return Task.FromResult<QuickInfo?>(null);
		}

		var content = document.ContentAt(offset);
		if (content != null) {
			if (content.Name == "property")
				return Task.FromResult(PropertyInfo(content, catalog) is { } property
					? Info(text, content.OpenEnd, content.ContentEnd, property)
					: null);
			if (ElementInfo(content.Name) is { } element)
				return Task.FromResult(Info(text, content.NameStart, content.NameEnd, element));
		}
		return Task.FromResult<QuickInfo?>(null);
	}

	static QuickInfo Info(string text, int start, int end, string value)
		=> new QuickInfo(value, SpanOf(text, start, end));

	static string? AttributeInfo(GtkUiElement element, GtkUiAttribute attribute, GtkUiDocument document, GirCatalog? catalog)
	{
		var builderClass = element.Name is "object" or "template" ? element.Value("class") : element.NearestObject()?.Value("class");
		switch (element.Name + "/" + attribute.Name) {
			case "object/class":
			case "template/class":
				var cls = catalog?.FindByTypeName(attribute.Value);
				return cls == null ? null : ClassInfo(cls);
			case "object/id":
				var target = element.Name == "object" ? element : null;
				return "id: " + attribute.Value + (target?.Value("class") is { } idClass ? "  (" + idClass + ")" : "");
			case "property/name":
				if (catalog?.FindProperty(builderClass!, attribute.Value) is { } property)
					return PropertyText(property);
				return null;
			case "signal/name":
				var signal = catalog?.SignalsOf(builderClass!).FirstOrDefault(s => s.Name == attribute.Value);
				return signal == null ? null : SignalText(signal);
			case "child/type":
				return SlotDescriptions.TryGetValue(attribute.Value, out var slot) ? attribute.Value + ": " + slot : null;
			case "requires/lib":
				return attribute.Value == "libadwaita" ? "libadwaita: the Adwaita widgets (Adw*)." : "gtk: GTK 4.";
		}
		if (element.Name == "lookup" && attribute.Name == "name" && document.FindById(attribute.Value) is { } referenced)
			return "id: " + attribute.Value + (referenced.Value("class") is { } referenceClass ? "  (" + referenceClass + ")" : "");
		if (attribute.Name == "id")
			return "id: " + attribute.Value;
		if (ElementAttributes.TryGetValue(element.Name, out var names) && names.Contains(attribute.Name)
			&& AttributeDescriptions.TryGetValue(attribute.Name, out var description))
			return attribute.Name + ": " + description;
		return null;
	}

	static string? PropertyInfo(GtkUiElement property, GirCatalog? catalog)
		=> PropertyOf(property, catalog) is { } info ? PropertyText(info) : null;

	static string PropertyText(GirProperty property)
	{
		var builder = new StringBuilder();
		builder.Append(property.Name).Append(": ").Append(BuilderName(property.TypeName));
		if (property.DefaultValue is { Length: > 0 } value)
			builder.Append("  (default: ").Append(value).Append(')');
		if (property.ConstructOnly)
			builder.Append("  [construct-only]");
		if (property.Deprecated)
			builder.Append("  [deprecated]");
		builder.Append('\n').Append(BuilderName(property.DeclaringType));
		var summary = Summary(property.Description);
		if (summary.Length > 0)
			builder.Append("\n\n").Append(summary);
		return builder.ToString();
	}

	static string SignalText(GirSignal signal)
	{
		var builder = new StringBuilder();
		builder.Append(signal.Name).Append("  ->  ").Append(signal.ReturnType == "none" ? "void" : signal.ReturnType);
		if (signal.Deprecated)
			builder.Append("  [deprecated]");
		builder.Append('\n').Append(BuilderName(signal.DeclaringType));
		return builder.ToString();
	}

	static string ClassInfo(GirClass cls)
	{
		var builder = new StringBuilder();
		builder.Append(cls.TypeName);
		if (cls.Parent != null)
			builder.Append(" : ").Append(BuilderName(cls.Parent));
		builder.Append("  (").Append(GirCatalog.NamespaceOf(cls)).Append(')');
		if (cls.Abstract)
			builder.Append("  [abstract]");
		if (cls.Deprecated)
			builder.Append("  [deprecated]");
		var summary = Summary(cls.Description);
		if (summary.Length > 0)
			builder.Append("\n\n").Append(summary);
		return builder.ToString();
	}

	static string? ElementInfo(string name)
		=> ElementDescriptions.TryGetValue(name, out var description) ? name + ": " + description : null;

	#endregion

	#region diagnostics

	public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DocumentId documentId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryGet(documentId, out var text))
			return Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(Array.Empty<LanguageDiagnostic>());
		return Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(ComputeDiagnostics(text));
	}

	static List<LanguageDiagnostic> ComputeDiagnostics(string text)
	{
		var document = GtkUiDocument.Parse(text);
		var catalog = GtkGirCatalog.Catalog;
		var diagnostics = new List<LanguageDiagnostic>();

		void Add(string id, string message, DiagnosticSeverity severity, int start, int end)
			=> diagnostics.Add(new LanguageDiagnostic(id, message, severity, SpanOf(text, start, end)));

		foreach (var error in document.Errors)
			Add("GTK1001", error.Message, DiagnosticSeverity.Error, error.Offset, error.Offset);

		if (catalog == null)
			Add("GTK1000", GtkGirCatalog.Diagnostic, DiagnosticSeverity.Info, 0, 0);

		var roots = document.Roots;
		if (roots.Count == 0) {
			if (text.Trim().Length > 0)
				Add("GTK1002", "The document has no <interface> root element.", DiagnosticSeverity.Error, 0, 0);
			return diagnostics;
		}
		if (roots[0].Name != "interface")
			Add("GTK1002", "A GtkBuilder document's root element is <interface>, not <" + roots[0].Name + ">.", DiagnosticSeverity.Warning, roots[0].NameStart, roots[0].NameEnd);

		var interfaceElement = roots[0];
		var requires = interfaceElement.Children.Where(c => c.Name == "requires").ToList();
		if (requires.Count == 0)
			Add("GTK1003", "Add <requires lib=\"gtk\" version=\"4.0\"/> so the file declares the GTK version it needs.", DiagnosticSeverity.Info, interfaceElement.NameStart, interfaceElement.NameEnd);

		var ids = new Dictionary<string, GtkUiElement>(StringComparer.Ordinal);
		foreach (var element in document.All) {
			if (element.Name is not ("object" or "menu" or "item" or "link" or "section" or "submenu"))
				continue;
			if (element.Attribute("id") is not { HasValue: true } idAttribute || idAttribute.Value.Length == 0)
				continue;
			if (ids.ContainsKey(idAttribute.Value)) {
				Add("GTK1004", "The id '" + idAttribute.Value + "' is already used by another object.", DiagnosticSeverity.Error, idAttribute.ValueStart, idAttribute.ValueEnd);
			} else {
				ids[idAttribute.Value] = element;
			}
		}

		var usesLibadwaita = false;
		foreach (var element in document.All) {
			switch (element.Name) {
				case "object":
					var classAttribute = element.Attribute("class");
					if (classAttribute == null || !classAttribute.HasValue || classAttribute.Value.Length == 0) {
						Add("GTK1005", "An <object> needs a class attribute.", DiagnosticSeverity.Error, element.NameStart, element.NameEnd);
						break;
					}
					var builderClass = classAttribute.Value;
					var cls = catalog?.FindByTypeName(builderClass);
					if (catalog != null && cls == null)
						Add("GTK1006", "Unknown GTK class '" + builderClass + "'. A project widget needs a preview mapping; check the installed GIR for the rest.", DiagnosticSeverity.Warning, classAttribute.ValueStart, classAttribute.ValueEnd);
					if (cls != null && GirCatalog.NamespaceOf(cls) == "Adw")
						usesLibadwaita = true;
					CheckObjectChildren(element, builderClass, Add);
					CheckProperties(element, builderClass, catalog, document, ids, Add);
					CheckSignals(element, builderClass, catalog, Add);
					break;
				case "property":
					if (element.Attribute("name") is not { HasValue: true } nameAttribute || nameAttribute.Value.Length == 0)
						Add("GTK1007", "A <property> needs a name attribute.", DiagnosticSeverity.Error, element.NameStart, element.NameEnd);
					break;
				case "signal":
					if (element.Attribute("name") is not { HasValue: true } signalAttribute || signalAttribute.Value.Length == 0)
						Add("GTK1008", "A <signal> needs a name attribute.", DiagnosticSeverity.Error, element.NameStart, element.NameEnd);
					break;
				case "child":
					CheckChildSlot(element, document, catalog, Add);
					break;
			}
		}

		if (usesLibadwaita && !requires.Any(r => r.Value("lib") == "libadwaita"))
			Add("GTK1009", "The document uses libadwaita classes; add <requires lib=\"libadwaita\" version=\"1.0\"/>.", DiagnosticSeverity.Warning, interfaceElement.NameStart, interfaceElement.NameEnd);

		diagnostics.Sort((left, right) => Compare(left.Span.Start, right.Span.Start));
		return diagnostics;
	}

	static int Compare(TextPosition left, TextPosition right)
		=> left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column);

	static void CheckObjectChildren(GtkUiElement element, string builderClass, Action<string, string, DiagnosticSeverity, int, int> add)
	{
		// Only <child> elements are children for the single-child and paned rules: a property such
		// as GtkWindow:titlebar is a separate slot, so a window may hold a child and a titlebar.
		var children = element.Children.Where(c => c.Name == "child" && c.Children.Any(grandchild => grandchild.Name == "object")).ToList();
		var count = children.Count;
		if (builderClass == "GtkPaned" && count > GtkDropPlanner.PanedCapacity)
			add("GTK1010", "A GtkPaned holds only a start and an end child.", DiagnosticSeverity.Error, element.NameStart, element.NameEnd);
		else if (GtkDropPlanner.SingleChildClasses.Contains(builderClass) && count > 1)
			add("GTK1011", "A " + builderClass + " holds a single child; GTK keeps only one.", DiagnosticSeverity.Error, element.NameStart, element.NameEnd);
		if (builderClass == "GtkColumnView") {
			foreach (var child in children.SelectMany(c => c.Children).Where(c => c.Name == "object")) {
				var childClass = child.Value("class");
				if (childClass != null && childClass != "GtkColumnViewColumn")
					add("GTK1012", "A GtkColumnView holds GtkColumnViewColumn children, not " + childClass + ".", DiagnosticSeverity.Warning, child.NameStart, child.NameEnd);
			}
		}
	}

	static void CheckProperties(GtkUiElement element, string builderClass, GirCatalog? catalog, GtkUiDocument document,
		Dictionary<string, GtkUiElement> ids, Action<string, string, DiagnosticSeverity, int, int> add)
	{
		if (catalog == null || catalog.FindByTypeName(builderClass) == null)
			return;
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var property in element.Children.Where(c => c.Name == "property")) {
			if (property.Attribute("name") is not { HasValue: true } nameAttribute || nameAttribute.Value.Length == 0)
				continue;
			var name = nameAttribute.Value;
			if (property.Attribute("bind-source") != null)
				continue;
			if (!seen.Add(name))
				add("GTK1013", "The property '" + name + "' is set more than once on this object.", DiagnosticSeverity.Warning, nameAttribute.ValueStart, nameAttribute.ValueEnd);
			var info = catalog.FindProperty(builderClass, name);
			if (info == null) {
				add("GTK1014", "Unknown property '" + name + "' for " + builderClass + ".", DiagnosticSeverity.Warning, nameAttribute.ValueStart, nameAttribute.ValueEnd);
				continue;
			}
			if (property.HasElementChildren)
				continue;
			var value = property.Content(document.Text).Trim();
			if (value.Length == 0)
				continue;
			if (info.Kind is GirValueKind.Enum or GirValueKind.Flags) {
				var valid = info.Kind == GirValueKind.Flags
					? value.Split('|').All(part => part.Trim().Length == 0 || info.Enum?.Normalize(part) != null)
					: info.Enum?.Normalize(value) != null;
				if (!valid)
					add("GTK1015", "'" + value + "' is not a value of " + (info.Enum?.TypeName ?? info.TypeName) + ".", DiagnosticSeverity.Warning, property.OpenEnd, property.ContentEnd);
			} else if (info.Kind == GirValueKind.Reference && !ids.ContainsKey(value)) {
				add("GTK1016", "No object has the id '" + value + "'.", DiagnosticSeverity.Warning, property.OpenEnd, property.ContentEnd);
			}
		}
	}

	static void CheckSignals(GtkUiElement element, string builderClass, GirCatalog? catalog, Action<string, string, DiagnosticSeverity, int, int> add)
	{
		if (catalog == null || catalog.FindByTypeName(builderClass) == null)
			return;
		foreach (var signal in element.Children.Where(c => c.Name == "signal")) {
			if (signal.Attribute("name") is not { HasValue: true } nameAttribute || nameAttribute.Value.Length == 0)
				continue;
			if (catalog.SignalsOf(builderClass).All(s => s.Name != nameAttribute.Value))
				add("GTK1017", "Unknown signal '" + nameAttribute.Value + "' for " + builderClass + ".", DiagnosticSeverity.Warning, nameAttribute.ValueStart, nameAttribute.ValueEnd);
			if (signal.Attribute("handler") is not { HasValue: true } handler || handler.Value.Length == 0)
				add("GTK1018", "A <signal> needs a handler attribute.", DiagnosticSeverity.Warning, signal.NameStart, signal.NameEnd);
		}
	}

	static void CheckChildSlot(GtkUiElement child, GtkUiDocument document, GirCatalog? catalog, Action<string, string, DiagnosticSeverity, int, int> add)
	{
		if (child.Parent?.Name != "object")
			return;
		var builderClass = child.Parent.Value("class");
		if (builderClass == null || !ChildSlots.TryGetValue(builderClass, out var slots))
			return;
		if (child.Attribute("type") is not { HasValue: true } type || type.Value.Length == 0)
			return;
		if (!slots.Contains(type.Value, StringComparer.Ordinal))
			add("GTK1019", builderClass + " has no child slot '" + type.Value + "'; expected " + string.Join(", ", slots) + ".", DiagnosticSeverity.Warning, type.ValueStart, type.ValueEnd);
	}

	#endregion

	#region navigation

	public Task<IReadOnlyList<NavigationTarget>> GoToDefinitionAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryGet(documentId, out var text))
			return Task.FromResult<IReadOnlyList<NavigationTarget>>(Array.Empty<NavigationTarget>());
		var document = GtkUiDocument.Parse(text);
		if (IdAt(document, text, offset) is not { } token || document.FindById(token.Id) is not { } target || target.Attribute("id") is not { } declaration)
			return Task.FromResult<IReadOnlyList<NavigationTarget>>(Array.Empty<NavigationTarget>());
		return Task.FromResult<IReadOnlyList<NavigationTarget>>(new[] {
			new NavigationTarget(documentId.FileName, PositionOf(text, declaration.ValueStart), SpanOf(text, declaration.ValueStart, declaration.ValueEnd))
		});
	}

	public Task<SymbolReferencesResult?> FindReferencesAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryGet(documentId, out var text))
			return Task.FromResult<SymbolReferencesResult?>(null);
		var document = GtkUiDocument.Parse(text);
		if (IdAt(document, text, offset) is not { } token)
			return Task.FromResult<SymbolReferencesResult?>(null);
		var references = Occurrences(document, text, token.Id)
			.Select(occurrence => new NavigationTarget(documentId.FileName, PositionOf(text, occurrence.Start), SpanOf(text, occurrence.Start, occurrence.End)))
			.ToList();
		return Task.FromResult<SymbolReferencesResult?>(new SymbolReferencesResult(token.Id, references));
	}

	public Task<string?> GetSymbolNameAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryGet(documentId, out var text))
			return Task.FromResult<string?>(null);
		return Task.FromResult(IdAt(GtkUiDocument.Parse(text), text, offset)?.Id);
	}

	public Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> RenameSymbolAsync(
		DocumentId documentId, int offset, string newName, CancellationToken cancellationToken,
		bool renameOverloads = false, bool renameInStrings = false, bool renameInComments = false)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var edits = new Dictionary<string, IReadOnlyList<TextEdit>>(StringComparer.OrdinalIgnoreCase);
		if (!TryGet(documentId, out var text) || !IsValidId(newName))
			return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>(edits);
		var document = GtkUiDocument.Parse(text);
		if (IdAt(document, text, offset) is not { } token || token.Id == newName)
			return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>(edits);
		// Another object already owns the new id: renaming would merge two objects into one.
		if (document.Ids().Contains(newName, StringComparer.Ordinal))
			return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>(edits);
		var occurrences = Occurrences(document, text, token.Id).ToList();
		if (occurrences.Count == 0)
			return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>(edits);
		edits[documentId.FileName] = occurrences
			.Select(occurrence => new TextEdit(SpanOf(text, occurrence.Start, occurrence.End), newName))
			.ToList();
		return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>(edits);
	}

	public Task<bool> IsValidIdentifierAsync(DocumentId documentId, string name, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(IsValidId(name));
	}

	/// <summary>A GtkBuilder id is an XML ID (an NCName): a letter or '_' followed by letters,
	/// digits, '_', '-' or '.'.</summary>
	static bool IsValidId(string? name)
	{
		if (string.IsNullOrEmpty(name) || !(char.IsLetter(name[0]) || name[0] == '_'))
			return false;
		return name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');
	}

	/// <summary>The id token at <paramref name="offset"/>: an id declaration, a lookup name,
	/// or the text of a reference-valued property.</summary>
	static (string Id, int Start, int End)? IdAt(GtkUiDocument document, string text, int offset)
	{
		var tag = document.TagAt(offset);
		if (tag != null) {
			if (AttributeAt(tag, offset) is not { HasValue: true } attribute)
				return null;
			if (attribute.Name == "id" && attribute.Value.Length > 0)
				return (attribute.Value, attribute.ValueStart, attribute.ValueEnd);
			if (attribute.Name == "name" && tag.Name == "lookup" && attribute.Value.Length > 0)
				return (attribute.Value, attribute.ValueStart, attribute.ValueEnd);
			return null;
		}

		var content = document.ContentAt(offset);
		if (content is { Name: "property", HasElementChildren: false } && PropertyOf(content, GtkGirCatalog.Catalog) is { Kind: GirValueKind.Reference } && content.Content(text).Trim() is { Length: > 0 } raw) {
			var start = content.OpenEnd + (content.Content(text).Length - content.Content(text).TrimStart().Length);
			var end = start + raw.Length;
			if (offset >= start && offset <= end)
				return (raw, start, end);
		}
		return null;
	}

	/// <summary>Every span in the document that holds <paramref name="id"/>, in document order.</summary>
	static IEnumerable<(int Start, int End)> Occurrences(GtkUiDocument document, string text, string id)
	{
		foreach (var (_, attribute, _) in document.IdOccurrences()) {
			if (string.Equals(attribute.Value, id, StringComparison.Ordinal))
				yield return (attribute.ValueStart, attribute.ValueEnd);
		}
		foreach (var property in document.All.Where(e => e is { Name: "property", HasElementChildren: false })) {
			if (PropertyOf(property, GtkGirCatalog.Catalog) is not { Kind: GirValueKind.Reference })
				continue;
			var raw = property.Content(text);
			if (raw.Trim() != id)
				continue;
			var start = property.OpenEnd + (raw.Length - raw.TrimStart().Length);
			yield return (start, start + id.Length);
		}
	}

	#endregion

	#region outline

	public Task<IReadOnlyList<DocumentOutlineNode>> GetDocumentOutlineAsync(DocumentId documentId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryGet(documentId, out var text))
			return Task.FromResult<IReadOnlyList<DocumentOutlineNode>>(Array.Empty<DocumentOutlineNode>());
		var document = GtkUiDocument.Parse(text);
		var nodes = new List<DocumentOutlineNode>();
		foreach (var root in document.Roots) {
			foreach (var child in root.Children)
				AddOutline(child, text, nodes);
		}
		return Task.FromResult<IReadOnlyList<DocumentOutlineNode>>(nodes);
	}

	static void AddOutline(GtkUiElement element, string text, List<DocumentOutlineNode> nodes)
	{
		if (element.Name == "object") {
			var builderClass = element.Value("class") ?? "object";
			var id = element.Value("id");
			var name = string.IsNullOrEmpty(id) ? builderClass : id;
			var spanAttribute = element.Attribute("id") is { HasValue: true } idAttribute && !string.IsNullOrEmpty(id)
				? idAttribute : element.Attribute("class");
			var span = spanAttribute != null
				? SpanOf(text, spanAttribute.ValueStart, spanAttribute.ValueEnd)
				: SpanOf(text, element.NameStart, element.NameEnd);
			var children = new List<DocumentOutlineNode>();
			foreach (var child in element.Children)
				AddOutline(child, text, children);
			nodes.Add(new DocumentOutlineNode(name, builderClass, span, children, SpanOf(text, element.Start, element.End)));
			return;
		}
		foreach (var child in element.Children)
			AddOutline(child, text, nodes);
	}

	#endregion

	#region helpers

	static GtkUiAttribute? AttributeAt(GtkUiElement element, int offset)
		=> element.Attributes.FirstOrDefault(a => a.Name.Length > 0 && offset >= a.ValueStart && offset <= a.ValueEnd);

	static GirProperty? PropertyOf(GtkUiElement property, GirCatalog? catalog)
	{
		if (catalog == null || property.Name != "property")
			return null;
		var builderClass = property.NearestObject()?.Value("class");
		var name = property.Value("name");
		if (builderClass == null || string.IsNullOrEmpty(name))
			return null;
		return catalog.FindProperty(builderClass, name);
	}

	/// <summary>The GtkBuilder spelling of a GIR-qualified name: "Gtk.Widget" is "GtkWidget", which
	/// is what the file writes in class=.</summary>
	static string BuilderName(string name)
		=> name.IndexOf('.') >= 0 ? name.Replace(".", "") : name;

	static string Summary(string? description)
	{
		if (string.IsNullOrWhiteSpace(description))
			return "";
		var text = string.Join(" ", description.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()));
		return text.Length <= 240 ? text : text.Substring(0, 237) + "...";
	}

	static int OffsetOf(string text, TextPosition position)
	{
		var line = 1;
		var offset = 0;
		while (offset < text.Length && line < position.Line) {
			if (text[offset] == '\n')
				line++;
			offset++;
		}
		return Math.Min(text.Length, offset + Math.Max(0, position.Column - 1));
	}

	static TextPosition PositionOf(string text, int offset)
	{
		offset = Math.Max(0, Math.Min(offset, text.Length));
		var line = 1;
		var lineStart = 0;
		for (var index = 0; index < offset; index++) {
			if (text[index] == '\n') {
				line++;
				lineStart = index + 1;
			}
		}
		return new TextPosition(line, offset - lineStart + 1);
	}

	static TextSpan SpanOf(string text, int start, int end)
	{
		if (end < start)
			(end, start) = (start, end);
		return new TextSpan(PositionOf(text, start), PositionOf(text, end));
	}

	#endregion

	#region unsupported members

	public Task<IReadOnlyList<TextEdit>> FormatAsync(DocumentId documentId, TextSpan? span, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<TextEdit>>(Array.Empty<TextEdit>());

	public Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(DocumentId documentId, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<SemanticToken>>(Array.Empty<SemanticToken>());

	public Task<IReadOnlyList<CodeActionInfo>> GetCodeActionsAsync(DocumentId documentId, TextSpan span, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<CodeActionInfo>>(Array.Empty<CodeActionInfo>());

	public Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> ApplyCodeActionAsync(DocumentId documentId, string actionId, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>(new Dictionary<string, IReadOnlyList<TextEdit>>());

	public Task<ExtractInterfaceInfo?> GetExtractInterfaceInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<ExtractInterfaceInfo?>(null);

	public Task<ExtractInterfaceResult?> ExtractInterfaceAsync(DocumentId documentId, int offset, string interfaceName,
		IReadOnlyList<string> memberIds, bool addInterfaceToClass, bool includeComments, CancellationToken cancellationToken)
		=> Task.FromResult<ExtractInterfaceResult?>(null);

	public Task<SymbolKindInfo?> GetSymbolKindAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<SymbolKindInfo?>(null);

	public Task<SymbolHierarchyResult?> GetBaseSymbolsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<SymbolHierarchyResult?>(null);

	public Task<SymbolHierarchyResult?> GetDerivedSymbolsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<SymbolHierarchyResult?>(null);

	public Task<string?> GetHelpKeywordAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<string?>(null);

	public Task<string?> GetContainingTypeNameAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<string?>(null);

	public Task<IReadOnlyList<NavigationTarget>> FindMemberAsync(string typeFullName, string methodName, int? parameterCount, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<NavigationTarget>>(Array.Empty<NavigationTarget>());

	public Task RefreshProjectAsync(DocumentId documentId, CancellationToken cancellationToken) => Task.CompletedTask;

	#endregion
}
