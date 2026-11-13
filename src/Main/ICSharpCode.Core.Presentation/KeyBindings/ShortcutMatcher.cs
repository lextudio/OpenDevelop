using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace ICSharpCode.Core.Presentation
{
	/// <summary>
	/// Matches a key press against the commands a control handles itself (a terminal's copy, a design surface's delete), with the user's overrides from the
	/// Keyboard page. A command with an override is matched by that gesture only (an empty override means it has no shortcut, a text that is not a gesture
	/// matches nothing); the others by their defaults. Overrides win over defaults. Pure, so it can be tested. doc/technotes/keyboard-shortcuts.md.
	/// </summary>
	public static class ShortcutMatcher
	{
		/// <param name="commands">The commands that exist where the key was pressed.</param>
		/// <param name="keyOf">The command key (what KeyBindings.xml stores) of a command.</param>
		/// <param name="matchesDefault">Whether the key press is the command's default shortcut.</param>
		/// <param name="overrides">The user's overrides, command key -> gesture text (empty = none); null for none.</param>
		/// <param name="match">The command the key press is.</param>
		public static bool TryMatch<T>(IEnumerable<T> commands, Func<T, string> keyOf, Func<T, bool> matchesDefault,
		                               Key key, ModifierKeys modifiers, IReadOnlyDictionary<string, string> overrides, out T match)
		{
			if (commands == null) throw new ArgumentNullException("commands");
			if (keyOf == null) throw new ArgumentNullException("keyOf");
			if (matchesDefault == null) throw new ArgumentNullException("matchesDefault");
			if (overrides != null) {
				foreach (T command in commands) {
					string text;
					if (overrides.TryGetValue(keyOf(command), out text) && Matches(text, key, modifiers)) {
						match = command;
						return true;
					}
				}
			}
			foreach (T command in commands) {
				if (overrides != null && overrides.ContainsKey(keyOf(command)))
					continue;
				if (matchesDefault(command)) {
					match = command;
					return true;
				}
			}
			match = default(T);
			return false;
		}

		static bool Matches(string gestureText, Key key, ModifierKeys modifiers)
		{
			if (string.IsNullOrWhiteSpace(gestureText))
				return false;
			try {
				KeyGesture gesture = MenuService.ParseShortcut(gestureText);
				return gesture.Key == key && gesture.Modifiers == modifiers;
			} catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException || ex is FormatException) {
				return false;
			}
		}
	}
}
