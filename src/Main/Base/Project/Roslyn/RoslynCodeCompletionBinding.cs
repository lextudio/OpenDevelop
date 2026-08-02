// Phase 2 (see doc/technotes/csharp-roslyn.md): real code completion for .cs files backed by
// the shared ILanguageService contract selected by LanguageServiceRegistry.

using System;
using System.Threading;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace ICSharpCode.SharpDevelop.Roslyn
{
	public class RoslynCodeCompletionBinding : ICodeCompletionBinding
	{
		public CodeCompletionKeyPressResult HandleKeyPress(ITextEditor editor, char ch)
		{
			return CodeCompletionKeyPressResult.None;
		}

		public bool HandleKeyPressed(ITextEditor editor, char ch)
		{
			if (ch == '.' || char.IsLetter(ch) || ch == '_') {
				return ShowCompletion(editor);
			}
			return false;
		}

		public bool CtrlSpace(ITextEditor editor)
		{
			return ShowCompletion(editor);
		}

		static bool ShowCompletion(ITextEditor editor)
		{
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(editor.FileName, out var service))
				return false;

			try {
				var documentId = new ICSharpCode.SharpDevelop.LanguageServices.DocumentId(editor.FileName);
				service.UpsertDocumentAsync(documentId, editor.Document.Text, CancellationToken.None).GetAwaiter().GetResult();
				var completions = service.GetCompletionsAsync(documentId, editor.Caret.Offset, CancellationToken.None).GetAwaiter().GetResult();
				if (completions.Items.Count == 0)
					return false;
				editor.ShowCompletionWindow(LanguageServiceCompletionItemList.FromResult(completions));
				return true;
			} catch (Exception ex) {
				LoggingService.Warn("RoslynCodeCompletionBinding: GetCompletionsAsync failed. " + ex.Message);
				return false;
			}
		}
	}
}
