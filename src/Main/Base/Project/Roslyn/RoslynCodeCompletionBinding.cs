// Phase 2 (see doc/technotes/csharp-roslyn.md): real code completion for .cs files backed by
// Microsoft.CodeAnalysis.Completion.CompletionService, reusing RoslynWorkspaceHelper's workspace
// (the same one Phase 1's RoslynParser uses) rather than standing up a second, disconnected one.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;

namespace ICSharpCode.SharpDevelop.Roslyn
{
	public class RoslynCodeCompletionBinding : ICodeCompletionBinding
	{
		public CodeCompletionKeyPressResult HandleKeyPress(ITextEditor editor, char ch)
		{
			return CodeCompletionKeyPressResult.None;
		}

		public bool HandleKeyPressed(ITextEditor editor, char ch)
		{
			if (ch == '.' || char.IsLetter(ch) || ch == '_') {
				return ShowCompletion(editor);
			}
			return false;
		}

		public bool CtrlSpace(ITextEditor editor)
		{
			return ShowCompletion(editor);
		}

		static bool ShowCompletion(ITextEditor editor)
		{
			var document = RoslynWorkspaceHelper.FindDocument(editor.FileName, editor.Document.Text);
			if (document == null)
				return false;

			var completionService = CompletionService.GetService(document);
			if (completionService == null)
				return false;

			int position = editor.Caret.Offset;
			CompletionList completions;
			try {
				completions = completionService.GetCompletionsAsync(document, position).GetAwaiter().GetResult();
			} catch (Exception ex) {
				LoggingService.Warn("RoslynCodeCompletionBinding: GetCompletionsAsync failed. " + ex.Message);
				return false;
			}
			if (completions == null || completions.ItemsList.Count == 0)
				return false;

			var span = completions.Span;
			var list = new DefaultCompletionItemList {
				PreselectionLength = position - span.Start,
			};
			foreach (var item in completions.ItemsList.OrderByDescending(i => i.Rules.MatchPriority)) {
				list.Items.Add(new RoslynCompletionItem(item, completionService, document, span.Start, span.End));
			}
			list.SortItems();
			editor.ShowCompletionWindow(list);
			return true;
		}
	}

	sealed class RoslynCompletionItem : ICompletionItem
	{
		readonly CompletionItem item;
		readonly CompletionService service;
		readonly Microsoft.CodeAnalysis.Document document;

		public RoslynCompletionItem(CompletionItem item, CompletionService service, Microsoft.CodeAnalysis.Document document, int startOffset, int endOffset)
		{
			this.item = item;
			this.service = service;
			this.document = document;
		}

		public string Text { get { return item.DisplayText; } }
		public string Description { get { return item.InlineDescription; } }
		public double Priority { get { return 0; } }

		public IImage Image {
			get {
				string resourceName = MapTagToIcon(item.Tags);
				return resourceName != null ? SD.ResourceService.GetImage(resourceName) : null;
			}
		}

		static string MapTagToIcon(System.Collections.Immutable.ImmutableArray<string> tags)
		{
			if (tags.Contains(WellKnownTags.Class)) return "Icons.16x16.Class";
			if (tags.Contains(WellKnownTags.Interface)) return "Icons.16x16.Interface";
			if (tags.Contains(WellKnownTags.Structure)) return "Icons.16x16.Struct";
			if (tags.Contains(WellKnownTags.Enum)) return "Icons.16x16.Enum";
			if (tags.Contains(WellKnownTags.Delegate)) return "Icons.16x16.Delegate";
			if (tags.Contains(WellKnownTags.Method) || tags.Contains(WellKnownTags.ExtensionMethod)) return "Icons.16x16.Method";
			if (tags.Contains(WellKnownTags.Field)) return "Icons.16x16.Field";
			if (tags.Contains(WellKnownTags.Property)) return "Icons.16x16.Property";
			if (tags.Contains(WellKnownTags.Event)) return "Icons.16x16.Event";
			if (tags.Contains(WellKnownTags.Namespace)) return "Icons.16x16.NameSpace";
			if (tags.Contains(WellKnownTags.Keyword)) return "Icons.16x16.Keyword";
			if (tags.Contains(WellKnownTags.Local) || tags.Contains(WellKnownTags.Parameter)) return "Icons.16x16.Local";
			return null;
		}

		public void Complete(ICSharpCode.SharpDevelop.Editor.CodeCompletion.CompletionContext context)
		{
			var change = Task.Run(() => service.GetChangeAsync(document, item, cancellationToken: CancellationToken.None)).GetAwaiter().GetResult();
			var text = change.TextChange.NewText ?? string.Empty;
			var span = change.TextChange.Span;
			context.Editor.Document.Replace(span.Start, span.Length, text);
			context.EndOffset = span.Start + text.Length;
		}
	}
}
