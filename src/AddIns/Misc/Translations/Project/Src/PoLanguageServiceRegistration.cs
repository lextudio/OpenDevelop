using System;
using System.Runtime.CompilerServices;
using System.Threading;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace ICSharpCode.Translations
{
	/// <summary>Registers <see cref="PoLanguageService"/> as the ".po" language service: completion, hover, diagnostics, outline
	/// and go to definition all resolve it through <see cref="LanguageServiceRegistry"/>, so nothing else knows about gettext.</summary>
	public sealed class RegisterPoLanguageServiceCommand : AbstractCommand, IDisposable
	{
		IDisposable registration;

		public override void Run()
		{
			registration = SD.GetRequiredService<LanguageServiceRegistry>().RegisterExtension(".po", new PoLanguageService());
		}

		public void Dispose()
		{
			registration?.Dispose();
			registration = null;
		}
	}

	/// <summary>Opens completion for .po files on the keys that mean something there ('#,' flags, a quote inside msgstr,
	/// keyword letters). The service decides what the position means and returns nothing elsewhere.</summary>
	public sealed class PoCodeCompletionBinding : ICodeCompletionBinding
	{
		sealed class PendingCompletion
		{
			public CancellationTokenSource? Cancellation;
		}

		static readonly ConditionalWeakTable<ITextEditor, PendingCompletion> pending = new();

		public CodeCompletionKeyPressResult HandleKeyPress(ITextEditor editor, char ch)
			=> CodeCompletionKeyPressResult.None;

		// ',' and a space reach the flags after "#,"; a quote reaches the source text inside msgstr "; letters reach the keywords.
		public bool HandleKeyPressed(ITextEditor editor, char ch)
		{
			if (ch == ',' || ch == ' ' || ch == '"' || ch == '-' || ch == '[' || char.IsLetterOrDigit(ch))
				return ShowCompletion(editor);
			return false;
		}

		public bool CtrlSpace(ITextEditor editor) => ShowCompletion(editor);

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
			return true;
		}

		static async void ShowCompletionAsync(ITextEditor editor, ICSharpCode.SharpDevelop.LanguageServices.ILanguageService service,
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
				LoggingService.Warn("PoCodeCompletionBinding: GetCompletionsAsync failed. " + ex.Message);
			} finally {
				if (ReferenceEquals(request.Cancellation, cancellation))
					request.Cancellation = null;
				cancellation.Dispose();
			}
		}
	}
}
