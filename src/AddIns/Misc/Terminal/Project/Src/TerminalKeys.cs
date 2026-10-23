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

		/// <summary>
		/// What a Mac user expects the Option/Cmd editing keys to do in a shell (the same mapping VS Code's terminal has by default):
		/// Option+Left/Right/Backspace/Delete work on words (ESC b, ESC f, ESC DEL, ESC d); Cmd+Left/Right go to the start/end of the line
		/// (Ctrl+A, Ctrl+E) and Cmd+Backspace/Delete delete to the start/end of the line (Ctrl+U, Ctrl+K). Only with exactly that one modifier.
		/// </summary>
		public static string MacLineEditing(Key key, ModifierKeys modifiers)
		{
			if (modifiers == ModifierKeys.Alt) {
				switch (key) {
					case Key.Left: return "\u001bb";
					case Key.Right: return "\u001bf";
					case Key.Back: return "\u001b\u007f";
					case Key.Delete: return "\u001bd";
				}
			} else if (modifiers == ModifierKeys.Windows) { // Cmd
				switch (key) {
					case Key.Left: return "\u0001";
					case Key.Right: return "\u0005";
					case Key.Back: return "\u0015";
					case Key.Delete: return "\u000b";
				}
			}
			return null;
		}

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
