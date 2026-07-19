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

// Rewritten against Microsoft.CodeAnalysis directly (see doc/technotes/csharp-roslyn.md, Phase 3).
// No longer a ResolveResultMenuCommand over the deleted NRefactory-era RefactoringService - resolves
// its own Roslyn symbol at the caret and calls SymbolFinder, same pattern as GoToDefinition.

using System;
using System.Linq;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor.Dialogs;
using ICSharpCode.SharpDevelop.Editor.Search;
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
			RunAsync(editor);
		}

		async void RunAsync(ITextEditor editor)
		{
			string fileName = editor.FileName.ToString();
			var location = editor.Caret.Location;

			ISymbol symbol;
			try {
				var document = RoslynWorkspaceHelper.FindDocument(fileName, editor.Document.Text);
				symbol = document != null ? RoslynWorkspaceHelper.GetSymbolAt(document, location) : null;
			} catch (Exception ex) {
				SD.MessageService.ShowException(ex, "Error resolving symbol for Find References.");
				return;
			}
			if (symbol == null)
				return;

			System.Collections.Generic.IReadOnlyList<Microsoft.CodeAnalysis.FindSymbols.ReferenceLocation> locations;
			try {
				locations = await RoslynWorkspaceHelper.FindReferencesAt(fileName, location);
			} catch (Exception ex) {
				SD.MessageService.ShowException(ex, "Error finding references.");
				return;
			}

			var matches = locations.Select(ToSearchResultMatch).Where(m => m != null).ToArray();
			string title = StringParser.Parse("${res:SharpDevelop.Refactoring.FindReferences}") + " '" + symbol.Name + "'";
			SearchResultsPad.Instance.ShowSearchResults(title, matches);
			SearchResultsPad.Instance.BringToFront();
		}

		static SearchResultMatch ToSearchResultMatch(Microsoft.CodeAnalysis.FindSymbols.ReferenceLocation referenceLocation)
		{
			if (referenceLocation.Document.FilePath == null)
				return null;

			var span = referenceLocation.Location.GetLineSpan();
			var start = new TextLocation(span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
			var end = new TextLocation(span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1);
			var sourceSpan = referenceLocation.Location.SourceSpan;

			return new SearchResultMatch(
				FileName.Create(referenceLocation.Document.FilePath),
				start, end,
				sourceSpan.Start, sourceSpan.Length,
				displayText: null, defaultTextColor: null);
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
			RunAsync(editor);
		}

		async void RunAsync(ITextEditor editor)
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
				await RoslynWorkspaceHelper.RenameSymbolAsync(symbol, dialog.NewSymbolName);
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
