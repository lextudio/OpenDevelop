using System;
using System.Collections;
using System.Collections.Generic;
using System.Composition;
using System.Windows;
using System.Windows.Controls;

using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Editor.Search;
using ICSharpCode.SharpDevelop.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Modern (doc/technotes/ilspy.md "Docking and layout replacement" item 4, 2026-08-03)
/// replacement for the legacy AddInTree-registered <see cref="SearchResultsPad"/>: shows the most
/// recent search's results plus its own toolbar, same behavior as before, just as a
/// <see cref="ToolPaneModel"/>. Implements <see cref="ISearchResultsHost"/> and registers itself
/// as that service so every AddIn that reports search results (SearchAndReplace, ResourceToolkit,
/// AvalonEdit.AddIn's OpenLens, TypeScript, CSharpBinding) can reach it without a compile-time
/// reference to this class - see <see cref="SearchResultsHost"/> (Base project) for how they do.
/// </summary>
[Export(typeof(SearchResultsPadViewModel))]
[Export("ToolPane", typeof(ToolPaneModel))]
[Shared]
internal sealed class SearchResultsPadViewModel : ToolPaneModel, ISearchResultsHost
{
    readonly Grid contentPanel = new Grid();
    readonly ToolBar toolBar = new ToolBar();
    readonly ContentPresenter contentPlaceholder = new ContentPresenter();
    IList defaultToolbarItems;

    ISearchResult activeSearchResult;
    readonly List<ISearchResult> lastSearches = new List<ISearchResult>();
    bool subscribed;

    public IEnumerable<ISearchResult> LastSearches => lastSearches;

    public event EventHandler SearchResultsShown = delegate { };

    public SearchResultsPadViewModel()
    {
        Title = "Search Results";
        ContentId = "SearchResultsPad";
        IsVisible = false; // Matches the legacy Pad's `defaultPosition = "Bottom, Hidden"`.
        IsCloseable = true;
        LegacyPadClass = typeof(SearchResultsPad).FullName;
        Content = contentPanel;

        // Registered eagerly, unlike the toolbar-building in EnsureSubscribed below: this is a
        // plain service-container add with no dependency on SD.Workbench/IWorkbench being ready
        // yet, and external callers (SearchResultsHost.Current, in the Base project) need to be
        // able to resolve this pad the very first time they touch it - deferring registration
        // itself (as opposed to deferring the toolbar construction, which does need to wait)
        // would make the very first call from any AddIn silently see no host at all.
        SD.Services.AddService(typeof(ISearchResultsHost), this);
    }

    /// <summary>
    /// Builds the toolbar on first real use rather than in the constructor - same early-startup
    /// hazard already found and fixed for <see cref="OutlineViewModel"/> et al.
    /// (<c>ToolBarService.CreateToolBarItems</c> resolves AddInTree command items, which can touch
    /// services not registered yet at MEF-composition time).
    /// </summary>
    void EnsureSubscribed()
    {
        if (subscribed || SD.Services.GetService(typeof(IWorkbench)) == null)
            return;
        subscribed = true;

        ToolBarTray.SetIsLocked(toolBar, true);
        defaultToolbarItems = ToolBarService.CreateToolBarItems(contentPanel, this, "/SharpDevelop/Pads/SearchResultPad/Toolbar");
        foreach (object toolBarItem in defaultToolbarItems)
            toolBar.Items.Add(toolBarItem);

        contentPanel.Children.Add(toolBar);
        contentPanel.Children.Add(contentPlaceholder);
        contentPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(contentPlaceholder, 1);
    }

    public override void Show()
    {
        EnsureSubscribed();
        base.Show();
    }

    public void ClearLastSearchesList()
    {
        EnsureSubscribed();
        lastSearches.Clear();
        if (activeSearchResult != null) {
            activeSearchResult.OnDeactivate();
            activeSearchResult = null;
        }
        contentPlaceholder.Content = null;
        toolBar.Items.Clear();
        foreach (object toolBarItem in defaultToolbarItems)
            toolBar.Items.Add(toolBarItem);
    }

    public void ShowSearchResults(ISearchResult result)
    {
        EnsureSubscribed();
        if (result == null)
            throw new ArgumentNullException(nameof(result));

        lastSearches.Remove(result);
        lastSearches.Insert(0, result);
        while (lastSearches.Count > 15)
            lastSearches.RemoveAt(15);

        if (activeSearchResult != result) {
            activeSearchResult?.OnDeactivate();
            activeSearchResult = result;
        }
        contentPlaceholder.Content = result.GetControl();

        toolBar.Items.Clear();
        foreach (object toolBarItem in defaultToolbarItems)
            toolBar.Items.Add(toolBarItem);
        IList additionalToolbarItems = result.GetToolbarItems();
        if (additionalToolbarItems != null) {
            toolBar.Items.Add(new Separator());
            foreach (object toolBarItem in additionalToolbarItems)
                toolBar.Items.Add(toolBarItem);
        }

        SearchResultsShown(this, EventArgs.Empty);
    }

    public void ShowSearchResults(string title, IEnumerable<SearchResultMatch> matches)
    {
        ShowSearchResults(SearchResultFactory.CreateSearchResult(title, matches));
    }

    public void ShowSearchResults(string title, IObservable<SearchedFile> matches)
    {
        ShowSearchResults(SearchResultFactory.CreateSearchResult(title, matches));
    }

    /// <summary>
    /// Was <c>Show()</c> directly - wrong for a pad this migration's other pads didn't have to
    /// worry about: when a pad's ContentId isn't in the restored layout file,
    /// AvalonDockLayout.LoadLayout excludes it from DockWorkspace.ToolPanes entirely (see
    /// doc/technotes/ilspy.md - the same exclusion `Outline`/`DefinitionView`/`TaskListPad` also
    /// hit), so `Show()` alone (which only flips IsVisible/IsActive on the model) has nothing real
    /// to materialize. `od.show-pad`'s route (`SD.Workbench.ActivatePad` ->
    /// `AvalonDockLayout.ActivatePad`) already handles this correctly by falling back to the
    /// legacy `AvalonPadContent` path when the MEF route was excluded - going through
    /// `PadDescriptor.BringPadToFront()` here reaches the same fallback instead of bypassing it.
    /// </summary>
    public void BringToFront() => SD.Workbench.GetPad(typeof(SearchResultsPad))?.BringPadToFront();
}
