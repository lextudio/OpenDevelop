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

// Symbol-search UI is backend-neutral; C#/VB use Roslyn and LSP-backed languages use the
// standard textDocument/references request through ILanguageService.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor.Dialogs;
using ICSharpCode.SharpDevelop.Editor.Search;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Roslyn;
using Microsoft.CodeAnalysis;
using TextLocation = ICSharpCode.AvalonEdit.Document.TextLocation;

namespace ICSharpCode.SharpDevelop.Editor.Commands
{
	/// <summary>
	/// Finds every reference to the symbol at the caret across the whole solution and shows
	/// them in the Search Results pad.
	/// </summary>
	public class FindReferencesCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			var editor = SD.GetActiveViewContentService<ITextEditor>();
			if (editor == null || editor.FileName == null)
				return;
			_ = RunAsync(editor);
		}

		async Task RunAsync(ITextEditor editor)
		{
			string fileName = editor.FileName.ToString();
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(editor.FileName, out var service))
				return;

			SymbolReferencesResult result;
			try {
				var id = new ICSharpCode.SharpDevelop.LanguageServices.DocumentId(fileName);
				await service.UpsertDocumentAsync(id, editor.Document.Text, CancellationToken.None);
				result = await service.FindReferencesAsync(id, editor.Caret.Offset, CancellationToken.None);
			} catch (Exception ex) {
				SD.MessageService.ShowException(ex, "Error finding references.");
				return;
			}
			if (result == null)
				return;

			var matches = result.References.Select(target => ToSearchResultMatch(target, editor)).Where(m => m != null).ToArray();
			string title = StringParser.Parse("${res:SharpDevelop.Refactoring.FindReferences}") + " '" + result.Subject + "'";
			SearchResultsPad.Instance.ShowSearchResults(title, matches);
			SearchResultsPad.Instance.BringToFront();
		}

		static SearchResultMatch ToSearchResultMatch(NavigationTarget target, ITextEditor activeEditor)
		{
			if (string.IsNullOrEmpty(target.FileName) || target.Span == null)
				return null;

			var span = target.Span.Value;
			string text;
			try {
				text = FileName.Create(target.FileName) == activeEditor.FileName
					? activeEditor.Document.Text
					: File.ReadAllText(target.FileName);
			} catch (IOException) {
				return null;
			} catch (UnauthorizedAccessException) {
				return null;
			}
			int startOffset = GetOffset(text, span.Start.Line, span.Start.Column);
			int endOffset = GetOffset(text, span.End.Line, span.End.Column);

			return new SearchResultMatch(
				FileName.Create(target.FileName),
				new TextLocation(span.Start.Line, span.Start.Column),
				new TextLocation(span.End.Line, span.End.Column),
				startOffset, Math.Max(0, endOffset - startOffset),
				displayText: null, defaultTextColor: null);
		}

		static int GetOffset(string text, int requestedLine, int requestedColumn)
		{
			int line = 1;
			int offset = 0;
			while (offset < text.Length && line < requestedLine) {
				if (text[offset++] == '\n')
					line++;
			}
			return Math.Min(text.Length, offset + Math.Max(0, requestedColumn - 1));
		}
	}

	/// <summary>
	/// Renames the symbol at the caret across the whole solution, via
	/// <see cref="RoslynWorkspaceHelper.RenameSymbolAsync"/> - the modern replacement for the
	/// deleted NRefactory-era ResolveResult-based RenameSymbolCommand/FindReferenceService.RenameSymbol.
	/// </summary>
	public class RenameSymbolCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			var editor = SD.GetActiveViewContentService<ITextEditor>();
			if (editor == null || editor.FileName == null)
				return;
			_ = RunAsync(editor);
		}

		async Task RunAsync(ITextEditor editor)
		{
			string fileName = editor.FileName.ToString();
			var location = editor.Caret.Location;

			ISymbol symbol;
			try {
				var document = RoslynWorkspaceHelper.FindDocument(fileName, editor.Document.Text);
				symbol = document != null ? RoslynWorkspaceHelper.GetSymbolAt(document, location) : null;
			} catch (Exception ex) {
				SD.MessageService.ShowException(ex, "Error resolving symbol for Rename.");
				return;
			}
			if (symbol == null)
				return;

			var dialog = new RenameSymbolDialog(name => IsValidIdentifier(symbol, name)) {
				Owner = SD.Workbench.MainWindow,
				OldSymbolName = symbol.Name,
				NewSymbolName = symbol.Name
			};
			if (dialog.ShowDialog() != true)
				return;

			try {
				await RoslynWorkspaceHelper.RenameSymbolAsync(
					symbol, dialog.NewSymbolName, dialog.RenameOverloads, dialog.RenameInStrings, dialog.RenameInComments);
			} catch (Exception ex) {
				SD.MessageService.ShowException(ex, "Error renaming symbol.");
			}
		}

		static bool IsValidIdentifier(ISymbol symbol, string name)
		{
			if (string.IsNullOrEmpty(name))
				return false;
			return symbol.Language == LanguageNames.VisualBasic
				? Microsoft.CodeAnalysis.VisualBasic.SyntaxFacts.IsValidIdentifier(name)
				: Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name);
		}
	}
}
