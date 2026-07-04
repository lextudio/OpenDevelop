// Clean-room stubs for the CPS active-configuration/threading/fault-handling services consumed
// by DependenciesSnapshotProvider. These live in the closed Microsoft.VisualStudio.ProjectSystem.dll
// base assembly (or its VS-hosted implementations) — not in the MIT .Managed repo we vendor.
// UnoDevelop has no per-TFM ConfiguredProject activation or JoinableTaskFactory-based threading
// model yet, so these are minimal, mostly-inert implementations sufficient to compile and run
// the linked subscriber pipeline against a single always-active configuration.
// See docs/project-system.md (Slice 42).

using System.Threading.Tasks.Dataflow;

namespace Microsoft.VisualStudio.ProjectSystem;

/// <summary>One slice's worth of (slice, subscription source) pairs, as produced per configuration-group update.</summary>
public sealed class ConfigurationSubscriptionSources : IEnumerable<(ProjectConfigurationSlice Slice, IActiveConfigurationSubscriptionSource Source)>
{
    private readonly ImmutableArray<(ProjectConfigurationSlice, IActiveConfigurationSubscriptionSource)> _items;

    public ConfigurationSubscriptionSources(IEnumerable<(ProjectConfigurationSlice, IActiveConfigurationSubscriptionSource)> items) =>
        _items = items.ToImmutableArray();

    public IEnumerator<(ProjectConfigurationSlice Slice, IActiveConfigurationSubscriptionSource Source)> GetEnumerator() =>
        ((IEnumerable<(ProjectConfigurationSlice, IActiveConfigurationSubscriptionSource)>)_items).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Publishes the current set of active configuration slices and their subscription sources.</summary>
public interface IActiveConfigurationGroupSubscriptionService
{
    ISourceBlock<IProjectVersionedValue<ConfigurationSubscriptionSources>> SourceBlock { get; }
}

/// <summary>Provides the project's currently-active <see cref="ConfiguredProject"/> as a data source.</summary>
public interface IActiveConfiguredProjectProvider : IProjectValueDataSource<ConfiguredProject>
{
}

/// <summary>
/// Minimal clean-room threading service surface. UnoDevelop runs its Solution Explorer/dependency
/// pipeline directly on its own dispatcher rather than a JoinableTaskFactory-based join/switch
/// model, so these members are opaque placeholders only referenced by type, never invoked.
/// </summary>
public interface IProjectThreadingService
{
    object? JoinableTaskFactory { get; }
    object? JoinableTaskContext { get; }
}

public interface IUnconfiguredProjectCommonServices : IProjectCommonServices
{
    IProjectThreadingService ThreadingService { get; }
}

/// <summary>Runs a delegate while ensuring the project stays loaded. UnoDevelop has no project-unload model, so this just invokes the delegate directly.</summary>
public interface IUnconfiguredProjectTasksService
{
    Task LoadedProjectAsync(Func<Task> action);
}

public enum ProjectFaultSeverity
{
    Recoverable,
    LimitedFunctionality,
    Crash,
}

/// <summary>No-op fault registration: UnoDevelop surfaces dataflow faults via unhandled-exception logging elsewhere rather than a dedicated fault-isolation service.</summary>
public interface IProjectFaultHandlerService
{
    void RegisterFaultHandler(IDataflowBlock block, UnconfiguredProject project, ProjectFaultSeverity severity);
}
