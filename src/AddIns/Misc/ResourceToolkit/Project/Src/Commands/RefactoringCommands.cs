using System;
using System.Collections.Generic;
using Hornung.ResourceToolkit.Gui;
using Hornung.ResourceToolkit.Refactoring;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;

namespace Hornung.ResourceToolkit.Commands
{
    public static class FindMissingResourceKeysHelper
    {
        public static void Run(SearchScope scope) {
            ResourceRefactoringService.FindReferencesToMissingKeys(null, scope);
        }
    }

    public class FindMissingResourceKeysWholeSolutionCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            FindMissingResourceKeysHelper.Run(SearchScope.WholeSolution);
        }
    }

    public class FindMissingResourceKeysCurrentProjectCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            FindMissingResourceKeysHelper.Run(SearchScope.CurrentProject);
        }
    }

    public class FindMissingResourceKeysCurrentFileCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            FindMissingResourceKeysHelper.Run(SearchScope.CurrentFile);
        }
    }

    public class FindMissingResourceKeysOpenFilesCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            FindMissingResourceKeysHelper.Run(SearchScope.OpenFiles);
        }
    }

    public class FindUnusedResourceKeysCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            ICollection<ResourceItem> unusedKeys;

            unusedKeys = ResourceRefactoringService.FindUnusedKeys(null);

            if (unusedKeys == null) {
                return;
            }

            if (unusedKeys.Count == 0) {
                MessageService.ShowMessage("${res:Hornung.ResourceToolkit.UnusedResourceKeys.NotFound}");
                return;
            }

            IWorkbench workbench = WorkbenchSingleton.Workbench;
            if (workbench != null) {
                UnusedResourceKeysViewContent vc = new UnusedResourceKeysViewContent(unusedKeys);
                workbench.ShowView(vc);
            }
        }
    }
}
