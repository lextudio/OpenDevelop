using System.Windows;
using System.Windows.Controls;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>
/// Click-to-sort for a <see cref="ListView"/> with a <see cref="GridView"/>. A column opts in by giving its
/// <see cref="GridViewColumnHeader"/> a <c>Tag</c> naming the sort key. Sorting is done on the item list
/// itself rather than through a collection view, so it behaves the same on every WPF implementation.
/// </summary>
internal sealed class ColumnSorter<T>
{
    readonly ListView list;
    readonly IReadOnlyDictionary<string, Func<T, IComparable>> keys;
    IReadOnlyList<T> items = [];
    string? column;
    bool descending;

    public ColumnSorter(ListView list, IReadOnlyDictionary<string, Func<T, IComparable>> keys)
    {
        this.list = list;
        this.keys = keys;
        list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
    }

    /// <summary>Replaces the items and sorts them by the given column.</summary>
    public void SetItems(IReadOnlyList<T> newItems, string defaultColumn, bool defaultDescending)
    {
        items = newItems;
        column = defaultColumn;
        descending = defaultDescending;
        Apply();
    }

    void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string tag } || !keys.ContainsKey(tag))
            return;
        // A new column starts ascending; clicking the same column again reverses it.
        descending = column == tag && !descending;
        column = tag;
        Apply();
    }

    void Apply()
    {
        if (column is null || !keys.TryGetValue(column, out var key))
        {
            list.ItemsSource = items;
            return;
        }
        list.ItemsSource = (descending ? items.OrderByDescending(key) : items.OrderBy(key)).ToList();
    }
}
