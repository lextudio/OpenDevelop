using System;
using System.Globalization;
using Hornung.ResourceToolkit.Gui;
using Hornung.ResourceToolkit.ResourceFileContent;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using ICSharpCode.SharpDevelop.Gui;

namespace Hornung.ResourceToolkit.CodeCompletion
{
    public sealed class NewResourceCodeCompletionItem : ResourceCodeCompletionItem
    {
        readonly IResourceFileContent content;
        readonly string preEnteredName;

        public NewResourceCodeCompletionItem(IResourceFileContent content, string preEnteredName)
            : base(StringParser.Parse("${res:Hornung.ResourceToolkit.CodeCompletion.AddNewEntry}"),
                   String.Format(CultureInfo.CurrentCulture, StringParser.Parse("${res:Hornung.ResourceToolkit.CodeCompletion.AddNewDescription}"), content != null ? content.FileName : null))
        {
            this.content = content;
            this.preEnteredName = preEnteredName;
        }

        public override void Complete(CompletionContext context)
        {
            EditStringResourceDialog dialog = new EditStringResourceDialog(this.content, this.preEnteredName, null, true);
            dialog.Owner = WorkbenchSingleton.MainWindow;
            if (dialog.ShowDialog() != true) {
                return;
            }

            if (this.content != null) {
                this.content.Add(dialog.Key, dialog.Value);
            }

            this.CompleteInternal(context, dialog.Key);
        }
    }
}
