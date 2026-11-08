// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Puts the language service's code actions (Roslyn quick fixes and refactorings for C#/VB) into the
// editor's light bulb. The bulb itself (ContextActionsRenderer, EditorActionsProvider) already
// exists and asks every IContextActionProvider registered on the editor; nothing registered a
// provider backed by ILanguageService.GetCodeActionsAsync until now, so those actions were
// reachable only through DevFlow (od.code-actions).
//
// Registered per language add-in with a <TextEditorExtension> node, like LanguageServiceOutlineExtension.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.Commands;
using ICSharpCode.SharpDevelop.Refactoring;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
	/// <summary>
	/// Adds a <see cref="LanguageServiceContextActionProvider"/> to every editor view of the
	/// languages that register this extension.
	/// </summary>
	public sealed class LanguageServiceContextActionsExtension : ITextEditorExtension
	{
		ITextEditor? editor;
		LanguageServiceContextActionProvider? provider;

		public void Attach(ITextEditor editor)
		{
			if (editor.ContextActionProviders.IsReadOnly)
				return;
			this.editor = editor;
			provider = new LanguageServiceContextActionProvider();
			editor.ContextActionProviders.Add(provider);
		}

		public void Detach()
		{
			if (editor != null && provider != null)
				editor.ContextActionProviders.Remove(provider);
			editor = null;
			provider = null;
		}
	}

	/// <summary>
	/// Offers the code actions the language service reports at the caret. Called on the GUI thread
	/// (see <see cref="IContextActionProvider.GetAvailableActionsAsync"/>), so nothing here blocks:
	/// the service call is awaited without capturing the context.
	/// </summary>
	public sealed class LanguageServiceContextActionProvider : IContextActionProvider
	{
		public string ID => "ICSharpCode.SharpDevelop.LanguageServices.CodeActions";
		public string DisplayName => "Code fixes and refactorings";
		public string Category => "Language service";
		public bool AllowHiding => true;
		public bool IsVisible { get; set; } = true;

		public async Task<IContextAction[]> GetAvailableActionsAsync(EditorRefactoringContext context, CancellationToken cancellationToken)
		{
			var fileName = context.FileName;
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (fileName == null || registry == null || !registry.TryGetService(fileName, out var service))
				return Array.Empty<IContextAction>();

			try {
				var id = new DocumentId(fileName.ToString());
				// The snapshot, not the live document: the bulb computes off the GUI thread's text at
				// the moment the caret settled, and Execute checks the editor still has that text.
				await service.UpsertDocumentAsync(id, context.TextSource.Text, cancellationToken).ConfigureAwait(false);

				var position = new TextPosition(context.CaretLocation.Line, context.CaretLocation.Column);
				var infos = await service.GetCodeActionsAsync(id, new TextSpan(position, position), cancellationToken).ConfigureAwait(false);
				return infos.Select(info => (IContextAction)new LanguageServiceContextAction(this, service, id, info)).ToArray();
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception ex) {
				// The renderer awaits every provider with Task.WhenAll from an async void timer handler that
				// only catches cancellation, so a fault here (a loose file, a solution being unloaded, a
				// workspace error) would surface as an unhandled dispatcher exception on every caret stop.
				LoggingService.Warn("Language service code actions failed for " + fileName + ": " + ex.Message);
				return Array.Empty<IContextAction>();
			}
		}
	}

	/// <summary>One code action from the language service, applied to the editor on Execute.</summary>
	public sealed class LanguageServiceContextAction : IContextAction
	{
		readonly ILanguageService service;
		readonly DocumentId documentId;
		readonly CodeActionInfo info;

		public LanguageServiceContextAction(IContextActionProvider provider, ILanguageService service, DocumentId documentId, CodeActionInfo info)
		{
			Provider = provider;
			this.service = service;
			this.documentId = documentId;
			this.info = info;
		}

		public IContextActionProvider Provider { get; }

		public string Title => info.Title;

		public string GetDisplayName(EditorRefactoringContext context) => info.Title;

		public void Execute(EditorRefactoringContext context)
		{
			_ = ExecuteAsync(context);
		}

		/// <summary>
		/// Applies the action. Returns false (after saying so in the status bar) when it could not be applied;
		/// public so DevFlow can await it instead of polling the editor.
		/// </summary>
		public async Task<bool> ExecuteAsync(EditorRefactoringContext context)
		{
			var editor = context.Editor;
			try {
				// The action id is bound to the text it was computed for, and its edits are line/column
				// positions in that text. If the user typed since, applying would corrupt the file.
				if (editor.Document.Text != context.TextSource.Text) {
					SD.StatusBar.SetMessage("The code changed since this suggestion was computed. Try again.", highlighted: true);
					return false;
				}

				var editsByFile = await service.ApplyCodeActionAsync(documentId, info.Id, CancellationToken.None);
				// Back on the GUI thread: this method was started from it and the await above keeps its context.
				var current = Path.GetFullPath(context.FileName.ToString());
				bool IsCurrent(string file) => string.Equals(Path.GetFullPath(file), current, StringComparison.OrdinalIgnoreCase);

				// The service reports edits only for documents that already exist, so an action that
				// generates a new file (or one with nested choices) comes back empty or pointing at a
				// file that is not on disk. Say so; a click that silently does nothing looks broken.
				var editable = editsByFile?.Where(pair => pair.Value.Count > 0).ToList();
				if (editable == null || editable.Count == 0 || editable.Any(pair => !IsCurrent(pair.Key) && !File.Exists(pair.Key))) {
					SD.StatusBar.SetMessage("'" + info.Title + "' cannot be applied yet: it would create a new file or needs a further choice.", highlighted: true);
					return false;
				}

				foreach (var pair in editable) {
					if (IsCurrent(pair.Key))
						ApplyToEditor(editor, context.TextSource.Text, pair.Value);
					else
						ApplyToFile(pair.Key, pair.Value);
				}
				return true;
			} catch (StaleCodeActionException) {
				SD.StatusBar.SetMessage("The code changed since this suggestion was computed. Try again.", highlighted: true);
				return false;
			} catch (Exception ex) {
				SD.MessageService.ShowException(ex, "Error applying '" + info.Title + "'.");
				return false;
			}
		}

		/// <summary>Edits are positions in <paramref name="snapshot"/>; apply back to front inside one undo step.</summary>
		static void ApplyToEditor(ITextEditor editor, string snapshot, IReadOnlyList<TextEdit> edits)
		{
			var document = editor.Document;
			document.StartUndoableAction();
			try {
				foreach (var edit in edits.OrderByDescending(e => e.Span.Start.Line).ThenByDescending(e => e.Span.Start.Column)) {
					int start = FindReferencesCommand.GetOffset(snapshot, edit.Span.Start.Line, edit.Span.Start.Column);
					int end = FindReferencesCommand.GetOffset(snapshot, edit.Span.End.Line, edit.Span.End.Column);
					document.Replace(start, end - start, edit.NewText);
				}
			} finally {
				document.EndUndoableAction();
			}
		}

		/// <summary>Edits to another file: same treatment as a multi-file rename (opened dirty, so the user can review).</summary>
		static void ApplyToFile(string fileName, IReadOnlyList<TextEdit> edits)
		{
			string text = File.ReadAllText(fileName);
			foreach (var edit in edits.OrderByDescending(e => e.Span.Start.Line).ThenByDescending(e => e.Span.Start.Column)) {
				int start = FindReferencesCommand.GetOffset(text, edit.Span.Start.Line, edit.Span.Start.Column);
				int end = FindReferencesCommand.GetOffset(text, edit.Span.End.Line, edit.Span.End.Column);
				text = text.Substring(0, start) + edit.NewText + text.Substring(end);
			}
			EditorFileOperations.OpenAndReplaceText(fileName, text);
		}
	}
}
