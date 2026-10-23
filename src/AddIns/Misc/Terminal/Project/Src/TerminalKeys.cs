using System.Windows.Input;
using G = LibGhostty.GhosttyNativeLibrary;

namespace OpenDevelop.Terminal
{
	/// <summary>WPF <see cref="Key"/> to libghostty's physical key codes.</summary>
	static class TerminalKeys
	{
		static readonly Dictionary<Key, int> Special = new Dictionary<Key, int> {
			[Key.Enter] = G.KeyEnter, [Key.Tab] = G.KeyTab, [Key.Back] = G.KeyBackspace, [Key.Escape] = G.KeyEscape,
			[Key.Up] = G.KeyArrowUp, [Key.Down] = G.KeyArrowDown, [Key.Left] = G.KeyArrowLeft, [Key.Right] = G.KeyArrowRight,
			[Key.Home] = G.KeyHome, [Key.End] = G.KeyEnd, [Key.PageUp] = G.KeyPageUp, [Key.PageDown] = G.KeyPageDown,
			[Key.Insert] = G.KeyInsert, [Key.Delete] = G.KeyDelete,
		};

		/// <param name="isText">The key types a character (letters, digits, space): those are only taken here with Ctrl/Alt.</param>
		public static bool TryMap(Key key, out int ghosttyKey, out string text, out bool isText)
		{
			text = null;
			isText = false;
			if (Special.TryGetValue(key, out ghosttyKey))
				return true;
			if (key >= Key.F1 && key <= Key.F12) {
				ghosttyKey = G.KeyF1 + (key - Key.F1);
				return true;
			}
			isText = true;
			if (key >= Key.A && key <= Key.Z) {
				ghosttyKey = G.KeyA + (key - Key.A);
				text = ((char)('a' + (key - Key.A))).ToString();
				return true;
			}
			if (key >= Key.D0 && key <= Key.D9) {
				ghosttyKey = G.KeyDigit0 + (key - Key.D0);
				text = ((char)('0' + (key - Key.D0))).ToString();
				return true;
			}
			if (key == Key.Space) {
				ghosttyKey = G.KeySpace;
				text = " ";
				return true;
			}
			isText = false;
			return false;
		}
	}
}
