using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Composition;
using System.Linq;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.Core.Presentation;
using ICSharpCode.TypeSystem;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Parser;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Modern (doc/technotes/ilspy.md "Docking and layout replacement" item 4, 2026-08-03)
/// replacement for the legacy AddInTree-registered <see cref="TaskListPad"/>: shows the comment
/// task list (TODO/FIXME/... markers found while parsing), same behavior as before, just as a
/// <see cref="ToolPaneModel"/>. <see cref="TaskListPad"/> keeps the static <c>Instance</c> surface
/// <c>TaskListPadCommands.cs</c>'s toolbar items already depend on, forwarding to this model.
/// </summary>
[Export(typeof(TaskListViewModel))]
[Export("ToolPane", typeof(ToolPaneModel))]
[Shared]
internal sealed class TaskListViewModel : ToolPaneModel
{
    public const string DefaultContextMenuAddInTreeEntry = "/SharpDevelop/Pads/TaskList/TaskContextMenu";

    readonly Dictionary<string, bool> displayedTokens = new Dictionary<string, bool>();
    readonly ObservableCollection<SDTask> tasks = new ObservableCollection<SDTask>();
    readonly Grid contentPanel = new Grid();
    readonly ListView taskView = new ListView();

    IUnresolvedTypeDefinition oldClass;
    int selectedScopeIndex;
    bool subscribed;

    public Dictionary<string, bool> DisplayedTokens => displayedTokens;

    public bool IsInitialized => subscribed;

    public int SelectedScopeIndex {
        get => selectedScopeIndex;
        set {
            selectedScopeIndex = value;
            if (subscribed)
                UpdateItems();
        }
    }

    public TaskListViewModel()
    {
        Title = "Task List";
        ContentId = "TaskList";
        IsVisible = true; // Matches the legacy Pad's `defaultPosition = "Bottom"`.
        IsCloseable = true;
        PreferredDockSide = ICSharpCode.SharpDevelop.ViewModels.PreferredDockSide.Bottom;
        LegacyPadClass = typeof(TaskListPad).FullName;
        Content = contentPanel;
    }

    /// <summary>
    /// Builds the toolbar/list content and subscribes to <c>TaskService</c>/<c>SD.Workbench</c>/
    /// <c>SD.ProjectService</c>/<c>SD.ParserService</c> on first real use rather than in the
    /// constructor - same early-startup hazard already found and fixed for
    /// <see cref="OutlineViewModel"/>/<see cref="DefinitionViewViewModel"/> (this model is
    /// constructed eagerly by MEF, before those services are registered).
    /// </summary>
    internal void EnsureSubscribed()
    {
        if (subscribed || SD.Services.GetService(typeof(IWorkbench)) == null)
            return;
        subscribed = true;

        TaskService.Cleared += TaskServiceCleared;
        TaskService.Added += TaskServiceAdded;
        TaskService.Removed += TaskServiceRemoved;
        TaskService.InUpdateChanged += TaskServiceInUpdateChanged;
        foreach (SDTask t in TaskService.CommentTasks)
            tasks.Add(t);

        InitializePadContent();

        SD.Workbench.ActiveViewContentChanged += WorkbenchActiveViewContentChanged;
        if (SD.Workbench.ActiveViewContent != null) {
            UpdateItems();
            WorkbenchActiveViewContentChanged(null, null);
        }

        SD.ProjectService.SolutionOpened += OnSolutionOpen;
        SD.ProjectService.SolutionClosed += OnSolutionClosed;
        SD.ProjectService.CurrentProjectChanged += ProjectServiceCurrentProjectChanged;
    }

    public override void Show()
    {
        EnsureSubscribed();
        base.Show();
    }

    void ProjectServiceCurrentProjectChanged(object sender, EventArgs e)
    {
        if (subscribed)
            UpdateItems();
    }

    void WorkbenchActiveViewContentChanged(object sender, EventArgs e)
    {
        if (subscribed)
            UpdateItems();

        ITextEditor editor = SD.GetActiveViewContentService<ITextEditor>();
        if (editor != null) {
            editor.Caret.LocationChanged -= CaretPositionChanged;
            editor.Caret.LocationChanged += CaretPositionChanged;
        }
    }

    void CaretPositionChanged(object sender, EventArgs e)
    {
        if (selectedScopeIndex > 2) {
            var current = GetCurrentClass();
            if (oldClass == null)
                oldClass = current;
            if (current != null && current.ReflectionName != oldClass.ReflectionName)
                UpdateItems();
        }
    }

    void TaskServiceInUpdateChanged(object sender, EventArgs e)
    {
        if (!TaskService.InUpdate)
            UpdateItems();
    }

    void InitializePadContent()
    {
        IReadOnlyList<string> tokens = SD.ParserService.TaskListTokens;

        foreach (string token in tokens) {
            if (!displayedTokens.ContainsKey(token))
                displayedTokens.Add(token, true);
        }

        var toolBar = ToolBarService.CreateToolBar(contentPanel, this, "/SharpDevelop/Pads/TaskList/Toolbar");
        var items = (IList)toolBar.ItemsSource;

        foreach (string token in tokens) {
            items.Add(new Separator());
            items.Add(new TaskListTokensToolbarCheckBox(token));
        }

        toolBar.Items.OfType<ComboBox>().ForEach(b => b.MinWidth = 75);

        contentPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        contentPanel.Children.Add(toolBar);
        contentPanel.Children.Add(taskView);
        Grid.SetRow(taskView, 1);

        taskView.ItemsSource = tasks;
        taskView.MouseDoubleClick += TaskViewMouseDoubleClick;
        taskView.Style = (Style)new TaskViewResources()["TaskListView"];
        taskView.ContextMenu = MenuService.CreateContextMenu(taskView, DefaultContextMenuAddInTreeEntry);

        taskView.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, ExecuteCopy, CanExecuteCopy));
        taskView.CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll, ExecuteSelectAll, CanExecuteSelectAll));
    }

    void TaskViewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        SDTask task = taskView.SelectedItem as SDTask;
        var item = taskView.ItemContainerGenerator.ContainerFromItem(task) as ListViewItem;
        UIElement element = e.MouseDevice.DirectlyOver as UIElement;
        if (task != null && task.FileName != null && element != null && item != null
            && element.IsDescendantOf(item)) {
            SD.FileService.JumpToFilePosition(task.FileName, task.Line, task.Column);
        }
    }

    public void UpdateItems()
    {
        tasks.Clear();
        foreach (SDTask t in TaskService.CommentTasks)
            AddItem(t);
    }

    void AddItem(SDTask item)
    {
        foreach (KeyValuePair<string, bool> pair in displayedTokens) {
            if (item.Description.StartsWith(pair.Key, StringComparison.Ordinal) && pair.Value && IsInScope(item))
                tasks.Add(item);
        }
    }

    bool IsInScope(SDTask item)
    {
        var current = GetCurrentClass();
        var itemClass = GetCurrentClass(item);

        switch (selectedScopeIndex) {
            case 0:
                if (ProjectService.OpenSolution != null) {
                    foreach (IProject proj in ProjectService.OpenSolution.Projects) {
                        if (proj.FindFile(item.FileName) != null)
                            return true;
                    }
                }
                return false;
            case 1:
                return ProjectService.CurrentProject != null && ProjectService.CurrentProject.FindFile(item.FileName) != null;
            case 2:
                return SD.Workbench.ViewContentCollection.Select(vc => vc.GetService<ITextEditor>()).Any(editor => editor != null && item.FileName == editor.FileName);
            case 3:
                return SD.Workbench.ActiveViewContent != null && SD.Workbench.ActiveViewContent.PrimaryFileName == item.FileName;
            case 4:
                return current != null && itemClass != null && current.Namespace == itemClass.Namespace;
            case 5:
                return current != null && itemClass != null && current == itemClass;
        }

        return true;
    }

    IUnresolvedTypeDefinition GetCurrentClass()
    {
        if (SD.Workbench.ActiveViewContent == null || SD.Workbench.ActiveViewContent.PrimaryFileName == null)
            return null;

        IUnresolvedFile parseInfo = SD.ParserService.GetExistingUnresolvedFile(SD.Workbench.ActiveViewContent.PrimaryFileName);
        if (parseInfo != null) {
            IPositionable positionable = SD.Workbench.ActiveViewContent.GetService<IPositionable>();
            if (positionable != null) {
                var c = parseInfo.GetInnermostTypeDefinition(positionable.Line, positionable.Column);
                if (c != null)
                    return c;
            }
        }

        return null;
    }

    IUnresolvedTypeDefinition GetCurrentClass(SDTask item)
    {
        IUnresolvedFile parseInfo = SD.ParserService.GetExistingUnresolvedFile(item.FileName);
        if (parseInfo != null) {
            var c = parseInfo.GetInnermostTypeDefinition(item.Line, item.Column);
            if (c != null)
                return c;
        }

        return null;
    }

    void OnSolutionOpen(object sender, SolutionEventArgs e) => tasks.Clear();

    void OnSolutionClosed(object sender, EventArgs e) => tasks.Clear();

    void TaskServiceCleared(object sender, EventArgs e) => tasks.Clear();

    void TaskServiceAdded(object sender, TaskEventArgs e)
    {
        if (e.Task.TaskType == TaskType.Comment)
            AddItem(e.Task);
    }

    void TaskServiceRemoved(object sender, TaskEventArgs e)
    {
        if (e.Task.TaskType == TaskType.Comment)
            tasks.Remove(e.Task);
    }

    void CanExecuteCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = taskView.SelectedItem != null;
    }

    void ExecuteCopy(object sender, ExecutedRoutedEventArgs e)
    {
        TaskViewResources.CopySelectionToClipboard(taskView);
    }

    void CanExecuteSelectAll(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = true;
    }

    void ExecuteSelectAll(object sender, ExecutedRoutedEventArgs e)
    {
        taskView.SelectAll();
    }
}
