using System;
using System.Linq;
using Hornung.ResourceToolkit.Resolver;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Editor;

namespace Hornung.ResourceToolkit.ToolTips
{
    public class ResourceToolTipProvider : ITextAreaToolTipProvider
    {
        public void HandleToolTipRequest(ToolTipRequestEventArgs e)
        {
            if (e.InDocument || e.LogicalPosition == null) return;

            var editor = e.Editor;
            if (editor == null) return;

            int offset = editor.Caret.Offset;
            if (offset < 0) return;

            ResourceResolveResult result = ResourceResolverService.Resolve(editor, null);
            if (result != null && result.ResourceFileContent != null && result.Key != null) {
                string toolTip = ResourceResolverService.FormatResourceDescription(result.ResourceFileContent, result.Key);
                e.SetToolTip(toolTip);
            }
        }
    }
}
