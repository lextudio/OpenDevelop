using System;
using System.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;

using XceedPropertyGrid = Xceed.Wpf.Toolkit.PropertyGrid.PropertyGrid;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Modern (doc/technotes/ilspy.md "Docking and layout replacement" item 4, 2026-08-03)
/// replacement for the legacy AddInTree-registered <see cref="PropertyPad"/>: shows the Xceed
/// property grid for whatever has focus, same behavior as before, just as a
/// <see cref="ToolPaneModel"/>. Implements <see cref="IPropertyPadHost"/> and registers itself as
/// that service so <see cref="PropertyContainer"/> (Base project) and other AddIns (e.g.
/// WpfDesign.AddIn) can reach it without a compile-time reference to this class.
/// </summary>
[Export(typeof(PropertyPadViewModel))]
[Export("ToolPane", typeof(ToolPaneModel))]
[Shared]
internal sealed class PropertyPadViewModel : ToolPaneModel, IPropertyPadHost, IDisposable
{
    readonly PropertyContainer emptyContainer = new PropertyContainer(false);
    readonly ContentPresenter contentPresenter = new ContentPresenter();
    readonly Grid propertyGridContainer = new Grid();
    readonly XceedPropertyGrid propertyGrid = new XceedPropertyGrid();

    PropertyContainer activeContainer;
    object currentReplacementContent;
    IHasPropertyContainer previousContent;
    bool subscribed;

    public XceedPropertyGrid Grid {
        get {
            EnsureSubscribed();
            return propertyGrid;
        }
    }

    public PropertyContainer ActiveContainer {
        get {
            EnsureSubscribed();
            return activeContainer;
        }
    }

    public PropertyPadViewModel()
    {
        Title = "Properties";
        ContentId = "PropertyPad";
        IsVisible = true; // Matches the legacy Pad's `defaultPosition = "Right"`.
        IsCloseable = true;
        PreferredDockSide = ICSharpCode.ILSpy.ViewModels.PreferredDockSide.Right;
        // Without a pixel DockSize the newly-docked right pane stays `1*` and AvalonDock's
        // OnFixChildrenDockLengths freezes it to the rendered star width on first layout - which
        // on the LibreWPF backend is the 25px DockMinWidth, collapsing the pad into a title-bar
        // sliver whose property grid renders nothing (measured: "Properties pad is empty" after
        // selecting a designed control). Same mechanism the ProjectBrowser/ErrorList/SearchResults
        // pads rely on to keep their docked size (see DockWorkspace.AfterInsertAnchorable).
        PreferredDockSize = 250;
        LegacyPadClass = typeof(PropertyPad).FullName;

        propertyGrid.IsCategorized = true;
        propertyGrid.ShowSearchBox = true;
        // The grid already sits inside the AvalonDock pane's own bordered ContentPanel
        // (AnchorablePaneControlStyle, src/Libraries/AvalonDock/.../Themes/generic.xaml) - its
        // own default 1px BorderThickness (Xceed's PropertyGrid style) just doubled that line.
        // Zeroing the instance property (not overriding the Style/Template) leaves the rest of
        // the default look intact.
        propertyGrid.BorderThickness = new Thickness(0);
        propertyGridContainer.Children.Add(propertyGrid);
        contentPresenter.Content = propertyGridContainer;
        Content = contentPresenter;

        // VS-style double-click on an Events row: the selected object (e.g. the WinForms
        // designer's remote component proxy) creates and binds the conventional handler.
        // Both the routed MouseDoubleClick and a manual press-timing check are wired, because
        // LibreWPF's ClickCount/MouseDoubleClick delivery is not reliable across control
        // subtypes - whichever fires first wins, and the manual path only counts presses that
        // actually landed on an Events row.
        propertyGrid.MouseDoubleClick += OnGridMouseDoubleClick;
        propertyGrid.PreviewMouseLeftButtonDown += OnGridPreviewMouseLeftButtonDown;

        SD.Services.AddService(typeof(IPropertyPadHost), this);
    }

    DateTime lastEventsRowPressUtc = DateTime.MinValue;

    void OnGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BindEventFromRow(e.OriginalSource))
            e.Handled = true;
    }

    void OnGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // LibreWPF does not reliably populate ClickCount on MouseLeftButtonDown; detect the
        // double click manually by press timing (same pattern as the WinForms design surface).
        var now = DateTime.UtcNow;
        var isDoubleClick = (now - lastEventsRowPressUtc).TotalMilliseconds < 800;
        lastEventsRowPressUtc = now;
        if (!isDoubleClick)
            return;
        if (BindEventFromRow(e.OriginalSource))
            e.Handled = true;
    }

    bool BindEventFromRow(object originalSource)
    {
        var hit = originalSource as DependencyObject;
        var chain = new System.Text.StringBuilder();
        while (hit != null) {
            var dc = (hit as FrameworkElement)?.DataContext;
            var content = (hit as System.Windows.Controls.ContentPresenter)?.Content;
            chain.Append(hit.GetType().Name)
                .Append("(dc=").Append(dc?.GetType().Name ?? "null")
                .Append(",content=").Append(content?.GetType().Name ?? "null")
                .Append(") < ");
            if (dc is Xceed.Wpf.Toolkit.PropertyGrid.EventItem viaDataContext)
            {
                return BindEventItem(viaDataContext);
            }
            if (content is Xceed.Wpf.Toolkit.PropertyGrid.EventItem eventItem)
            {
                return BindEventItem(eventItem);
            }
            hit = VisualTreeHelper.GetParent(hit);
        }
        ICSharpCode.Core.LoggingService.Debug("[PropertyGrid] double-click not on an EventItem; chain: " + chain);
        return false;
    }

    bool BindEventItem(Xceed.Wpf.Toolkit.PropertyGrid.EventItem eventItem)
    {
        if (propertyGrid.SelectedObject is IEventBindingHost bindable) {
            ICSharpCode.Core.LoggingService.Debug("[PropertyGrid] double-click on event '" + eventItem.Descriptor.Name + "' -> BindEvent");
            bindable.BindEvent(eventItem.Descriptor.Name);
            return true;
        }
        ICSharpCode.Core.LoggingService.Debug("[PropertyGrid] double-click on '" + eventItem.Descriptor.Name + "' but SelectedObject is not IEventBindingHost");
        return false;
    }

    /// <summary>
    /// Real on-screen bounds of an Events-view row, computed via the row element's own
    /// PointToScreen - the same trusted coordinate source the toolbox query actions use.
    /// DevFlow's generic UI-tree walk reports stale/offset bounds for this virtualized Xceed
    /// grid (measured: clicks aimed at its coordinates landed one-to-three rows off), so
    /// synthetic-pointer tests must aim with this instead. Returns null when the named event
    /// isn't realized in the visual tree (scrolled out of view).
    /// </summary>
    public (double X, double Y, double Width, double Height)? QueryEventRowScreenBounds(string eventName)
    {
        var row = FindEventRowElement(propertyGrid, eventName);
        if (row == null)
            return null;
        var topLeft = row.PointToScreen(new Point(0, 0));
        return (topLeft.X, topLeft.Y, row.ActualWidth, row.ActualHeight);
    }

    static FrameworkElement? FindEventRowElement(DependencyObject root, string eventName)
    {
        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < children; i++) {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element
                && element.DataContext is Xceed.Wpf.Toolkit.PropertyGrid.EventItem viaDataContext
                && viaDataContext.Descriptor.Name == eventName)
                return element;
            var nested = FindEventRowElement(child, eventName);
            if (nested != null)
                return nested;
        }
        return null;
    }


    /// <summary>
    /// Subscribes to <c>SD.Workbench</c> events on first real use rather than in the constructor -
    /// same early-startup hazard already found and fixed for <see cref="OutlineViewModel"/> et al.
    /// Called from every externally-reachable entry point (<see cref="Grid"/>,
    /// <see cref="ActiveContainer"/>, <see cref="UpdateSelectedObjectIfActive"/>, <see cref="Show"/>),    /// not just <c>Show()</c> - unlike <c>Outline</c>/<c>DefinitionView</c>, this pad defaults
    /// *visible*, so nothing ever calls <c>Show()</c> on it in the ordinary MEF-path case (only
    /// activating a pane the user never touches calls that); the pad still needs to react to
    /// selection changes from the moment anything - e.g. <see cref="PropertyContainer"/> setting
    /// <c>SelectedObject</c> - actually touches it (measured live: without this, the WPF Designer's
    /// selection never reached the Properties pad's grid at all, since the subscription that keeps
    /// them in sync had simply never happened).
    /// </summary>
    internal void EnsureSubscribed()
    {
        if (subscribed || SD.Services.GetService(typeof(IWorkbench)) == null)
            return;
        subscribed = true;
        SD.Workbench.ActiveContentChanged += WorkbenchActiveContentChanged;
        SD.Workbench.ActiveViewContentChanged += WorkbenchActiveContentChanged;
        WorkbenchActiveContentChanged(null, null);
    }

    public override void Show()
    {
        EnsureSubscribed();
        base.Show();
    }

    public void UpdateSelectedObjectIfActive(PropertyContainer container)
    {
        EnsureSubscribed();
        if (activeContainer != container)
            return;
        if (container.SelectedObjects != null)
            propertyGrid.SelectedObject = container.SelectedObjects;
        else
            propertyGrid.SelectedObject = container.SelectedObject;
    }

    void UpdateReplacementContent(PropertyContainer container)
    {
        if (activeContainer != container)
            return;
        var replacement = container.PropertyGridReplacementContent;
        if (currentReplacementContent != replacement) {
            currentReplacementContent = replacement;
            contentPresenter.Content = replacement ?? propertyGridContainer;
        }
    }

    void SetActiveContainer(PropertyContainer pc)
    {
        if (activeContainer == pc)
            return;
        activeContainer = pc ?? emptyContainer;
        UpdateSelectedObjectIfActive(activeContainer);
        UpdateReplacementContent(activeContainer);
    }

    void WorkbenchActiveContentChanged(object sender, EventArgs e)
    {
        var activeViewOrPad = SD.Workbench.ActiveContent;
        // Secondary designer views (WinUI, WPF and Forms) implement IHasPropertyContainer
        // directly.  They are not IServiceProvider instances, so looking only through GetService
        // loses their selection whenever ActiveContent is the view itself; the grid then retains
        // the empty container even though the design surface has selected a control.
        IHasPropertyContainer c = activeViewOrPad as IHasPropertyContainer
            ?? (activeViewOrPad as IServiceProvider)?.GetService<IHasPropertyContainer>();
        if (c == null) {
            c = SD.Workbench.ActiveViewContent as IHasPropertyContainer;
        }
        if (c == null) {
            if (previousContent == null) {
                c = SD.GetActiveViewContentService<IHasPropertyContainer>();
            } else {
                if (previousContent is IViewContent && previousContent != SD.Workbench.ActiveViewContent) {
                    c = null;
                } else {
                    c = previousContent;
                }
            }
        }
        SetActiveContainer(c?.PropertyContainer);
        previousContent = c;
    }

    public void Dispose()
    {
        if (!subscribed)
            return;
        SD.Workbench.ActiveContentChanged -= WorkbenchActiveContentChanged;
        SD.Workbench.ActiveViewContentChanged -= WorkbenchActiveContentChanged;
    }
}
