using System;
using System.Collections.Generic;

using System.Windows;
using System.Windows.Media;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.TypeSystem;

using TextLocation = ICSharpCode.AvalonEdit.Document.TextLocation;

namespace ICSharpCode.SharpDevelop.Editor.Search;

/// <summary>
/// Stateless search-result construction helpers, split out of <c>SearchResultsPad</c>
/// (doc/technotes/ilspy.md "Docking and layout replacement" item 4, 2026-08-03) so they stay
/// reachable from every AddIn regardless of where the pad's own (stateful, <c>ToolPaneModel</c>-
/// based) implementation lives - these never depended on the pad instance at all, just on
/// <c>ISearchResultFactory</c> AddInTree extensions and rendering helpers.
/// </summary>
public static class SearchResultFactory
{
    /// <inheritdoc cref="ISearchResultFactory.CreateSearchResult(string,IEnumerable{SearchResultMatch})"/>
    public static ISearchResult CreateSearchResult(string title, IEnumerable<SearchResultMatch> matches)
    {
        if (title == null)
            throw new ArgumentNullException("title");
        if (matches == null)
            throw new ArgumentNullException("matches");
        foreach (ISearchResultFactory factory in AddInTree.BuildItems<ISearchResultFactory>("/SharpDevelop/Pads/SearchResultPad/Factories", null, false)) {
            ISearchResult result = factory.CreateSearchResult(title, matches);
            if (result != null)
                return result;
        }
        return new DummySearchResult { Text = title };
    }

    /// <inheritdoc cref="ISearchResultFactory.CreateSearchResult(string,IObservable{SearchResultMatch})"/>
    public static ISearchResult CreateSearchResult(string title, IObservable<SearchedFile> matches)
    {
        if (title == null)
            throw new ArgumentNullException("title");
        if (matches == null)
            throw new ArgumentNullException("matches");
        foreach (ISearchResultFactory factory in AddInTree.BuildItems<ISearchResultFactory>("/SharpDevelop/Pads/SearchResultPad/Factories", null, false)) {
            ISearchResult result = factory.CreateSearchResult(title, matches);
            if (result != null)
                return result;
        }
        return new DummySearchResult { Text = title };
    }

    public static RichText CreateInlineBuilder(TextLocation startPosition, TextLocation endPosition, IDocument document, IHighlighter highlighter)
    {
        if (startPosition.Line >= 1 && startPosition.Line <= document.LineCount) {
            var highlightedLine = highlighter.HighlightLine(startPosition.Line);
            var documentLine = highlightedLine.DocumentLine;
            var inlineBuilder = highlightedLine.ToRichTextModel();
            // reset bold/italics
            inlineBuilder.SetFontWeight(0, documentLine.Length, FontWeights.Normal);
            inlineBuilder.SetFontStyle(0, documentLine.Length, FontStyles.Normal);

            // now highlight the match in bold
            if (startPosition.Column >= 1) {
                if (endPosition.Line == startPosition.Line && endPosition.Column > startPosition.Column) {
                    // subtract one from the column to get the offset inside the line's text
                    int startOffset = startPosition.Column - 1;
                    int endOffset = Math.Min(documentLine.Length, endPosition.Column - 1);
                    inlineBuilder.SetFontWeight(startOffset, endOffset - startOffset, FontWeights.Bold);
                }
            }
            return new RichText(document.GetText(documentLine), inlineBuilder);
        }
        return null;
    }

    sealed class DummySearchResult : ISearchResult
    {
        public string Text { get; set; }

        public object GetControl()
        {
            return "Could not find ISearchResultFactory. Did you disable the search+replace addin?";
        }

        public System.Collections.IList GetToolbarItems()
        {
            return null;
        }

        public void OnDeactivate()
        {
        }
    }
}
