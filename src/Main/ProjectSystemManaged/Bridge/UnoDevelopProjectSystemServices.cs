// Minimal concrete implementations of the CPS project-lifecycle/threading/fault-handling
// services required to construct DependenciesSnapshotProvider (Dataflow/ActiveConfigurationServices.cs,
// slice 42). UnoDevelop has no project-unload model, no JoinableTaskFactory-based threading, and
// no dataflow fault-isolation service, so these are inert pass-throughs. See docs/project-system.md
// (Slice 43).

using System.Threading.Tasks.Dataflow;

namespace Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Subscriptions;

internal sealed class UnoDevelopThreadingService : IProjectThreadingService
{
    public object? JoinableTaskFactory => null;
    public object? JoinableTaskContext => null;
}

internal sealed class UnoDevelopUnconfiguredProjectCommonServices : IUnconfiguredProjectCommonServices
{
    public UnoDevelopUnconfiguredProjectCommonServices(UnconfiguredProject project) => Project = project;
    public UnconfiguredProject Project { get; }
    public IProjectThreadingService ThreadingService { get; } = new UnoDevelopThreadingService();
}

internal sealed class UnoDevelopUnconfiguredProjectTasksService : IUnconfiguredProjectTasksService
{
    public Task LoadedProjectAsync(Func<Task> action) => action();
}

internal sealed class UnoDevelopProjectFaultHandlerService : IProjectFaultHandlerService
{
    public void RegisterFaultHandler(System.Threading.Tasks.Dataflow.IDataflowBlock block, UnconfiguredProject project, ProjectFaultSeverity severity)
    {
    }
}

/// <summary>UnoDevelop has one always-active configuration per project (no Debug/Release-style configuration switching modeled), so this is a single-value data source.</summary>
internal sealed class UnoDevelopActiveConfiguredProjectProvider : IActiveConfiguredProjectProvider
{
    private readonly SingleValueDataSource<ConfiguredProject> _inner;

    public UnoDevelopActiveConfiguredProjectProvider(ConfiguredProject configuredProject) => _inner = new SingleValueDataSource<ConfiguredProject>(configuredProject);

    public NamedIdentity DataSourceKey => _inner.DataSourceKey;
    public IComparable DataSourceVersion => _inner.DataSourceVersion;
    public IReceivableSourceBlock<IProjectVersionedValue<ConfiguredProject>> SourceBlock => _inner.SourceBlock;
    public void Dispose() => _inner.Dispose();
}

/// <summary>Publishes a fixed, one-time set of configuration slices — UnoDevelop resolves target frameworks up front rather than discovering them incrementally.</summary>
internal sealed class UnoDevelopActiveConfigurationGroupSubscriptionService : IActiveConfigurationGroupSubscriptionService
{
    private readonly SingleValueDataSource<ConfigurationSubscriptionSources> _inner;

    public UnoDevelopActiveConfigurationGroupSubscriptionService(ConfigurationSubscriptionSources sources) =>
        _inner = new SingleValueDataSource<ConfigurationSubscriptionSources>(sources);

    public System.Threading.Tasks.Dataflow.ISourceBlock<IProjectVersionedValue<ConfigurationSubscriptionSources>> SourceBlock => _inner.SourceBlock;
}
