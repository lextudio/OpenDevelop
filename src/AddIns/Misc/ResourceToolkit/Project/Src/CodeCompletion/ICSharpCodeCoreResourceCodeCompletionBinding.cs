using System;
using System.Linq;
using Hornung.ResourceToolkit.Resolver;
using Hornung.ResourceToolkit.ResourceFileContent;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;

namespace Hornung.ResourceToolkit.CodeCompletion
{
    public class ICSharpCodeCoreResourceCodeCompletionBinding : AbstractNRefactoryResourceCodeCompletionBinding
    {
        protected override bool CompletionPossible(ITextEditor editor, char ch)
        {
            return ch == ':';
        }

        public new CodeCompletionKeyPressResult HandleKeyPress(ITextEditor editor, char ch)
        {
            if (ch == ':') {
                int offset = editor.Caret.Offset;
                if (offset >= 7) {
                    string textBefore = editor.Document.GetText(offset - 7, 7);
                    if (textBefore == "${res:") {
                        var localSet = ICSharpCodeCoreResourceResolver.GetICSharpCodeCoreLocalResourceSet(editor.FileName);
                        var hostSet = ICSharpCodeCoreResourceResolver.GetICSharpCodeCoreHostResourceSet(editor.FileName);

                        IResourceFileContent content = localSet.ResourceFileContent;
                        if (hostSet.ResourceFileContent != null) {
                            if (content != null) {
                                content = new MergedResourceFileContent(content, new IResourceFileContent[] { hostSet.ResourceFileContent });
                            } else {
                                content = hostSet.ResourceFileContent;
                            }
                        }

                        if (content != null) {
                            var list = new ResourceCodeCompletionItemList(content, null);
                            editor.ShowCompletionWindow(list);
                            return CodeCompletionKeyPressResult.Completed;
                        }
                    }
                }
            }
            return CodeCompletionKeyPressResult.None;
        }
    }
}
