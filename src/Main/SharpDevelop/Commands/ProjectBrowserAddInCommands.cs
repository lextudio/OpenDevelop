// Ported from UnoDevelop's ProjectBrowserAddInCommands.cs (see doc/technotes/solution-explorer.md).
// Commands tied to WinUI-only concerns (NuGet package management dialog, T4 template runner,
// MainPage.Current-based toolbar actions) are out of MVP scope and were not ported; every command
// here maps 1:1 onto IProjectBrowserController, which OpenDevelop already implements natively.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Services;

namespace ICSharpCode.SharpDevelop.Commands;

internal abstract class ProjectBrowserCommandBase : AbstractMenuCommand
{
    protected IProjectBrowserController Controller => ServiceSingleton.GetRequiredService<IProjectBrowserController>();

    protected ProjectBrowserNodeContext? OwnerNode => Owner as ProjectBrowserNodeContext;

    protected IProject? ResolveOwnerProject()
    {
        if (OwnerNode is null)
        {
            return null;
        }

        var solution = SD.ProjectService.CurrentSolution;
        if (solution is null || solution.Projects is null)
        {
            return null;
        }

        if (OwnerNode.ProjectPathHint is string hintedPath
            && !string.IsNullOrWhiteSpace(hintedPath))
        {
            try
            {
                var normalizedHint = Path.GetFullPath(hintedPath);
                var byHint = solution.Projects.FirstOrDefault(project =>
                    string.Equals(Path.GetFullPath(project.FileName.ToString()), normalizedHint, StringComparison.OrdinalIgnoreCase));
                if (byHint is not null)
                {
                    return byHint;
                }
            }
            catch
            {
                // Ignore path normalization failures and continue with other resolution paths.
            }
        }

        if (OwnerNode.BoundProjectTree?.Root?.FilePath is string rootPath
            && !string.IsNullOrWhiteSpace(rootPath))
        {
            try
            {
                var normalizedRootPath = Path.GetFullPath(rootPath);
                var byTreeRootPath = solution.Projects.FirstOrDefault(project =>
                    string.Equals(Path.GetFullPath(project.FileName.ToString()), normalizedRootPath, StringComparison.OrdinalIgnoreCase));
                if (byTreeRootPath is not null)
                {
                    return byTreeRootPath;
                }
            }
            catch
            {
                // Ignore path normalization failures and continue with other resolution paths.
            }
        }

        if (!string.IsNullOrWhiteSpace(OwnerNode.FullPath))
        {
            try
            {
                var normalizedNodePath = Path.GetFullPath(OwnerNode.FullPath);
                var byPath = solution.Projects.FirstOrDefault(project =>
                    string.Equals(Path.GetFullPath(project.FileName.ToString()), normalizedNodePath, StringComparison.OrdinalIgnoreCase));
                if (byPath is not null)
                {
                    return byPath;
                }

                if ((OwnerNode.IsFileLike || OwnerNode.Kind == ProjectBrowserNodeKind.Folder)
                    && File.Exists(normalizedNodePath))
                {
                    var byContainingFile = SD.ProjectService.FindProjectContainingFile(FileName.Create(normalizedNodePath));
                    if (byContainingFile is not null)
                    {
                        return byContainingFile;
                    }
                }
            }
            catch
            {
                // Ignore path normalization failures and continue with other resolution paths.
            }
        }

        if (OwnerNode.Kind == ProjectBrowserNodeKind.Project)
        {
            var byName = solution.Projects.FirstOrDefault(project =>
                string.Equals(project.Name, OwnerNode.Name, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        return null;
    }

    protected ProjectItem? ResolveOwnerProjectItem(IProject project)
    {
        if (OwnerNode is null)
        {
            return null;
        }

        var items = project.Items.CreateSnapshot();
        if (!string.IsNullOrWhiteSpace(OwnerNode.IncludeHint))
        {
            var byInclude = items.FirstOrDefault(item =>
                string.Equals(item.Include, OwnerNode.IncludeHint, StringComparison.OrdinalIgnoreCase));
            if (byInclude is not null)
            {
                return byInclude;
            }
        }

        if (!string.IsNullOrWhiteSpace(OwnerNode.FullPath) && File.Exists(OwnerNode.FullPath))
        {
            var normalizedPath = Path.GetFullPath(OwnerNode.FullPath);
            var byPath = items.OfType<FileProjectItem>().FirstOrDefault(item =>
                string.Equals(Path.GetFullPath(item.FileName.ToString()), normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (byPath is not null)
            {
                return byPath;
            }
        }

        return null;
    }
}

internal sealed class RefreshProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override void Run() => Controller.Refresh();
}

internal sealed class OpenProjectBrowserItemCommand : ProjectBrowserCommandBase
{
    public override void Run() => Controller.Open(OwnerNode);
}

internal sealed class NewFolderProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override void Run() => Controller.CreateFolder(OwnerNode);
}

internal sealed class NewFileProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override void Run() => Controller.CreateFile(OwnerNode);
}

internal sealed class NewItemProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override void Run() => Controller.AddNewItem(OwnerNode);
}

internal sealed class NewProjectProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override void Run() => Controller.AddNewProject(OwnerNode);
}

internal sealed class AddExistingFileProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override void Run() => Controller.AddExistingFile(OwnerNode);
}

internal sealed class AddExistingFolderProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override void Run() => Controller.AddExistingFolder(OwnerNode);
}

internal sealed class RenameProjectBrowserItemCommand : ProjectBrowserCommandBase
{
    public override void Run() => Controller.Rename(OwnerNode);
}

internal sealed class DeleteProjectBrowserItemCommand : ProjectBrowserCommandBase
{
    public override void Run() => Controller.Delete(OwnerNode);
}

internal sealed class RemoveFromProjectProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override bool IsEnabled => OwnerNode is not null
        && (OwnerNode.Kind == ProjectBrowserNodeKind.Project
            || OwnerNode.Kind == ProjectBrowserNodeKind.File
            || OwnerNode.Kind == ProjectBrowserNodeKind.Folder);

    public override void Run() => Controller.RemoveFromProject(OwnerNode);
}

internal sealed class RemoveReferenceProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override bool IsEnabled => OwnerNode?.Kind is ProjectBrowserNodeKind.Reference
        or ProjectBrowserNodeKind.ProjectReference
        or ProjectBrowserNodeKind.PackageReference;

    public override void Run() => Controller.RemoveReference(OwnerNode);
}

internal sealed class OpenProjectReferenceProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override bool IsEnabled => OwnerNode?.Kind == ProjectBrowserNodeKind.ProjectReference;

    public override void Run() => Controller.OpenProjectReference(OwnerNode);
}

internal sealed class IncludeInProjectProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override bool IsEnabled => OwnerNode?.Kind == ProjectBrowserNodeKind.GhostFile;

    public override void Run() => Controller.IncludeInProject(OwnerNode);
}

internal sealed class ExcludeFromProjectProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override bool IsEnabled => OwnerNode?.Kind is ProjectBrowserNodeKind.File or ProjectBrowserNodeKind.LinkedFile;

    public override void Run() => Controller.ExcludeFromProject(OwnerNode);
}

internal sealed class OpenWithProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override bool IsEnabled => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public override void Run() => Controller.OpenWith(OwnerNode);
}

internal sealed class CopyPathProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override void Run() => Controller.CopyPath(OwnerNode);
}

internal sealed class OpenFolderProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override void Run() => Controller.OpenFolder(OwnerNode);
}

internal sealed class SetStartupProjectProjectBrowserCommand : ProjectBrowserCommandBase
{
    public override void Run() => Controller.SetStartupProject(OwnerNode);
}
