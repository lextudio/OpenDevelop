using System;
using Hornung.ResourceToolkit.Resolver;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;

namespace Hornung.ResourceToolkit.CodeCompletion
{
    public class ResourceCodeCompletionItem : DefaultCompletionItem
    {
        public ResourceCodeCompletionItem(string key, string description)
            : base(key)
        {
            this.Description = description;
            this.Image = ClassBrowserIconService.Const;
        }

        public override void Complete(CompletionContext context)
        {
            this.CompleteInternal(context, this.Text);
        }

        protected void CompleteInternal(CompletionContext context, string key)
        {
            string insertString;
            if (key != null) {
                insertString = RoslynAstCacheService.GenerateKeyLiteral(key);
            } else {
                insertString = key;
            }
            context.Editor.Document.Replace(context.StartOffset, context.Length, insertString);
            context.EndOffset = context.StartOffset + insertString.Length;
        }
    }
}
