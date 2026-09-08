using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
			// Expanding a context action must not block the UI thread on the language service
			// (roslyn-host-process.md §5.1); the popup is opened once the query completes.
			_ = RunAsync();
		}

		async Task RunAsync()
		{
			var result = await QueryAsync();
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

		static async Task<SymbolHierarchyResult> QueryAsync()
		{
			ITextEditor editor = SD.GetActiveViewContentService<ITextEditor>();
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (editor == null || registry == null || !registry.TryGetProtocol(editor.FileName, out var protocol))
				return null;
			var document = new ICSharpCode.SharpDevelop.LanguageServices.Protocol.TextDocumentIdentifier(editor.FileName);
			await protocol.TextDocumentDidChangeAsync(document, editor.Document.Text, CancellationToken.None);
			return (await protocol.TypeHierarchySubtypesAsync(document, editor.Caret.Offset, CancellationToken.None)).Value;
		}
	}
}
