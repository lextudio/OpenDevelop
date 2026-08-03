using System;
using System.Collections.ObjectModel;
using System.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Modern (doc/technotes/ilspy.md "Docking and layout replacement" item 4, 2026-08-03)
/// replacement for the legacy AddInTree-registered <see cref="ErrorListPad"/>: shows build
/// errors/warnings/messages, same behavior as before, just as a <see cref="ToolPaneModel"/>.
/// <see cref="ErrorListToolbarCommands"/>'s toggle buttons resolve this model directly via MEF
/// rather than a static <c>Instance</c>, same as <see cref="TaskListViewModel"/>.
/// </summary>
[Export(typeof(ErrorListViewModel))]
[Export("ToolPane", typeof(ToolPaneModel))]
[Shared]
internal sealed class ErrorListViewModel : ToolPaneModel
{
    public const string DefaultContextMenuAddInTreeEntry = "/SharpDevelop/Pads/ErrorList/TaskContextMenu";

    readonly Grid contentPanel = new Grid();
    readonly ListView errorView = new ListView();
    readonly ObservableCollection<SDTask> errors = new ObservableCollection<SDTask>();

    ToolBar toolBar;
    Properties properties;
    bool subscribed;

    public bool ShowErrors {
        get { EnsureSubscribed(); return EnsureProperties().Get<bool>("ShowErrors", true); }
        set {
            EnsureSubscribed();
            EnsureProperties().Set<bool>("ShowErrors", value);
            InternalShowResults();
        }
    }

    public bool ShowMessages {
        get { EnsureSubscribed(); return EnsureProperties().Get<bool>("ShowMessages", true); }
        set {
            EnsureSubscribed();
            EnsureProperties().Set<bool>("ShowMessages", value);
            InternalShowResults();
        }
    }

    public bool ShowWarnings {
        get { EnsureSubscribed(); return EnsureProperties().Get<bool>("ShowWarnings", true); }
        set {
            EnsureSubscribed();
            EnsureProperties().Set<bool>("ShowWarnings", value);
            InternalShowResults();
        }
    }

    public ErrorListViewModel()
    {
        Title = "Error List";
        ContentId = "ErrorList";
        IsVisible = true; // Matches the legacy Pad's `defaultPosition = "Bottom"`.
        IsCloseable = true;
        LegacyPadClass = typeof(ErrorListPad).FullName;
        Content = contentPanel;
    }

    Properties EnsureProperties() => properties ??= ICSharpCode.Core.PropertyService.NestedProperties("ErrorListPad");

    /// <summary>
    /// Builds the toolbar/list content and subscribes to <c>TaskService</c>/<c>SD.BuildService</c>/
    /// <c>SD.ProjectService</c> on first real use rather than in the constructor - same
    /// early-startup hazard already found and fixed for <see cref="TaskListViewModel"/> et al.
    /// Deferred to every externally-reachable entry point, not only <see cref="Show"/>, since this
    /// pad defaults visible (like <see cref="PropertyPadViewModel"/>) - nothing calls Show() on an
    /// already-visible MEF-composed pane.
    /// </summary>
    internal void EnsureSubscribed()
    {
        if (subscribed || SD.Services.GetService(typeof(IWorkbench)) == null)
            return;
        subscribed = true;

        TaskService.Cleared += TaskServiceCleared;
        TaskService.Added += TaskServiceAdded;
        TaskService.Removed += TaskServiceRemoved;
        TaskService.InUpdateChanged += delegate {
            if (!TaskService.InUpdate)
                InternalShowResults();
        };

        SD.BuildService.BuildFinished += ProjectServiceEndBuild;
        SD.ProjectService.SolutionOpened += OnSolutionOpen;
        SD.ProjectService.SolutionClosed += OnSolutionClosed;
        foreach (SDTask t in TaskService.Tasks.Where(t => t.TaskType != TaskType.Comment))
            errors.Add(t);

        toolBar = ToolBarService.CreateToolBar(contentPanel, this, "/SharpDevelop/Pads/ErrorList/Toolbar");

        contentPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        contentPanel.Children.Add(toolBar);
        contentPanel.Children.Add(errorView);
        Grid.SetRow(errorView, 1);
        errorView.ItemsSource = errors;
        errorView.MouseDoubleClick += ErrorViewMouseDoubleClick;
        errorView.Style = (Style)new TaskViewResources()["TaskListView"];
        errorView.ContextMenu = MenuService.CreateContextMenu(errorView, DefaultContextMenuAddInTreeEntry);

        errorView.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, ExecuteCopy, CanExecuteCopy));
        errorView.CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll, ExecuteSelectAll, CanExecuteSelectAll));

        InternalShowResults();
    }

    public override void Show()
    {
        EnsureSubscribed();
        base.Show();
    }

    void ErrorViewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        SDTask task = errorView.SelectedItem as SDTask;
        var item = errorView.ItemContainerGenerator.ContainerFromItem(task) as ListViewItem;
        UIElement element = e.MouseDevice.DirectlyOver as UIElement;
        if (task != null && task.FileName != null && element != null && item != null
            && element.IsDescendantOf(item)) {
            SD.FileService.JumpToFilePosition(task.FileName, task.Line, task.Column);
        }
    }

    void OnSolutionOpen(object sender, SolutionEventArgs e)
    {
        errors.Clear();
        MenuService.UpdateText(toolBar.Items);
    }

    void OnSolutionClosed(object sender, EventArgs e)
    {
        errors.Clear();
        MenuService.UpdateText(toolBar.Items);
    }

    void ProjectServiceEndBuild(object sender, EventArgs e)
    {
        if (TaskService.TaskCount > 0 && Project.BuildOptions.ShowErrorListAfterBuild) {
            SD.MainThread.InvokeIfRequired(() => {
                SD.Workbench.GetPad(typeof(ErrorListPad)).BringPadToFront();
            });
        }
    }

    void AddTask(SDTask task)
    {
        switch (task.TaskType) {
            case TaskType.Warning:
                if (!ShowWarnings)
                    return;
                break;
            case TaskType.Error:
                if (!ShowErrors)
                    return;
                break;
            case TaskType.Message:
                if (!ShowMessages)
                    return;
                break;
            default:
                return;
        }

        errors.Add(task);
    }

    void TaskServiceCleared(object sender, EventArgs e)
    {
        if (TaskService.InUpdate)
            return;
        errors.Clear();
        MenuService.UpdateText(toolBar.Items);
    }

    void TaskServiceAdded(object sender, TaskEventArgs e)
    {
        if (TaskService.InUpdate)
            return;
        AddTask(e.Task);
        MenuService.UpdateText(toolBar.Items);
    }

    void TaskServiceRemoved(object sender, TaskEventArgs e)
    {
        if (TaskService.InUpdate)
            return;
        errors.Remove(e.Task);
        MenuService.UpdateText(toolBar.Items);
    }

    void InternalShowResults()
    {
        errors.Clear();
        foreach (SDTask task in TaskService.Tasks)
            AddTask(task);
        MenuService.UpdateText(toolBar.Items);
    }

    void CanExecuteCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = errorView.SelectedItem != null;
    }

    void ExecuteCopy(object sender, ExecutedRoutedEventArgs e)
    {
        TaskViewResources.CopySelectionToClipboard(errorView);
    }

    void CanExecuteSelectAll(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = true;
    }

    void ExecuteSelectAll(object sender, ExecutedRoutedEventArgs e)
    {
        errorView.SelectAll();
    }
}
