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

[Export(typeof(SolutionExplorerViewModel))]
[Export("ToolPane", typeof(ToolPaneModel))]
[Shared]
internal sealed class SolutionExplorerViewModel : ToolPaneModel, ISolutionExplorerHost, IDisposable
{
    private readonly ISolutionExplorerController controller = ServiceSingleton.GetRequiredService<ISolutionExplorerController>();
    private SolutionExplorerNodeModel selectedNode;

    public SolutionExplorerViewModel()
    {
        Title = "Solution Explorer";
        ContentId = "SolutionExplorer";
        IsVisible = true;
        IsCloseable = true;
        Content = new SolutionExplorerView { DataContext = this };

        controller.BindHost(this);

        SD.ProjectService.SolutionOpened += ProjectServiceChanged;
        SD.ProjectService.SolutionClosed += ProjectServiceChanged;
        SD.ProjectService.ProjectItemAdded += ProjectServiceChanged;
        SD.ProjectService.ProjectItemRemoved += ProjectServiceChanged;

        RefreshSolutionTree();
    }

    public ObservableCollection<SolutionExplorerNodeModel> RootNodes { get; } = new ObservableCollection<SolutionExplorerNodeModel>();

    public SolutionExplorerNodeModel SelectedNode {
        get => selectedNode;
        set => SetProperty(ref selectedNode, value);
    }

    SolutionExplorerNodeContext ISolutionExplorerHost.SelectedNode => SelectedNode?.ToContext();

    public void OpenSelected()
    {
        if (SelectedNode != null) {
            controller.Open(SelectedNode.ToContext());
        }
    }

    public ContextMenu CreateContextMenu(SolutionExplorerNodeModel node)
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

    void ISolutionExplorerHost.RefreshSolutionTree()
    {
        RefreshSolutionTree();
    }

    void ISolutionExplorerHost.OpenFileInWorkbench(string filePath)
    {
        if (File.Exists(filePath)) {
            SD.FileService.OpenFile(FileName.Create(filePath));
        }
    }

    string ISolutionExplorerHost.ShowInputBox(string title, string prompt, string defaultValue)
    {
        return ServiceSingleton.GetRequiredService<IMessageService>().ShowInputBox(title, prompt, defaultValue);
    }

    bool ISolutionExplorerHost.ConfirmDelete(string name)
    {
        return ServiceSingleton.GetRequiredService<IMessageService>().AskQuestion("Are you sure you want to delete '" + name + "'?");
    }

    void ISolutionExplorerHost.CloseViewsForPath(string path)
    {
        var view = SD.FileService.GetOpenFile(FileName.Create(path));
        view?.WorkbenchWindow?.CloseWindow(force: true);
    }

    void ISolutionExplorerHost.RetargetViewForRename(string oldPath, string newPath)
    {
        var view = SD.FileService.GetOpenFile(FileName.Create(oldPath));
        view?.WorkbenchWindow?.CloseWindow(force: true);
    }

    private void RefreshSolutionTree()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            RootNodes.Clear();
            var sol = SD.ProjectService.CurrentSolution;
            LoggingService.Info("RefreshSolutionTree: sol=" + (sol != null ? sol.Name + " projects=" + sol.Projects.Count : "null"));
            var root = SolutionExplorerTreeBuilder.BuildSolutionTree(sol);
            LoggingService.Info("RefreshSolutionTree: root=" + (root != null ? root.Children.Count.ToString() : "null")
                + ", descendants=" + (root != null ? CountDescendants(root).ToString() : "0"));
            if (root != null) {
                RootNodes.Add(root);
            }
        });
    }
    
    private static int CountDescendants(SolutionExplorerNodeModel node)
    {
        var count = node.Children.Count;
        foreach (var child in node.Children) {
            count += CountDescendants(child);
        }
        return count;
    }

    private void ProjectServiceChanged(object sender, EventArgs e)
    {
        LoggingService.Info("SolutionExplorerViewModel.ProjectServiceChanged: sender=" + sender?.GetType().Name + " e=" + e?.GetType().Name + " CurrentSolution=" + (SD.ProjectService.CurrentSolution != null ? "set" : "null"));
        RefreshSolutionTree();
    }
}
