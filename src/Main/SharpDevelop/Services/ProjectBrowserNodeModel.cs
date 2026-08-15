#nullable enable
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ICSharpCode.SharpDevelop.Project;
using Microsoft.VisualStudio.ProjectSystem;

namespace ICSharpCode.SharpDevelop.Services;

// Framework-agnostic tree node shape shared by both hosts' Project Browser pads (see
// doc/technotes/solution-explorer.md) - WPF-specific rendering (Icon/overlay ImageSource
// properties) lives in ProjectBrowserNodeModel.Wpf.cs, compiled only into OpenDevelop, since
// System.Windows.Media isn't available under Uno.Sdk.
internal sealed partial class ProjectBrowserNodeModel : INotifyPropertyChanged
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

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/> for the given property name.</summary>
    public void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public ProjectBrowserNodeContext ToContext()
    {
        return new ProjectBrowserNodeContext(Name, FullPath, IsDirectory, Kind, BoundItem, BoundProjectTree, ProjectPathHint, IncludeHint);
    }
}
