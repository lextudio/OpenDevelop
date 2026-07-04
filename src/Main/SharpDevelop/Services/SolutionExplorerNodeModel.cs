using System.Collections.Generic;
using ICSharpCode.SharpDevelop.Project;
using Microsoft.VisualStudio.ProjectSystem;

namespace ICSharpCode.SharpDevelop.Services;

internal sealed class SolutionExplorerNodeModel
{
    public SolutionExplorerNodeModel(
        string name,
        string fullPath,
        bool isDirectory,
        SolutionExplorerNodeKind kind,
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

    public SolutionExplorerNodeKind Kind { get; }

    public ISolutionItem? BoundItem { get; }

    public IProjectTree? BoundProjectTree { get; }

    public string? ProjectPathHint { get; }

    public string? IncludeHint { get; }

    public bool IsExpanded { get; }

    public List<SolutionExplorerNodeModel> Children { get; } = new();

    public SolutionExplorerNodeContext ToContext()
    {
        return new SolutionExplorerNodeContext(Name, FullPath, IsDirectory, Kind, BoundItem, BoundProjectTree, ProjectPathHint, IncludeHint);
    }
}
