using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Templates;
// FileDialogService is per-host (OpenDevelop's own WPF one already lives in this namespace, so no
// using is needed for it there; UnoDevelop's lives in UnoDevelop.Services instead).
#if HAS_UNO
using UnoDevelop.Services;
#endif

namespace ICSharpCode.SharpDevelop.Services;

internal interface IProjectBrowserHost
{
    ProjectBrowserNodeContext? SelectedNode { get; }
    void RefreshSolutionTree();
    void OpenFileInWorkbench(string filePath);
    string? ShowInputBox(string title, string prompt, string defaultValue);
    bool ConfirmDelete(string name);
    void CloseViewsForPath(string path);
    void RetargetViewForRename(string oldPath, string newPath);
}

internal interface IProjectBrowserService
{
    string CreateFolder(string targetDirectory, string baseName = "NewFolder");
    string CreateFile(string targetDirectory, string baseName = "NewFile", string extension = ".cs", string? initialContent = "// New file\n");
    IReadOnlyList<string> ImportExistingFiles(string targetDirectory, IEnumerable<string> sourcePaths);
    string ImportExistingFolder(string targetDirectory, string sourceDirectory);
    string RenameItem(string sourcePath, bool isDirectory, string newName);
    void DeleteItem(string sourcePath, bool isDirectory);
    bool TryIncludeItemInProject(string itemPath, out string includedItemName);
    bool TryExcludeItemFromProject(string itemPath, bool isDirectory, out string excludedItemName);
    bool TryRemoveItemFromProject(string itemPath, bool isDirectory, out string removedItemName, string? projectPathHint = null, string? includeHint = null);
    bool TryRemoveReference(string? projectPathHint, string include, ProjectBrowserNodeKind kind, out string removedName);
    bool TryRemoveProject(string projectPath, out string removedProjectName);
    bool TrySetStartupProject(string projectPath, out IProject? project);
}

internal interface IProjectBrowserController
{
    void BindHost(IProjectBrowserHost host);
    void Refresh();
    void Open(ProjectBrowserNodeContext? node = null);
    void CreateFolder(ProjectBrowserNodeContext? node = null);
    void CreateFile(ProjectBrowserNodeContext? node = null);
    void AddExistingFile(ProjectBrowserNodeContext? node = null);
    void AddExistingFolder(ProjectBrowserNodeContext? node = null);
    void AddNewItem(ProjectBrowserNodeContext? node = null);
    void AddNewProject(ProjectBrowserNodeContext? node = null);
    void Rename(ProjectBrowserNodeContext? node = null);
    void Delete(ProjectBrowserNodeContext? node = null);
    void IncludeInProject(ProjectBrowserNodeContext? node = null);
    void ExcludeFromProject(ProjectBrowserNodeContext? node = null);
    void RemoveFromProject(ProjectBrowserNodeContext? node = null);
    void RemoveReference(ProjectBrowserNodeContext? node = null);
    void OpenProjectReference(ProjectBrowserNodeContext? node = null);
    void OpenWith(ProjectBrowserNodeContext? node = null);
    void CopyPath(ProjectBrowserNodeContext? node = null);
    void OpenFolder(ProjectBrowserNodeContext? node = null);
    void SetStartupProject(ProjectBrowserNodeContext? node = null);
}

/// <summary>Host-neutral result of the "Add New Item" dialog - see <see cref="ProjectBrowserControllerBase.ShowNewItemDialogAsync"/>.</summary>
internal sealed record NewItemDialogOutcome(TemplateSummary SelectedTemplate, string ItemName, IReadOnlyDictionary<string, string> AdditionalParameters);

/// <summary>Host-neutral result of the "Add New Project" dialog - see <see cref="ProjectBrowserControllerBase.ShowNewProjectDialogAsync"/>.</summary>
internal sealed record NewProjectDialogOutcome(TemplateSummary SelectedTemplate, string ProjectName, string Location, IReadOnlyDictionary<string, string> AdditionalParameters);

/// <summary>
/// Shared Project Browser command surface (see doc/technotes/solution-explorer.md) - every command
/// that only touches SharpDevelop's own IProject/ISolution/IMessageService model lives here, once,
/// for both hosts. The three genuinely native touchpoints (new-item/new-project dialog UI, and
/// clipboard) are the only things a concrete host subclass has to supply.
/// </summary>
internal abstract class ProjectBrowserControllerBase : IProjectBrowserController
{
    private readonly IProjectBrowserService _explorerService;
    protected IProjectBrowserHost? Host { get; private set; }

    protected ProjectBrowserControllerBase(IProjectBrowserService explorerService)
    {
        _explorerService = explorerService;
    }

    /// <summary>Shows the host's native "Add New Item" dialog/window. Null return means the user cancelled.</summary>
    protected abstract Task<NewItemDialogOutcome?> ShowNewItemDialogAsync(TemplateDiscoveryService service, string targetDirectory);

    /// <summary>Shows the host's native "Add New Project" dialog/window. Null return means the user cancelled.</summary>
    protected abstract Task<NewProjectDialogOutcome?> ShowNewProjectDialogAsync(TemplateDiscoveryService service, string defaultLocation);

    /// <summary>Puts <paramref name="text"/> on the host's native clipboard.</summary>
    protected abstract void CopyTextToClipboard(string text);

    public void BindHost(IProjectBrowserHost host)
    {
        Host = host;
    }

    public void Refresh()
    {
        Host?.RefreshSolutionTree();
    }

    public void Open(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || !target.IsFileLike || target.Kind == ProjectBrowserNodeKind.MissingFile)
        {
            return;
        }

        Host?.OpenFileInWorkbench(target.FullPath);
    }

    public void CreateFolder(ProjectBrowserNodeContext? node = null)
    {
        ExecuteFileSystemAction(() =>
        {
            var targetDirectory = ResolveTargetDirectoryForCreate(ResolveNode(node));
            var folderPath = _explorerService.CreateFolder(targetDirectory);
            Host?.RefreshSolutionTree();
        }, "Failed to create folder.");
    }

    public void CreateFile(ProjectBrowserNodeContext? node = null)
    {
        ExecuteFileSystemAction(() =>
        {
            var targetDirectory = ResolveTargetDirectoryForCreate(ResolveNode(node));
            var filePath = _explorerService.CreateFile(targetDirectory);
            Host?.RefreshSolutionTree();
            Host?.OpenFileInWorkbench(filePath);
        }, "Failed to create file.");
    }

    public async void AddNewItem(ProjectBrowserNodeContext? node = null)
    {
        try
        {
            var selected = ResolveNode(node);
            var targetDirectory = ResolveTargetDirectoryForCreate(selected);

            using var service = new TemplateDiscoveryService();
            var dialog = await ShowNewItemDialogAsync(service, targetDirectory);
            if (dialog is null)
                return;

            var itemName = dialog.ItemName;
            var template = dialog.SelectedTemplate;

            var parameters = new Dictionary<string, string>(dialog.AdditionalParameters, StringComparer.OrdinalIgnoreCase);

            var result = await service.InstantiateAsync(
                template, itemName, targetDirectory, parameters, CancellationToken.None);

            if (!result.Success)
            {
                ServiceSingleton.GetRequiredService<IMessageService>()
                    .ShowError($"Failed to create '{itemName}': {result.ErrorMessage}");
                return;
            }

            // For T4 template files, automatically set the custom tool generator so the template
            // is processed on save (like the legacy .xft system did).
            foreach (var path in result.PrimaryOutputPaths)
            {
                if (!path.EndsWith(".tt", StringComparison.OrdinalIgnoreCase))
                    continue;

                var project = ServiceSingleton.GetRequiredService<IProjectService>()
                    .FindProjectContainingFile(FileName.Create(path));
                if (project is null)
                    continue;

                var item = project.Items.CreateSnapshot()
                    .OfType<FileProjectItem>()
                    .FirstOrDefault(i => string.Equals(
                        Path.GetFullPath(i.FileName.ToString()),
                        Path.GetFullPath(path),
                        StringComparison.OrdinalIgnoreCase));
                if (item is null)
                    continue;

                if (string.IsNullOrEmpty(item.CustomTool))
                    item.CustomTool = "TextTemplatingFileGenerator";
            }

            if (result.PrimaryOutputPaths.Count > 0)
            {
                Host?.RefreshSolutionTree();
                Host?.OpenFileInWorkbench(result.PrimaryOutputPaths[0]);
            }
        }
        catch (Exception ex)
        {
            ServiceSingleton.GetRequiredService<IMessageService>()
                .ShowException(ex, "Failed to add new item.");
        }
    }

    public async void AddNewProject(ProjectBrowserNodeContext? node = null)
    {
        try
        {
            var selected = ResolveNode(node);
            var defaultLocation = ResolveTargetDirectoryForCreate(selected);

            using var service = new TemplateDiscoveryService();
            var dialog = await ShowNewProjectDialogAsync(service, defaultLocation);
            if (dialog is null)
                return;

            var projectName = dialog.ProjectName;
            var location = dialog.Location;
            var template = dialog.SelectedTemplate;

            var projectDir = Path.Combine(location, projectName);
            Directory.CreateDirectory(projectDir);

            var result = await service.InstantiateAsync(
                template, projectName, projectDir,
                parameters: dialog.AdditionalParameters,
                CancellationToken.None);

            if (!result.Success)
            {
                ServiceSingleton.GetRequiredService<IMessageService>()
                    .ShowError($"Failed to create project '{projectName}': {result.ErrorMessage}");
                return;
            }

            var projectService = ServiceSingleton.GetRequiredService<IProjectService>();

            var generatedSolutionFile = FindGeneratedSolutionFile(result, projectDir);
            var generatedProjectFiles = FindGeneratedProjectFiles(result, projectDir);

            var currentSolution = projectService.CurrentSolution;
            if (currentSolution is not null)
            {
                if (generatedProjectFiles.Count == 0)
                {
                    if (generatedSolutionFile is not null)
                    {
                        ServiceSingleton.GetRequiredService<IMessageService>()
                            .ShowError("The selected template created a solution file. Create it with no solution open, or use a project template when adding to an existing solution.");
                        return;
                    }

                    ServiceSingleton.GetRequiredService<IMessageService>()
                        .ShowError($"Template '{template.Name}' did not generate a project file.");
                    return;
                }

                var targetFolder = ResolveTargetSolutionFolder(selected, currentSolution);
                var existing = new HashSet<string>(
                    currentSolution.Projects.Select(project => Path.GetFullPath(project.FileName.ToString())),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var projectPath in generatedProjectFiles)
                {
                    var normalizedPath = Path.GetFullPath(projectPath);
                    if (existing.Contains(normalizedPath))
                        continue;

                    targetFolder.AddExistingProject(FileName.Create(normalizedPath));
                    existing.Add(normalizedPath);
                }

                Host?.RefreshSolutionTree();
                if (generatedProjectFiles.Count > 0)
                {
                    Host?.OpenFileInWorkbench(generatedProjectFiles[0]);
                }
            }
            else
            {
                if (generatedSolutionFile is not null)
                {
                    projectService.OpenSolution(FileName.Create(generatedSolutionFile));
                    Host?.RefreshSolutionTree();
                    return;
                }

                if (generatedProjectFiles.Count == 0)
                {
                    ServiceSingleton.GetRequiredService<IMessageService>()
                        .ShowError($"Template '{template.Name}' did not generate a solution or project file.");
                    return;
                }

                // No solution was generated by the template, so create a wrapper .slnx and add all projects.
                var solutionDir = Path.GetDirectoryName(generatedProjectFiles[0]) ?? location;
                var solutionFileName = Path.Combine(solutionDir, projectName + ".slnx");
                var newSolution = projectService.CreateEmptySolutionFile(FileName.Create(solutionFileName));
                foreach (var projectPath in generatedProjectFiles)
                {
                    newSolution.AddExistingProject(FileName.Create(projectPath));
                }

                projectService.OpenSolution(newSolution);
                Host?.RefreshSolutionTree();
                Host?.OpenFileInWorkbench(generatedProjectFiles[0]);
            }
        }
        catch (Exception ex)
        {
            ServiceSingleton.GetRequiredService<IMessageService>()
                .ShowException(ex, "Failed to add new project.");
        }
    }

    static string? FindGeneratedSolutionFile(TemplateInstantiationResult result, string fallbackRoot)
    {
        var fromPrimary = result.PrimaryOutputPaths
            .FirstOrDefault(IsSolutionFilePath);
        if (!string.IsNullOrWhiteSpace(fromPrimary) && File.Exists(fromPrimary))
            return fromPrimary;

        var root = Directory.Exists(result.OutputDirectory) ? result.OutputDirectory : fallbackRoot;
        if (!Directory.Exists(root))
            return null;

        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .FirstOrDefault(IsSolutionFilePath);
    }

    static List<string> FindGeneratedProjectFiles(TemplateInstantiationResult result, string fallbackRoot)
    {
        var paths = result.PrimaryOutputPaths
            .Where(IsProjectFilePath)
            .Select(Path.GetFullPath)
            .ToList();
        if (paths.Count > 0)
            return paths;

        var root = Directory.Exists(result.OutputDirectory) ? result.OutputDirectory : fallbackRoot;
        if (!Directory.Exists(root))
            return new List<string>();

        return Directory.EnumerateFiles(root, "*.*proj", SearchOption.AllDirectories)
            .Where(IsProjectFilePath)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static bool IsProjectFilePath(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);

    static bool IsSolutionFilePath(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    static ISolutionFolder ResolveTargetSolutionFolder(ProjectBrowserNodeContext? selected, ISolution currentSolution)
    {
        if (selected?.BoundItem is ISolutionFolder folder)
            return folder;

        if (selected?.BoundItem is IProject project)
            return project.ParentFolder ?? currentSolution;

        return currentSolution;
    }

    public async void AddExistingFile(ProjectBrowserNodeContext? node = null)
    {
        var selected = ResolveNode(node);
        var targetDirectory = ResolveTargetDirectoryForCreate(selected);

        var paths = await FileDialogService.PickFilesAsync("All files|*.*");
        if (paths.Length == 0)
            return;

        ExecuteFileSystemAction(() =>
        {
            var imported = _explorerService.ImportExistingFiles(targetDirectory, paths);
            if (imported.Count == 0)
                return;
            Host?.RefreshSolutionTree();
            if (imported.Count == 1)
            {
                Host?.OpenFileInWorkbench(imported[0]);
                return;
            }
        }, "Failed to add existing file.");
    }

    public async void AddExistingFolder(ProjectBrowserNodeContext? node = null)
    {
        var selected = ResolveNode(node);
        var targetDirectory = ResolveTargetDirectoryForCreate(selected);

        var folderPath = await FileDialogService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folderPath))
            return;

        ExecuteFileSystemAction(() =>
        {
            var importedFolder = _explorerService.ImportExistingFolder(targetDirectory, folderPath);
            Host?.RefreshSolutionTree();
        }, "Failed to add existing folder.");
    }

    public void Rename(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || IsVirtualProjectFile(target) || (!target.IsFileLike && target.Kind != ProjectBrowserNodeKind.Folder))
        {
            return;
        }

        var currentName = Path.GetFileName(target.FullPath);
        var newName = Host?.ShowInputBox("Rename", "Enter new name:", currentName);
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, currentName, StringComparison.Ordinal))
        {
            return;
        }

        ExecuteFileSystemAction(() =>
        {
            var newPath = _explorerService.RenameItem(target.FullPath, target.Kind == ProjectBrowserNodeKind.Folder, newName);
            Host?.RetargetViewForRename(target.FullPath, newPath);
            Host?.RefreshSolutionTree();
        }, "Failed to rename item.");
    }

    public void Delete(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null)
        {
            return;
        }

        var isDirectory = target.Kind == ProjectBrowserNodeKind.Folder || target.Kind == ProjectBrowserNodeKind.Project;
        if (IsVirtualProjectFile(target) || (!target.IsFileLike && !isDirectory))
        {
            return;
        }

        if (Host is not null && !Host.ConfirmDelete(target.Name))
        {
            return;
        }

        ExecuteFileSystemAction(() =>
        {
            Host?.CloseViewsForPath(target.FullPath);
            _explorerService.DeleteItem(target.FullPath, isDirectory);
            Host?.RefreshSolutionTree();
        }, "Failed to delete item.");
    }

    public void RemoveFromProject(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null)
        {
            return;
        }

        ExecuteFileSystemAction(() =>
        {
            if (IsVirtualProjectFile(target))
            {
                return;
            }

            if (target.IsFileLike || target.Kind == ProjectBrowserNodeKind.Folder)
            {
                var projectPathHint = target.BoundProjectTree?.Root?.FilePath;
                var includeHint = target.IncludeHint;
                if (!_explorerService.TryRemoveItemFromProject(target.FullPath, target.Kind == ProjectBrowserNodeKind.Folder, out var removedItemName, projectPathHint, includeHint))
                {
                    return;
                }

                Host?.RefreshSolutionTree();
                return;
            }

            if (target.Kind != ProjectBrowserNodeKind.Project)
            {
                return;
            }

            if (!_explorerService.TryRemoveProject(target.FullPath, out var removedProjectName))
            {
                return;
            }

            Host?.RefreshSolutionTree();
        }, "Failed to remove project from solution.");
    }

    public void IncludeInProject(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || target.Kind != ProjectBrowserNodeKind.GhostFile)
        {
            return;
        }

        ExecuteFileSystemAction(() =>
        {
            if (!_explorerService.TryIncludeItemInProject(target.FullPath, out _))
            {
                return;
            }

            Host?.RefreshSolutionTree();
        }, "Failed to include item in project.");
    }

    public void ExcludeFromProject(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null
            || target.Kind is not (ProjectBrowserNodeKind.File or ProjectBrowserNodeKind.LinkedFile))
        {
            return;
        }

        ExecuteFileSystemAction(() =>
        {
            if (!_explorerService.TryExcludeItemFromProject(target.FullPath, isDirectory: false, out _))
            {
                return;
            }

            Host?.RefreshSolutionTree();
        }, "Failed to exclude item from project.");
    }

    public void RemoveReference(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target?.Kind is not (ProjectBrowserNodeKind.Reference
                or ProjectBrowserNodeKind.ProjectReference
                or ProjectBrowserNodeKind.PackageReference))
        {
            return;
        }

        var projectPathHint = target.BoundProjectTree?.Root?.FilePath;
        var include = target.IncludeHint;
        if (string.IsNullOrWhiteSpace(include))
        {
            include = target.Name;
        }

        ExecuteFileSystemAction(() =>
        {
            if (!_explorerService.TryRemoveReference(projectPathHint, include ?? string.Empty, target.Kind, out _))
            {
                return;
            }

            Host?.RefreshSolutionTree();
        }, "Failed to remove reference from project.");
    }

    public void OpenProjectReference(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || target.Kind != ProjectBrowserNodeKind.ProjectReference)
        {
            return;
        }

        if (File.Exists(target.FullPath))
        {
            Host?.OpenFileInWorkbench(target.FullPath);
        }
    }

    public void OpenWith(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || string.IsNullOrWhiteSpace(target.FullPath))
        {
            return;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "rundll32.exe",
                    Arguments = "shell32.dll,OpenAs_RunDLL \"" + target.FullPath + "\"",
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = target.FullPath,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            ServiceSingleton.GetRequiredService<IMessageService>().ShowException(ex, "Failed to open with the system chooser.");
        }
    }

    public void CopyPath(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || string.IsNullOrWhiteSpace(target.FullPath))
        {
            return;
        }

        CopyTextToClipboard(target.FullPath);
    }

    public void OpenFolder(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null)
        {
            return;
        }

        var directory = target.IsFileLike
            ? Path.GetDirectoryName(target.FullPath)
            : target.FullPath;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + directory + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ServiceSingleton.GetRequiredService<IMessageService>().ShowException(ex, "Failed to open folder.");
        }
    }

    public void SetStartupProject(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || target.Kind != ProjectBrowserNodeKind.Project)
        {
            return;
        }

        if (_explorerService.TrySetStartupProject(target.FullPath, out _))
        {
        }
    }

    private ProjectBrowserNodeContext? ResolveNode(ProjectBrowserNodeContext? node)
    {
        return node ?? Host?.SelectedNode;
    }

    private string ResolveTargetDirectoryForCreate(ProjectBrowserNodeContext? selected)
    {
        if (selected is null)
        {
            return Directory.GetCurrentDirectory();
        }

        if (selected.IsFileLike || selected.Kind == ProjectBrowserNodeKind.Project)
        {
            return Path.GetDirectoryName(selected.FullPath) ?? Directory.GetCurrentDirectory();
        }

        if (selected.Kind == ProjectBrowserNodeKind.Solution)
        {
            return Path.GetDirectoryName(selected.FullPath) ?? Directory.GetCurrentDirectory();
        }

        return selected.FullPath;
    }

    private static void ExecuteFileSystemAction(Action action, string failureMessage)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ServiceSingleton.GetRequiredService<IMessageService>().ShowException(ex, failureMessage);
        }
    }

    private static bool IsVirtualProjectFile(ProjectBrowserNodeContext node)
    {
        return node.Kind is ProjectBrowserNodeKind.MissingFile or ProjectBrowserNodeKind.GhostFile;
    }
}
