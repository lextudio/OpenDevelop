// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.ContextActions;
using ICSharpCode.SharpDevelop.Refactoring;

namespace ICSharpCode.AvalonEdit.AddIn.ContextActions
{
	// Never actually used its resolved symbol - just needed an editor - so this is a plain
	// AbstractMenuCommand now instead of a ResolveResultMenuCommand (see doc/technotes/csharp-roslyn.md).
	public class ClipboardRing : AbstractMenuCommand
	{
		public override void Run()
		{
			ITextEditor editor = SD.GetActiveViewContentService<ITextEditor>();
			if(editor == null)
				return;
			
			EditorRefactoringContext context = new EditorRefactoringContext(editor);
			MakePopupWithClipboardOptions(context).OpenAtCaretAndFocus();
		}
		
		static ContextActionsPopup MakePopupWithClipboardOptions(EditorRefactoringContext context)
		{
			var popupViewModel = new ContextActionsPopupViewModel();
			var actions = BuildClipboardRingData(context);
			
			string labelSource = "${res:SharpDevelop.SideBar.ClipboardRing}";
			if (actions == null || actions.Count == 0) 
				labelSource = "${res:SharpDevelop.Refactoring.ClipboardRingEmpty}";
			
			popupViewModel.Title = MenuService.ConvertLabel(StringParser.Parse(labelSource));
			popupViewModel.Actions = actions;
			return new ClipboardRingPopup { Actions = popupViewModel };
		}
		
		static ObservableCollection<ContextActionViewModel> BuildClipboardRingData(EditorRefactoringContext context)
		{
			var clipboardRingItems = ClipboardRingService.GetClipboardRingItems();
			
			var list = new ObservableCollection<ContextActionViewModel>();
			foreach (var item in clipboardRingItems) {
				list.Add(new ContextActionViewModel(new ClipboardRingAction(item), context));
			}
			
			return list;
		}
	}
}
