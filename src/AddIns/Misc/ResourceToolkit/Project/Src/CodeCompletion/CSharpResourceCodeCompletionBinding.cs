using System;
using ICSharpCode.SharpDevelop.Editor;

namespace Hornung.ResourceToolkit.CodeCompletion
{
    public class CSharpResourceCodeCompletionBinding : AbstractNRefactoryResourceCodeCompletionBinding
    {
        protected override bool CompletionPossible(ITextEditor editor, char ch)
        {
            return ch == '"';
        }
    }
}
