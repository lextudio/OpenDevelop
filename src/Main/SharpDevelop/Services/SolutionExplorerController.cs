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

    // AddNewItem/AddNewProject need a "new item"/"new project" template-picker dialog. UnoDevelop's
    // versions (UnoDevelop.Templates.NewItemDialog/NewProjectDialog, plus its portable
    // TemplateDiscoveryService/TemplateInstantiationResult template engine, none of which exist in
    // OpenDevelop yet) are WinUI dialogs - out of scope for R6b (model/provider/command layer only).
    // Stubbed pending a WPF template-picker dialog + porting TemplateDiscoveryService in R6c (see
    // doc/technotes/solution-explorer.md).
    public void AddNewItem(SolutionExplorerNodeContext? node = null)
    {
        ServiceSingleton.GetRequiredService<IMessageService>()
            .ShowMessage("Add New Item is not yet implemented (pending R6c template-picker dialog).");
    }

    public void AddNewProject(SolutionExplorerNodeContext? node = null)
    {
        ServiceSingleton.GetRequiredService<IMessageService>()
            .ShowMessage("Add New Project is not yet implemented (pending R6c template-picker dialog).");
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
