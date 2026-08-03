using System;
using System.Composition;
using System.Windows.Controls;

using ICSharpCode.SharpDevelop.ViewModels;
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
        LegacyPadClass = typeof(PropertyPad).FullName;

        propertyGrid.IsCategorized = true;
        propertyGrid.ShowSearchBox = true;
        propertyGridContainer.Children.Add(propertyGrid);
        contentPresenter.Content = propertyGridContainer;
        Content = contentPresenter;

        SD.Services.AddService(typeof(IPropertyPadHost), this);
    }

    /// <summary>
    /// Subscribes to <c>SD.Workbench</c> events on first real use rather than in the constructor -
    /// same early-startup hazard already found and fixed for <see cref="OutlineViewModel"/> et al.
    /// Called from every externally-reachable entry point (<see cref="Grid"/>,
    /// <see cref="ActiveContainer"/>, <see cref="UpdateSelectedObjectIfActive"/>, <see cref="Show"/>),
    /// not just <c>Show()</c> - unlike <c>Outline</c>/<c>DefinitionView</c>, this pad defaults
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
        IHasPropertyContainer c = (activeViewOrPad as IServiceProvider)?.GetService<IHasPropertyContainer>();
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
