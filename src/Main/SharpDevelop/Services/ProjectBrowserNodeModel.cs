using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using Microsoft.VisualStudio.ProjectSystem;

namespace ICSharpCode.SharpDevelop.Services;

internal sealed class ProjectBrowserNodeModel
{
    public ProjectBrowserNodeModel(
        string name,
        string fullPath,
        bool isDirectory,
        ProjectBrowserNodeKind kind,
        ISolutionItem? boundItem = null,
        IProjectTree? boundProjectTree = null,
        string? projectPathHint = null,
        string? includeHint = null,
        bool isExpanded = false)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Kind = kind;
        BoundItem = boundItem;
        BoundProjectTree = boundProjectTree;
        ProjectPathHint = projectPathHint;
        IncludeHint = includeHint;
        IsExpanded = isExpanded;
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public ProjectBrowserNodeKind Kind { get; }

    public ISolutionItem? BoundItem { get; }

    public IProjectTree? BoundProjectTree { get; }

    public string? ProjectPathHint { get; }

    public string? IncludeHint { get; }

    public bool IsExpanded { get; }

    public List<ProjectBrowserNodeModel> Children { get; } = new();

    public BitmapSource Icon => ProjectBrowserIconService.GetIcon(this);

    public ImageSource OverlayIcon => ServiceSingleton.ServiceProvider.GetService<IProjectBrowserOverlayService>()?.GetOverlay(FullPath, IsDirectory);

    public string OverlayStatusKey => ServiceSingleton.ServiceProvider.GetService<IProjectBrowserOverlayService>()?.GetOverlayKey(FullPath, IsDirectory) ?? string.Empty;

    public ProjectBrowserNodeContext ToContext()
    {
        return new ProjectBrowserNodeContext(Name, FullPath, IsDirectory, Kind, BoundItem, BoundProjectTree, ProjectPathHint, IncludeHint);
    }
}
