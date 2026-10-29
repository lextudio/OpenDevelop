using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ICSharpCode.GtkDesigner;

/// <summary>A deliberately data-only preview contract for application GtkBuilder classes. It never
/// loads a project's assembly: a custom type may only be represented by an already known GTK type
/// and explicit property-name aliases.</summary>
public sealed class GtkCustomWidgetPreview
{
	readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
	public sealed class Entry
	{
		public string PreviewClass { get; init; } = "";
		public IReadOnlyDictionary<string, string> PropertyMap { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
	}
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
			if (!json.RootElement.TryGetProperty("customWidgets", out var widgets) || widgets.ValueKind != JsonValueKind.Object)
				throw new InvalidOperationException("expected an object property 'customWidgets'.");
			foreach (var item in widgets.EnumerateObject()) {
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
}
