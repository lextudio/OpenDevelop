using System;
using Hornung.ResourceToolkit.Resolver;
using Hornung.ResourceToolkit.ResourceFileContent;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;

namespace Hornung.ResourceToolkit.CodeCompletion
{
    public abstract class AbstractNRefactoryResourceCodeCompletionBinding : ICodeCompletionBinding
    {
        public CodeCompletionKeyPressResult HandleKeyPress(ITextEditor editor, char ch)
        {
            if (this.CompletionPossible(editor, ch)) {
                ResourceResolveResult result = ResourceResolverService.Resolve(editor, ch);
                if (result != null) {
                    IResourceFileContent content;
                    if ((content = result.ResourceFileContent) != null) {
                        if (result.ResourceSetReference.ResourceSetName == ICSharpCodeCoreResourceResolver.ICSharpCodeCoreLocalResourceSetName) {
                            IResourceFileContent hostContent = ICSharpCodeCoreResourceResolver.GetICSharpCodeCoreHostResourceSet(editor.FileName).ResourceFileContent;
                            if (hostContent != null) {
                                content = new MergedResourceFileContent(content, new IResourceFileContent[] { hostContent });
                            }
                        }
                        editor.ShowCompletionWindow(new ResourceCodeCompletionItemList(content, null));
                        return CodeCompletionKeyPressResult.Completed;
                    }
                }
            }
            return CodeCompletionKeyPressResult.None;
        }

        public bool HandleKeyPressed(ITextEditor editor, char ch)
        {
            return false;
        }

        public bool CtrlSpace(ITextEditor editor) { return false; }

        protected abstract bool CompletionPossible(ITextEditor editor, char ch);
    }
}
