using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ICSharpCode.SharpDevelop.Services;

/// <summary>Projects-specific data and commands hosted in the reusable StickyTreeView.</summary>
internal partial class ProjectBrowserView : UserControl
{
    private static readonly HashSet<ProjectBrowserNodeKind> PinnableKinds = new() {
        ProjectBrowserNodeKind.Solution,
        ProjectBrowserNodeKind.Project,
        ProjectBrowserNodeKind.Folder,
        ProjectBrowserNodeKind.DependenciesFolder,
        ProjectBrowserNodeKind.ReferencesFolder,
        ProjectBrowserNodeKind.PackagesFolder,
        ProjectBrowserNodeKind.GhostFolder,
    };

    public ProjectBrowserView()
    {
        InitializeComponent();
        treeView.StickyItemPredicate = item => item is ProjectBrowserNodeModel node && PinnableKinds.Contains(node.Kind);
        treeView.StickyRowClicked += (_, item) => treeView.SelectAndBringIntoView(item);
        treeView.Tree.MouseDoubleClick += TreeViewOnMouseDoubleClick;
        treeView.Tree.PreviewMouseRightButtonDown += TreeViewOnPreviewMouseRightButtonDown;
        treeView.Tree.SelectedItemChanged += TreeViewOnSelectedItemChanged;
    }

    internal Controls.StickyTreeView StickyTree => treeView;
    public object InitiallyFocusedControl => treeView.Tree;
    private ProjectBrowserViewModel ViewModel => (ProjectBrowserViewModel)DataContext;

    private void TreeViewOnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => ViewModel.SelectedNode = e.NewValue as ProjectBrowserNodeModel;

    private void TreeViewOnMouseDoubleClick(object sender, MouseButtonEventArgs e) => ViewModel.OpenSelected();

    private void TreeViewOnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;
        var item = FindAncestor<TreeViewItem>(source);
        if (item?.DataContext is not ProjectBrowserNodeModel node)
            return;
        item.IsSelected = true;
        e.Handled = true;
        var menu = ViewModel.CreateContextMenu(node);
        menu.PlacementTarget = item;
        menu.IsOpen = true;
    }

    private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null) {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

}
