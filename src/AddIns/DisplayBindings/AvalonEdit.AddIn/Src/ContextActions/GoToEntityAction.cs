using System;
using System.Collections.ObjectModel;
using System.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor.ContextActions;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Refactoring;

namespace ICSharpCode.AvalonEdit.AddIn.ContextActions
{
	public sealed class GoToEntityAction : IContextAction
	{
		readonly SymbolNavigationNode node;

		public static ContextActionViewModel MakeViewModel(SymbolNavigationNode node)
		{
			return new ContextActionViewModel {
				Action = new GoToEntityAction(node),
				Comment = string.IsNullOrEmpty(node.Container) ? null : "(in " + node.Container + ")",
				ChildActions = new ObservableCollection<ContextActionViewModel>(node.Children.Select(MakeViewModel))
			};
		}

		public GoToEntityAction(SymbolNavigationNode node) => this.node = node ?? throw new ArgumentNullException(nameof(node));

		public string GetDisplayName(EditorRefactoringContext context) => node.Name;

		public void Execute(EditorRefactoringContext context) => FileService.JumpToFilePosition(
			FileName.Create(node.Target.FileName), node.Target.Position.Line, node.Target.Position.Column);

		IContextActionProvider IContextAction.Provider => null;
	}
}
