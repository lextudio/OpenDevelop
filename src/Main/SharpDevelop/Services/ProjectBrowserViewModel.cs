using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Composition;
using System.Windows.Input;
using System.Windows.Media;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.ViewModels;

namespace ICSharpCode.SharpDevelop.Services;

[Export(typeof(ProjectBrowserViewModel))]
[Export("ToolPane", typeof(ToolPaneModel))]
[Shared]
internal sealed class ProjectBrowserViewModel : ToolPaneModel, IProjectBrowserHost, IDisposable
{
    private readonly IProjectBrowserController controller = ServiceSingleton.GetRequiredService<IProjectBrowserController>();
    private readonly IProjectBrowserOverlayService overlayService = ServiceSingleton.ServiceProvider.GetService<IProjectBrowserOverlayService>();
    private readonly PropertyContainer propertyContainer = new PropertyContainer();
    private readonly SemaphoreSlim treeBuildGate = new SemaphoreSlim(1, 1);
    private ProjectBrowserNodeModel selectedNode;
    private bool showAllFiles;
    private bool isLoading;
    private bool disposed;
    private int treeRefreshVersion;
    private Task currentTreeRefresh = Task.CompletedTask;

    public ProjectBrowserViewModel()
    {
        Title = "Projects";
        ContentId = "ProjectBrowser";
        IsVisible = true;
        IsCloseable = true;
        // Host-neutral pane/workspace contract vertical slice (doc/technotes/ilspy.md "Immediate
        // next actions" #3): this used to be a `ContentId == "ProjectBrowser"` special case inside
        // DockWorkspace.AfterInsertAnchorable; now any pane can express the same preference.
        PreferredDockSize = 280;
        // Matches the legacy Pad's `defaultPosition = "Left"`; used when a layout switch re-docks
        // this pane outside any persisted layout (AvalonDockLayout.LoadLayout, 2026-08-09).
        PreferredDockSide = ICSharpCode.SharpDevelop.ViewModels.PreferredDockSide.Left;
        // Generalized from AvalonDockLayout's own hardcoded class-name check
        // (doc/technotes/ilspy.md "Docking and layout replacement", 2026-08-03) - same effective
        // mapping, just declared here instead of in the shell.
        LegacyPadClass = typeof(ProjectBrowserPad).FullName;
        Content = new ProjectBrowserView { DataContext = this };
        ShowPropertiesCommand = new DelegateCommand(ShowProperties, () => SelectedNode != null);
        ShowAllFilesCommand = new DelegateCommand(ToggleShowAllFiles);
        RefreshCommand = new DelegateCommand(RefreshSolutionTree);
        CollapseAllCommand = new DelegateCommand(() => CollapseAllRequested?.Invoke(this, EventArgs.Empty));
        ShowAllFiles = SD.PropertyService.Get("ProjectBrowser.ShowAll", false);

        controller.BindHost(this);

        SD.ProjectService.SolutionOpened += ProjectServiceChanged;
        SD.ProjectService.SolutionClosed += ProjectServiceChanged;
        SD.ProjectService.ProjectItemAdded += ProjectServiceChanged;
        SD.ProjectService.ProjectItemRemoved += ProjectServiceChanged;
        ProjectTargetFrameworkService.ActiveTargetFrameworkChanged += ProjectTargetFrameworkChanged;
        if (overlayService != null) {
            overlayService.Invalidated += ProjectBrowserOverlayInvalidated;
        }

        RefreshSolutionTree();
    }

    public ObservableCollection<ProjectBrowserNodeModel> RootNodes { get; } = new ObservableCollection<ProjectBrowserNodeModel>();
    
    public event EventHandler CollapseAllRequested;
    
    public ICommand ShowPropertiesCommand { get; }
    
    public ICommand ShowAllFilesCommand { get; }
    
    public ICommand RefreshCommand { get; }
    
    public ICommand CollapseAllCommand { get; }
    
    public ImageSource PropertiesIcon { get; } = PresentationResourceService.GetBitmapSource("Icons.16x16.PropertiesIcon");
    
    public ImageSource ShowAllFilesIcon { get; } = PresentationResourceService.GetBitmapSource("ProjectBrowser.Toolbar.ShowHiddenFiles");
    
    public ImageSource RefreshIcon { get; } = PresentationResourceService.GetBitmapSource("Icons.16x16.BrowserRefresh");
    
    public ImageSource CollapseAllIcon { get; } = PresentationResourceService.GetBitmapSource("Icons.16x16.Collection");

    public ProjectBrowserNodeModel SelectedNode {
        get => selectedNode;
        set {
            if (SetProperty(ref selectedNode, value)) {
                UpdateCurrentProject(value);
                propertyContainer.SelectedObject = value != null ? new ProjectBrowserNodeProperties(value.ToContext()) : null;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    // Keep the legacy Project Browser contract: selecting a project or any node below it
    // makes that project current. A solution node has no owning project and clears the value.
    // A null selection is transient while the WPF tree is rebuilt, so it must not disturb the
    // current project (the old WinForms BeforeSelect handler likewise ignored a null node).
    private static void UpdateCurrentProject(ProjectBrowserNodeModel node)
    {
        if (node == null) {
            return;
        }

        IProject project = node.BoundItem as IProject;
        if (project == null && !string.IsNullOrWhiteSpace(node.ProjectPathHint)) {
            project = SD.ProjectService.CurrentSolution?.Projects.FirstOrDefault(candidate =>
                string.Equals(candidate.FileName?.ToString(), node.ProjectPathHint, StringComparison.OrdinalIgnoreCase));
        }

        SD.ProjectService.CurrentProject = project;
    }
    
    public bool ShowAllFiles {
        get => showAllFiles;
        set {
            if (SetProperty(ref showAllFiles, value)) {
                SD.PropertyService.Set("ProjectBrowser.ShowAll", value);
                RefreshSolutionTree();
            }
        }
    }

    public bool IsLoading {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    ProjectBrowserNodeContext IProjectBrowserHost.SelectedNode => SelectedNode?.ToContext();

    public void OpenSelected()
    {
        if (SelectedNode != null) {
            controller.Open(SelectedNode.ToContext());
        }
    }
    
    public void ShowProperties()
    {
        propertyContainer.SelectedObject = SelectedNode != null ? new ProjectBrowserNodeProperties(SelectedNode.ToContext()) : null;
        SD.Workbench.GetPad(typeof(PropertyPad))?.BringPadToFront();
    }

    public ContextMenu CreateContextMenu(ProjectBrowserNodeModel node)
    {
        var context = node.ToContext();
        return ICSharpCode.Core.Presentation.MenuService.CreateContextMenu(context, context.ContextMenuPath);
    }

    public void Dispose()
    {
        disposed = true;
        Interlocked.Increment(ref treeRefreshVersion);
        SD.ProjectService.SolutionOpened -= ProjectServiceChanged;
        SD.ProjectService.SolutionClosed -= ProjectServiceChanged;
        SD.ProjectService.ProjectItemAdded -= ProjectServiceChanged;
        SD.ProjectService.ProjectItemRemoved -= ProjectServiceChanged;
        ProjectTargetFrameworkService.ActiveTargetFrameworkChanged -= ProjectTargetFrameworkChanged;
        if (overlayService != null) {
            overlayService.Invalidated -= ProjectBrowserOverlayInvalidated;
        }
    }

    private void ProjectTargetFrameworkChanged(object sender, ProjectTargetFrameworkChangedEventArgs e)
    {
        RefreshSolutionTree();
    }

    void IProjectBrowserHost.RefreshSolutionTree()
    {
        RefreshSolutionTree();
    }

    void IProjectBrowserHost.OpenFileInWorkbench(string filePath)
    {
        if (File.Exists(filePath)) {
            SD.FileService.OpenFile(FileName.Create(filePath));
        }
    }

    string IProjectBrowserHost.ShowInputBox(string title, string prompt, string defaultValue)
    {
        return ServiceSingleton.GetRequiredService<IMessageService>().ShowInputBox(title, prompt, defaultValue);
    }

    bool IProjectBrowserHost.ConfirmDelete(string name)
    {
        return ServiceSingleton.GetRequiredService<IMessageService>().AskQuestion("Are you sure you want to delete '" + name + "'?");
    }

    void IProjectBrowserHost.CloseViewsForPath(string path)
    {
        var view = SD.FileService.GetOpenFile(FileName.Create(path));
        view?.WorkbenchWindow?.CloseWindow(force: true);
    }

    void IProjectBrowserHost.RetargetViewForRename(string oldPath, string newPath)
    {
        var view = SD.FileService.GetOpenFile(FileName.Create(oldPath));
        view?.WorkbenchWindow?.CloseWindow(force: true);
    }

    internal async Task WaitForCurrentRefreshAsync()
    {
        Task refresh;
        do {
            refresh = currentTreeRefresh;
            await refresh;
        } while (refresh != currentTreeRefresh);
    }

    private void RefreshSolutionTree()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) {
            _ = dispatcher.BeginInvoke(new Action(RefreshSolutionTree));
            return;
        }

        if (disposed) {
            return;
        }

        // Display the lightweight pane immediately. Tree construction also refreshes Git status,
        // which may start an external process, so none of that work belongs on the UI thread.
        var solution = SD.ProjectService.CurrentSolution;
        var includeAllFiles = ShowAllFiles;
        var gitStatusRoots = solution != null ? ProjectBrowserTreeBuilder.GetGitStatusRoots(solution) : Array.Empty<string>();
        int refreshVersion = Interlocked.Increment(ref treeRefreshVersion);
        RootNodes.Clear();
        SelectedNode = null;
        IsLoading = solution != null;

        currentTreeRefresh = BuildSolutionTreeAsync(solution, includeAllFiles, gitStatusRoots, refreshVersion);
    }

    private async Task BuildSolutionTreeAsync(ISolution solution, bool includeAllFiles, string[] gitStatusRoots, int refreshVersion)
    {
        try {
            await treeBuildGate.WaitAsync();
            try {
                // Coalesce the burst of project-item events commonly raised while a solution is
                // opening. Only the newest request needs to walk the project and the file system.
                if (refreshVersion != Volatile.Read(ref treeRefreshVersion) || disposed) {
                    return;
                }
                // Only external/file-system work runs in the background. SharpDevelop's project
                // collections are UI-thread-affine and must not be walked from Task.Run.
                await Task.Run(() => ProjectBrowserTreeBuilder.RefreshGitStatus(gitStatusRoots));

                // LibreWPF does not install a SynchronizationContext that guarantees an await
                // continuation returns to the WPF dispatcher. Explicitly marshal both the native
                // project-model walk and every ObservableCollection/property update.
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (refreshVersion != Volatile.Read(ref treeRefreshVersion) || disposed) {
                        return;
                    }

                    var root = ProjectBrowserTreeBuilder.BuildSolutionTree(
                        solution,
                        includeAllFiles,
                        refreshGitStatus: false);
                    if (root != null) {
                        RootNodes.Add(root);
                    }
                    IsLoading = false;
                });
            } finally {
                treeBuildGate.Release();
            }
        } catch (Exception ex) {
            LoggingService.Warn("Could not build the Solution Explorer tree.", ex);
            if (refreshVersion == Volatile.Read(ref treeRefreshVersion) && !disposed) {
                await Application.Current.Dispatcher.InvokeAsync(() => IsLoading = false);
            }
        }
    }
    
    private void ToggleShowAllFiles()
    {
        ShowAllFiles = !ShowAllFiles;
    }

    private void ProjectServiceChanged(object sender, EventArgs e)
    {
        RefreshSolutionTree();
    }

    private void ProjectBrowserOverlayInvalidated(object sender, ProjectBrowserOverlayInvalidatedEventArgs e)
    {
        // Only one file's Git status changed (Git status is re-checked on every save) -
        // refresh just that node's badge in place. Rebuilding the tree or refreshing every
        // badge would needlessly collapse the user's Solution Explorer navigation.
        if (string.IsNullOrEmpty(e.Path))
        {
            foreach (var root in RootNodes)
            {
                RefreshOverlayRecursive(root);
            }
            return;
        }
        foreach (var root in RootNodes)
        {
            if (RefreshOverlayForPath(root, e.Path))
            {
                return;
            }
        }
    }

    static void RefreshOverlayRecursive(ProjectBrowserNodeModel node)
    {
        node.OnOverlayChanged();
        foreach (var child in node.Children)
        {
            RefreshOverlayRecursive(child);
        }
    }

    static bool RefreshOverlayForPath(ProjectBrowserNodeModel node, string path)
    {
        if (string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase))
        {
            node.OnOverlayChanged();
            return true;
        }
        foreach (var child in node.Children)
        {
            if (RefreshOverlayForPath(child, path))
            {
                return true;
            }
        }
        return false;
    }
    
    private sealed class DelegateCommand : ICommand
    {
        readonly Action execute;
        readonly Func<bool> canExecute;
        
        public DelegateCommand(Action execute, Func<bool> canExecute = null)
        {
            this.execute = execute;
            this.canExecute = canExecute;
        }
        
        public event EventHandler CanExecuteChanged {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
        
        public bool CanExecute(object parameter)
        {
            return canExecute == null || canExecute();
        }
        
        public void Execute(object parameter)
        {
            execute();
        }
    }
}
