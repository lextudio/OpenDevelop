using System;
using Hornung.ResourceToolkit.Resolver;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;

namespace Hornung.ResourceToolkit.Gui
{
    public class UnusedResourceKeysHideICSharpCodeCoreHostResourcesCommand : AbstractCheckableMenuCommand, IFilter<ResourceItem>
    {
        string icSharpCodeCoreHostResourceFileName;

        public override void Run()
        {
            base.Run();

            var ownerButton = this.Owner as System.Windows.Controls.Primitives.ToggleButton;
            if (ownerButton != null) {
                var host = ownerButton.Tag as IFilterHost<ResourceItem>;
                if (host != null) {
                    if (this.IsChecked) {
                        this.icSharpCodeCoreHostResourceFileName = ICSharpCodeCoreResourceResolver.GetICSharpCodeCoreHostResourceSet(null).FileName;
                        host.RegisterFilter(this);
                    } else {
                        host.UnregisterFilter(this);
                    }
                }
            }
        }

        public bool IsMatch(ResourceItem item)
        {
            return !FileUtility.IsEqualFileName(item.FileName, this.icSharpCodeCoreHostResourceFileName);
        }
    }
}
