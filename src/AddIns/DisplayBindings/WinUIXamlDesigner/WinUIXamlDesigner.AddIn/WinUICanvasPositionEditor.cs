using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>
/// Computes source edits for a child positioned by a WinUI <c>Canvas</c>. Canvas position is an
/// attached-property contract, not a Margin convention: a move must preserve whichever side the
/// source already anchors (Left versus Right, Top versus Bottom).
/// </summary>
public static class WinUICanvasPositionEditor
{
	static readonly XName CanvasName = XName.Get("Canvas", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");

	public sealed record Change(string Attribute, string Value);

	public static bool IsCanvasChild(XElement element)
	{
		ArgumentNullException.ThrowIfNull(element);
		return element.Parent?.Name == CanvasName;
	}

	public static IReadOnlyList<Change> PositionDelta(XElement element, double deltaX, double deltaY)
	{
		ArgumentNullException.ThrowIfNull(element);
		if (!IsCanvasChild(element))
			return Array.Empty<Change>();

		var changes = new List<Change>();
		AddAxisChanges(changes, element, "Canvas.Left", "Canvas.Right", deltaX);
		AddAxisChanges(changes, element, "Canvas.Top", "Canvas.Bottom", deltaY);
		return changes;
	}

	static void AddAxisChanges(List<Change> changes, XElement element, string leadingAttribute, string trailingAttribute, double delta)
	{
		var roundedDelta = Math.Round(delta);
		if (roundedDelta == 0)
			return;
		var leading = element.Attribute(leadingAttribute);
		var trailing = element.Attribute(trailingAttribute);
		double leadingValue = 0;
		double trailingValue = 0;
		if ((leading != null && !TryParse(leading.Value, out leadingValue))
			|| (trailing != null && !TryParse(trailing.Value, out trailingValue)))
			return;
		// WinUI's Canvas resolves Left/Top before Right/Bottom when both are authored. Keep the
		// non-winning anchor untouched: rewriting it would leave an inert, stale value that
		// becomes observable if the winning anchor is later removed.
		if (leading != null)
			changes.Add(new Change(leadingAttribute, Format(leadingValue + roundedDelta)));
		else if (trailing != null)
			changes.Add(new Change(trailingAttribute, Format(trailingValue - roundedDelta)));
		if (leading == null && trailing == null)
			changes.Add(new Change(leadingAttribute, Format(roundedDelta)));
	}

	static bool TryParse(string value, out double result)
		=> Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

	static string Format(double value) => Math.Round(value).ToString(CultureInfo.InvariantCulture);
}
