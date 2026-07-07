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

namespace ICSharpCode.SharpDevelop.Services;

internal interface ISolutionExplorerHost
{
    SolutionExplorerNodeContext? SelectedNode { get; }
    void RefreshSolutionTree();
    void OpenFileInWorkbench(string filePath);
    string? ShowInputBox(string title, string prompt, string defaultValue);
    bool ConfirmDelete(string name);
    void CloseViewsForPath(string path);
    void RetargetViewForRename(string oldPath, string newPath);
}

internal interface ISolutionExplorerService
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
    bool TryRemoveReference(string? projectPathHint, string include, SolutionExplorerNodeKind kind, out string removedName);
    bool TryRemoveProject(string projectPath, out string removedProjectName);
    bool TrySetStartupProject(string projectPath, out IProject? project);
}

internal interface ISolutionExplorerController
{
    void BindHost(ISolutionExplorerHost host);
    void Refresh();
    void Open(SolutionExplorerNodeContext? node = null);
    void CreateFolder(SolutionExplorerNodeContext? node = null);
    void CreateFile(SolutionExplorerNodeContext? node = null);
    void AddExistingFile(SolutionExplorerNodeContext? node = null);
    void AddExistingFolder(SolutionExplorerNodeContext? node = null);
    void AddNewItem(SolutionExplorerNodeContext? node = null);
    void AddNewProject(SolutionExplorerNodeContext? node = null);
    void Rename(SolutionExplorerNodeContext? node = null);
    void Delete(SolutionExplorerNodeContext? node = null);
    void IncludeInProject(SolutionExplorerNodeContext? node = null);
    void ExcludeFromProject(SolutionExplorerNodeContext? node = null);
    void RemoveFromProject(SolutionExplorerNodeContext? node = null);
    void RemoveReference(SolutionExplorerNodeContext? node = null);
    void OpenProjectReference(SolutionExplorerNodeContext? node = null);
    void OpenWith(SolutionExplorerNodeContext? node = null);
    void CopyPath(SolutionExplorerNodeContext? node = null);
    void OpenFolder(SolutionExplorerNodeContext? node = null);
    void SetStartupProject(SolutionExplorerNodeContext? node = null);
}

internal static class FileDialogService
{
    public static Task<string[]> PickFilesAsync(string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter, Multiselect = true };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>());
    }

    public static Task<string?> PickFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FolderName : null);
    }
}

internal sealed class SolutionExplorerController : ISolutionExplorerController
{
    private readonly ISolutionExplorerService _explorerService;
    private ISolutionExplorerHost? _host;

    public SolutionExplorerController()
    {
        _explorerService = new SharpDevelopSolutionExplorerService();
    }

    public void BindHost(ISolutionExplorerHost host)
    {
        _host = host;
    }

    public void Refresh()
    {
        _host?.RefreshSolutionTree();
    }

    public void Open(SolutionExplorerNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || !target.IsFileLike || target.Kind == SolutionExplorerNodeKind.MissingFile)
        {
            return;
        }

        _host?.OpenFileInWorkbench(target.FullPath);
    }

    public void CreateFolder(SolutionExplorerNodeContext? node = null)
    {
        ExecuteFileSystemAction(() =>
        {
            var targetDirectory = ResolveTargetDirectoryForCreate(ResolveNode(node));
            var folderPath = _explorerService.CreateFolder(targetDirectory);
            _host?.RefreshSolutionTree();
        }, "Failed to create folder.");
    }

    public void CreateFile(SolutionExplorerNodeContext? node = null)
    {
        ExecuteFileSystemAction(() =>
        {
            var targetDirectory = ResolveTargetDirectoryForCreate(ResolveNode(node));
            var filePath = _explorerService.CreateFile(targetDirectory);
            _host?.RefreshSolutionTree();
            _host?.OpenFileInWorkbench(filePath);
        }, "Failed to create file.");
    }

    public async void AddNewItem(SolutionExplorerNodeContext? node = null)
    {
        try
        {
            var selected = ResolveNode(node);
            var targetDirectory = ResolveTargetDirectoryForCreate(selected);

            using var service = new TemplateDiscoveryService();
            var owner = System.Windows.Application.Current.MainWindow;
            var dialog = await NewItemWindow.ShowAsync(service, targetDirectory, owner);
            if (dialog is null || dialog.SelectedTemplate is null)
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

            if (result.PrimaryOutputPaths.Count > 0)
            {
                _host?.RefreshSolutionTree();
                _host?.OpenFileInWorkbench(result.PrimaryOutputPaths[0]);
            }
        }
        catch (Exception ex)
        {
            ServiceSingleton.GetRequiredService<IMessageService>()
                .ShowException(ex, "Failed to add new item.");
        }
    }

    public async void AddNewProject(SolutionExplorerNodeContext? node = null)
    {
        try
        {
            var selected = ResolveNode(node);
            var defaultLocation = ResolveTargetDirectoryForCreate(selected);

            using var service = new TemplateDiscoveryService();
            var owner = System.Windows.Application.Current.MainWindow;
            var dialog = await NewProjectWindow.ShowAsync(service, defaultLocation, owner);
            if (dialog is null || dialog.SelectedTemplate is null)
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

                _host?.RefreshSolutionTree();
                if (generatedProjectFiles.Count > 0)
                {
                    _host?.OpenFileInWorkbench(generatedProjectFiles[0]);
                }
            }
            else
            {
                if (generatedSolutionFile is not null)
                {
                    projectService.OpenSolution(FileName.Create(generatedSolutionFile));
                    _host?.RefreshSolutionTree();
                    return;
                }

                if (generatedProjectFiles.Count == 0)
                {
                    ServiceSingleton.GetRequiredService<IMessageService>()
                        .ShowError($"Template '{template.Name}' did not generate a solution or project file.");
                    return;
                }

                var solutionDir = Path.GetDirectoryName(generatedProjectFiles[0]) ?? location;
                var solutionFileName = Path.Combine(solutionDir, projectName + ".slnx");
                var newSolution = projectService.CreateEmptySolutionFile(FileName.Create(solutionFileName));
                foreach (var projectPath in generatedProjectFiles)
                {
                    newSolution.AddExistingProject(FileName.Create(projectPath));
                }

                projectService.OpenSolution(newSolution);
                _host?.RefreshSolutionTree();
                _host?.OpenFileInWorkbench(generatedProjectFiles[0]);
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

    static ISolutionFolder ResolveTargetSolutionFolder(SolutionExplorerNodeContext? selected, ISolution currentSolution)
    {
        if (selected?.BoundItem is ISolutionFolder folder)
            return folder;

        if (selected?.BoundItem is IProject project)
            return project.ParentFolder ?? currentSolution;

        return currentSolution;
    }

    public async void AddExistingFile(SolutionExplorerNodeContext? node = null)
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
            _host?.RefreshSolutionTree();
            if (imported.Count == 1)
            {
                _host?.OpenFileInWorkbench(imported[0]);
                return;
            }
        }, "Failed to add existing file.");
    }

    public async void AddExistingFolder(SolutionExplorerNodeContext? node = null)
    {
        var selected = ResolveNode(node);
        var targetDirectory = ResolveTargetDirectoryForCreate(selected);

        var folderPath = await FileDialogService.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folderPath))
            return;

        ExecuteFileSystemAction(() =>
        {
            var importedFolder = _explorerService.ImportExistingFolder(targetDirectory, folderPath);
            _host?.RefreshSolutionTree();
        }, "Failed to add existing folder.");
    }

    public void Rename(SolutionExplorerNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || IsVirtualProjectFile(target) || (!target.IsFileLike && target.Kind != SolutionExplorerNodeKind.Folder))
        {
            return;
        }

        var currentName = Path.GetFileName(target.FullPath);
        var newName = _host?.ShowInputBox("Rename", "Enter new name:", currentName);
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, currentName, StringComparison.Ordinal))
        {
            return;
        }

        ExecuteFileSystemAction(() =>
        {
            var newPath = _explorerService.RenameItem(target.FullPath, target.Kind == SolutionExplorerNodeKind.Folder, newName);
            _host?.RetargetViewForRename(target.FullPath, newPath);
            _host?.RefreshSolutionTree();
        }, "Failed to rename item.");
    }

    public void Delete(SolutionExplorerNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null)
        {
            return;
        }

        var isDirectory = target.Kind == SolutionExplorerNodeKind.Folder || target.Kind == SolutionExplorerNodeKind.Project;
        if (IsVirtualProjectFile(target) || (!target.IsFileLike && !isDirectory))
        {
            return;
        }

        if (_host is not null && !_host.ConfirmDelete(target.Name))
        {
            return;
        }

        ExecuteFileSystemAction(() =>
        {
            _host?.CloseViewsForPath(target.FullPath);
            _explorerService.DeleteItem(target.FullPath, isDirectory);
            _host?.RefreshSolutionTree();
        }, "Failed to delete item.");
    }

    public void RemoveFromProject(SolutionExplorerNodeContext? node = null)
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

            if (target.IsFileLike || target.Kind == SolutionExplorerNodeKind.Folder)
            {
                var projectPathHint = target.BoundProjectTree?.Root?.FilePath;
                var includeHint = target.IncludeHint;
                if (!_explorerService.TryRemoveItemFromProject(target.FullPath, target.Kind == SolutionExplorerNodeKind.Folder, out var removedItemName, projectPathHint, includeHint))
                {
                    return;
                }

                _host?.RefreshSolutionTree();
                return;
            }

            if (target.Kind != SolutionExplorerNodeKind.Project)
            {
                return;
            }

            if (!_explorerService.TryRemoveProject(target.FullPath, out var removedProjectName))
            {
                return;
            }

            _host?.RefreshSolutionTree();
        }, "Failed to remove project from solution.");
    }

    public void IncludeInProject(SolutionExplorerNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || target.Kind != SolutionExplorerNodeKind.GhostFile)
        {
            return;
        }

        ExecuteFileSystemAction(() =>
        {
            if (!_explorerService.TryIncludeItemInProject(target.FullPath, out _))
            {
                return;
            }

            _host?.RefreshSolutionTree();
        }, "Failed to include item in project.");
    }

    public void ExcludeFromProject(SolutionExplorerNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null
            || target.Kind is not (SolutionExplorerNodeKind.File or SolutionExplorerNodeKind.LinkedFile))
        {
            return;
        }

        ExecuteFileSystemAction(() =>
        {
            if (!_explorerService.TryExcludeItemFromProject(target.FullPath, isDirectory: false, out _))
            {
                return;
            }

            _host?.RefreshSolutionTree();
        }, "Failed to exclude item from project.");
    }

    public void RemoveReference(SolutionExplorerNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target?.Kind is not (SolutionExplorerNodeKind.Reference
                or SolutionExplorerNodeKind.ProjectReference
                or SolutionExplorerNodeKind.PackageReference))
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

            _host?.RefreshSolutionTree();
        }, "Failed to remove reference from project.");
    }

    public void OpenProjectReference(SolutionExplorerNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || target.Kind != SolutionExplorerNodeKind.ProjectReference)
        {
            return;
        }

        if (File.Exists(target.FullPath))
        {
            _host?.OpenFileInWorkbench(target.FullPath);
        }
    }

    public void OpenWith(SolutionExplorerNodeContext? node = null)
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

    public void CopyPath(SolutionExplorerNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || string.IsNullOrWhiteSpace(target.FullPath))
        {
            return;
        }

        System.Windows.Clipboard.SetText(target.FullPath);
    }

    public void OpenFolder(SolutionExplorerNodeContext? node = null)
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

    public void SetStartupProject(SolutionExplorerNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || target.Kind != SolutionExplorerNodeKind.Project)
        {
            return;
        }

        if (_explorerService.TrySetStartupProject(target.FullPath, out _))
        {
        }
    }

    private SolutionExplorerNodeContext? ResolveNode(SolutionExplorerNodeContext? node)
    {
        return node ?? _host?.SelectedNode;
    }

    private string ResolveTargetDirectoryForCreate(SolutionExplorerNodeContext? selected)
    {
        if (selected is null)
        {
            return Directory.GetCurrentDirectory();
        }

        if (selected.IsFileLike || selected.Kind == SolutionExplorerNodeKind.Project)
        {
            return Path.GetDirectoryName(selected.FullPath) ?? Directory.GetCurrentDirectory();
        }

        if (selected.Kind == SolutionExplorerNodeKind.Solution)
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

    private static bool IsVirtualProjectFile(SolutionExplorerNodeContext node)
    {
        return node.Kind is SolutionExplorerNodeKind.MissingFile or SolutionExplorerNodeKind.GhostFile;
    }

}
