using System;
using System.Linq;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
    public sealed class LanguageServiceCompletionItemList : DefaultCompletionItemList
    {
        public static LanguageServiceCompletionItemList FromResult(CompletionResult result)
        {
            if (result is null)
                throw new ArgumentNullException(nameof(result));

            var list = new LanguageServiceCompletionItemList();
            foreach (var item in result.Items.Select(ConvertItem))
            {
                list.Items.Add(item);
            }

            list.SortItems();
            list.SuggestedItem = list.Items.FirstOrDefault();
            return list;
        }

        /// <summary>
        /// The list for a window opened at <paramref name="caretOffset"/>: the word already typed (the result's
        /// replacement span, else the identifier characters before the caret) becomes the preselection, so the window
        /// starts at the word, filters by it and highlights the matching item instead of showing every item from the
        /// caret with the first one selected.
        /// </summary>
        public static LanguageServiceCompletionItemList FromResult(CompletionResult result, IDocument document, int caretOffset)
        {
            var list = FromResult(result);
            if (document is null)
                return list;
            caretOffset = Math.Max(0, Math.Min(caretOffset, document.TextLength));
            int start = caretOffset;
            int end = caretOffset;
            if (result.ReplacementSpan is TextSpan span) {
                start = Math.Max(0, Math.Min(document.GetOffset(span.Start.Line, span.Start.Column), caretOffset));
                end = Math.Max(caretOffset, Math.Min(document.GetOffset(span.End.Line, span.End.Column), document.TextLength));
            } else {
                while (start > 0 && IsWordCharacter(document.GetCharAt(start - 1)))
                    start--;
            }
            list.PreselectionLength = caretOffset - start;
            list.PostselectionLength = end - caretOffset;
            if (list.PreselectionLength > 0)
                list.SuggestedItem = null; // the window selects the best match for the typed word
            return list;
        }

        static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

        static ICompletionItem ConvertItem(CompletionItem item)
        {
            return new LanguageServiceCompletionItem(item);
        }

        sealed class LanguageServiceCompletionItem : DefaultCompletionItem
        {
            readonly CompletionItem _item;

            public LanguageServiceCompletionItem(CompletionItem item)
                : base(item.InsertionText)
            {
                _item = item ?? throw new ArgumentNullException(nameof(item));
                Description = item.Description ?? string.Empty;
            }

            public override string Description { get; set; }

            public override void Complete(CompletionContext context)
            {
                if (context is null)
                    throw new ArgumentNullException(nameof(context));

                context.Editor.Document.Replace(context.StartOffset, context.Length, _item.InsertionText);
                context.EndOffset = context.StartOffset + _item.InsertionText.Length;
            }
        }
    }
}
