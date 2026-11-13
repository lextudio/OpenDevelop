using System.Linq;
using System.Windows.Input;
using ICSharpCode.Core.Presentation;

namespace OpenDevelop.Terminal
{
	/// <summary>The commands the terminal handles itself when it has the keyboard focus (everything else goes to the shell).</summary>
	public enum TerminalShortcut
	{
		Copy, Paste, Find, ZoomIn, ZoomOut, ZoomReset,
		/// <summary>macOS only.</summary>
		SelectAll,
		/// <summary>macOS only.</summary>
		Clear,
		ScrollPageUp, ScrollPageDown, ScrollToTop, ScrollToBottom
	}

	/// <summary>
	/// The terminal's own shortcuts as keyboard shortcut commands (key <c>terminal:Copy</c>, scope and group "Terminal") and the matching of a key press against
	/// them, with the user's overrides from the Keyboard page. Pure of any view state so it can be tested. doc/technotes/keyboard-shortcuts.md.
	/// </summary>
	public static class TerminalShortcuts
	{
		public const string KeyPrefix = "terminal:";
		public const string Scope = "Terminal";

		public static string KeyOf(TerminalShortcut command)
		{
			return KeyPrefix + command;
		}

		/// <summary>The commands of this platform (SelectAll and Clear exist on macOS only), for the Keyboard page.</summary>
		public static IEnumerable<ExternalCommand> Describe(bool isMac)
		{
			// Cmd is reported as the Windows modifier; on every platform Ctrl+Shift also works as the default for the clipboard, zoom and find commands
			string chord = isMac ? "Windows+" : "Ctrl+Shift+";
			yield return Command(TerminalShortcut.Copy, "Copy", chord + "C");
			yield return Command(TerminalShortcut.Paste, "Paste", chord + "V");
			yield return Command(TerminalShortcut.Find, "Find", chord + "F");
			yield return Command(TerminalShortcut.ZoomIn, "Zoom In", chord + "OemPlus");
			yield return Command(TerminalShortcut.ZoomOut, "Zoom Out", chord + "OemMinus");
			yield return Command(TerminalShortcut.ZoomReset, "Reset Zoom", chord + "D0");
			if (isMac) {
				yield return Command(TerminalShortcut.SelectAll, "Select All", "Windows+A");
				yield return Command(TerminalShortcut.Clear, "Clear", "Windows+K");
			}
			yield return Command(TerminalShortcut.ScrollPageUp, "Scroll Page Up", "Shift+PageUp");
			yield return Command(TerminalShortcut.ScrollPageDown, "Scroll Page Down", "Shift+PageDown");
			yield return Command(TerminalShortcut.ScrollToTop, "Scroll To Top", "Shift+Home");
			yield return Command(TerminalShortcut.ScrollToBottom, "Scroll To Bottom", "Shift+End");
		}

		static ExternalCommand Command(TerminalShortcut command, string name, string defaultGesture)
		{
			return new ExternalCommand(KeyOf(command), name, Scope, Scope, defaultGesture);
		}

		/// <summary>
		/// The terminal command a key press is, or null when it is not one (the key then goes to the shell). A command with an override is matched by that
		/// gesture only (an empty override means it has no shortcut); the others by their defaults. See <see cref="ShortcutMatcher"/>.
		/// </summary>
		/// <param name="overrides">The user's overrides, command key -> gesture text (empty = none); null for none.</param>
		public static TerminalShortcut? Match(Key key, ModifierKeys modifiers, bool isMac, IReadOnlyDictionary<string, string> overrides)
		{
			var commands = Enum.GetValues(typeof(TerminalShortcut)).Cast<TerminalShortcut>().Where(c => IsOnPlatform(c, isMac)).ToList();
			TerminalShortcut match;
			return ShortcutMatcher.TryMatch(commands, KeyOf, c => MatchesDefault(c, key, modifiers), key, modifiers, overrides, out match)
				? match : (TerminalShortcut?)null;
		}

		static bool IsOnPlatform(TerminalShortcut command, bool isMac)
		{
			return isMac || (command != TerminalShortcut.SelectAll && command != TerminalShortcut.Clear);
		}

		/// <summary>What the terminal has always accepted: Cmd (any other modifiers too) or Ctrl+Shift for the clipboard, zoom and find commands.</summary>
		static bool MatchesDefault(TerminalShortcut command, Key key, ModifierKeys modifiers)
		{
			bool cmd = (modifiers & ModifierKeys.Windows) != 0;
			bool chord = cmd || modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
			switch (command) {
				case TerminalShortcut.Copy: return chord && key == Key.C;
				case TerminalShortcut.Paste: return chord && key == Key.V;
				case TerminalShortcut.Find: return chord && key == Key.F;
				case TerminalShortcut.ZoomIn: return chord && (key == Key.OemPlus || key == Key.Add);
				case TerminalShortcut.ZoomOut: return chord && (key == Key.OemMinus || key == Key.Subtract);
				case TerminalShortcut.ZoomReset: return chord && (key == Key.D0 || key == Key.NumPad0);
				case TerminalShortcut.SelectAll: return modifiers == ModifierKeys.Windows && key == Key.A;
				case TerminalShortcut.Clear: return modifiers == ModifierKeys.Windows && key == Key.K;
				case TerminalShortcut.ScrollPageUp: return modifiers == ModifierKeys.Shift && key == Key.PageUp;
				case TerminalShortcut.ScrollPageDown: return modifiers == ModifierKeys.Shift && key == Key.PageDown;
				case TerminalShortcut.ScrollToTop: return modifiers == ModifierKeys.Shift && key == Key.Home;
				case TerminalShortcut.ScrollToBottom: return modifiers == ModifierKeys.Shift && key == Key.End;
				default: return false;
			}
		}
	}

	/// <summary>Registers the terminal's own shortcuts with the Keyboard page at startup.</summary>
	public sealed class RegisterTerminalKeyCommandsCommand : ICSharpCode.Core.AbstractCommand
	{
		public override void Run()
		{
			ExternalCommands.Register(() => TerminalShortcuts.Describe(OperatingSystem.IsMacOS()));
		}
	}
}
