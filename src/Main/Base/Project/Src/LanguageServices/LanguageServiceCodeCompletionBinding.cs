#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
	/// <summary>
	/// Completion for any file whose <see cref="ILanguageService"/> is registered on
	/// <see cref="LanguageServiceRegistry"/>. A language contributes only which typed characters may open
	/// the window (<see cref="IsTriggerCharacter"/>); the service decides what the caret position means and
	/// returns nothing elsewhere, so a trigger character that has nothing to complete opens no window.
	///
	/// The request is asynchronous and cancels the editor's previous one, so a fast typist never blocks the UI
	/// thread on the service, and the window opens only if the document, file and caret still match what was
	/// asked about.
	/// </summary>
	public abstract class LanguageServiceCodeCompletionBinding : ICodeCompletionBinding
	{
		sealed class PendingCompletion
		{
			public CancellationTokenSource? Cancellation;
		}

		static readonly ConditionalWeakTable<ITextEditor, PendingCompletion> pending = new();

		/// <summary>True when typing <paramref name="ch"/> should ask the service for completions.</summary>
		protected abstract bool IsTriggerCharacter(char ch);

		public CodeCompletionKeyPressResult HandleKeyPress(ITextEditor editor, char ch)
			=> CodeCompletionKeyPressResult.None;

		public bool HandleKeyPressed(ITextEditor editor, char ch)
			=> IsTriggerCharacter(ch)
				// An open window filters itself as the word grows; a new request would replace it with an unfiltered one.
				&& !((char.IsLetter(ch) || ch == '_') && editor?.ActiveCompletionWindow != null)
				&& ShowCompletion(editor);

		public bool CtrlSpace(ITextEditor editor) => ShowCompletion(editor);

		bool ShowCompletion(ITextEditor editor)
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
			return true;
		}

		async void ShowCompletionAsync(ITextEditor editor, ILanguageService service, PendingCompletion request, CancellationTokenSource cancellation)
		{
			var document = editor.Document;
			var fileName = editor.FileName;
			var text = document.Text;
			var offset = editor.Caret.Offset;
			try {
				var documentId = new DocumentId(fileName);
				await service.UpsertDocumentAsync(documentId, text, cancellation.Token);
				var completions = await service.GetCompletionsAsync(documentId, offset, cancellation.Token);
				if (cancellation.IsCancellationRequested || completions.Items.Count == 0
					|| editor.Document != document || editor.FileName != fileName
					|| editor.Caret.Offset != offset || document.Text != text)
					return;
				editor.ShowCompletionWindow(LanguageServiceCompletionItemList.FromResult(completions, document, offset));
			} catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
			} catch (Exception ex) {
				LoggingService.Warn(GetType().Name + ": GetCompletionsAsync failed. " + ex.Message);
			} finally {
				if (ReferenceEquals(request.Cancellation, cancellation))
					request.Cancellation = null;
				cancellation.Dispose();
			}
		}
	}
}
