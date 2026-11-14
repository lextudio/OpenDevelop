using System;
using System.Globalization;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>
/// Computes the source edit for an element the designer positions with its Margin - every parent
/// except a Canvas, which uses <see cref="WinUICanvasPositionEditor"/>. The delta lands on the side
/// the element's own alignment anchors, so a right-aligned element keeps its right Margin.
///
/// A zero delta asks for no edit at all: a resize-handle drag moves nothing, and
/// <see cref="WinUIXamlDocumentEditor.SetAttribute"/> records a history entry even when the value
/// is unchanged, so writing the untouched Margin there leaves a second, empty Undo step after a
/// resize (found by the real-pointer resize journey).
/// </summary>
public static class WinUIXamlMarginEditor
{
	/// <summary>
	/// The Margin the delta produces. Returns false when the delta rounds to zero and therefore
	/// needs no attribute edit; <paramref name="margin"/> is null when the element should carry no
	/// Margin at all.
	/// </summary>
	public static bool TryDelta(XElement element, double deltaX, double deltaY, out string margin)
	{
		ArgumentNullException.ThrowIfNull(element);
		margin = null;
		var x = (int)Math.Round(deltaX);
		var y = (int)Math.Round(deltaY);
		if (x == 0 && y == 0)
			return false;

		var (left, top, right, bottom) = Parse((string)element.Attribute("Margin"));
		switch ((string)element.Attribute("HorizontalAlignment"))
		{
			case "Right": right -= x; break;
			case "Center": left += x; right -= x; break;
			default: left += x; break;
		}
		switch ((string)element.Attribute("VerticalAlignment"))
		{
			case "Bottom": bottom -= y; break;
			case "Center": top += y; bottom -= y; break;
			default: top += y; break;
		}

		if (left != 0 || top != 0 || right != 0 || bottom != 0)
			margin = Format(left, top, right, bottom);
		return true;
	}

	static (double Left, double Top, double Right, double Bottom) Parse(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return (0, 0, 0, 0);
		var parts = value.Split(',');
		if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var all))
			return (all, all, all, all);
		if (parts.Length == 2
			&& double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lr)
			&& double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var tb))
			return (lr, tb, lr, tb);
		if (parts.Length == 4
			&& double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l)
			&& double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
			&& double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
			&& double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
			return (l, t, r, b);
		return (0, 0, 0, 0);
	}

	/// <summary>Keeps the source compact: "v", "h v", or "l t r b".</summary>
	static string Format(double left, double top, double right, double bottom)
	{
		if (left == right && top == bottom)
			return left == top
				? left.ToString(CultureInfo.InvariantCulture)
				: $"{left.ToString(CultureInfo.InvariantCulture)},{top.ToString(CultureInfo.InvariantCulture)}";
		return $"{left.ToString(CultureInfo.InvariantCulture)},{top.ToString(CultureInfo.InvariantCulture)}," +
			$"{right.ToString(CultureInfo.InvariantCulture)},{bottom.ToString(CultureInfo.InvariantCulture)}";
	}
}
