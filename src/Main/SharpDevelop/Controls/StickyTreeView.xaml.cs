#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ICSharpCode.SharpDevelop.Controls;

/// <summary>
/// A hierarchical tree with ancestor rows pinned at the top of its real scrolling viewport.
/// Consumers provide their normal tree item template and a template for the duplicate pinned row.
/// </summary>
internal partial class StickyTreeView : UserControl
{
    private sealed record StickyHeaderRow(object Item, Thickness Indent);
    internal sealed record StickyRowLayout(string Name, Rect RealHeader, Rect Overlay);
    internal sealed record StickyLayoutSnapshot(Rect Viewport, Rect OverlayPanel, double VerticalOffset,
        IReadOnlyList<StickyRowLayout> Rows);

    private readonly List<StickyHeaderRow> stickyHeaderRows = new();
    private ScrollViewer? scrollViewer;
    private double lastScrollOffset = double.NaN;

    public StickyTreeView()
    {
        InitializeComponent();
        tree.Loaded += TreeOnLoaded;
    }

    public TreeView Tree => tree;

    public IEnumerable? ItemsSource {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(StickyTreeView),
        new PropertyMetadata(null, (d, _) => ((StickyTreeView)d).tree.ItemsSource = ((StickyTreeView)d).ItemsSource));

    public DataTemplate? ItemTemplate {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }
    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(StickyTreeView),
        new PropertyMetadata(null, (d, _) => ((StickyTreeView)d).tree.ItemTemplate = ((StickyTreeView)d).ItemTemplate));

    public Style? ItemContainerStyle {
        get => (Style?)GetValue(ItemContainerStyleProperty);
        set => SetValue(ItemContainerStyleProperty, value);
    }
    public static readonly DependencyProperty ItemContainerStyleProperty = DependencyProperty.Register(
        nameof(ItemContainerStyle), typeof(Style), typeof(StickyTreeView),
        new PropertyMetadata(null, (d, _) => ((StickyTreeView)d).tree.ItemContainerStyle = ((StickyTreeView)d).ItemContainerStyle));

    public DataTemplate? StickyHeaderTemplate {
        get => (DataTemplate?)GetValue(StickyHeaderTemplateProperty);
        set => SetValue(StickyHeaderTemplateProperty, value);
    }
    public static readonly DependencyProperty StickyHeaderTemplateProperty = DependencyProperty.Register(
        nameof(StickyHeaderTemplate), typeof(DataTemplate), typeof(StickyTreeView),
        new PropertyMetadata(null, (_, _) => { }));

    public int MaxStickyRows { get; set; } = 5;

    /// <summary>Returns true for expanded container items eligible to be represented by a sticky row.</summary>
    public Func<object, bool>? StickyItemPredicate { get; set; }

    /// <summary>Raised after the overlay is rebuilt; useful for consumers that map a pinned row back to data.</summary>
    public event EventHandler<object>? StickyRowClicked;

    private void TreeOnLoaded(object sender, RoutedEventArgs e)
    {
        if (!EnsureScrollViewer())
            tree.LayoutUpdated += TreeOnLayoutUpdatedUntilScrollViewerFound;
    }

    private void TreeOnLayoutUpdatedUntilScrollViewerFound(object? sender, EventArgs e)
    {
        if (EnsureScrollViewer())
            tree.LayoutUpdated -= TreeOnLayoutUpdatedUntilScrollViewerFound;
    }

    private bool EnsureScrollViewer()
    {
        if (scrollViewer != null)
            return true;
        tree.ApplyTemplate();
        scrollViewer = FindDescendant<ScrollViewer>(tree);
        if (scrollViewer == null)
            return false;
        scrollViewer.ScrollChanged += (_, _) => ScheduleUpdateStickyHeaders();
        ScheduleUpdateStickyHeaders();
        return true;
    }

    private void ScheduleUpdateStickyHeaders()
        => Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(UpdateStickyHeaders));

    private void UpdateStickyHeaders()
    {
        if (scrollViewer == null)
            return;

        var pinned = new List<StickyHeaderRow>();
        // header coordinates below are relative to scrollViewer, whose viewport starts at 0 in
        // that coordinate space.  ViewportTop() is only for placing the sibling overlay in this
        // control's coordinate space.
        CollectPinnedRows(tree, pinned, 0);
        if (pinned.Count > MaxStickyRows)
            pinned.RemoveRange(0, pinned.Count - MaxStickyRows);

        var offsetChanged = Math.Abs(scrollViewer.VerticalOffset - lastScrollOffset) > 0.01;
        if (!offsetChanged && SameRows(pinned))
            return;

        lastScrollOffset = scrollViewer.VerticalOffset;
        stickyHeaderRows.Clear();
        stickyHeaderRows.AddRange(pinned);
        stickyHeaders.Items.Clear();

        foreach (var row in pinned) {
            var presenter = new ContentPresenter {
                Margin = row.Indent,
                Content = row.Item,
                ContentTemplate = StickyHeaderTemplate,
            };
            var border = new Border { Background = Brushes.Transparent, Child = presenter };
            border.MouseLeftButtonDown += (_, _) => StickyRowClicked?.Invoke(this, row.Item);
            stickyHeaders.Items.Add(border);
        }

        // The overlay and TreeView share this Grid cell.  Do not use the ScrollViewer's content
        // translation as a Grid margin: some templates report an internal horizontal inset here,
        // and applying it again shifted the pinned rows by 10 DIP.  The row indent is already
        // measured relative to the TreeView, so the sibling layer must retain that same origin.
        stickyHeaderPanel.Margin = new Thickness(0, 0, 0, 0);
        stickyHeaderPanel.ClearValue(WidthProperty);
        stickyHeaderPanel.Visibility = pinned.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CollectPinnedRows(ItemsControl parent, List<StickyHeaderRow> pinned, double coverThreshold)
    {
        if (scrollViewer == null)
            return;
        foreach (var item in parent.Items) {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container
                || GetHeaderElement(container) is not FrameworkElement header)
                continue;

            var headerBottom = header.TranslatePoint(new Point(0, header.ActualHeight), scrollViewer).Y;
            if (headerBottom > coverThreshold)
                return;

            var itemBottom = container.TranslatePoint(new Point(0, container.ActualHeight), scrollViewer).Y;
            if (itemBottom <= coverThreshold || !container.IsExpanded)
                continue;

            if (StickyItemPredicate?.Invoke(item) == true) {
                var indent = header.TranslatePoint(default, tree).X;
                pinned.Add(new StickyHeaderRow(item, new Thickness(indent, 0, 0, 0)));
                CollectPinnedRows(container, pinned, coverThreshold + header.ActualHeight);
            }
            return;
        }
    }

    internal StickyLayoutSnapshot GetLayoutSnapshot()
    {
        EnsureScrollViewer();
        var viewport = scrollViewer == null ? Rect.Empty : new Rect(ViewportLeft(), ViewportTop(), scrollViewer.ViewportWidth, scrollViewer.ViewportHeight);
        var panelOrigin = stickyHeaderPanel.TranslatePoint(default, this);
        var rows = stickyHeaders.Items.OfType<Border>().Select((border, index) => {
            // The Border deliberately spans the whole overlay for hit testing.  The visible row
            // itself is its indented ContentPresenter, which is the geometry callers must
            // compare with the TreeView header.
            var overlay = (FrameworkElement)border.Child;
            var overlayOrigin = overlay.TranslatePoint(default, this);
            var item = stickyHeaderRows[index].Item;
            var real = FindContainer(tree, item) is TreeViewItem container && GetHeaderElement(container) is FrameworkElement header
                ? RectFrom(header, this) : Rect.Empty;
            return new StickyRowLayout(item.ToString() ?? string.Empty, real, new Rect(overlayOrigin, overlay.RenderSize));
        }).ToArray();
        return new StickyLayoutSnapshot(viewport, new Rect(panelOrigin, stickyHeaderPanel.RenderSize), scrollViewer?.VerticalOffset ?? 0, rows);
    }

    public void ScrollToVerticalOffset(double offset)
    {
        if (EnsureScrollViewer())
            scrollViewer!.ScrollToVerticalOffset(offset);
    }

    public void SelectAndBringIntoView(object item)
    {
        if (FindContainer(tree, item) is not TreeViewItem container)
            return;
        container.BringIntoView();
        container.IsSelected = true;
    }

    private void StickyHeadersOnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (scrollViewer == null)
            return;
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static FrameworkElement? GetHeaderElement(TreeViewItem item)
        => item.Template?.FindName("PART_Header", item) as FrameworkElement;

    private static TreeViewItem? FindContainer(ItemsControl parent, object item)
    {
        foreach (var candidate in parent.Items) {
            if (parent.ItemContainerGenerator.ContainerFromItem(candidate) is not TreeViewItem container)
                continue;
            if (ReferenceEquals(candidate, item))
                return container;
            if (FindContainer(container, item) is TreeViewItem found)
                return found;
        }
        return null;
    }

    private double ViewportTop() => scrollViewer?.TranslatePoint(default, this).Y ?? 0;
    private double ViewportLeft() => scrollViewer?.TranslatePoint(default, this).X ?? 0;
    private static Rect RectFrom(FrameworkElement element, UIElement relativeTo) => new(element.TranslatePoint(default, relativeTo), element.RenderSize);

    private bool SameRows(IReadOnlyList<StickyHeaderRow> candidate)
        => candidate.Count == stickyHeaderRows.Count && candidate.SequenceEqual(stickyHeaderRows);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T result)
            return result;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
            if (FindDescendant<T>(VisualTreeHelper.GetChild(root, i)) is T found)
                return found;
        }
        return null;
    }
}
