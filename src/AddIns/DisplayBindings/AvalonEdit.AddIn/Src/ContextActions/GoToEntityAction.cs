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

// Rewritten against Microsoft.CodeAnalysis directly (see doc/technotes/csharp-roslyn.md) -
// no longer takes ICSharpCode.TypeSystem.IEntity.

using System;
using System.Collections.ObjectModel;
using System.Linq;

using ICSharpCode.SharpDevelop.Roslyn;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor.ContextActions;
using ICSharpCode.SharpDevelop.Refactoring;
using Microsoft.CodeAnalysis;

namespace ICSharpCode.AvalonEdit.AddIn.ContextActions
{
	public class GoToEntityAction : IContextAction
	{
		public static ContextActionViewModel MakeViewModel(ISymbol symbol, ObservableCollection<ContextActionViewModel> childActions)
		{
			var displayFormat = new SymbolDisplayFormat(
				typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
				genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
				memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType,
				parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName);
			return new ContextActionViewModel {
				Action = new GoToEntityAction(symbol, symbol.ToDisplayString(displayFormat)),
				Image = RoslynSymbolIcons.GetImage(symbol),
				Comment = symbol.ContainingNamespace != null && !symbol.ContainingNamespace.IsGlobalNamespace
					? string.Format("(in {0})", symbol.ContainingNamespace.ToDisplayString())
					: null,
				ChildActions = childActions
			};
		}

		public string DisplayName { get; private set; }

		public ISymbol Entity { get; private set; }

		public string GetDisplayName(EditorRefactoringContext context)
		{
			return DisplayName;
		}

		public GoToEntityAction(ISymbol entity, string displayName)
		{
			if (entity == null)
				throw new ArgumentNullException("entity");
			if (displayName == null)
				throw new ArgumentNullException("displayName");
			this.Entity = entity;
			this.DisplayName = displayName;
		}

		public void Execute(EditorRefactoringContext context)
		{
			var location = this.Entity.Locations.FirstOrDefault(l => l.IsInSource);
			if (location == null)
				return;
			var span = location.GetLineSpan();
			FileService.JumpToFilePosition(
				FileName.Create(span.Path), span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
		}

		IContextActionProvider IContextAction.Provider {
			get { return null; }
		}
	}
}
