using System;
using ICSharpCode.SharpDevelop.Project;
using Microsoft.VisualStudio.ProjectSystem;

namespace ICSharpCode.SharpDevelop.Services;

[Flags]
internal enum ProjectBrowserNodeState
{
    None = 0,
    HasPath = 1 << 0,
    Solution = 1 << 1,
    Project = 1 << 2,
    Folder = 1 << 3,
    File = 1 << 4,
    Openable = 1 << 5,
    CreateChild = 1 << 6,
    Renameable = 1 << 7,
    Deletable = 1 << 8,
    StartupProject = 1 << 9,
    SolutionOpen = 1 << 10,
    RemovableFromSolution = 1 << 11,
    AddExisting = 1 << 12,
    OpenWith = 1 << 13,
    IncludeInProject = 1 << 14,
    ExcludeFromProject = 1 << 15,
    RemovableReference = 1 << 16,
    OpenReference = 1 << 17
}

internal enum ProjectBrowserNodeKind
{
    Unknown,
    Solution,
    Project,
    Folder,
    File,
    DependenciesFolder,
    ReferencesFolder,
    PackagesFolder,
    Reference,
    ProjectReference,
    PackageReference,
    LinkedFile,
    MissingFile,
    GhostFile,
    GhostFolder
}

internal sealed record ProjectBrowserNodeContext(
    string Name,
    string FullPath,
    bool IsDirectory,
    ProjectBrowserNodeKind Kind = ProjectBrowserNodeKind.Unknown,
    ISolutionItem? BoundItem = null,
    IProjectTree? BoundProjectTree = null,
    string? ProjectPathHint = null,
    string? IncludeHint = null) : ICSharpCode.Core.IOwnerState
{

    public Enum InternalState => State;

    public bool IsFileLike =>
        Kind is ProjectBrowserNodeKind.File
            or ProjectBrowserNodeKind.LinkedFile
            or ProjectBrowserNodeKind.MissingFile
            or ProjectBrowserNodeKind.GhostFile;

    public bool IsProjectItemLike =>
        IsFileLike
            || Kind is ProjectBrowserNodeKind.Reference
                or ProjectBrowserNodeKind.ProjectReference
                or ProjectBrowserNodeKind.PackageReference;

    public ProjectBrowserNodeState State =>
        ProjectBrowserNodeState.HasPath
        | (Kind switch
        {
            ProjectBrowserNodeKind.Solution => ProjectBrowserNodeState.Solution | ProjectBrowserNodeState.CreateChild,
            ProjectBrowserNodeKind.Project => ProjectBrowserNodeState.Project | ProjectBrowserNodeState.CreateChild | ProjectBrowserNodeState.Deletable | ProjectBrowserNodeState.StartupProject | ProjectBrowserNodeState.RemovableFromSolution | ProjectBrowserNodeState.AddExisting | ProjectBrowserNodeState.OpenWith,
            ProjectBrowserNodeKind.DependenciesFolder or ProjectBrowserNodeKind.ReferencesFolder or ProjectBrowserNodeKind.PackagesFolder => ProjectBrowserNodeState.Folder,
            ProjectBrowserNodeKind.Reference or ProjectBrowserNodeKind.PackageReference => ProjectBrowserNodeState.File | ProjectBrowserNodeState.RemovableReference,
            ProjectBrowserNodeKind.ProjectReference => ProjectBrowserNodeState.File | ProjectBrowserNodeState.RemovableReference | ProjectBrowserNodeState.OpenReference,
            ProjectBrowserNodeKind.Folder => ProjectBrowserNodeState.Folder | ProjectBrowserNodeState.CreateChild | ProjectBrowserNodeState.Renameable | ProjectBrowserNodeState.Deletable | ProjectBrowserNodeState.RemovableFromSolution | ProjectBrowserNodeState.AddExisting,
            ProjectBrowserNodeKind.GhostFolder => ProjectBrowserNodeState.Folder,
            ProjectBrowserNodeKind.File or ProjectBrowserNodeKind.LinkedFile => ProjectBrowserNodeState.File | ProjectBrowserNodeState.Openable | ProjectBrowserNodeState.Renameable | ProjectBrowserNodeState.Deletable | ProjectBrowserNodeState.RemovableFromSolution | ProjectBrowserNodeState.OpenWith | ProjectBrowserNodeState.ExcludeFromProject,
            ProjectBrowserNodeKind.MissingFile => ProjectBrowserNodeState.File | ProjectBrowserNodeState.RemovableFromSolution,
            ProjectBrowserNodeKind.GhostFile => ProjectBrowserNodeState.File | ProjectBrowserNodeState.Openable | ProjectBrowserNodeState.OpenWith | ProjectBrowserNodeState.IncludeInProject,
            _ => ProjectBrowserNodeState.None
        });

    public string IconUri =>
        Kind switch
        {
            ProjectBrowserNodeKind.Solution => "ms-appx:///Icons/SolutionFolderSwitch_16x.svg",
            ProjectBrowserNodeKind.Project => "ms-appx:///Icons/Application_16x.svg",
            ProjectBrowserNodeKind.DependenciesFolder => "ms-appx:///Icons/Reference_16x.svg",
            ProjectBrowserNodeKind.ReferencesFolder => "ms-appx:///Icons/Reference_16x.svg",
            ProjectBrowserNodeKind.PackagesFolder => "ms-appx:///Icons/Library_16x.svg",
            ProjectBrowserNodeKind.Reference => "ms-appx:///Icons/Reference_16x.svg",
            ProjectBrowserNodeKind.ProjectReference => "ms-appx:///Icons/Application_16x.svg",
            ProjectBrowserNodeKind.PackageReference => "ms-appx:///Icons/Library_16x.svg",
            ProjectBrowserNodeKind.Folder or ProjectBrowserNodeKind.GhostFolder => IsDirectory ? "ms-appx:///Icons/Folder_16x.svg" : ResolveFileIcon(FullPath),
            ProjectBrowserNodeKind.File or ProjectBrowserNodeKind.LinkedFile or ProjectBrowserNodeKind.MissingFile or ProjectBrowserNodeKind.GhostFile => ResolveFileIcon(FullPath),
            _ => "ms-appx:///Icons/CSFile_16x.svg"
        };

    private static string ResolveFileIcon(string path)
    {
        var ext = System.IO.Path.GetExtension(path)?.ToLowerInvariant();
        return ext switch
        {
            ".cs" => "ms-appx:///Icons/CSFile_16x.svg",
            ".xaml" => "ms-appx:///Icons/Control_16x.svg",
            ".json" => "ms-appx:///Icons/JSONFile_16x.svg",
            ".xml" or ".config" => "ms-appx:///Icons/XMLFile_16x.svg",
            ".htm" or ".html" => "ms-appx:///Icons/HTMLFile_16x.svg",
            ".css" => "ms-appx:///Icons/StyleSheet_16x.svg",
            ".js" => "ms-appx:///Icons/JSScript_16x.svg",
            ".md" or ".markdown" => "ms-appx:///Icons/MarkdownFile_16x.svg",
            ".sql" => "ms-appx:///Icons/SQLFile_16x.svg",
            ".resx" => "ms-appx:///Icons/ResourceSymbols_16x.svg",
            ".settings" => "ms-appx:///Icons/SettingsFile_16x.svg",
            ".txt" => "ms-appx:///Icons/TextFile_16x.svg",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".cur" or ".bmp" or ".svg" => "ms-appx:///Icons/Image_16x.svg",
            ".cshtml" or ".razor" => "ms-appx:///Icons/CSRazorFile_16x.svg",
            ".aspx" or ".ascx" => "ms-appx:///Icons/ASPXFile_16x.svg",
            ".master" => "ms-appx:///Icons/MasterPage_16x.svg",
            ".skin" => "ms-appx:///Icons/SkinFile_16x.svg",
            ".manifest" => "ms-appx:///Icons/Manifest_16x.svg",
            ".dll" or ".exe" => "ms-appx:///Icons/BinaryFile_16x.svg",
            ".vb" => "ms-appx:///Icons/VBFile_16x.svg",
            ".fs" => "ms-appx:///Icons/FSFile_16x.svg",
            ".cpp" or ".c" => "ms-appx:///Icons/CPPSourceFile_16x.svg",
            ".h" or ".hpp" => "ms-appx:///Icons/CPPHeaderFile_16x.svg",
            ".csproj" or ".vbproj" or ".fsproj" => "ms-appx:///Icons/CSClassLibrary_16x.svg",
            ".slnx" or ".sln" => "ms-appx:///Icons/SolutionFolderSwitch_16x.svg",
            _ => "ms-appx:///Icons/CSFile_16x.svg"
        };
    }

    public string ContextMenuPath =>
        Kind switch
        {
            ProjectBrowserNodeKind.Solution => "/SharpDevelop/Pads/ProjectBrowser/ContextMenu/SolutionNode",
            ProjectBrowserNodeKind.Project => "/SharpDevelop/Pads/ProjectBrowser/ContextMenu/ProjectNode",
            ProjectBrowserNodeKind.Folder or ProjectBrowserNodeKind.GhostFolder or ProjectBrowserNodeKind.DependenciesFolder or ProjectBrowserNodeKind.ReferencesFolder or ProjectBrowserNodeKind.PackagesFolder => "/SharpDevelop/Pads/ProjectBrowser/ContextMenu/FolderNode",
            ProjectBrowserNodeKind.Reference or ProjectBrowserNodeKind.ProjectReference => "/SharpDevelop/Pads/ProjectBrowser/ContextMenu/ReferenceNode",
            ProjectBrowserNodeKind.PackageReference => "/SharpDevelop/Pads/ProjectBrowser/ContextMenu/PackageReferenceNode",
            ProjectBrowserNodeKind.File or ProjectBrowserNodeKind.LinkedFile or ProjectBrowserNodeKind.MissingFile or ProjectBrowserNodeKind.GhostFile => "/SharpDevelop/Pads/ProjectBrowser/ContextMenu/FileNode",
            _ => "/SharpDevelop/Pads/ProjectBrowser/ContextMenu/UnknownNode"
        };

    public override string ToString() => Name;
}

internal sealed record ProjectBrowserPadContext(ProjectBrowserNodeState State) : ICSharpCode.Core.IOwnerState
{
    public Enum InternalState => State;
}
