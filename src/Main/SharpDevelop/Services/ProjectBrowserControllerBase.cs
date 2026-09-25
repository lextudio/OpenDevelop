#nullable enable
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
    bool IsShowingAllFiles { get; }
    void RefreshSolutionTree();
    void OpenFileInWorkbench(string filePath);
    string? ShowInputBox(string title, string prompt, string defaultValue);
    bool ConfirmDelete(string name);
    void CloseViewsForPath(string path);
    void RetargetViewForRename(string oldPath, string newPath);
    void ShowPropertiesForNode(ProjectBrowserNodeContext node);
    void ToggleShowAllFiles();
    void CollapseAll();
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
    void OpenTerminal(ProjectBrowserNodeContext? node = null);
    void AddReference(ProjectBrowserNodeContext? node = null);
    void RunProject(ProjectBrowserNodeContext? node = null, bool withDebugging = true);
    void AddExistingProject(ProjectBrowserNodeContext? node = null);
    void NewSolutionFolder(ProjectBrowserNodeContext? node = null);
    void AddSolutionItems(ProjectBrowserNodeContext? node = null);
    bool CanRunCustomTool(ProjectBrowserNodeContext? node = null);
    void RunCustomTool(ProjectBrowserNodeContext? node = null);
    void SetStartupProject(ProjectBrowserNodeContext? node = null);
    bool CanCutOrCopy(ProjectBrowserNodeContext? node = null);
    bool CanPaste(ProjectBrowserNodeContext? node = null);
    void Cut(ProjectBrowserNodeContext? node = null);
    void Copy(ProjectBrowserNodeContext? node = null);
    void Paste(ProjectBrowserNodeContext? node = null);
    void ShowPropertiesForNode(ProjectBrowserNodeContext? node = null);
    void ToggleShowAll();
    bool IsShowAllFilesEnabled { get; }
    void CollapseAll();
}

/// <summary>Host-neutral result of the "Add New Item" dialog - see <see cref="ProjectBrowserControllerBase.ShowNewItemDialogAsync"/>.</summary>
internal sealed record NewItemDialogOutcome(TemplateSummary SelectedTemplate, string ItemName, IReadOnlyDictionary<string, string?> AdditionalParameters);

/// <summary>Host-neutral result of the "Add New Project" dialog - see <see cref="ProjectBrowserControllerBase.ShowNewProjectDialogAsync"/>.</summary>
internal sealed record NewProjectDialogOutcome(TemplateSummary SelectedTemplate, string ProjectName, string Location, IReadOnlyDictionary<string, string?> AdditionalParameters);

/// <summary>A solution project that can be offered by the "Add Reference" dialog.</summary>
internal sealed record ReferenceCandidate(string Name, string ProjectPath);

/// <summary>Host-neutral result of the "Add Reference" dialog - see <see cref="ProjectBrowserControllerBase.ShowAddReferenceDialogAsync"/>.</summary>
internal sealed record AddReferenceDialogOutcome(IReadOnlyList<string> ProjectPaths, IReadOnlyList<string> AssemblyPaths);

/// <summary>
/// Shared Project Browser command surface (see doc/technotes/solution-explorer.md) - every command
/// that only touches SharpDevelop's own IProject/ISolution/IMessageService model lives here, once,
/// for both hosts. The three genuinely native touchpoints (new-item/new-project dialog UI, and
/// clipboard) are the only things a concrete host subclass has to supply.
/// </summary>
internal abstract class ProjectBrowserControllerBase : IProjectBrowserController
{
    private readonly IProjectBrowserService _explorerService;
    private (string Path, bool IsDirectory, bool IsCut)? _clipboardItem;
    protected IProjectBrowserHost? Host { get; private set; }

    protected ProjectBrowserControllerBase(IProjectBrowserService explorerService)
    {
        _explorerService = explorerService;
    }

    /// <summary>Shows the host's native "Add New Item" dialog/window. Null return means the user cancelled.</summary>
    protected abstract Task<NewItemDialogOutcome?> ShowNewItemDialogAsync(TemplateDiscoveryService service, string targetDirectory);

    /// <summary>Shows the host's native "Add New Project" dialog/window. Null return means the user cancelled.</summary>
    protected abstract Task<NewProjectDialogOutcome?> ShowNewProjectDialogAsync(TemplateDiscoveryService service, string defaultLocation);

    /// <summary>Shows the host's "Add Reference" dialog. Null return means the user cancelled or the
    /// host has no such dialog; virtual rather than abstract so a host can opt in later.</summary>
    protected virtual Task<AddReferenceDialogOutcome?> ShowAddReferenceDialogAsync(string projectName, IReadOnlyList<ReferenceCandidate> candidates)
        => Task.FromResult<AddReferenceDialogOutcome?>(null);

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

    public void ShowPropertiesForNode(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null)
        {
            return;
        }

        Host?.ShowPropertiesForNode(target);
    }

    public void ToggleShowAll()
    {
        Host?.ToggleShowAllFiles();
    }

    public bool IsShowAllFilesEnabled => Host?.IsShowingAllFiles ?? false;

    public void CollapseAll()
    {
        Host?.CollapseAll();
    }

    public void Open(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || !target.IsFileNode || target.Kind == ProjectBrowserNodeKind.MissingFile)
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

            var parameters = new Dictionary<string, string?>(dialog.AdditionalParameters, StringComparer.OrdinalIgnoreCase);

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

                currentSolution.Save();
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
        if (target?.Kind == ProjectBrowserNodeKind.SolutionFolder && target.BoundItem is ISolutionFolder solutionFolder)
        {
            RenameSolutionFolder(solutionFolder);
            return;
        }

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

            if (target.Kind is ProjectBrowserNodeKind.SolutionFolder or ProjectBrowserNodeKind.SolutionItem)
            {
                RemoveFromSolution(target);
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

            // TryRemoveProject only edits the in-memory model; the solution file has to be written.
            SD.ProjectService.CurrentSolution?.Save();
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

        var directory = target.IsFileNode
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

    public void OpenTerminal(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null)
        {
            return;
        }

        var directory = target.IsFileNode
            ? Path.GetDirectoryName(target.FullPath)
            : target.FullPath;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    // Windows Terminal when installed; the App Execution Alias throws when it is not.
                    Process.Start(new ProcessStartInfo("wt.exe", "-d \"" + directory + "\"") { UseShellExecute = true });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = directory, UseShellExecute = true });
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open", "-a Terminal \"" + directory + "\"") { UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo("x-terminal-emulator") { WorkingDirectory = directory, UseShellExecute = false });
            }
        }
        catch (Exception ex)
        {
            ServiceSingleton.GetRequiredService<IMessageService>().ShowException(ex, "Failed to open a terminal.");
        }
    }

    public async void AddReference(ProjectBrowserNodeContext? node = null)
    {
        try
        {
            var project = ResolveProject(ResolveNode(node));
            var solution = SD.ProjectService.CurrentSolution;
            if (project is null || solution is null)
            {
                return;
            }

            var alreadyReferenced = new HashSet<string>(
                project.Items.OfType<ProjectReferenceProjectItem>()
                    .Select(item => item.FileName?.ToString())
                    .Where(path => !string.IsNullOrEmpty(path))!,
                StringComparer.OrdinalIgnoreCase);
            var candidates = solution.Projects
                .Where(p => p != project && !alreadyReferenced.Contains(p.FileName.ToString()))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new ReferenceCandidate(p.Name, p.FileName.ToString()))
                .ToArray();

            var outcome = await ShowAddReferenceDialogAsync(project.Name, candidates);
            if (outcome is null)
            {
                return;
            }

            foreach (var projectPath in outcome.ProjectPaths)
            {
                var referenced = solution.Projects.FirstOrDefault(p =>
                    string.Equals(p.FileName.ToString(), projectPath, StringComparison.OrdinalIgnoreCase));
                if (referenced is null)
                {
                    continue;
                }

                var item = new ProjectReferenceProjectItem(project, referenced);
                // SDK-style projects resolve a ProjectReference by path alone; the GUID/name metadata
                // the legacy constructor writes is only noise in the project file.
                item.RemoveMetadata("Project");
                item.RemoveMetadata("Name");
                ProjectServiceCompat.AddProjectItem(project, item);
            }

            var failed = new List<string>();
            foreach (var assemblyPath in outcome.AssemblyPaths)
            {
                System.Reflection.AssemblyName assemblyName;
                try
                {
                    assemblyName = System.Reflection.AssemblyName.GetAssemblyName(assemblyPath);
                }
                catch (Exception ex) when (ex is BadImageFormatException or IOException)
                {
                    failed.Add(Path.GetFileName(assemblyPath));
                    continue;
                }

                var item = new ReferenceProjectItem(project, assemblyName.Name ?? Path.GetFileNameWithoutExtension(assemblyPath))
                {
                    HintPath = FileUtility.GetRelativePath(project.Directory, FileName.Create(assemblyPath))
                };
                ProjectServiceCompat.AddProjectItem(project, item);
            }

            project.Save();
            Host?.RefreshSolutionTree();

            if (failed.Count > 0)
            {
                ServiceSingleton.GetRequiredService<IMessageService>().ShowError(
                    "These files are not .NET assemblies and were not added:\n" + string.Join("\n", failed));
            }
        }
        catch (Exception ex)
        {
            ServiceSingleton.GetRequiredService<IMessageService>().ShowException(ex, "Failed to add reference.");
        }
    }

    public void RunProject(ProjectBrowserNodeContext? node = null, bool withDebugging = true)
    {
        var project = ResolveProject(ResolveNode(node));
        if (project is null)
        {
            return;
        }

        if (!project.IsStartable)
        {
            ServiceSingleton.GetRequiredService<IMessageService>().ShowError("${res:BackendBindings.ExecutionManager.CantExecuteDLLError}");
            return;
        }

        var build = new ICSharpCode.SharpDevelop.Project.Commands.BuildProjectBeforeExecute(project);
        build.BuildComplete += delegate
        {
            if (build.LastBuildResults.ErrorCount == 0)
            {
                project.Start(withDebugging);
            }
        };
        build.Run();
    }

    public async void AddExistingProject(ProjectBrowserNodeContext? node = null)
    {
        try
        {
            var solution = SD.ProjectService.CurrentSolution;
            if (solution is null)
            {
                return;
            }

            var paths = await FileDialogService.PickFilesAsync(
                "Project files (*.csproj;*.vbproj;*.fsproj)|*.csproj;*.vbproj;*.fsproj|All files (*.*)|*.*");
            var added = false;
            foreach (var path in paths)
            {
                if (solution.Projects.Any(p => string.Equals(p.FileName.ToString(), path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                TargetSolutionFolder(ResolveNode(node), solution).AddExistingProject(FileName.Create(path));
                added = true;
            }

            if (added)
            {
                solution.Save();
                Host?.RefreshSolutionTree();
            }
        }
        catch (Exception ex)
        {
            ServiceSingleton.GetRequiredService<IMessageService>().ShowException(ex, "Failed to add the project to the solution.");
        }
    }

    public void NewSolutionFolder(ProjectBrowserNodeContext? node = null)
    {
        var solution = SD.ProjectService.CurrentSolution;
        if (solution is null)
        {
            return;
        }

        var parent = TargetSolutionFolder(ResolveNode(node), solution);
        var name = Host?.ShowInputBox("New Solution Folder", "Folder name:", UniqueFolderName(parent, "New Folder"))?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        if (FindChildFolder(parent, name) is not null)
        {
            ServiceSingleton.GetRequiredService<IMessageService>().ShowError("'" + parent.Name + "' already contains a folder named '" + name + "'.");
            return;
        }

        ExecuteFileSystemAction(() =>
        {
            parent.CreateFolder(name);
            solution.Save();
            Host?.RefreshSolutionTree();
        }, "Failed to create the solution folder.");
    }

    public async void AddSolutionItems(ProjectBrowserNodeContext? node = null)
    {
        try
        {
            var solution = SD.ProjectService.CurrentSolution;
            if (solution is null)
            {
                return;
            }

            var paths = await FileDialogService.PickFilesAsync("All files|*.*");
            if (paths.Length == 0)
            {
                return;
            }

            // A solution item has to live in a solution folder (.slnx has no top-level <File>), so
            // items added on the solution node go to "Solution Items", as in Visual Studio.
            var target = ResolveNode(node);
            var folder = target?.Kind == ProjectBrowserNodeKind.SolutionFolder && target.BoundItem is ISolutionFolder selected
                ? selected
                : FindChildFolder(solution, "Solution Items") ?? solution.CreateFolder("Solution Items");
            foreach (var path in paths)
            {
                if (!folder.Items.OfType<ISolutionFileItem>().Any(f => string.Equals(f.FileName.ToString(), path, StringComparison.OrdinalIgnoreCase)))
                {
                    folder.AddFile(FileName.Create(path));
                }
            }

            solution.Save();
            Host?.RefreshSolutionTree();
        }
        catch (Exception ex)
        {
            ServiceSingleton.GetRequiredService<IMessageService>().ShowException(ex, "Failed to add solution items.");
        }
    }

    void RenameSolutionFolder(ISolutionFolder folder)
    {
        var newName = Host?.ShowInputBox("Rename", "Enter new name:", folder.Name)?.Trim();
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, folder.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (folder.ParentFolder is not null && FindChildFolder(folder.ParentFolder, newName) is { } existing && existing != folder)
        {
            ServiceSingleton.GetRequiredService<IMessageService>().ShowError("'" + folder.ParentFolder.Name + "' already contains a folder named '" + newName + "'.");
            return;
        }

        ExecuteFileSystemAction(() =>
        {
            folder.Name = newName;
            SD.ProjectService.CurrentSolution?.Save();
            Host?.RefreshSolutionTree();
        }, "Failed to rename the solution folder.");
    }

    void RemoveFromSolution(ProjectBrowserNodeContext target)
    {
        if (target.BoundItem is not { ParentFolder: { } parent } item)
        {
            return;
        }

        // Removing a folder takes its projects out of the solution with it (their files stay on
        // disk), so say so before doing it - as Visual Studio does.
        if (item is ISolutionFolder folder)
        {
            var projectCount = CountProjects(folder);
            var question = projectCount == 0
                ? "Remove the solution folder '" + folder.Name + "'?"
                : "Remove the solution folder '" + folder.Name + "' and the " + projectCount + " project(s) in it from the solution?\n\nNo files are deleted.";
            if (!ServiceSingleton.GetRequiredService<IMessageService>().AskQuestion(question, "Remove Solution Folder"))
            {
                return;
            }
        }

        parent.Items.Remove(item);
        SD.ProjectService.CurrentSolution?.Save();
        Host?.RefreshSolutionTree();
    }

    static int CountProjects(ISolutionFolder folder) =>
        folder.Items.OfType<IProject>().Count() + folder.Items.OfType<ISolutionFolder>().Sum(CountProjects);

    /// <summary>The solution folder a solution-level command applies to: the selected solution folder, else the solution itself.</summary>
    static ISolutionFolder TargetSolutionFolder(ProjectBrowserNodeContext? target, ISolution solution) =>
        target?.Kind == ProjectBrowserNodeKind.SolutionFolder && target.BoundItem is ISolutionFolder folder ? folder : solution;

    static ISolutionFolder? FindChildFolder(ISolutionFolder parent, string name) =>
        parent.Items.OfType<ISolutionFolder>().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    static string UniqueFolderName(ISolutionFolder parent, string baseName)
    {
        var name = baseName;
        for (var i = 2; FindChildFolder(parent, name) is not null; i++)
        {
            name = baseName + " " + i;
        }
        return name;
    }

    public bool CanRunCustomTool(ProjectBrowserNodeContext? node = null)
        => !string.IsNullOrEmpty(FindFileItem(ResolveNode(node))?.CustomTool);

    public void RunCustomTool(ProjectBrowserNodeContext? node = null)
    {
        var item = FindFileItem(ResolveNode(node));
        if (item is null || string.IsNullOrEmpty(item.CustomTool))
        {
            return;
        }

        CustomToolsService.RunCustomTool(item, true);
    }

    static FileProjectItem? FindFileItem(ProjectBrowserNodeContext? target)
    {
        if (target is null || !target.IsFileLike || string.IsNullOrWhiteSpace(target.FullPath))
        {
            return null;
        }

        return ResolveProject(target)?.FindFile(FileName.Create(target.FullPath));
    }

    /// <summary>The project a node belongs to (the project itself for a project node), falling back to
    /// the current project when the command was not raised from a Project Browser node.</summary>
    static IProject? ResolveProject(ProjectBrowserNodeContext? target)
    {
        var solution = SD.ProjectService.CurrentSolution;
        var hint = target?.Kind == ProjectBrowserNodeKind.Project
            ? target.FullPath
            : target?.BoundProjectTree?.Root?.FilePath ?? target?.ProjectPathHint;
        if (solution is not null && !string.IsNullOrWhiteSpace(hint))
        {
            var byHint = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.FileName.ToString(), hint, StringComparison.OrdinalIgnoreCase));
            if (byHint is not null)
            {
                return byHint;
            }
        }

        return SD.ProjectService.CurrentProject;
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

    public bool CanCutOrCopy(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || IsVirtualProjectFile(target))
        {
            return false;
        }

        return target.IsFileLike || target.Kind == ProjectBrowserNodeKind.Folder;
    }

    public bool CanPaste(ProjectBrowserNodeContext? node = null)
    {
        if (_clipboardItem is null)
        {
            return false;
        }

        return ResolveNode(node) is not null;
    }

    public void Cut(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || !CanCutOrCopy(target))
        {
            return;
        }

        var isDirectory = target.Kind == ProjectBrowserNodeKind.Folder;
        _clipboardItem = (target.FullPath, isDirectory, IsCut: true);
    }

    public void Copy(ProjectBrowserNodeContext? node = null)
    {
        var target = ResolveNode(node);
        if (target is null || !CanCutOrCopy(target))
        {
            return;
        }

        var isDirectory = target.Kind == ProjectBrowserNodeKind.Folder;
        _clipboardItem = (target.FullPath, isDirectory, IsCut: false);
    }

    public void Paste(ProjectBrowserNodeContext? node = null)
    {
        if (_clipboardItem is not { } item || !File.Exists(item.Path) && !Directory.Exists(item.Path))
        {
            _clipboardItem = null;
            return;
        }

        var targetDirectory = ResolveTargetDirectoryForCreate(ResolveNode(node));

        ExecuteFileSystemAction(() =>
        {
            if (item.IsDirectory)
            {
                _explorerService.ImportExistingFolder(targetDirectory, item.Path);
            }
            else
            {
                _explorerService.ImportExistingFiles(targetDirectory, new[] { item.Path });
            }

            if (item.IsCut)
            {
                Host?.CloseViewsForPath(item.Path);
                _explorerService.DeleteItem(item.Path, item.IsDirectory);
                _clipboardItem = null;
            }

            Host?.RefreshSolutionTree();
        }, "Failed to paste item.");
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

        // Solution folders are virtual and a solution item's file may live anywhere; new content
        // for either belongs beside the solution file.
        if (selected.Kind is ProjectBrowserNodeKind.SolutionFolder or ProjectBrowserNodeKind.SolutionItem)
        {
            return SD.ProjectService.CurrentSolution?.Directory.ToString() ?? Directory.GetCurrentDirectory();
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
