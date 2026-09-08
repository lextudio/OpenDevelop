using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
			// Fire-and-forget onto RunAsync, the pattern FindReferencesCommand already uses here.
			// F12 must not block the UI thread on the language service: in-process that is a stall
			// for as long as Roslyn takes, and once the service moves out of process it becomes a
			// cross-process round trip behind a keypress (roslyn-host-process.md §5.1).
			_ = RunAsync();
		}

		async Task RunAsync()
		{
			var editor = SD.GetActiveViewContentService<ITextEditor>();
			var registry = SD.GetService<LanguageServiceRegistry>();
			// Routed through the protocol rather than ILanguageService directly: this call site is
			// then already written against the surface that survives the language service moving
			// out of process (roslyn-host-process.md Phase 2).
			if (editor == null || registry == null || !registry.TryGetProtocol(editor.FileName, out var protocol))
				return;

			var document = new ICSharpCode.SharpDevelop.LanguageServices.Protocol.TextDocumentIdentifier(editor.FileName);
			await protocol.TextDocumentDidChangeAsync(document, editor.Document.Text, CancellationToken.None);
			var targets = (await protocol.TextDocumentDefinitionAsync(document, editor.Caret.Offset, CancellationToken.None)).Value;
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
