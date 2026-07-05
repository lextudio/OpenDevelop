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

// Rewritten against Microsoft.CodeAnalysis directly (see doc/technotes/csharp-roslyn.md, Phase 1
// "option (b)"). No longer a ResolveResultMenuCommand - resolves its own Roslyn symbol at the caret.

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Editor.ContextActions;
using ICSharpCode.SharpDevelop.Refactoring;
using ICSharpCode.SharpDevelop.Roslyn;
using Microsoft.CodeAnalysis;

namespace ICSharpCode.SharpDevelop.Editor.Commands
{
	public class GoToDefinition : AbstractMenuCommand
	{
		public override void Run()
		{
			ITextEditor editor = SD.GetActiveViewContentService<ITextEditor>();
			if (editor == null)
				return;
			RunOn(RoslynWorkspaceHelper.GetSymbolAtCaret(editor));
		}

		/// <summary>
		/// Navigates to the definition of the given Roslyn symbol. Used directly by
		/// CodeEditorView's Ctrl+Click handler, which already has the symbol at hand.
		/// </summary>
		public void RunOn(ISymbol symbol)
		{
			if (symbol == null)
				return;

			var namedType = symbol as INamedTypeSymbol;
			if (namedType != null && namedType.DeclaringSyntaxReferences.Length > 1) {
				ShowPopupWithPartialClasses(namedType);
				return;
			}

			var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
			if (location == null) {
				// No source location (e.g. defined in a referenced assembly): nothing more we can
				// do without a decompiler/metadata-as-source view, which isn't wired up yet.
				return;
			}

			try {
				var span = location.GetLineSpan();
				FileService.JumpToFilePosition(new FileName(span.Path), span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
			} catch (Exception ex) {
				MessageService.ShowException(ex, "Error jumping to '" + location.SourceTree.FilePath + "'.");
			}
		}

		void ShowPopupWithPartialClasses(INamedTypeSymbol definition)
		{
			var popupViewModel = new ContextActionsPopupViewModel();
			popupViewModel.Title = MenuService.ConvertLabel(StringParser.Parse(
				"${res:SharpDevelop.Refactoring.PartsOfClass}", new StringTagPair("Name", definition.Name)));
			var parts = definition.DeclaringSyntaxReferences
				.Select(r => r.GetSyntax().GetLocation())
				.Where(l => l.IsInSource);
			popupViewModel.Actions = new ObservableCollection<ContextActionViewModel>(parts.Select(MakeViewModel));
			var uiService = SD.GetActiveViewContentService<IEditorUIService>();
			if (uiService != null)
				uiService.ShowContextActionsPopup(popupViewModel);
		}

		ContextActionViewModel MakeViewModel(Location location)
		{
			var span = location.GetLineSpan();
			return new ContextActionViewModel {
				Action = new GoToLocationAction(span),
				Image = IconService.GetImageSource(IconService.GetImageForFile(span.Path)),
				Comment = string.Format("(in {0})", Path.GetDirectoryName(span.Path)),
				ChildActions = null
			};
		}

		class GoToLocationAction : IContextAction
		{
			readonly FileLinePositionSpan span;

			public GoToLocationAction(FileLinePositionSpan span)
			{
				this.span = span;
			}

			public string GetDisplayName(EditorRefactoringContext context)
			{
				return Path.GetFileName(span.Path);
			}

			public void Execute(EditorRefactoringContext context)
			{
				SD.FileService.JumpToFilePosition(new FileName(span.Path), span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
			}

			IContextActionProvider IContextAction.Provider {
				get { return null; }
			}
		}
	}
}
