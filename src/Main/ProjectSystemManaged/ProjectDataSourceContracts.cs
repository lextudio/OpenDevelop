using System.Threading.Tasks.Dataflow;

namespace Microsoft.VisualStudio.ProjectSystem;

public interface IProjectValueDataSource<T> : IDisposable
{
    NamedIdentity DataSourceKey { get; }
    IComparable DataSourceVersion { get; }
    IReceivableSourceBlock<IProjectVersionedValue<T>> SourceBlock { get; }
}

/// <summary>
/// Services common to both configured and unconfigured project data sources.
/// Minimal clean-room shape: enough for <see cref="ProjectValueDataSourceBase{T}"/> callers
/// to type-check against; UnoDevelop has no real project-unload/fault-isolation model yet.
/// </summary>
public interface IProjectCommonServices
{
    UnconfiguredProject Project { get; }
}

public interface IUnconfiguredProjectServices : IProjectCommonServices
{
}

/// <summary>
/// Base for a hand-managed (non-chained) CPS data source: owns its own broadcast block and
/// posts values to it directly, as opposed to <see cref="ChainedProjectValueDataSourceBase{T}"/>
/// which derives its value stream from other sources.
/// </summary>
public abstract class ProjectValueDataSourceBase<T> : IProjectValueDataSource<T>
{
    protected ProjectValueDataSourceBase(IProjectCommonServices commonServices, bool synchronousDisposal = false, bool registerDataSource = true)
    {
    }

    public abstract NamedIdentity DataSourceKey { get; }
    public abstract IComparable DataSourceVersion { get; }
    public abstract IReceivableSourceBlock<IProjectVersionedValue<T>> SourceBlock { get; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>
/// Base for a CPS data source whose value stream is derived from other upstream data sources
/// rather than posted to directly. Owns an internal broadcast block; derived classes wire
/// upstream sources into the ingestion target block supplied to <see cref="LinkExternalInput"/>.
/// </summary>
public abstract class ChainedProjectValueDataSourceBase<T> : IProjectValueDataSource<T>
{
    private readonly object _lock = new();
    private readonly IBroadcastBlock<IProjectVersionedValue<T>> _broadcastBlock = DataflowBlockSlim.CreateBroadcastBlock<IProjectVersionedValue<T>>();
    private readonly IReceivableSourceBlock<IProjectVersionedValue<T>> _publicBlock;
    private bool _linked;
    private IDisposable? _link;
    private int _version;

    protected ChainedProjectValueDataSourceBase(UnconfiguredProject containingProject, bool synchronousDisposal = false, bool registerDataSource = true)
    {
        ContainingProject = containingProject;
        _publicBlock = _broadcastBlock.SafePublicize();
    }

    protected UnconfiguredProject ContainingProject { get; }

    public virtual NamedIdentity DataSourceKey { get; } = new(nameof(ChainedProjectValueDataSourceBase<T>));

    public IComparable DataSourceVersion => _version;

    public IReceivableSourceBlock<IProjectVersionedValue<T>> SourceBlock
    {
        get
        {
            EnsureLinked();
            return _publicBlock;
        }
    }

    private void EnsureLinked()
    {
        if (_linked)
        {
            return;
        }

        lock (_lock)
        {
            if (_linked)
            {
                return;
            }

            _linked = true;
            var ingest = new System.Threading.Tasks.Dataflow.ActionBlock<IProjectVersionedValue<T>>(v =>
            {
                _version++;
                _broadcastBlock.Post(v);
            });
            _link = LinkExternalInput(ingest);
        }
    }

    /// <summary>Wires upstream data source(s) into <paramref name="targetBlock"/>, called once on first subscription.</summary>
    protected abstract IDisposable? LinkExternalInput(System.Threading.Tasks.Dataflow.ITargetBlock<IProjectVersionedValue<T>> targetBlock);

    /// <summary>
    /// No-op in this shim: the real CPS base uses this to avoid JoinableTaskFactory deadlocks when
    /// synchronously joining upstream sources. UnoDevelop has no such join-blocking threading model.
    /// </summary>
    protected IDisposable JoinUpstreamDataSources(params object[] sources) => NullDisposable.Instance;

    public void Dispose()
    {
        _link?.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed class NullDisposable : IDisposable
{
    public static readonly NullDisposable Instance = new();
    public void Dispose()
    {
    }
}
