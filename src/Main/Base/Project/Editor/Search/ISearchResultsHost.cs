using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Editor.Search;

/// <summary>
/// The Search Results pad's real behavior, as seen by every caller that isn't the pad itself
/// (doc/technotes/ilspy.md "Docking and layout replacement" item 4, 2026-08-03) - split out, same
/// shape as <c>IPaneModelHost</c>/<c>IPropertyPadHost</c>, so the many AddIns that report search
/// results (SearchAndReplace, ResourceToolkit, AvalonEdit.AddIn's OpenLens, TypeScript,
/// CSharpBinding - all of which only reference the Base project) can reach the live pad without a
/// compile-time reference to <c>SearchResultsPadViewModel</c>, whose real implementation lives in
/// the App project alongside every other migrated pad's <c>ToolPaneModel</c>. Registered via
/// <c>SD.Services.AddService(typeof(ISearchResultsHost), this)</c> in that view model's
/// constructor; resolved by callers through <see cref="SearchResultsHost.Current"/> below rather
/// than each doing its own <c>SD.Services.GetService</c> cast.
/// </summary>
public interface ISearchResultsHost
{
    IEnumerable<ISearchResult> LastSearches { get; }

    event EventHandler SearchResultsShown;

    void ClearLastSearchesList();

    void ShowSearchResults(ISearchResult result);

    void ShowSearchResults(string title, IEnumerable<SearchResultMatch> matches);

    void ShowSearchResults(string title, IObservable<SearchedFile> matches);

    void BringToFront();
}

/// <summary>
/// Resolves the live <see cref="ISearchResultsHost"/> - the replacement for the old
/// <c>SearchResultsPad.Instance</c> static singleton, now that the real pad implementation moved
/// out of this (Base) project. Every external caller that used to write
/// <c>SearchResultsPad.Instance.ShowSearchResults(...)</c> now writes
/// <c>SearchResultsHost.Current.ShowSearchResults(...)</c> - same call shape, resolved through the
/// service container instead of a compile-time type reference.
/// </summary>
public static class SearchResultsHost
{
    public static ISearchResultsHost Current =>
        SD.Services.GetService(typeof(ISearchResultsHost)) as ISearchResultsHost;
}
