// Concrete IActiveConfigurationSubscriptionSource for one project configuration slice (TFM).
// UnoDevelop gathers all dependency metadata up front (from live MSBuild evaluation or unloaded
// project XML — see UnoDevelopProjectTreeProvider), so unlike a real CPS design-time build there
// is exactly one "evaluation" to post per slice, not a stream of incremental updates over time.
// See docs/project-system.md (Slice 43).

using System.Threading.Tasks.Dataflow;
using Microsoft.VisualStudio.ProjectSystem.Properties;

namespace Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Subscriptions;

internal sealed class UnoDevelopActiveConfigurationSubscriptionSource : IActiveConfigurationSubscriptionSource
{
    public UnoDevelopActiveConfigurationSubscriptionSource(ConfiguredProject configuredProject, IProjectCatalogSnapshot catalogSnapshot)
    {
        ActiveConfiguredProjectSource = new SingleValueDataSource<ConfiguredProject>(configuredProject);
        ProjectCatalogSource = new SingleValueDataSource<IProjectCatalogSnapshot>(catalogSnapshot);
    }

    public ProjectRuleSource ProjectRuleSource { get; } = new();
    public ProjectRuleSource JointRuleSource { get; } = new();

    IProjectRuleSource IActiveConfigurationSubscriptionSource.ProjectRuleSource => ProjectRuleSource;
    IProjectRuleSource IActiveConfigurationSubscriptionSource.JointRuleSource => JointRuleSource;

    public IProjectValueDataSource<ConfiguredProject> ActiveConfiguredProjectSource { get; }
    public IProjectValueDataSource<IProjectCatalogSnapshot> ProjectCatalogSource { get; }

    /// <summary>
    /// Posts <paramref name="update"/> to both the evaluation-only and joint rule sources, since
    /// UnoDevelop has no separate design-time-build "resolved" phase to distinguish them.
    /// <paramref name="generation"/> is stamped into the posted value's <c>DataSourceVersions</c>
    /// under <see cref="DependenciesSnapshotSession.GenerationKey"/> — since the dataflow graph is
    /// now a long-lived, continuously-running pipeline (slice 47) rather than rebuilt per call, a
    /// caller awaiting "the next snapshot" needs a way to distinguish a snapshot reflecting *this*
    /// post from a still-in-flight emission from a previous one.
    /// </summary>
    public void PostUpdate(IProjectSubscriptionUpdate update, int generation)
    {
        var versions = Empty.ProjectValueVersions.Add(DependenciesSnapshotSession.GenerationKey, generation);
        var value = new ProjectVersionedValue<IProjectSubscriptionUpdate>(update, versions);
        ProjectRuleSource.Post(value);
        JointRuleSource.Post(value);
    }
}

/// <summary>A data source that always has exactly one value, available immediately to any consumer that links to it (matches CPS's late-linking-sees-last-value dataflow semantics).</summary>
internal sealed class SingleValueDataSource<T> : IProjectValueDataSource<T>
{
    private readonly IBroadcastBlock<IProjectVersionedValue<T>> _block = DataflowBlockSlim.CreateBroadcastBlock<IProjectVersionedValue<T>>();
    private readonly IReceivableSourceBlock<IProjectVersionedValue<T>> _publicBlock;

    public SingleValueDataSource(T value)
    {
        _publicBlock = _block.SafePublicize();
        _block.Post(new ProjectVersionedValue<T>(value, Empty.ProjectValueVersions));
    }

    public NamedIdentity DataSourceKey { get; } = new(nameof(SingleValueDataSource<T>));
    public IComparable DataSourceVersion => 1;
    public IReceivableSourceBlock<IProjectVersionedValue<T>> SourceBlock => _publicBlock;

    public void Dispose()
    {
    }
}
