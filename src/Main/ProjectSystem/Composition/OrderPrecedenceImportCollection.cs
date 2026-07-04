// Clean-room stub reconstructed from MIT dotnet/project-system usage.
// OrderPrecedenceImportCollection, IOrderPrecedenceMetadataView, ImportOrderPrecedenceComparer
// are CPS SDK types — reconstructed as minimal stubs.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Microsoft.VisualStudio.ProjectSystem;

/// <summary>Metadata view for ordered MEF exports.</summary>
public interface IOrderPrecedenceMetadataView
{
    int OrderPrecedence { get; }
    string[] AppliesTo { get; }
}

/// <summary>
/// Minimal stub of CPS's OrderPrecedenceImportCollection&lt;T&gt;.
/// Holds an ordered list of Lazy&lt;T, IOrderPrecedenceMetadataView&gt; exports.
/// </summary>
public sealed class OrderPrecedenceImportCollection<T>
    : IEnumerable<Lazy<T, IOrderPrecedenceMetadataView>>
{
    private readonly List<Lazy<T, IOrderPrecedenceMetadataView>> _items = new();

    public OrderPrecedenceImportCollection(
        ImportOrderPrecedenceComparer.PreferenceOrder preferenceOrder = ImportOrderPrecedenceComparer.PreferenceOrder.PreferredComesLast,
        object? projectCapabilityCheckProvider = null)
    {
    }

    public int Count => _items.Count;

    /// <summary>Returns the underlying export values in order.</summary>
    public IEnumerable<T> ExtensionValues() => _items.Select(l => l.Value);

    public void Add(Lazy<T, IOrderPrecedenceMetadataView> item) => _items.Add(item);

    public IEnumerator<Lazy<T, IOrderPrecedenceMetadataView>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class OrderPrecedenceImportCollectionExtensions
{
    public static ImmutableArray<T> ToImmutableValueArray<T>(this OrderPrecedenceImportCollection<T> collection) =>
        collection.ExtensionValues().ToImmutableArray();
}

/// <summary>Ordering comparer stub.</summary>
public static class ImportOrderPrecedenceComparer
{
    public enum PreferenceOrder
    {
        PreferredComesFirst = 0,
        PreferredComesLast = 1,
    }
}
