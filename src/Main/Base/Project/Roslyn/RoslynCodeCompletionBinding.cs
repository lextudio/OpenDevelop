// Phase 2 (see doc/technotes/csharp-roslyn.md): real code completion for .cs files backed by
// the shared ILanguageService contract selected by LanguageServiceRegistry.

using System;
using System.Threading;
using System.Runtime.CompilerServices;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace ICSharpCode.SharpDevelop.Roslyn
{
	public class RoslynCodeCompletionBinding : ICodeCompletionBinding
	{
		sealed class PendingCompletion { public CancellationTokenSource Cancellation; }
		static readonly ConditionalWeakTable<ITextEditor, PendingCompletion> pending = new();
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
			if (editor == null || editor.FileName == null)
				return false;
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(editor.FileName, out var service))
				return false;
			var request = pending.GetValue(editor, _ => new PendingCompletion());
			request.Cancellation?.Cancel();
			var cancellation = new CancellationTokenSource();
			request.Cancellation = cancellation;
			ShowCompletionAsync(editor, service, request, cancellation);
			// HandleKeyPressed runs after insertion. Claim this language's completion request,
			// not the character; the reply may legitimately contain no suggestions.
			return true;
		}

		static async void ShowCompletionAsync(ITextEditor editor, LanguageServices.ILanguageService service,
			PendingCompletion request, CancellationTokenSource cancellation)
		{
			var document = editor.Document;
			var fileName = editor.FileName;
			var text = document.Text;
			var offset = editor.Caret.Offset;
			try {
				var documentId = new ICSharpCode.SharpDevelop.LanguageServices.DocumentId(fileName);
				await service.UpsertDocumentAsync(documentId, text, cancellation.Token);
				var completions = await service.GetCompletionsAsync(documentId, offset, cancellation.Token);
				if (cancellation.IsCancellationRequested || completions.Items.Count == 0
					|| editor.Document != document || editor.FileName != fileName
					|| editor.Caret.Offset != offset || document.Text != text)
					return;
				editor.ShowCompletionWindow(LanguageServiceCompletionItemList.FromResult(completions));
			} catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
			} catch (Exception ex) {
				LoggingService.Warn("RoslynCodeCompletionBinding: GetCompletionsAsync failed. " + ex.Message);
			} finally {
				if (ReferenceEquals(request.Cancellation, cancellation)) request.Cancellation = null;
				cancellation.Dispose();
			}
		}
	}
}
