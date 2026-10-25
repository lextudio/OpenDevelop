// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace Debugger.AddIn.Pads.Controls
{
	/// <summary>
	/// Completion for expressions typed into the debug console / watch / breakpoint condition
	/// editors. These editors hold no file of their own, so the language service gets a copy of the
	/// frame's source file with the typed expression inserted as a statement just before the frame's
	/// line (so the frame's locals and the enclosing type are in scope) and is asked for completions
	/// right after the expression. The real document's current text is put back afterwards.
	///
	/// This replaces <c>CreateCompletionBinding</c> on the C# language binding, which is a stub that
	/// always returns null (completion is driven by <c>RoslynCodeCompletionBinding</c> against real
	/// editors), so completion in these editors never worked through it.
	/// </summary>
	static class DebuggerDotCompletion
	{
		/// <summary>
		/// Completions for <paramref name="expression"/> (which ends at the caret) as if typed just
		/// before line <paramref name="contextLine"/> of <paramref name="contextFile"/>; null when no
		/// language service handles that file.
		/// </summary>
		public static async Task<CompletionResult> GetCompletionsAsync(FileName contextFile, int contextLine, string expression, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (contextFile == null || string.IsNullOrWhiteSpace(expression))
				return null;
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(contextFile, out var service))
				return null;

			string original = ReadSourceText(contextFile);
			if (original == null)
				return null;
			int lineStart = OffsetOfLine(original, contextLine);
			string synthetic = original.Insert(lineStart, expression + ";" + Environment.NewLine);
			var documentId = new ICSharpCode.SharpDevelop.LanguageServices.DocumentId(contextFile);
			try {
				await service.UpsertDocumentAsync(documentId, synthetic, cancellationToken).ConfigureAwait(true);
				return await service.GetCompletionsAsync(documentId, lineStart + expression.Length, cancellationToken).ConfigureAwait(true);
			} finally {
				// Put back the file's text as it is NOW (the editor may have been typed in while the
				// language service worked), not the snapshot taken before.
				await service.UpsertDocumentAsync(documentId, ReadSourceText(contextFile) ?? original, CancellationToken.None).ConfigureAwait(true);
			}
		}

		/// <summary>
		/// Shows the completion window for what was just typed in <paramref name="editor"/>, unless
		/// the text moved on (<paramref name="currentExpression"/> changed) while the language
		/// service was working.
		/// </summary>
		public static async Task ShowCompletionAsync(ITextEditor editor, Func<string> currentExpression, FileName contextFile, int contextLine)
		{
			try {
				string expression = currentExpression();
				var result = await GetCompletionsAsync(contextFile, contextLine, expression).ConfigureAwait(true);
				if (result == null || result.Items.Count == 0 || currentExpression() != expression)
					return;
				editor.ShowCompletionWindow(LanguageServiceCompletionItemList.FromResult(result));
			} catch (Exception ex) {
				LoggingService.Warn("Debugger: dot-completion failed", ex);
			}
		}

		// The open editor's live text when the file is open, else the file on disk.
		static string ReadSourceText(FileName file)
		{
			var openFile = SD.FileService.GetOpenFile(file);
			var editor = openFile?.GetService<ITextEditor>();
			if (editor != null)
				return editor.Document.Text;
			return File.Exists(file) ? File.ReadAllText(file) : null;
		}

		// Offset of the first character of the 1-based line (end of text when there are fewer lines).
		static int OffsetOfLine(string text, int line)
		{
			int offset = 0;
			for (int current = 1; current < line; current++) {
				int next = text.IndexOf('\n', offset);
				if (next < 0)
					return text.Length;
				offset = next + 1;
			}
			return offset;
		}
	}
}
