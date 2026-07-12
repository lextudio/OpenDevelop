using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ICSharpCode.SharpDevelop.Services;

internal partial class ProjectBrowserView : UserControl
{
    public ProjectBrowserView()
    {
        InitializeComponent();
    }

    public object InitiallyFocusedControl => treeView;

    private ProjectBrowserViewModel ViewModel => (ProjectBrowserViewModel)DataContext;

    private void TreeViewOnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        ViewModel.SelectedNode = e.NewValue as ProjectBrowserNodeModel;
    }

    private void TreeViewOnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ViewModel.OpenSelected();
    }

    private void TreeViewOnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) {
            return;
        }

        var item = FindAncestor<TreeViewItem>(source);
        if (item?.DataContext is not ProjectBrowserNodeModel node) {
            return;
        }

        item.IsSelected = true;
        e.Handled = true;

        var menu = ViewModel.CreateContextMenu(node);
        menu.PlacementTarget = item;
        menu.IsOpen = true;
    }

    private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null) {
            if (current is T match) {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
