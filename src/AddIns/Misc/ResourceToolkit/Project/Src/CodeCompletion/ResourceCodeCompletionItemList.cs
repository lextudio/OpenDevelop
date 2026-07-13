using System.Collections.Generic;
using Hornung.ResourceToolkit.ResourceFileContent;
using Hornung.ResourceToolkit.Resolver;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;

namespace Hornung.ResourceToolkit.CodeCompletion
{
    public class ResourceCodeCompletionItemList : DefaultCompletionItemList
    {
        public ResourceCodeCompletionItemList(IResourceFileContent content, string classNamePrefix)
        {
            if (content != null) {
                foreach (KeyValuePair<string, object> entry in content.Data) {
                    this.Items.Add(new ResourceCodeCompletionItem(
                        classNamePrefix + entry.Key,
                        ResourceResolverService.FormatResourceDescription(content, entry.Key)
                    ));
                }
            }

            this.Items.Add(new NewResourceCodeCompletionItem(content, null));
            this.PreselectionLength = 0;
        }
    }
}
