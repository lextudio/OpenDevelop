using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Editor.ContextActions;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Refactoring;

namespace ICSharpCode.SharpDevelop.Editor.Commands
{
	public class GoToDefinition : AbstractMenuCommand
	{
		public override void Run()
		{
			var editor = SD.GetActiveViewContentService<ITextEditor>();
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (editor == null || registry == null || !registry.TryGetService(editor.FileName, out var service))
				return;

			var id = new ICSharpCode.SharpDevelop.LanguageServices.DocumentId(editor.FileName);
			service.UpsertDocumentAsync(id, editor.Document.Text, CancellationToken.None).GetAwaiter().GetResult();
			var targets = service.GoToDefinitionAsync(id, editor.Caret.Offset, CancellationToken.None).GetAwaiter().GetResult();
			if (targets.Count == 1) {
				Jump(targets[0]);
			} else if (targets.Count > 1) {
				var model = new ContextActionsPopupViewModel {
					Title = MenuService.ConvertLabel("${res:SharpDevelop.Refactoring.PartsOfClass}"),
					Actions = new ObservableCollection<ContextActionViewModel>(targets.Select(MakeViewModel))
				};
				SD.GetActiveViewContentService<IEditorUIService>()?.ShowContextActionsPopup(model);
			}
		}

		static ContextActionViewModel MakeViewModel(NavigationTarget target) => new() {
			Action = new GoToLocationAction(target),
			Image = IconService.GetImageSource(IconService.GetImageForFile(target.FileName)),
			Comment = "(in " + Path.GetDirectoryName(target.FileName) + ")"
		};

		static void Jump(NavigationTarget target) => FileService.JumpToFilePosition(
			FileName.Create(target.FileName), target.Position.Line, target.Position.Column);

		sealed class GoToLocationAction : IContextAction
		{
			readonly NavigationTarget target;
			public GoToLocationAction(NavigationTarget target) => this.target = target;
			public string GetDisplayName(EditorRefactoringContext context) => Path.GetFileName(target.FileName);
			public void Execute(EditorRefactoringContext context) => Jump(target);
			IContextActionProvider IContextAction.Provider => null;
		}
	}
}
