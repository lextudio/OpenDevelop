using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace ICSharpCode.GtkDesigner;

/// <summary>
/// Makes a copy of a .ui document that the preview's GtkBuilder can instantiate. GtkBuilder
/// rejects the whole document over one problem - an application-defined widget class ("Invalid
/// object type"), a property the class does not have, a value it cannot parse, a reference to a
/// missing object - so a custom widget or a hand-edited typo would otherwise leave nothing to
/// show. Only the preview copy changes; the document the designer edits and saves is untouched,
/// so nothing is lost (doc/technotes/gtk-designer.md: "unsupported but valid XML is round-tripped
/// and shown as a read-only/placeholder node").
/// </summary>
public static class GtkPreviewSanitizer
{
	/// <summary>Returns the instantiable copy; each change it made is added to <paramref name="diagnostics"/>.</summary>
	public static string Sanitize(string uiText, GirCatalog catalog, IList<string> diagnostics, Func<string?, string?>? layoutChildClass = null, Func<GirClass, string?>? unavailableReason = null, string? baseDirectory = null, string? previewDirectory = null)
	{
		var document = XDocument.Parse(uiText);
		// A composite template's class is the application's own GType, which the preview never
		// registers; previewed, it is simply an instance of its parent type (same id: the class name).
		foreach (var template in document.Descendants().Where(e => e.Name.LocalName == "template").ToList()) {
			var className = (string?)template.Attribute("class") ?? "";
			template.Name = template.Name.Namespace + "object";
			template.SetAttributeValue("class", (string?)template.Attribute("parent") ?? "GtkWidget");
			template.SetAttributeValue("parent", null);
			template.SetAttributeValue("id", className);
		}
		var ids =new HashSet<string>(document.Descendants().Where(e => e.Name.LocalName == "object").Select(e => (string?)e.Attribute("id")).Where(id => id != null)!, StringComparer.Ordinal);
		foreach (var obj in document.Descendants().Where(e => e.Name.LocalName == "object").ToList()) {
			var builderClass = (string?)obj.Attribute("class") ?? "";
			var id = (string?)obj.Attribute("id") ?? "(anonymous)";
			var girClass = catalog.FindByTypeName(builderClass);
			// Known to GIR but not loadable here (e.g. an Adw class without libadwaita): same stand-in.
			var unavailable = girClass is { Abstract: false } ? unavailableReason?.Invoke(girClass) : null;
			if (girClass is not { Abstract: false } || unavailable != null) {
				ReplaceWithPlaceholder(obj, builderClass);
				diagnostics.Add(unavailable != null
					? $"'{id}' is a {builderClass}: {unavailable} It is previewed as a placeholder and kept unchanged in the file."
					: $"'{id}' is a {builderClass}, which is not a GTK type the designer can instantiate; it is previewed as a placeholder and kept unchanged in the file.");
				continue;
			}
			foreach (var property in obj.Elements().Where(e => e.Name.LocalName == "property").ToList())
				Check(property, builderClass, id, catalog, ids, diagnostics, "", baseDirectory, previewDirectory);
			var parentObject = obj.Parent?.Name.LocalName == "child" ? obj.Parent.Parent : null;
			var layoutClass = layoutChildClass?.Invoke((string?)parentObject?.Attribute("class"));
			foreach (var layout in obj.Elements().Where(e => e.Name.LocalName == "layout").ToList()) {
				if (layoutClass == null) {
					layout.Remove();
					diagnostics.Add($"'{id}' has <layout> properties, but its container has no layout manager the designer knows; they are not previewed.");
					continue;
				}
				foreach (var property in layout.Elements().Where(e => e.Name.LocalName == "property").ToList())
					Check(property, layoutClass, id, catalog, ids, diagnostics, "layout ", baseDirectory, previewDirectory);
			}
		}
		return document.ToString(SaveOptions.DisableFormatting);
	}

	static void Check(XElement property, string ownerClass, string id, GirCatalog catalog, HashSet<string> ids, IList<string> diagnostics, string what, string? baseDirectory, string? previewDirectory)
	{
		var name = (string?)property.Attribute("name") ?? "";
		// <property name="content"><object .../></property> embeds its value; the object itself is
		// sanitised in its own right, and its text is not an id to resolve.
		if (property.Elements().Any(e => e.Name.LocalName is "object" or "template")) return;
		var info = catalog.FindProperty(ownerClass, name);
		if (info == null) {
			property.Remove();
			diagnostics.Add($"'{id}' sets {what}property '{name}', which {ownerClass} does not have; it is not previewed.");
			return;
		}
		if (info.Kind == GirValueKind.Reference) {
			if (!ids.Contains(property.Value.Trim())) {
				property.Remove();
				diagnostics.Add($"'{id}'.{name} refers to '{property.Value.Trim()}', which is not in this document; it is not previewed.");
			}
			return;
		}
		if (info.Kind == GirValueKind.Path || info.Kind == GirValueKind.String && name is "file" or "filename")
			property.Value = PreviewPath(property.Value.Trim(), baseDirectory, previewDirectory);
		if (!GirCatalog.TryNormalizeValue(info, property.Value, out _, out var error)) {
			property.Remove();
			diagnostics.Add($"'{id}'.{name}: {error} It is not previewed.");
		}
	}

	/// <summary>
	/// A file reference as the preview's GtkBuilder can load it. GtkBuilder only turns a file into an
	/// image when it resolves a RELATIVE path against the file it is building from (measured on GTK
	/// 4 / Windows: an absolute path or file:// URI sets GtkPicture:file but loads no texture), and
	/// the preview builds from its own temp file, not the .ui. So: resolve the reference against the
	/// .ui's folder (relative) or take it as given (absolute, file:// URI), then express it relative
	/// to the preview file's folder. Resource paths and other URIs are left alone.
	/// </summary>
	public static string PreviewPath(string value, string? baseDirectory, string? previewDirectory)
	{
		string? absolute = null;
		if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var uri)) absolute = uri.LocalPath;
		else if (IsRelativePath(value)) { if (baseDirectory != null) absolute = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, value)); }
		else if (System.IO.Path.IsPathRooted(value) && !value.StartsWith("/org/", StringComparison.Ordinal)) absolute = value;
		if (absolute == null) return value;
		if (previewDirectory == null || !string.Equals(System.IO.Path.GetPathRoot(absolute), System.IO.Path.GetPathRoot(previewDirectory), StringComparison.OrdinalIgnoreCase))
			return absolute;   // another drive: no relative form; best effort
		return System.IO.Path.GetRelativePath(previewDirectory, absolute).Replace('\\', '/');
	}

	static bool IsRelativePath(string value) => value.Length > 0 && !value.Contains("://", StringComparison.Ordinal)
		&& !value.StartsWith("resource:", StringComparison.Ordinal) && !value.StartsWith("/org/", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(value);

	/// <summary>A labelled stand-in occupying the custom widget's place: a vertical GtkBox when it
	/// has children (they still preview inside it), a GtkLabel naming the class otherwise. The id
	/// stays, so selection, bounds and the outline still map to the object.</summary>
	static void ReplaceWithPlaceholder(XElement obj, string builderClass)
	{
		var hasChildren = obj.Elements().Any(e => e.Name.LocalName == "child");
		foreach (var e in obj.Elements().Where(e => e.Name.LocalName is "property" or "signal" or "layout").ToList())
			if (e.Name.LocalName != "layout") e.Remove();
		obj.SetAttributeValue("class", hasChildren ? "GtkBox" : "GtkLabel");
		if (hasChildren) obj.AddFirst(new XElement("property", new XAttribute("name", "orientation"), "vertical"));
		else obj.AddFirst(new XElement("property", new XAttribute("name", "label"), "‹" + builderClass + "›"));
	}
}
