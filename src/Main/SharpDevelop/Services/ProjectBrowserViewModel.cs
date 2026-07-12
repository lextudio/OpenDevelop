using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Composition;
using System.Windows.Input;
using System.Windows.Media.Imaging;

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
    private ProjectBrowserNodeModel selectedNode;
    private bool showAllFiles;

    public ProjectBrowserViewModel()
    {
        Title = "Projects";
        ContentId = "ProjectBrowser";
        IsVisible = true;
        IsCloseable = true;
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
    
    public BitmapSource PropertiesIcon { get; } = PresentationResourceService.GetBitmapSource("Icons.16x16.PropertiesIcon");
    
    public BitmapSource ShowAllFilesIcon { get; } = PresentationResourceService.GetBitmapSource("ProjectBrowser.Toolbar.ShowHiddenFiles");
    
    public BitmapSource RefreshIcon { get; } = PresentationResourceService.GetBitmapSource("Icons.16x16.BrowserRefresh");
    
    public BitmapSource CollapseAllIcon { get; } = PresentationResourceService.GetBitmapSource("Icons.16x16.Collection");

    public ProjectBrowserNodeModel SelectedNode {
        get => selectedNode;
        set {
            if (SetProperty(ref selectedNode, value)) {
                propertyContainer.SelectedObject = value != null ? new ProjectBrowserNodeProperties(value.ToContext()) : null;
                CommandManager.InvalidateRequerySuggested();
            }
        }
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
        SD.ProjectService.SolutionOpened -= ProjectServiceChanged;
        SD.ProjectService.SolutionClosed -= ProjectServiceChanged;
        SD.ProjectService.ProjectItemAdded -= ProjectServiceChanged;
        SD.ProjectService.ProjectItemRemoved -= ProjectServiceChanged;
        if (overlayService != null) {
            overlayService.Invalidated -= ProjectBrowserOverlayInvalidated;
        }
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

    private void RefreshSolutionTree()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            RootNodes.Clear();
            var root = ProjectBrowserTreeBuilder.BuildSolutionTree(SD.ProjectService.CurrentSolution, ShowAllFiles);
            if (root != null) {
                RootNodes.Add(root);
            }
        });
    }
    
    private void ToggleShowAllFiles()
    {
        ShowAllFiles = !ShowAllFiles;
    }

    private void ProjectServiceChanged(object sender, EventArgs e)
    {
        RefreshSolutionTree();
    }

    private void ProjectBrowserOverlayInvalidated(object sender, EventArgs e)
    {
        RefreshSolutionTree();
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
