using System.Windows;
using System.Windows.Media;

namespace OpenDevelop.Terminal
{
	/// <summary>Reads the terminal's colours from the IDE theme's semantic brushes. UI thread only.</summary>
	static class TerminalTheme
	{
		public static TerminalColors Current()
		{
			uint background = Brush("ToolWindowBackground", 0x1e1e1e);
			uint foreground = Brush("Foreground", 0xcccccc);
			double luminance = (0.299 * (background >> 16 & 0xff) + 0.587 * (background >> 8 & 0xff) + 0.114 * (background & 0xff)) / 255;
			return new TerminalColors(background, foreground, luminance < 0.5);
		}

		static uint Brush(string key, uint fallback)
		{
			if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
				return (uint)(brush.Color.R << 16 | brush.Color.G << 8 | brush.Color.B);
			return fallback;
		}
	}
}
