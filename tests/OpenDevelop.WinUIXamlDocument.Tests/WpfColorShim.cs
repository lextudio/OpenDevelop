// The linked WinUIXamlDocumentEditor validates a rich-text run's literal foreground with WPF's ColorConverter. These tests never take that
// path (they measure parse/save and attribute edits), and referencing WPF would make the test project Windows-only for one helper, so this
// is the smallest stand-in that compiles: it accepts "#RGB", "#ARGB", "#RRGGBB" and "#AARRGGBB" and rejects everything else (the real one
// also accepts color names).
namespace System.Windows.Media
{
	internal readonly struct Color
	{
	}

	internal static class ColorConverter
	{
		public static object ConvertFromString(string value)
		{
			if (value is { Length: > 1 } && value[0] == '#' && value.Length is 4 or 5 or 7 or 9
				&& value.Skip(1).All(Uri.IsHexDigit))
				return new Color();
			throw new FormatException("Not a color: " + value);
		}
	}
}
