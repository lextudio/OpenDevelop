using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ICSharpCode.SharpDevelop.Services;

internal partial class ProjectBrowserView : UserControl
{
    /// <summary>One pinned row: the node to draw plus the indent that lines it up with the real row.</summary>
    private sealed record StickyHeaderRow(ProjectBrowserNodeModel Node, Thickness Indent);

    // Any node kind that can contain other nodes is a candidate to pin - Dependencies/References/
    // Packages/plain folders included, not just Solution/Project. A leaf kind (File, Reference,
    // ...) never appears here because it never has children for CollectPinnedRows to recurse into,
    // but listing this explicitly keeps the intent visible instead of relying on that as an
    // incidental proof.
    private static readonly HashSet<ProjectBrowserNodeKind> PinnableKinds = new() {
        ProjectBrowserNodeKind.Solution,
        ProjectBrowserNodeKind.Project,
        ProjectBrowserNodeKind.Folder,
        ProjectBrowserNodeKind.DependenciesFolder,
        ProjectBrowserNodeKind.ReferencesFolder,
        ProjectBrowserNodeKind.PackagesFolder,
        ProjectBrowserNodeKind.GhostFolder,
    };

    // Same default as VS Code's workbench.tree.stickyScrollMaxItemCount. When the ancestor chain
    // is longer than this, the innermost rows are kept and the outermost (typically Solution) are
    // dropped first - the deepest folder is the context that matters most once you are that deep.
    private const int MaxStickyRows = 5;

    private readonly List<StickyHeaderRow> stickyHeaderRows = new();
    private ScrollViewer treeScrollViewer;
    private double lastScrollOffset;

    public ProjectBrowserView()
    {
        InitializeComponent();
        DataContextChanged += ProjectBrowserViewDataContextChanged;
        treeView.Loaded += TreeViewOnLoaded;
    }

    private void TreeViewOnLoaded(object sender, RoutedEventArgs e)
    {
        if (!EnsureScrollViewer()) {
            // The template may not be applied yet on the first Loaded; retry on the next layout
            // pass rather than giving up, which would disable the feature for the whole session.
            treeView.LayoutUpdated += TreeViewOnLayoutUpdatedUntilScrollViewerFound;
        }
    }

    private void TreeViewOnLayoutUpdatedUntilScrollViewerFound(object sender, System.EventArgs e)
    {
        if (EnsureScrollViewer()) {
            treeView.LayoutUpdated -= TreeViewOnLayoutUpdatedUntilScrollViewerFound;
        }
    }

    private bool EnsureScrollViewer()
    {
        if (treeScrollViewer != null) {
            return true;
        }

        treeView.ApplyTemplate();
        treeScrollViewer = FindDescendant<ScrollViewer>(treeView);
        if (treeScrollViewer == null) {
            return false;
        }

        // ScrollChanged also fires when the extent changes, so expanding or collapsing a node
        // re-evaluates the pinned rows without needing a separate LayoutUpdated subscription.
        treeScrollViewer.ScrollChanged += (_, _) => ScheduleUpdateStickyHeaders();
        ScheduleUpdateStickyHeaders();
        return true;
    }

    /// <summary>
    /// Defers to Render priority - after all layout passes and scroll-state bookkeeping have
    /// fully settled. Loaded priority was too early: UpdateLayout() inside a ScrollChanged
    /// handler doesn't guarantee the ScrollViewer's internal offset tracking has caught up,
    /// so TranslatePoint returned stale Y values and the SameRows guard suppressed the rebuild.
    /// Render priority eliminates both problems without needing a forced UpdateLayout() call.
    /// </summary>
    private void ScheduleUpdateStickyHeaders()
        => Dispatcher.BeginInvoke(DispatcherPriority.Render, new System.Action(UpdateStickyHeaders));

    /// <summary>
    /// Pins the container rows (solution, project, Dependencies, folders, ...) that the topmost
    /// visible row sits underneath.
    /// </summary>
    private void UpdateStickyHeaders()
    {
        var pinned = new List<StickyHeaderRow>();
        var currentOffset = treeScrollViewer?.VerticalOffset ?? 0;
        if (treeScrollViewer != null) {
            CollectPinnedRows(treeView, pinned, 0);
            if (pinned.Count > MaxStickyRows) {
                pinned.RemoveRange(0, pinned.Count - MaxStickyRows);
            }
        }

        // SameRows can miss cases where the scroll offset moved but the pinned set happened
        // to produce the same node+indent list (e.g. scrolling within the same project).
        // Track the offset and force a rebuild when it changes beyond a small tolerance.
        bool offsetChanged = Math.Abs(currentOffset - lastScrollOffset) > 0.5;
        if (!offsetChanged && SameRows(pinned)) {
            return;
        }

        lastScrollOffset = currentOffset;
        stickyHeaderRows.Clear();
        stickyHeaderRows.AddRange(pinned);

        // Built as plain UIElements with Margin assigned directly, not through an
        // ItemsControl.ItemTemplate binding: binding Margin to each row's Thickness left every
        // row unindented (verified via the live control tree - Margin stayed 0 for every row
        // regardless of the bound value), so this sidesteps data-binding for that property
        // entirely rather than chase why it silently failed on this stack.
        var rowTemplate = (DataTemplate)Resources["ProjectBrowserNodeRow"];
        stickyHeaders.Items.Clear();
        foreach (var row in pinned) {
            var content = new ContentPresenter {
                Margin = row.Indent,
                Content = row.Node,
                ContentTemplate = rowTemplate,
            };
            var rowBorder = new Border { Background = Brushes.Transparent, Child = content };
            rowBorder.MouseLeftButtonDown += (_, _) => JumpToRealRow(row.Node);
            stickyHeaders.Items.Add(rowBorder);
        }

        stickyHeaderPanel.Visibility = pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool SameRows(List<StickyHeaderRow> candidate)
    {
        if (candidate.Count != stickyHeaderRows.Count) {
            return false;
        }

        for (int i = 0; i < candidate.Count; i++) {
            if (!Equals(candidate[i], stickyHeaderRows[i])) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Walks down the realized containers collecting the row at each level that the user has
    /// scrolled "inside" - the one whose own header would render underneath the sticky rows
    /// already committed by its ancestors - so it is what needs pinning next.
    /// </summary>
    /// <param name="coverThreshold">
    /// How far down (in treeScrollViewer-relative pixels) the sticky rows built by ancestor
    /// levels already reach. A row whose header bottom is still above this line is not just
    /// "off the top of the scroll viewport" - it is specifically hidden *underneath the sticky
    /// overlay itself*, which starts at 0 same as the viewport but is only as tall as the rows
    /// pinned so far. Comparing against a fixed 0 (i.e. only the viewport edge) instead of this
    /// growing line is what previously picked a project that had already scrolled fully past
    /// (its header technically still within the viewport, just hidden under the sticky panel's
    /// own later rows) instead of the one actually sitting at the boundary.
    /// </param>
    /// <remarks>
    /// Deliberately geometry-based rather than hit-testing the top pixel: this only ever inspects
    /// the handful of containers on the path to the viewport, and it does not depend on
    /// hit-testing behaviour, which is the part of the portable WPF stack most likely to differ.
    ///
    /// A sibling only qualifies as "what needs pinning" when it straddles the cover line: its own
    /// header must have scrolled up to (at least partially) hide behind the sticky rows built so
    /// far (headerBottom &lt;= coverThreshold), AND its subtree must still extend past that same
    /// line (itemBottom &gt; coverThreshold) - i.e. the user is still scrolled "inside" it. A
    /// sibling whose header is merely off past the line but whose ENTIRE subtree has also already
    /// scrolled past (itemBottom &lt;= coverThreshold too) is stale: the user has moved on
    /// entirely, so it is skipped in favour of a later sibling - checking headerBottom alone
    /// previously kept such a stale project pinned even once its successor's header was fully
    /// visible on screen with nothing left to pin it for.
    ///
    /// A collapsed container can never satisfy this straddle test - its container height equals
    /// its header height, so headerBottom and itemBottom coincide and there is no line position
    /// where header is hidden but the (nonexistent) subtree still extends past it. It is excluded
    /// explicitly all the same, both for clarity and as a defensive backstop.
    /// </remarks>
    private void CollectPinnedRows(ItemsControl parent, List<StickyHeaderRow> pinned, double coverThreshold)
    {
        foreach (var item in parent.Items) {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) {
                continue;
            }

            if (GetHeaderElement(container) is not FrameworkElement header) {
                continue;
            }

            var headerBottom = header.TranslatePoint(new Point(0, header.ActualHeight), treeScrollViewer).Y;
            if (headerBottom > coverThreshold) {
                // This row's header would render below the sticky overlay built so far - nothing
                // here or later (siblings are laid out top-to-bottom) needs pinning.
                return;
            }

            var itemBottom = container.TranslatePoint(new Point(0, container.ActualHeight), treeScrollViewer).Y;
            if (itemBottom <= coverThreshold || !container.IsExpanded) {
                // Header hidden behind the sticky rows built so far, but nothing of this row is
                // still ahead of that line (or there is nothing to be "inside" - it is collapsed):
                // the user has scrolled entirely past it, so it is stale. Move on to the next
                // sibling instead of latching onto it.
                continue;
            }

            if (container.DataContext is ProjectBrowserNodeModel node && PinnableKinds.Contains(node.Kind)) {
                // Measured against treeView specifically: it is a genuine visual ancestor of
                // header, unlike stickyHeaderPanel (a Grid sibling), so this never needs WPF to
                // walk up to a common ancestor and back down through an unrelated branch.
                // stickyHeaderPanel occupies the same Grid cell at the same horizontal position,
                // so no further correction is needed to use this value as its own Margin.
                var indent = header.TranslatePoint(default, treeView).X;
                pinned.Add(new StickyHeaderRow(node, new Thickness(indent, 0, 0, 0)));
                CollectPinnedRows(container, pinned, coverThreshold + header.ActualHeight);
            }

            return;
        }
    }

    private static FrameworkElement GetHeaderElement(TreeViewItem item)
        => item.Template?.FindName("PART_Header", item) as FrameworkElement;

    /// <summary>Clicking a pinned row jumps back to the real one, the way VS Code's sticky scroll does.</summary>
    private void JumpToRealRow(ProjectBrowserNodeModel node)
    {
        if (FindContainer(treeView, node) is TreeViewItem container) {
            container.BringIntoView();
            container.IsSelected = true;
        }
    }

    /// <summary>The overlay sits over the tree, so the wheel has to keep scrolling what is underneath.</summary>
    private void StickyHeadersOnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (treeScrollViewer == null) {
            return;
        }

        treeScrollViewer.ScrollToVerticalOffset(treeScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static TreeViewItem FindContainer(ItemsControl parent, ProjectBrowserNodeModel node)
    {
        foreach (var item in parent.Items) {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container) {
                continue;
            }

            if (ReferenceEquals(item, node)) {
                return container;
            }

            if (FindContainer(container, node) is TreeViewItem match) {
                return match;
            }
        }

        return null;
    }

    private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) {
            return match;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++) {
            if (FindDescendant<T>(VisualTreeHelper.GetChild(root, i)) is T found) {
                return found;
            }
        }

        return null;
    }

    public object InitiallyFocusedControl => treeView;

    private ProjectBrowserViewModel ViewModel => (ProjectBrowserViewModel)DataContext;
    
    private void ProjectBrowserViewDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ProjectBrowserViewModel oldViewModel) {
            oldViewModel.CollapseAllRequested -= ViewModelCollapseAllRequested;
        }
        if (e.NewValue is ProjectBrowserViewModel newViewModel) {
            newViewModel.CollapseAllRequested += ViewModelCollapseAllRequested;
        }
    }
    
    private void ViewModelCollapseAllRequested(object sender, System.EventArgs e)
    {
        foreach (var item in treeView.Items) {
            if (treeView.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem treeViewItem) {
                Collapse(treeViewItem);
            }
        }
    }

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
    
    private static void Collapse(TreeViewItem item)
    {
        item.IsExpanded = false;
        foreach (var child in item.Items) {
            if (item.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem childItem) {
                Collapse(childItem);
            }
        }
    }
}
