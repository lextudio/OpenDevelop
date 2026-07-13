using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;

namespace ICSharpCode.SharpDevelop.LanguageServices.Lsp
{
	public sealed class LspCodeCompletionBinding : ICodeCompletionBinding
	{
		public CodeCompletionKeyPressResult HandleKeyPress(ITextEditor editor, char ch)
		{
			return CodeCompletionKeyPressResult.None;
		}

		public bool HandleKeyPressed(ITextEditor editor, char ch)
		{
			if (ch == '<' || ch == '.' || ch == ':' || ch == '=' || char.IsLetter(ch) || ch == '_')
				return ShowCompletion(editor);
			return false;
		}

		public bool CtrlSpace(ITextEditor editor)
		{
			return ShowCompletion(editor);
		}

		static bool ShowCompletion(ITextEditor editor)
		{
			if (editor == null || editor.FileName == null)
				return false;

			var service = LspServiceManager.GetService(editor.FileName);
			if (service == null)
				return false;

			var documentId = new DocumentId(editor.FileName);
			try {
				service.UpsertDocumentAsync(documentId, editor.Document.Text, System.Threading.CancellationToken.None)
					.GetAwaiter()
					.GetResult();

				var result = service.GetCompletionsAsync(documentId, editor.Caret.Offset, System.Threading.CancellationToken.None)
					.GetAwaiter()
					.GetResult();
				if (result.Items.Count == 0)
					return false;

				var list = LanguageServiceCompletionItemList.FromResult(result);
				editor.ShowCompletionWindow(list);
				return true;
			} catch (Exception ex) {
				LoggingService.Warn("LspCodeCompletionBinding: completion failed. " + ex.Message);
				return false;
			}
		}

	}
}
