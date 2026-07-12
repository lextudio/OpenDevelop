using System;
using System.Linq;
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
