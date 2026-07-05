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

// The live-editor-caret path is rewritten against Microsoft.CodeAnalysis directly (see
// doc/technotes/csharp-roslyn.md, Phase 1 "option (b)"). The IMemberModel path is untouched -
// that's SharpDevelop's separate background project-content model (bookmarks etc.), not part of
// the ParserService/IParser resolve flow this rewrite targets.

using System.Collections.Generic;
using System.Windows.Controls;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.TypeSystem;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.SharpDevelop.Roslyn;
using Microsoft.CodeAnalysis;
using ISymbol = Microsoft.CodeAnalysis.ISymbol;

namespace ICSharpCode.SharpDevelop.Editor.Commands
{
	/// <summary>
	/// Builds context menu items with commands related to the declaring type of a member.
	/// </summary>
	public class DeclaringTypeSubMenuBuilder : IMenuItemBuilder
	{
		public IEnumerable<object> BuildItems(Codon codon, object parameter)
		{
			if (parameter is IMemberModel) {
				// Menu is directly created from a member model (e.g. bookmarks etc.)
				return BuildItemsForEntityModelMember((IMemberModel)parameter);
			}

			ISymbol symbol = parameter as ISymbol;
			if (symbol == null) {
				var editor = parameter as ITextEditor ?? SD.GetActiveViewContentService<ITextEditor>();
				symbol = editor != null ? RoslynWorkspaceHelper.GetSymbolAtCaret(editor) : null;
			}

			bool isMember = symbol is IMethodSymbol || symbol is IFieldSymbol || symbol is IPropertySymbol || symbol is IEventSymbol;
			if (!isMember)
				return null;

			INamedTypeSymbol declaringType = symbol.ContainingType;
			if (declaringType == null)
				return null;

			var items = new List<object>();
			var declaringTypeItem = new MenuItem {
				Header = SD.ResourceService.GetString("SharpDevelop.Refactoring.DeclaringType") + ": " + declaringType.Name,
				Icon = new Image { Source = RoslynSymbolIcons.GetImage(declaringType) }
			};

			var subItems = MenuService.CreateMenuItems(null, declaringType, "/SharpDevelop/EntityContextMenu");
			if (subItems != null) {
				foreach (var item in subItems) {
					declaringTypeItem.Items.Add(item);
				}
			}
			items.Add(declaringTypeItem);

			return items;
		}

		IEnumerable<object> BuildItemsForEntityModelMember(IMemberModel memberModel)
		{
			IMember member = memberModel.Resolve();
			ITypeDefinition declaringType = member != null ? member.DeclaringTypeDefinition : null;
			if (declaringType == null)
				return null;

			var items = new List<object>();
			var declaringTypeItem = new MenuItem {
				Header = SD.ResourceService.GetString("SharpDevelop.Refactoring.DeclaringType") + ": " + declaringType.Name,
				Icon = new Image { Source = ClassBrowserIconService.GetIcon(declaringType).ImageSource }
			};

			var subItems = MenuService.CreateMenuItems(null, declaringType, "/SharpDevelop/EntityContextMenu");
			if (subItems != null) {
				foreach (var item in subItems) {
					declaringTypeItem.Items.Add(item);
				}
			}
			items.Add(declaringTypeItem);

			return items;
		}
	}
}
