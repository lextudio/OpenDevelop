using ICSharpCode.SharpDevelop;

namespace OpenDevelop.Terminal
{
	/// <summary>User settings of the terminal, stored in <c>SD.PropertyService</c>; the default shell lives in <see cref="TerminalShells"/>.</summary>
	static class TerminalSettings
	{
		const string FontSizeKey = "Terminal.FontSize";
		const string ScrollbackKey = "Terminal.ScrollbackLines";

		/// <summary>Raised on the UI thread after the options were saved; open views re-measure with the new font size.</summary>
		public static event Action Changed;

		public static double FontSize {
			get => Math.Clamp(SD.PropertyService.Get(FontSizeKey, 13.0), 6, 48);
			set => SD.PropertyService.Set(FontSizeKey, Math.Clamp(value, 6, 48));
		}

		/// <summary>Lines kept above the screen; applies to terminals started afterwards.</summary>
		public static int ScrollbackLines {
			get => Math.Clamp(SD.PropertyService.Get(ScrollbackKey, 10_000), 0, 1_000_000);
			set => SD.PropertyService.Set(ScrollbackKey, Math.Clamp(value, 0, 1_000_000));
		}

		public static void RaiseChanged() => Changed?.Invoke();
	}
}
