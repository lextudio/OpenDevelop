// A long-lived per-project dataflow graph (MSBuildDependencySubscriber + DependenciesSnapshotProvider,
// slices 41/42, wired via CompositionScope, slice 45), kept alive across Solution Explorer refreshes
// instead of rebuilt from scratch every call. Only the *evaluation data* changes between refreshes
// (posted via UnoDevelopActiveConfigurationSubscriptionSource.PostUpdate) — the dataflow topology
// (blocks, links, composed parts) is built once. See docs/project-system.md (Slice 47).

using System.Threading.Tasks.Dataflow;
using Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Snapshot;
using Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Subscriptions;
using Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Subscriptions.MSBuildDependencies;

namespace Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies;

internal sealed class DependenciesSnapshotSession : IAsyncDisposable
{
    private readonly DependenciesSnapshotProvider _provider;
    private readonly IReadOnlyDictionary<string, UnoDevelopActiveConfigurationSubscriptionSource> _sourcesByTargetFramework;

    // Per-slice, per-rule-name prior item metadata from the previous RefreshAsync call — see
    // DependencyBridgeSubscriptionTranslator.BuildUpdate's priorItemsByRuleName parameter. Without
    // this, a dependency removed from the project would never be reported as removed once the
    // dataflow graph (and MSBuildDependencyCollection's internal state) is kept alive across calls.
    private readonly Dictionary<string, Dictionary<string, IImmutableDictionary<string, IImmutableDictionary<string, string>>>> _priorItemsByRuleNamePerTfm = new();

    private DependenciesSnapshotSession(
        DependenciesSnapshotProvider provider,
        IReadOnlyDictionary<string, UnoDevelopActiveConfigurationSubscriptionSource> sourcesByTargetFramework)
    {
        _provider = provider;
        _sourcesByTargetFramework = sourcesByTargetFramework;
    }

    /// <summary>The set of target frameworks this session's dataflow graph was built for — a session must be rebuilt (not reused) if a project's TFM set changes.</summary>
    public IReadOnlyCollection<string> TargetFrameworks => _sourcesByTargetFramework.Keys.ToImmutableArray();

    public static async Task<DependenciesSnapshotSession> CreateAsync(string projectPath, IReadOnlyCollection<string> targetFrameworks)
    {
        var unconfiguredProject = new DependencyTreeBridgeBuilder.BridgeUnconfiguredProject(projectPath);

        var slices = targetFrameworks
            .Select(tfm => (Tfm: tfm, Slice: ProjectConfigurationSlice.Create(
                string.IsNullOrEmpty(tfm)
                    ? ImmutableDictionary<string, string>.Empty
                    : ImmutableDictionary<string, string>.Empty.Add("TargetFramework", tfm))))
            .ToImmutableArray();

        var sourcesByTfm = new Dictionary<string, UnoDevelopActiveConfigurationSubscriptionSource>();
        var subscriptionSources = new List<(ProjectConfigurationSlice, IActiveConfigurationSubscriptionSource)>();
        foreach (var (tfm, slice) in slices)
        {
            var configuredProject = new DependencyTreeBridgeBuilder.BridgeConfiguredProject(unconfiguredProject, slice);
            var catalog = new DependencyTreeBridgeBuilder.BridgeCatalogSnapshot();
            var source = new UnoDevelopActiveConfigurationSubscriptionSource(configuredProject, catalog);

            sourcesByTfm[tfm] = source;
            subscriptionSources.Add((slice, source));
        }

        var sliceScope = new CompositionScope(RealMefHost.ExportProvider)
            .WithInstance<UnconfiguredProject>(unconfiguredProject);
        var sliceSubscriber = sliceScope.Activate<MSBuildDependencySubscriber>();

        var activeConfiguredProject = new DependencyTreeBridgeBuilder.BridgeConfiguredProject(unconfiguredProject, slices[0].Slice);

        var providerScope = new CompositionScope(RealMefHost.ExportProvider)
            .WithInstance<UnconfiguredProject>(unconfiguredProject)
            .WithInstance<IUnconfiguredProjectCommonServices>(new UnoDevelopUnconfiguredProjectCommonServices(unconfiguredProject))
            .WithInstance<IUnconfiguredProjectTasksService>(new UnoDevelopUnconfiguredProjectTasksService())
            .WithInstance<IActiveConfigurationGroupSubscriptionService>(new UnoDevelopActiveConfigurationGroupSubscriptionService(new ConfigurationSubscriptionSources(subscriptionSources)))
            .WithInstance<IActiveConfiguredProjectProvider>(new UnoDevelopActiveConfiguredProjectProvider(activeConfiguredProject))
            .WithInstance<IProjectThreadingService>(new UnoDevelopThreadingService())
            .WithInstance<IProjectFaultHandlerService>(new UnoDevelopProjectFaultHandlerService())
            .WithManyInstances<IDependencySliceSubscriber>(new[] { sliceSubscriber })
            .WithManyInstances(Array.Empty<IDependencySubscriber>());

        var provider = providerScope.Activate<DependenciesSnapshotProvider>();

        // Sets up the dataflow topology (blocks + links). No snapshot is produced yet — the
        // per-slice MSBuildDependencySubscriber output stays empty until PostUpdate posts real
        // rule data (see RefreshAsync), same as a real design-time build hasn't run yet.
        await provider.EnsureInitializedAsync();

        return new DependenciesSnapshotSession(provider, sourcesByTfm);
    }

    /// <summary>Well-known key stamping which <see cref="RefreshAsync"/> generation produced a given snapshot — see <see cref="UnoDevelopActiveConfigurationSubscriptionSource.PostUpdate"/>.</summary>
    public static readonly NamedIdentity GenerationKey = new(nameof(GenerationKey));

    private int _generation;

    /// <summary>
    /// Posts new evaluation data into each target framework's slice and awaits a snapshot that
    /// actually reflects it — the dataflow topology from <see cref="CreateAsync"/> is a long-lived,
    /// continuously-running pipeline (slice 47) reused across calls, not rebuilt, so a naive "wait
    /// for the next emitted value" can race with a still-in-flight emission from a *previous*
    /// RefreshAsync call. Each call stamps a monotonically increasing generation number into its
    /// posted updates (<see cref="GenerationKey"/>) and only accepts a snapshot whose merged
    /// <c>DataSourceVersions</c> carries that generation or later.
    /// </summary>
    public async Task<DependenciesSnapshot?> RefreshAsync(
        IReadOnlyDictionary<string, IReadOnlyList<DependencyBridgeItem>> itemsByTargetFramework,
        TimeSpan? timeout = null)
    {
        var generation = ++_generation;

        var tcs = new TaskCompletionSource<DependenciesSnapshot>();
        using var subscription = _provider.Source.LinkTo(
            new ActionBlock<IProjectVersionedValue<DependenciesSnapshot>>(v =>
            {
                if (v.DataSourceVersions.TryGetValue(GenerationKey, out var version) && version.CompareTo(generation) >= 0)
                {
                    tcs.TrySetResult(v.Value);
                }
            }),
            new DataflowLinkOptions());

        foreach (var (tfm, source) in _sourcesByTargetFramework)
        {
            if (itemsByTargetFramework.TryGetValue(tfm, out var items))
            {
                if (!_priorItemsByRuleNamePerTfm.TryGetValue(tfm, out var priorItemsByRuleName))
                {
                    _priorItemsByRuleNamePerTfm[tfm] = priorItemsByRuleName = new Dictionary<string, IImmutableDictionary<string, IImmutableDictionary<string, string>>>();
                }

                source.PostUpdate(DependencyBridgeSubscriptionTranslator.BuildUpdate(items, priorItemsByRuleName), generation);
            }
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
        return completed == tcs.Task ? tcs.Task.Result : null;
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
