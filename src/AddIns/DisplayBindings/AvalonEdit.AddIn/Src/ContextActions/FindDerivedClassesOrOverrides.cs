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
// derived-type search uses Roslyn's SymbolFinder instead of the old
// ICSharpCode.SharpDevelop.Refactoring.FindReferenceService.BuildDerivedTypesGraph.

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
using Microsoft.CodeAnalysis.FindSymbols;

namespace ICSharpCode.AvalonEdit.AddIn.ContextActions
{
	public class FindDerivedClassesOrOverrides : AbstractMenuCommand
	{
		public override void Run()
		{
			ITextEditor editor = SD.GetActiveViewContentService<ITextEditor>();
			ISymbol symbolUnderCaret = RoslynWorkspaceHelper.GetSymbolAtCaret(editor);

			var typeSymbol = symbolUnderCaret as INamedTypeSymbol;
			if (typeSymbol != null && !typeSymbol.IsSealed) {
				MakePopupWithDerivedClasses(typeSymbol).OpenAtCaretAndFocus();
				return;
			}

			var method = symbolUnderCaret as IMethodSymbol;
			if (method != null && (method.MethodKind == MethodKind.Constructor || method.MethodKind == MethodKind.Destructor)) {
				MakePopupWithDerivedClasses(method.ContainingType).OpenAtCaretAndFocus();
				return;
			}

			if (symbolUnderCaret is IMethodSymbol || symbolUnderCaret is IPropertySymbol || symbolUnderCaret is IEventSymbol) {
				if (IsOverridable(symbolUnderCaret)) {
					MakePopupWithOverrides(symbolUnderCaret).OpenAtCaretAndFocus();
					return;
				}
			}

			MessageService.ShowError("${res:ICSharpCode.Refactoring.NoClassOrOverridableSymbolUnderCursorError}");
		}

		static bool IsOverridable(ISymbol member)
		{
			return (member.IsVirtual || member.IsAbstract || member.IsOverride)
				&& !member.IsSealed
				&& member.ContainingType != null;
		}

		#region Derived Classes
		static ITreeNode<INamedTypeSymbol> BuildDerivedTypesTree(INamedTypeSymbol baseClass)
		{
			var solution = RoslynWorkspaceHelper.GetSolution();
			var allDerived = (baseClass.TypeKind == TypeKind.Interface
				? SymbolFinder.FindImplementationsAsync(baseClass, solution).Result
				: SymbolFinder.FindDerivedClassesAsync(baseClass, solution).Result)
				.OfType<INamedTypeSymbol>().ToList();
			return BuildNode(baseClass, allDerived);
		}

		static TreeNode<INamedTypeSymbol> BuildNode(INamedTypeSymbol type, List<INamedTypeSymbol> allDerived)
		{
			var node = new TreeNode<INamedTypeSymbol>(type);
			var immediateChildren = allDerived.Where(
				candidate => SymbolEqualityComparer.Default.Equals(candidate.BaseType, type)
				             || candidate.Interfaces.Contains(type, SymbolEqualityComparer.Default)).ToList();
			node.Children = immediateChildren.Select(child => (ITreeNode<INamedTypeSymbol>)BuildNode(child, allDerived)).ToList();
			return node;
		}

		static ContextActionsPopup MakePopupWithDerivedClasses(INamedTypeSymbol baseClass)
		{
			var derivedClassesTree = BuildDerivedTypesTree(baseClass);
			var popupViewModel = new ContextActionsPopupViewModel();
			popupViewModel.Title = MenuService.ConvertLabel(StringParser.Parse(
				"${res:SharpDevelop.Refactoring.ClassesDerivingFrom}", new StringTagPair("Name", baseClass.Name)));
			popupViewModel.Actions = BuildTreeViewModel(derivedClassesTree.Children);
			return new ContextActionsPopup { Actions = popupViewModel };
		}

		static ObservableCollection<ContextActionViewModel> BuildTreeViewModel(IEnumerable<ITreeNode<INamedTypeSymbol>> classTree)
		{
			return new ObservableCollection<ContextActionViewModel>(
				classTree.Select(
					node => GoToEntityAction.MakeViewModel(node.Content, BuildTreeViewModel(node.Children))));
		}
		#endregion

		#region Overrides
		static ContextActionsPopup MakePopupWithOverrides(ISymbol member)
		{
			var derivedClassesTree = BuildDerivedTypesTree(member.ContainingType);
			var popupViewModel = new ContextActionsPopupViewModel {
				Title = MenuService.ConvertLabel(StringParser.Parse(
					"${res:SharpDevelop.Refactoring.OverridesOf}",
					new StringTagPair("Name", member.ToDisplayString()))
				)
			};
			popupViewModel.Actions = new OverridesPopupTreeViewModelBuilder(member).BuildTreeViewModel(derivedClassesTree.Children);
			return new ContextActionsPopup { Actions = popupViewModel };
		}

		class OverridesPopupTreeViewModelBuilder
		{
			readonly ISymbol member;

			public OverridesPopupTreeViewModelBuilder(ISymbol member)
			{
				this.member = member;
			}

			public ObservableCollection<ContextActionViewModel> BuildTreeViewModel(IEnumerable<ITreeNode<INamedTypeSymbol>> classTree)
			{
				var c = new ObservableCollection<ContextActionViewModel>();
				foreach (var node in classTree) {
					var childNodes = BuildTreeViewModel(node.Children);

					ISymbol derivedMember = RoslynSymbolHelper.GetDerivedMember(member, node.Content);
					if (derivedMember != null) {
						c.Add(GoToEntityAction.MakeViewModel(derivedMember, childNodes));
					} else {
						// If the member doesn't exist in the derived class, directly append the
						// children of that derived class here.
						foreach (var child in childNodes)
							c.Add(child);
						// This is necessary so that the method C.M() is shown in the case
						// "class A { virtual void M(); } class B : A {} class C : B { override void M(); }"
					}
				}
				return c;
			}
		}
		#endregion
	}
}
