// Clean-room stub of CPS's ProjectDataSources sync-link framework, used by
// DependenciesSnapshotProvider to combine several independently-versioned dataflow streams
// (e.g. active configuration, project catalog, per-slice dependencies) into one consistent
// value. The real implementation lives in the closed Microsoft.VisualStudio.ProjectSystem.dll;
// this is a from-scratch "combine latest, merge versions" implementation — simpler than
// upstream's (no back-pressure/consistency-window gating on out-of-order versions), but
// behaviorally equivalent for UnoDevelop's single-writer-per-source usage.
// See docs/project-system.md (Slice 42).

using System.Threading.Tasks.Dataflow;

namespace Microsoft.VisualStudio.ProjectSystem;

public static class SourceBlockAndLinkExtensions
{
    public static ProjectDataSources.SourceBlockAndLink<T> SyncLinkOptions<T>(this ISourceBlock<T> block) =>
        new(block, DataflowOption.PropagateCompletion);
}

/// <summary>No-op pass-through: the real filter drops updates for configurations other than the active one. UnoDevelop has a single active configuration per slice, so there is nothing to filter yet.</summary>
public static class ConfiguredDependencyFilterBlock
{
    public static ISourceBlock<T> TransformSource<T>(ISourceBlock<T> source, Utilities.DisposableBag disposables, string? nameFormat = null) => source;
}

public static class ProjectDataSources
{
    public static readonly NamedIdentity ConfiguredProjectIdentity = new(nameof(ConfiguredProjectIdentity));
    public static readonly NamedIdentity ConfiguredProjectVersion = new(nameof(ConfiguredProjectVersion));

    /// <summary>A source block paired with the link options to use when synchronizing it.</summary>
    public readonly struct SourceBlockAndLink<T>
    {
        public ISourceBlock<T> Block { get; }
        public DataflowLinkOptions LinkOptions { get; }

        public SourceBlockAndLink(ISourceBlock<T> block, DataflowLinkOptions linkOptions)
        {
            Block = block;
            LinkOptions = linkOptions;
        }
    }

    /// <summary>Combines three independently-versioned sources into one, emitting once all three have produced a value and again on every subsequent update.</summary>
    public static IDisposable SyncLinkTo<T1, T2, T3>(
        SourceBlockAndLink<IProjectVersionedValue<T1>> source1,
        SourceBlockAndLink<IProjectVersionedValue<T2>> source2,
        SourceBlockAndLink<IProjectVersionedValue<T3>> source3,
        ITargetBlock<IProjectVersionedValue<(T1, T2, T3)>> target,
        DataflowLinkOptions linkOptions,
        CancellationToken cancellationToken = default)
    {
        var syncer = new TripleSyncer<T1, T2, T3>(target);
        var link1 = source1.Block.LinkTo(new ActionBlock<IProjectVersionedValue<T1>>(syncer.OnValue1), source1.LinkOptions);
        var link2 = source2.Block.LinkTo(new ActionBlock<IProjectVersionedValue<T2>>(syncer.OnValue2), source2.LinkOptions);
        var link3 = source3.Block.LinkTo(new ActionBlock<IProjectVersionedValue<T3>>(syncer.OnValue3), source3.LinkOptions);
        return new CompositeDisposable(link1, link2, link3);
    }

    /// <summary>Combines an arbitrary number of homogeneously-typed versioned sources into one tuple update.</summary>
    public static IDisposable SyncLinkTo<T>(
        ImmutableList<SourceBlockAndLink<T>> sources,
        ITargetBlock<Tuple<ImmutableList<T>, IImmutableDictionary<NamedIdentity, IComparable>>> target,
        DataflowLinkOptions linkOptions)
        where T : IProjectValueVersions
    {
        var syncer = new CollectionSyncer<T>(sources.Count, target);
        var links = new IDisposable[sources.Count];
        for (var i = 0; i < sources.Count; i++)
        {
            var index = i;
            links[i] = sources[i].Block.LinkTo(new ActionBlock<T>(v => syncer.OnValue(index, v)), sources[i].LinkOptions);
        }

        return new CompositeDisposable(links);
    }

    /// <summary>
    /// No-op in this shim: the real CPS implementation uses this to avoid JoinableTaskFactory
    /// deadlocks when synchronously joining upstream sources during disposal. UnoDevelop has no
    /// such join-blocking threading model.
    /// </summary>
    public static IDisposable JoinUpstreamDataSources(object? joinableTaskFactory, object? faultHandler, params object?[] sources) =>
        NullDisposable.Instance;

    private sealed class TripleSyncer<T1, T2, T3>
    {
        private readonly object _lock = new();
        private readonly ITargetBlock<IProjectVersionedValue<(T1, T2, T3)>> _target;
        private IProjectVersionedValue<T1>? _v1;
        private IProjectVersionedValue<T2>? _v2;
        private IProjectVersionedValue<T3>? _v3;

        public TripleSyncer(ITargetBlock<IProjectVersionedValue<(T1, T2, T3)>> target) => _target = target;

        public void OnValue1(IProjectVersionedValue<T1> v) { lock (_lock) { _v1 = v; TryEmit(); } }
        public void OnValue2(IProjectVersionedValue<T2> v) { lock (_lock) { _v2 = v; TryEmit(); } }
        public void OnValue3(IProjectVersionedValue<T3> v) { lock (_lock) { _v3 = v; TryEmit(); } }

        private void TryEmit()
        {
            if (_v1 is null || _v2 is null || _v3 is null)
            {
                return;
            }

            var versions = MergeVersions(_v1.DataSourceVersions, _v2.DataSourceVersions, _v3.DataSourceVersions);
            _target.Post(new ProjectVersionedValue<(T1, T2, T3)>((_v1.Value, _v2.Value, _v3.Value), versions));
        }
    }

    private sealed class CollectionSyncer<T> where T : IProjectValueVersions
    {
        private readonly object _lock = new();
        private readonly T?[] _values;
        private readonly ITargetBlock<Tuple<ImmutableList<T>, IImmutableDictionary<NamedIdentity, IComparable>>> _target;
        private int _filledCount;

        public CollectionSyncer(int count, ITargetBlock<Tuple<ImmutableList<T>, IImmutableDictionary<NamedIdentity, IComparable>>> target)
        {
            _values = new T?[count];
            _target = target;
        }

        public void OnValue(int index, T value)
        {
            lock (_lock)
            {
                if (_values[index] is null)
                {
                    _filledCount++;
                }

                _values[index] = value;

                if (_filledCount < _values.Length)
                {
                    return;
                }

                var listBuilder = ImmutableList.CreateBuilder<T>();
                var versions = ImmutableDictionary<NamedIdentity, IComparable>.Empty;
                foreach (var v in _values)
                {
                    listBuilder.Add(v!);
                    foreach (var kv in v!.DataSourceVersions)
                    {
                        versions = versions.SetItem(kv.Key, kv.Value);
                    }
                }

                _target.Post(Tuple.Create(listBuilder.ToImmutable(), (IImmutableDictionary<NamedIdentity, IComparable>)versions));
            }
        }
    }

    private static IImmutableDictionary<NamedIdentity, IComparable> MergeVersions(params IImmutableDictionary<NamedIdentity, IComparable>[] sources)
    {
        var result = ImmutableDictionary<NamedIdentity, IComparable>.Empty;
        foreach (var source in sources)
        {
            foreach (var kv in source)
            {
                result = result.SetItem(kv.Key, kv.Value);
            }
        }

        return result;
    }
}

/// <summary>Unwraps a collection of data sources (each producing one value of <typeparamref name="TItem"/>) into a single chained data source producing the merged collection.</summary>
public sealed class UnwrapCollectionChainedProjectValueDataSource<TCollection, TItem> :
    ChainedProjectValueDataSourceBase<IReadOnlyCollection<TItem>>,
    ITargetBlock<IProjectVersionedValue<TCollection>>
    where TCollection : IEnumerable<IProjectValueDataSource<TItem>>
{
    private readonly Func<TCollection, IEnumerable<IProjectValueDataSource<TItem>>> _getDataSources;
    private readonly IBroadcastBlock<IProjectVersionedValue<TCollection>> _input = DataflowBlockSlim.CreateBroadcastBlock<IProjectVersionedValue<TCollection>>();

    public UnwrapCollectionChainedProjectValueDataSource(UnconfiguredProject containingProject, Func<TCollection, IEnumerable<IProjectValueDataSource<TItem>>> getDataSource)
        : base(containingProject, synchronousDisposal: false, registerDataSource: false)
    {
        _getDataSources = getDataSource;
    }

    // ITargetBlock<IProjectVersionedValue<TCollection>> — delegates entirely to the internal
    // broadcast block, so callers may either LinkTo() this instance directly or call Post() on it.
    Task IDataflowBlock.Completion => _input.Completion;
    void IDataflowBlock.Complete() => _input.Complete();
    void IDataflowBlock.Fault(Exception exception) => _input.Fault(exception);
    DataflowMessageStatus ITargetBlock<IProjectVersionedValue<TCollection>>.OfferMessage(
        DataflowMessageHeader messageHeader, IProjectVersionedValue<TCollection> messageValue, ISourceBlock<IProjectVersionedValue<TCollection>>? source, bool consumeToAccept) =>
        ((ITargetBlock<IProjectVersionedValue<TCollection>>)_input).OfferMessage(messageHeader, messageValue, source, consumeToAccept);

    /// <summary>Posts the current collection of data sources to unwrap. Each call replaces the prior subscription set.</summary>
    public bool Post(IProjectVersionedValue<TCollection> value) => _input.Post(value);

    protected override IDisposable? LinkExternalInput(ITargetBlock<IProjectVersionedValue<IReadOnlyCollection<TItem>>> targetBlock)
    {
        var disposables = new Utilities.DisposableBag();
        List<IDisposable> itemLinks = new();

        var forward = new ActionBlock<IProjectVersionedValue<TCollection>>(update =>
        {
            foreach (var link in itemLinks)
            {
                link.Dispose();
            }

            itemLinks.Clear();

            var sources = _getDataSources(update.Value).ToImmutableArray();
            if (sources.Length == 0)
            {
                targetBlock.Post(update.Derive(_ => (IReadOnlyCollection<TItem>)Array.Empty<TItem>()));
                return;
            }

            var sourceBlocks = sources.Select(s => new ProjectDataSources.SourceBlockAndLink<IProjectVersionedValue<TItem>>(s.SourceBlock, DataflowOption.PropagateCompletion)).ToImmutableList();

            var merge = new ActionBlock<Tuple<ImmutableList<IProjectVersionedValue<TItem>>, IImmutableDictionary<NamedIdentity, IComparable>>>(tuple =>
            {
                var items = tuple.Item1.Select(v => v.Value).ToImmutableArray();
                targetBlock.Post(new ProjectVersionedValue<IReadOnlyCollection<TItem>>(items, tuple.Item2));
            });

            itemLinks.Add(ProjectDataSources.SyncLinkTo(sourceBlocks, merge, DataflowOption.PropagateCompletion));
        });

        disposables.Add(_input.LinkTo(forward, DataflowOption.PropagateCompletion));
        disposables.Add(new DisposableDelegate(() =>
        {
            foreach (var link in itemLinks)
            {
                link.Dispose();
            }
        }));

        return disposables;
    }
}

public sealed class DisposableDelegate : IDisposable
{
    private readonly Action _action;

    public DisposableDelegate(Action action) => _action = action;

    public void Dispose() => _action();
}
