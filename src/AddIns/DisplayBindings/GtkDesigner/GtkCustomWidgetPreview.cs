using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ICSharpCode.GtkDesigner;

/// <summary>The preview contract for application GtkBuilder classes, read from a sibling
/// <c>.gtkdesigner.json</c>. This type only parses data and never loads anything. A custom type is
/// represented either by an already known GTK type with explicit property-name aliases
/// (<c>customWidgets</c>), or by itself: an <c>assemblies</c> entry names a project output assembly
/// that the disposable design host - never the IDE - loads so its GObject subclasses can be built.</summary>
public sealed class GtkCustomWidgetPreview
{
	readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
	public sealed class Entry
	{
		public string PreviewClass { get; init; } = "";
		public IReadOnlyDictionary<string, string> PropertyMap { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
	}
	/// <summary>Full paths of the project assemblies the design host may load, as declared in <c>assemblies</c>
	/// (relative to the declaration file). Only existing .dll files are listed.</summary>
	public IReadOnlyList<string> Assemblies => assemblies;
	readonly List<string> assemblies = new();
	public bool TryGet(string className, out Entry entry) => entries.TryGetValue(className, out entry!);

	/// <summary>Reads the optional sibling <c>.gtkdesigner.json</c>. Invalid declarations do not
	/// affect the document; callers receive a diagnostic and retain the safe placeholder fallback.</summary>
	public static GtkCustomWidgetPreview Load(string? uiFileName, IList<string> diagnostics)
	{
		var result = new GtkCustomWidgetPreview();
		if (string.IsNullOrWhiteSpace(uiFileName)) return result;
		var path = Path.Combine(Path.GetDirectoryName(uiFileName) ?? "", ".gtkdesigner.json");
		if (!File.Exists(path)) return result;
		try {
			using var json = JsonDocument.Parse(File.ReadAllText(path));
			var hasAssemblies = json.RootElement.TryGetProperty("assemblies", out var declared);
			if (hasAssemblies) ReadAssemblies(declared, Path.GetDirectoryName(path) ?? "", result.assemblies, diagnostics);
			var hasWidgets = json.RootElement.TryGetProperty("customWidgets", out var widgets);
			if (!hasWidgets && !hasAssemblies) throw new InvalidOperationException("expected an object property 'customWidgets' or an array property 'assemblies'.");
			if (hasWidgets && widgets.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("'customWidgets' must be an object.");
			if (hasWidgets) foreach (var item in widgets.EnumerateObject()) {
				if (item.Value.ValueKind != JsonValueKind.Object || !item.Value.TryGetProperty("previewClass", out var preview) || preview.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(preview.GetString())) {
					diagnostics.Add("GTK custom-preview declaration '" + item.Name + "' has no previewClass and was ignored.");
					continue;
				}
				var map = new Dictionary<string, string>(StringComparer.Ordinal);
				if (item.Value.TryGetProperty("propertyMap", out var properties)) {
					if (properties.ValueKind != JsonValueKind.Object) { diagnostics.Add("GTK custom-preview declaration '" + item.Name + "'.propertyMap must be an object and was ignored."); continue; }
					foreach (var property in properties.EnumerateObject()) if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString())) map[property.Name] = property.Value.GetString()!;
				}
				result.entries[item.Name] = new Entry { PreviewClass = preview.GetString()!, PropertyMap = map };
			}
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
			diagnostics.Add("Could not read GTK custom-preview declarations '" + path + "': " + ex.Message);
		}
		return result;
	}

	static void ReadAssemblies(JsonElement declared, string directory, List<string> into, IList<string> diagnostics)
	{
		if (declared.ValueKind != JsonValueKind.Array) { diagnostics.Add("GTK custom-widget 'assemblies' must be an array and was ignored."); return; }
		foreach (var item in declared.EnumerateArray()) {
			var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
			if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { diagnostics.Add("GTK custom-widget assembly entry " + item + " is not a .dll path and was ignored."); continue; }
			var full = Path.GetFullPath(Path.Combine(directory, name));
			if (!File.Exists(full)) { diagnostics.Add("GTK custom-widget assembly '" + full + "' does not exist (build the project first); its widgets are previewed as placeholders."); continue; }
			if (!into.Exists(known => string.Equals(known, full, StringComparison.Ordinal))) into.Add(full);
		}
	}
}
