using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;

namespace OpenDevelop.Terminal
{
	/// <summary>Runs text from the active editor in the active terminal ("Send to Terminal").</summary>
	static class TerminalEditorBridge
	{
		/// <summary>The selection of the active editor, or when nothing is selected the line the caret is on; null when there is no editor or the text is blank.</summary>
		public static string TextToSend(ITextEditor editor)
		{
			if (editor == null)
				return null;
			string text = editor.SelectionLength > 0
				? editor.SelectedText
				: editor.Document.GetText(editor.Document.GetLineByNumber(editor.Caret.Line));
			return string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd('\r', '\n');
		}

		/// <summary>
		/// Shows the pad (starting a terminal if there is none), pastes the text as the terminal's own Paste does (bracketed when the
		/// program asked for it, newlines as Enter) and presses Enter. The keyboard stays where it was. Returns the text sent, or null.
		/// </summary>
		public static async Task<string> SendActiveEditorTextAsync()
		{
			string text = TextToSend(SD.GetActiveViewContentService<ITextEditor>());
			if (text == null)
				return null;
			// Creating the pad normally starts a first terminal; when this request has to start one anyway, do not start two.
			TerminalPad.SkipInitialSession = TerminalPaneModel.Instance == null;
			try {
				SD.Workbench.GetPad(typeof(TerminalPad))?.BringPadToFront();
			} finally {
				TerminalPad.SkipInitialSession = false;
			}
			var pad = TerminalPaneModel.Instance;
			if (pad == null)
				return null;
			if (pad.Active == null)
				await pad.NewSessionAsync();
			var session = pad.Active;
			if (session == null)
				return null;
			session.Paste(text);
			session.Send("\r");
			return text;
		}
	}
}
