using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>Applies one Grid track insertion to source XAML.  The caller supplies measured
/// geometry from the active host, so Auto/star tracks are never reconstructed from source text.</summary>
public static class WinUIGridTrackEditor
{
	public static bool TrySplit(XElement grid, bool isRow, IReadOnlyList<DesignerGridTrackInfo> tracks,
		double position, out string error)
	{
		error = "";
		if (grid == null || !string.Equals(grid.Name.LocalName, "Grid", StringComparison.Ordinal)) {
			error = "Not a Grid";
			return false;
		}
		var collectionName = isRow ? "RowDefinitions" : "ColumnDefinitions";
		var definitionName = isRow ? "RowDefinition" : "ColumnDefinition";
		var definitions = grid.Elements().FirstOrDefault(e => IsNamedElement(e, collectionName));
		var items = definitions?.Elements().Where(e => IsNamedElement(e, definitionName)).ToList();
		if (items is not { Count: > 0 } || tracks == null || tracks.Count != items.Count) {
			error = "Grid track geometry unavailable";
			return false;
		}
		var splitIndex = -1;
		for (var i = 0; i < tracks.Count; i++) {
			var track = tracks[i];
			if (!double.IsFinite(track.Offset) || !double.IsFinite(track.Size) || track.Size <= 1)
				continue;
			if (position > track.Offset && position < track.Offset + track.Size) {
				splitIndex = i;
				break;
			}
		}
		if (splitIndex < 0) {
			error = "The split position must be inside a Grid track";
			return false;
		}

		var old = items[splitIndex];
		var trackInfo = tracks[splitIndex];
		var ratio = (position - trackInfo.Offset) / trackInfo.Size;
		if (ratio <= 0 || ratio >= 1) {
			error = "The split position must be inside a Grid track";
			return false;
		}
		var lengthAttribute = isRow ? "Height" : "Width";
		if (!TryParseLength((string?)old.Attribute(lengthAttribute), trackInfo.Size, out var value, out var unit)) {
			error = "Grid track length is not a literal value";
			return false;
		}
		if (!TryFixChildIndices(grid, splitIndex, isRow, out error))
			return false;
		old.SetAttributeValue(lengthAttribute, Format(value * ratio, unit));
		var inserted = new XElement(old.Name);
		inserted.SetAttributeValue(lengthAttribute, Format(value * (1 - ratio), unit));
		old.AddAfterSelf(inserted);
		return true;
	}

	static bool TryFixChildIndices(XElement grid, int splitIndex, bool isRow, out string error)
	{
		error = "";
		var indexName = XName.Get(isRow ? "Grid.Row" : "Grid.Column");
		var spanName = XName.Get(isRow ? "Grid.RowSpan" : "Grid.ColumnSpan");
		var content = grid.Elements()
			.Where(e => !IsPropertyElement(e))
			.SelectMany(e => IsNamedElement(e, "Children") ? e.Elements() : new[] { e }).ToArray();
		var changes = new List<(XElement Child, XName Name, int Value, XElement? Property)>();
		foreach (var child in content) {
			if (!TryReadAttached(child, indexName, 0, out var index, out var indexProperty)
				|| !TryReadAttached(child, spanName, 1, out var span, out var spanProperty)) {
				error = "Grid child index or span is not a literal value";
				return false;
			}
			span = Math.Max(1, span);
			if (index > splitIndex)
				changes.Add((child, indexName, index + 1, indexProperty));
			else if (splitIndex < index + span)
				changes.Add((child, spanName, span + 1, spanProperty));
		}
		foreach (var change in changes) {
			if (change.Property != null) change.Property.Value = change.Value.ToString(CultureInfo.InvariantCulture);
			else change.Child.SetAttributeValue(change.Name, change.Value);
		}
		return true;
	}

	static bool TryReadAttached(XElement child, XName name, int fallback, out int value, out XElement? property)
	{
		property = child.Elements().FirstOrDefault(e => e.Name.LocalName == name.LocalName);
		var text = (string?)child.Attribute(name) ?? property?.Value;
		if (string.IsNullOrWhiteSpace(text)) { value = fallback; return true; }
		return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
	}

	static bool TryParseLength(string? text, double actual, out double value, out string unit)
	{
		if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "Auto", StringComparison.OrdinalIgnoreCase)) {
			value = actual; unit = "";
			return true;
		}
		if (text.EndsWith("*", StringComparison.Ordinal)) {
			var weight = text[..^1];
			if (string.IsNullOrEmpty(weight)) { value = 1; unit = "*"; return true; }
			if (double.TryParse(weight, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) { unit = "*"; return true; }
			unit = ""; return false;
		}
		if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) { unit = ""; return true; }
		unit = ""; return false;
	}

	static string Format(double value, string unit)
		=> Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture) + unit;

	static bool IsNamedElement(XElement element, string kind)
		=> element.Name.LocalName == kind || element.Name.LocalName.EndsWith("." + kind, StringComparison.Ordinal);

	// XAML property elements are named Owner.Property (for example Grid.Resources or
	// Grid.RowDefinitions), never content children. Only Grid.Children wraps actual children.
	static bool IsPropertyElement(XElement element)
		=> element.Name.LocalName.Contains('.', StringComparison.Ordinal) && !IsNamedElement(element, "Children");
}
