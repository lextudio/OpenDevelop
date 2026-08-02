using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.ContextActions;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace ICSharpCode.AvalonEdit.AddIn.ContextActions
{
	public class FindDerivedClassesOrOverrides : AbstractMenuCommand
	{
		public override void Run()
		{
			var result = Query();
			if (result == null) {
				MessageService.ShowError("${res:ICSharpCode.Refactoring.NoClassOrOverridableSymbolUnderCursorError}");
				return;
			}
			var model = new ContextActionsPopupViewModel {
				Title = MenuService.ConvertLabel(StringParser.Parse("${res:SharpDevelop.Refactoring.ClassesDerivingFrom}", new StringTagPair("Name", result.Subject))),
				Actions = new ObservableCollection<ContextActionViewModel>(result.Nodes.Select(GoToEntityAction.MakeViewModel))
			};
			new ContextActionsPopup { Actions = model }.OpenAtCaretAndFocus();
		}

		static SymbolHierarchyResult Query()
		{
			ITextEditor editor = SD.GetActiveViewContentService<ITextEditor>();
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (editor == null || registry == null || !registry.TryGetService(editor.FileName, out var service))
				return null;
			var id = new ICSharpCode.SharpDevelop.LanguageServices.DocumentId(editor.FileName);
			service.UpsertDocumentAsync(id, editor.Document.Text, CancellationToken.None).GetAwaiter().GetResult();
			return service.GetDerivedSymbolsAsync(id, editor.Caret.Offset, CancellationToken.None).GetAwaiter().GetResult();
		}
	}
}
