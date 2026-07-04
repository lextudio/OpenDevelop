// Clean-room stubs for CPS's dataflow value/versioning contracts.
// These types (IProjectVersionedValue<T>, IBroadcastBlock<T>, DataflowBlockSlim,
// ProjectValueDataSourceBase<T>) live in the closed Microsoft.VisualStudio.ProjectSystem
// assembly, not in the MIT dotnet/project-system repo. Reconstructed here from the public
// usage patterns in the linked upstream *.Managed subscriber files only (never decompiled).
// See docs/project-system.md (Slice 37/38) and memory/opensource-cps-shim.md.

using System.Threading.Tasks.Dataflow;

namespace Microsoft.VisualStudio.ProjectSystem;

/// <summary>Anything carrying the data-source versions that produced it, regardless of value type — lets heterogeneous versioned values be combined generically (see ProjectDataSources.SyncLinkTo).</summary>
public interface IProjectValueVersions
{
    IImmutableDictionary<NamedIdentity, IComparable> DataSourceVersions { get; }
}

/// <summary>A value paired with the data-source versions that produced it.</summary>
public interface IProjectVersionedValue<out T> : IProjectValueVersions
{
    T Value { get; }
}

public sealed class ProjectVersionedValue<T> : IProjectVersionedValue<T>
{
    public ProjectVersionedValue(T value, IImmutableDictionary<NamedIdentity, IComparable> dataSourceVersions)
    {
        Value = value;
        DataSourceVersions = dataSourceVersions;
    }

    public T Value { get; }
    public IImmutableDictionary<NamedIdentity, IComparable> DataSourceVersions { get; }
}

/// <summary>Well-known empty collections used when seeding a data source's first value.</summary>
public static class Empty
{
    public static readonly IImmutableDictionary<NamedIdentity, IComparable> ProjectValueVersions =
        ImmutableDictionary<NamedIdentity, IComparable>.Empty;
}

/// <summary>
/// CPS-specific broadcast block shape: a source block that also accepts posted values.
/// Backed by the real <see cref="BroadcastBlock{T}"/> from System.Threading.Tasks.Dataflow.
/// </summary>
public interface IBroadcastBlock<T> : ISourceBlock<T>, ITargetBlock<T>
{
    bool Post(T item);
}

internal sealed class BroadcastBlockAdapter<T> : IBroadcastBlock<T>
{
    private readonly BroadcastBlock<T> _inner;

    public BroadcastBlockAdapter(Func<T, T> cloningFunction) => _inner = new BroadcastBlock<T>(cloningFunction);

    public bool Post(T item) => _inner.Post(item);
    public Task Completion => _inner.Completion;
    public void Complete() => _inner.Complete();
    public void Fault(Exception exception) => ((IDataflowBlock)_inner).Fault(exception);
    public IDisposable LinkTo(ITargetBlock<T> target, DataflowLinkOptions linkOptions) => _inner.LinkTo(target, linkOptions);
    public DataflowMessageStatus OfferMessage(DataflowMessageHeader messageHeader, T messageValue, ISourceBlock<T>? source, bool consumeToAccept) =>
        ((ITargetBlock<T>)_inner).OfferMessage(messageHeader, messageValue, source, consumeToAccept);
    public T? ConsumeMessage(DataflowMessageHeader messageHeader, ITargetBlock<T> target, out bool messageConsumed) =>
        ((ISourceBlock<T>)_inner).ConsumeMessage(messageHeader, target, out messageConsumed);
    public void ReleaseReservation(DataflowMessageHeader messageHeader, ITargetBlock<T> target) =>
        ((ISourceBlock<T>)_inner).ReleaseReservation(messageHeader, target);
    public bool ReserveMessage(DataflowMessageHeader messageHeader, ITargetBlock<T> target) =>
        ((ISourceBlock<T>)_inner).ReserveMessage(messageHeader, target);
}

/// <summary>Wraps a source block to hide posting/completion capability from downstream consumers.</summary>
internal sealed class ReceivableSourceBlockAdapter<T> : IReceivableSourceBlock<T>
{
    private readonly ISourceBlock<T> _inner;

    public ReceivableSourceBlockAdapter(ISourceBlock<T> inner) => _inner = inner;

    public Task Completion => _inner.Completion;
    public void Complete() => _inner.Complete();
    public void Fault(Exception exception) => _inner.Fault(exception);
    public IDisposable LinkTo(ITargetBlock<T> target, DataflowLinkOptions linkOptions) => _inner.LinkTo(target, linkOptions);
    public T? ConsumeMessage(DataflowMessageHeader messageHeader, ITargetBlock<T> target, out bool messageConsumed) =>
        _inner.ConsumeMessage(messageHeader, target, out messageConsumed);
    public void ReleaseReservation(DataflowMessageHeader messageHeader, ITargetBlock<T> target) =>
        _inner.ReleaseReservation(messageHeader, target);
    public bool ReserveMessage(DataflowMessageHeader messageHeader, ITargetBlock<T> target) =>
        _inner.ReserveMessage(messageHeader, target);

    // Broadcast-sourced blocks don't buffer for pull-based receive; consumers use LinkTo.
    public bool TryReceive(Predicate<T>? filter, out T item)
    {
        item = default!;
        return false;
    }

    public bool TryReceiveAll(out IList<T>? items)
    {
        items = null;
        return false;
    }
}

public static class DataflowBlockSlimExtensions
{
    public static IReceivableSourceBlock<T> SafePublicize<T>(this IBroadcastBlock<T> block, string? nameFormat = null) =>
        new ReceivableSourceBlockAdapter<T>(block);
}

/// <summary>Factory for CPS-shaped dataflow blocks, backed by real TPL Dataflow blocks.</summary>
public static class DataflowBlockSlim
{
    public static IBroadcastBlock<T> CreateBroadcastBlock<T>(
        string? nameFormat = null,
        Func<T, T>? cloningFunction = null,
        bool skipIntermediateInputData = false) =>
        new BroadcastBlockAdapter<T>(cloningFunction ?? (x => x));

    public static TransformBlock<TIn, TOut> CreateTransformBlock<TIn, TOut>(
        Func<TIn, TOut> transformFunction,
        string? nameFormat = null,
        bool skipIntermediateInputData = false) =>
        new(transformFunction);

    public static TransformManyBlock<TIn, TOut> CreateTransformManyBlock<TIn, TOut>(
        Func<TIn, IEnumerable<TOut>> transformFunction,
        string? nameFormat = null,
        bool skipIntermediateInputData = false,
        bool skipIntermediateOutputData = false,
        CancellationToken cancellationToken = default) =>
        new(transformFunction, new ExecutionDataflowBlockOptions { CancellationToken = cancellationToken });
}
