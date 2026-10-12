using System.Windows.Input;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>What a key press does in a designer's inline text editor.</summary>
public enum InlineTextEditKeyAction
{
	/// <summary>Not an editor command; let the text box handle it.</summary>
	None,
	Commit,
	Cancel,
	InsertNewline
}

/// <summary>
/// The in-process WPF designer's <c>InPlaceEditor</c> contract: Enter commits, Shift+Enter inserts a
/// newline, Escape cancels. A newline is only meaningful where the edited value is multi-line text
/// (a TextBlock's Text); elsewhere Shift+Enter commits like Enter.
/// </summary>
public static class InlineTextEditKeys
{
	public const string MultilineToolTip = "Press Enter to apply changes,\nShift+Enter to insert a newline,\nEsc to cancel editing.";

	public static InlineTextEditKeyAction Resolve(Key key, ModifierKeys modifiers, bool acceptsNewlines)
	{
		if (key == Key.Escape)
			return InlineTextEditKeyAction.Cancel;
		if (key != Key.Enter)
			return InlineTextEditKeyAction.None;
		return acceptsNewlines && (modifiers & ModifierKeys.Shift) != 0
			? InlineTextEditKeyAction.InsertNewline
			: InlineTextEditKeyAction.Commit;
	}
}
