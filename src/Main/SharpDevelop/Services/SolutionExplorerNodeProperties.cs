using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.SharpDevelop.Services;

// Browsable property bag shown in the Properties pad for a selected Solution
// Explorer node. Kept separate from SolutionExplorerNodeContext so the grid
// shows only meaningful, user-facing fields (not IconUri/State/etc.).
internal sealed class SolutionExplorerNodeProperties
{
    private readonly SolutionExplorerNodeContext _node;

    private ProjectItem? ProjectItem => ResolveProjectItem();

    public SolutionExplorerNodeProperties(SolutionExplorerNodeContext node) => _node = node;

    [Category("General")]
    [DisplayName("Name")]
    [Description("The name of the selected item.")]
    public string Name => _node.Name;

    [Category("General")]
    [DisplayName("Type")]
    [Description("The kind of node (Solution, Project, Folder or File).")]
    public string Type => _node.Kind.ToString();

    [Category("General")]
    [DisplayName("Full Path")]
    [Description("The absolute path on disk.")]
    public string FullPath => _node.FullPath;

    [Category("File")]
    [DisplayName("Extension")]
    [Description("File extension, if the node is a file.")]
    public string Extension =>
        _node.IsFileLike ? Path.GetExtension(_node.FullPath) : string.Empty;

    [Category("File")]
    [DisplayName("Size (bytes)")]
    [Description("File size in bytes, if the node is a file.")]
    public long Size
    {
        get
        {
            try
            {
                return _node.IsFileLike && File.Exists(_node.FullPath)
                    ? new FileInfo(_node.FullPath).Length
                    : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    [Category("File")]
    [DisplayName("Last Modified")]
    [Description("Last write time of the file or folder.")]
    public string LastModified
    {
        get
        {
            try
            {
                if (File.Exists(_node.FullPath))
                    return File.GetLastWriteTime(_node.FullPath).ToString("g");
                if (Directory.Exists(_node.FullPath))
                    return Directory.GetLastWriteTime(_node.FullPath).ToString("g");
            }
            catch
            {
                // ignore
            }
            return string.Empty;
        }
    }

    [Category("File")]
    [DisplayName("Read Only")]
    [Description("Whether the file is marked read-only.")]
    public bool ReadOnly
    {
        get
        {
            try
            {
                return _node.IsFileLike
                    && File.Exists(_node.FullPath)
                    && (File.GetAttributes(_node.FullPath) & System.IO.FileAttributes.ReadOnly) == System.IO.FileAttributes.ReadOnly;
            }
            catch
            {
                return false;
            }
        }
    }

    [Category("Project Item")]
    [DisplayName("Item Type")]
    [Description("The MSBuild item type.")]
    public string ItemType => ProjectItem?.ItemType.ItemName ?? string.Empty;

    [Category("Project Item")]
    [DisplayName("Include")]
    [Description("The evaluated MSBuild Include value.")]
    public string Include => ProjectItem?.Include ?? _node.IncludeHint ?? string.Empty;

    [Category("Project Item")]
    [DisplayName("Dependent Upon")]
    [Description("The DependentUpon metadata value.")]
    public string DependentUpon => ProjectItem is FileProjectItem fileItem
        ? fileItem.DependentUpon
        : ProjectItem?.GetEvaluatedMetadata("DependentUpon") ?? string.Empty;

    [Category("Project Item")]
    [DisplayName("Link")]
    [Description("The Link metadata value.")]
    public string Link => ProjectItem?.GetEvaluatedMetadata("Link") ?? string.Empty;

    [Category("Project Item")]
    [DisplayName("Is Linked")]
    [Description("Whether the project item is a linked file.")]
    public bool IsLinked => ProjectItem is FileProjectItem fileItem && fileItem.IsLink;

    [Category("Reference")]
    [DisplayName("Hint Path")]
    [Description("The HintPath metadata for an assembly reference.")]
    public string HintPath => ProjectItem?.GetEvaluatedMetadata("HintPath") ?? string.Empty;

    [Category("Reference")]
    [DisplayName("Version")]
    [Description("The Version metadata, typically for a package reference.")]
    public string Version => ProjectItem?.GetEvaluatedMetadata("Version") ?? string.Empty;

    [Category("Reference")]
    [DisplayName("Private (Copy Local)")]
    [Description("The Private metadata controlling whether the reference is copied to the output directory.")]
    public string Private => ProjectItem?.GetEvaluatedMetadata("Private") ?? string.Empty;

    private ProjectItem? ResolveProjectItem()
    {
        var project = ResolveProject();
        if (project is null)
        {
            return null;
        }

        var items = project.Items.CreateSnapshot();
        if (!string.IsNullOrWhiteSpace(_node.IncludeHint))
        {
            var byInclude = items.FirstOrDefault(item =>
                string.Equals(item.Include, _node.IncludeHint, StringComparison.OrdinalIgnoreCase));
            if (byInclude is not null)
            {
                return byInclude;
            }
        }

        if (!string.IsNullOrWhiteSpace(_node.FullPath) && File.Exists(_node.FullPath))
        {
            var normalizedPath = Path.GetFullPath(_node.FullPath);
            var byPath = items.OfType<FileProjectItem>().FirstOrDefault(item =>
                string.Equals(Path.GetFullPath(item.FileName.ToString()), normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (byPath is not null)
            {
                return byPath;
            }
        }

        return null;
    }

    private IProject? ResolveProject()
    {
        if (_node.BoundItem is IProject directProject)
        {
            return directProject;
        }

        var solution = SD.ProjectService.CurrentSolution;
        if (solution?.Projects is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_node.ProjectPathHint))
        {
            try
            {
                var normalizedHint = Path.GetFullPath(_node.ProjectPathHint);
                var byHint = solution.Projects.FirstOrDefault(project =>
                    string.Equals(Path.GetFullPath(project.FileName.ToString()), normalizedHint, StringComparison.OrdinalIgnoreCase));
                if (byHint is not null)
                {
                    return byHint;
                }
            }
            catch
            {
                // Ignore path normalization failures and continue.
            }
        }

        if (_node.Kind == SolutionExplorerNodeKind.Project)
        {
            return solution.Projects.FirstOrDefault(project =>
                string.Equals(project.Name, _node.Name, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    public override string ToString() => _node.Name;
}
