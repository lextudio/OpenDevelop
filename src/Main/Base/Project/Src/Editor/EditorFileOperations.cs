using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;

namespace ICSharpCode.SharpDevelop.Editor;

/// <summary>
/// Applies a complete text replacement through an open editor where possible. This is project/UI
/// plumbing, deliberately independent of whichever language service produced the replacement.
/// </summary>
public static class EditorFileOperations
{
	public static void OpenAndReplaceText(string filePath, string newText)
	{
		var viewContent = SD.FileService.OpenFile(FileName.Create(filePath));
		var editor = viewContent?.GetService<ITextEditor>();
		if (editor != null) {
			using (editor.Document.OpenUndoGroup())
				editor.Document.Text = newText;
		} else {
			File.WriteAllText(filePath, newText);
		}
	}
}
