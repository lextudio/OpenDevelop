using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;

namespace ICSharpCode.SharpDevelop.Designer.Surface;

/// <summary>The commands the shared design surface handles itself while it has the keyboard focus.</summary>
public enum DesignerShortcut
{
	Undo, Redo, Delete, Rename
}

/// <summary>
/// The design surface's own shortcuts as keyboard shortcut commands (key <c>designer:Undo</c>, scope and group "Designer") and the matching of a key press
/// against them, with the user's overrides from the Keyboard page. Arrow-key nudging and Tab navigation are not part of it. doc/technotes/keyboard-shortcuts.md.
/// </summary>
public static class DesignerShortcuts
{
	public const string KeyPrefix = "designer:";
	public const string Scope = "Designer";

	public static string KeyOf(DesignerShortcut command) => KeyPrefix + command;

	/// <summary>The commands for the Keyboard page.</summary>
	public static IEnumerable<ExternalCommand> Describe()
	{
		yield return new ExternalCommand(KeyOf(DesignerShortcut.Undo), "Undo", Scope, Scope, "Ctrl+Z");
		yield return new ExternalCommand(KeyOf(DesignerShortcut.Redo), "Redo", Scope, Scope, "Ctrl+Y");
		yield return new ExternalCommand(KeyOf(DesignerShortcut.Delete), "Delete Selection", Scope, Scope, "Delete");
		yield return new ExternalCommand(KeyOf(DesignerShortcut.Rename), "Rename Selection", Scope, Scope, "F2");
	}

	/// <summary>The design surface command a key press is, or null. See <see cref="ShortcutMatcher"/>.</summary>
	public static DesignerShortcut? Match(Key key, ModifierKeys modifiers, IReadOnlyDictionary<string, string> overrides)
	{
		var commands = Enum.GetValues(typeof(DesignerShortcut)).Cast<DesignerShortcut>().ToList();
		return ShortcutMatcher.TryMatch(commands, KeyOf, c => MatchesDefault(c, key, modifiers), key, modifiers, overrides, out var match)
			? match : (DesignerShortcut?)null;
	}

	/// <summary>What the surface has always accepted: Ctrl+Z; Ctrl+Y and Ctrl+Shift+Z; Delete; F2.</summary>
	static bool MatchesDefault(DesignerShortcut command, Key key, ModifierKeys modifiers)
	{
		bool ctrl = (modifiers & ModifierKeys.Control) != 0;
		bool shift = (modifiers & ModifierKeys.Shift) != 0;
		return command switch {
			DesignerShortcut.Undo => ctrl && key == Key.Z && !shift,
			DesignerShortcut.Redo => ctrl && (key == Key.Y || (key == Key.Z && shift)),
			DesignerShortcut.Delete => key == Key.Delete,
			DesignerShortcut.Rename => key == Key.F2,
			_ => false
		};
	}
}

/// <summary>Registers the design surface's own shortcuts with the Keyboard page at startup.</summary>
public sealed class RegisterDesignerKeyCommandsCommand : AbstractCommand
{
	public override void Run() => ExternalCommands.Register(DesignerShortcuts.Describe);
}
