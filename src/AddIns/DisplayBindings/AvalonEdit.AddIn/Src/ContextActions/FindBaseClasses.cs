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

// Rewritten against Microsoft.CodeAnalysis directly (see doc/technotes/csharp-roslyn.md).
// No longer a ResolveResultMenuCommand (that requires an ICSharpCode.TypeSystem ResolveResult,
// which nothing produces today); resolves its own Roslyn symbol at the caret instead.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using ICSharpCode.SharpDevelop.Roslyn;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.ContextActions;
using Microsoft.CodeAnalysis;

namespace ICSharpCode.AvalonEdit.AddIn.ContextActions
{
	public class FindBaseClassesOrMembers : AbstractMenuCommand
	{
		public override void Run()
		{
			ITextEditor editor = SD.GetActiveViewContentService<ITextEditor>();
			ISymbol symbolUnderCaret = RoslynWorkspaceHelper.GetSymbolAtCaret(editor);

			var typeSymbol = symbolUnderCaret as INamedTypeSymbol;
			if (typeSymbol != null) {
				MakePopupWithBaseClasses(typeSymbol).OpenAtCaretAndFocus();
				return;
			}

			if (symbolUnderCaret is IMethodSymbol || symbolUnderCaret is IPropertySymbol
			    || symbolUnderCaret is IEventSymbol || symbolUnderCaret is IFieldSymbol) {
				var method = symbolUnderCaret as IMethodSymbol;
				if (method != null && (method.MethodKind == MethodKind.Constructor || method.MethodKind == MethodKind.Destructor)) {
					MakePopupWithBaseClasses(method.ContainingType).OpenAtCaretAndFocus();
				} else {
					MakePopupWithBaseMembers(symbolUnderCaret).OpenAtCaretAndFocus();
				}
				return;
			}

			MessageService.ShowError("${res:ICSharpCode.Refactoring.NoClassOrMemberUnderCursorError}");
		}

		static ContextActionsPopup MakePopupWithBaseClasses(INamedTypeSymbol type)
		{
			var baseClassList = new List<INamedTypeSymbol>();
			for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType) {
				baseClassList.Add(baseType);
			}
			baseClassList.AddRange(type.AllInterfaces);

			var popupViewModel = new ContextActionsPopupViewModel();
			popupViewModel.Title = MenuService.ConvertLabel(StringParser.Parse(
				"${res:SharpDevelop.Refactoring.BaseClassesOf}", new StringTagPair("Name", type.Name)));
			popupViewModel.Actions = new ObservableCollection<ContextActionViewModel>(
				baseClassList.Select(baseClass => GoToEntityAction.MakeViewModel(baseClass, null)));
			return new ContextActionsPopup { Actions = popupViewModel };
		}

		#region Base (overridden) members
		static ContextActionsPopup MakePopupWithBaseMembers(ISymbol member)
		{
			var popupViewModel = new ContextActionsPopupViewModel {
				Title = MenuService.ConvertLabel(StringParser.Parse(
					"${res:SharpDevelop.Refactoring.BaseMembersOf}",
					new StringTagPair("Name", member.ToDisplayString()))
				)
			};
			popupViewModel.Actions = BuildBaseMemberListViewModel(member);
			return new ContextActionsPopup { Actions = popupViewModel };
		}

		static ObservableCollection<ContextActionViewModel> BuildBaseMemberListViewModel(ISymbol member)
		{
			var c = new ObservableCollection<ContextActionViewModel>();
			ObservableCollection<ContextActionViewModel> lastBase = c;

			ISymbol current = member;
			while (current != null) {
				ISymbol baseMember = RoslynSymbolHelper.GetBaseMember(current);
				if (baseMember == null)
					break;
				var newChild = new ObservableCollection<ContextActionViewModel>();
				lastBase.Add(GoToEntityAction.MakeViewModel(baseMember, newChild));
				lastBase = newChild;
				current = baseMember;
			}

			return c;
		}
		#endregion
	}
}
