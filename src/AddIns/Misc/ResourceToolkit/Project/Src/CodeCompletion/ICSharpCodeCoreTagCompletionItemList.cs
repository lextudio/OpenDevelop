using System.Collections.Generic;
using Hornung.ResourceToolkit.ResourceFileContent;
using Hornung.ResourceToolkit.Resolver;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;

namespace Hornung.ResourceToolkit.CodeCompletion
{
    public class ICSharpCodeCoreTagCompletionItemList : DefaultCompletionItemList
    {
        public ICSharpCodeCoreTagCompletionItemList(IResourceFileContent content)
        {
            if (content != null) {
                foreach (KeyValuePair<string, object> entry in content.Data) {
                    this.Items.Add(new ResourceCodeCompletionItem(
                        entry.Key,
                        ResourceResolverService.FormatResourceDescription(content, entry.Key)
                    ));
                }
            }
            this.PreselectionLength = 0;
        }
    }
}
