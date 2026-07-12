using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Composition;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.ViewModels;

namespace ICSharpCode.SharpDevelop.Services;

[Export(typeof(ProjectBrowserViewModel))]
[Export("ToolPane", typeof(ToolPaneModel))]
[Shared]
internal sealed class ProjectBrowserViewModel : ToolPaneModel, IProjectBrowserHost, IDisposable
{
    private readonly IProjectBrowserController controller = ServiceSingleton.GetRequiredService<IProjectBrowserController>();
    private ProjectBrowserNodeModel selectedNode;

    public ProjectBrowserViewModel()
    {
        Title = "Project Browser";
        ContentId = "ProjectBrowser";
        IsVisible = true;
        IsCloseable = true;
        Content = new ProjectBrowserView { DataContext = this };

        controller.BindHost(this);

        SD.ProjectService.SolutionOpened += ProjectServiceChanged;
        SD.ProjectService.SolutionClosed += ProjectServiceChanged;
        SD.ProjectService.ProjectItemAdded += ProjectServiceChanged;
        SD.ProjectService.ProjectItemRemoved += ProjectServiceChanged;

        RefreshSolutionTree();
    }

    public ObservableCollection<ProjectBrowserNodeModel> RootNodes { get; } = new ObservableCollection<ProjectBrowserNodeModel>();

    public ProjectBrowserNodeModel SelectedNode {
        get => selectedNode;
        set => SetProperty(ref selectedNode, value);
    }

    ProjectBrowserNodeContext IProjectBrowserHost.SelectedNode => SelectedNode?.ToContext();

    public void OpenSelected()
    {
        if (SelectedNode != null) {
            controller.Open(SelectedNode.ToContext());
        }
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
            var root = ProjectBrowserTreeBuilder.BuildSolutionTree(SD.ProjectService.CurrentSolution);
            if (root != null) {
                RootNodes.Add(root);
            }
        });
    }

    private void ProjectServiceChanged(object sender, EventArgs e)
    {
        RefreshSolutionTree();
    }
}
