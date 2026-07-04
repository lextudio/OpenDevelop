// Clean-room stubs for the CPS design-time-build subscription surface consumed by
// MSBuildDependencySubscriber. These types (IProjectSubscriptionUpdate, the rule-scoped
// sources on IActiveConfigurationSubscriptionSource) live in the closed
// Microsoft.VisualStudio.ProjectSystem.dll base assembly. Reconstructed from the public
// usage pattern only. See docs/project-system.md (Slice 41).
//
// NOTE: "SourceBlock" here is deliberately NOT System.Threading.Tasks.Dataflow.ISourceBlock<T> —
// upstream's real rule-name-filtered LinkTo has no equivalent public single-predicate overload
// we can reuse faithfully, so IProjectRuleSourceBlock defines its own LinkTo taking a
// RuleNameLinkOptions that carries the filter, and filters internally using a real TPL
// TransformManyBlock before forwarding to the caller's target block.

using System.Threading.Tasks.Dataflow;
using Microsoft.VisualStudio.ProjectSystem.Properties;

namespace Microsoft.VisualStudio.ProjectSystem;

public interface IProjectSubscriptionUpdate
{
    IImmutableDictionary<string, IProjectChangeDescription> ProjectChanges { get; }
}

public interface IActiveConfigurationSubscriptionSource
{
    IProjectRuleSource ProjectRuleSource { get; }
    IProjectRuleSource JointRuleSource { get; }
    IProjectValueDataSource<ConfiguredProject> ActiveConfiguredProjectSource { get; }
    IProjectValueDataSource<IProjectCatalogSnapshot> ProjectCatalogSource { get; }
}

/// <summary>A design-time-build data source scoped to evaluation-only or joint (evaluation+build) rules.</summary>
public interface IProjectRuleSource
{
    IProjectRuleSourceBlock SourceBlock { get; }

    /// <summary>Posts a new subscription update to this source's linked consumers.</summary>
    bool Post(IProjectVersionedValue<IProjectSubscriptionUpdate> value);
}

public interface IProjectRuleSourceBlock
{
    IDisposable LinkTo(ITargetBlock<IProjectVersionedValue<IProjectSubscriptionUpdate>> target, RuleNameLinkOptions options);
}

/// <summary>Filter describing which rule name(s) a consumer wants updates for.</summary>
public sealed class RuleNameLinkOptions
{
    private readonly IImmutableSet<string>? _ruleNames;
    private readonly IImmutableSet<string>? _jointRuleNamesA;
    private readonly IImmutableSet<string>? _jointRuleNamesB;

    internal RuleNameLinkOptions(IImmutableSet<string>? ruleNames, IImmutableSet<string>? jointRuleNamesA, IImmutableSet<string>? jointRuleNamesB)
    {
        _ruleNames = ruleNames;
        _jointRuleNamesA = jointRuleNamesA;
        _jointRuleNamesB = jointRuleNamesB;
    }

    public bool Matches(IProjectSubscriptionUpdate update)
    {
        if (_ruleNames is not null)
        {
            return update.ProjectChanges.Keys.Any(_ruleNames.Contains);
        }

        if (_jointRuleNamesA is not null && _jointRuleNamesB is not null)
        {
            return update.ProjectChanges.Keys.Any(_jointRuleNamesA.Contains) ||
                   update.ProjectChanges.Keys.Any(_jointRuleNamesB.Contains);
        }

        return true;
    }
}

public static class DataflowOption
{
    /// <summary>Real TPL Dataflow link options: propagate completion/faults downstream.</summary>
    public static DataflowLinkOptions PropagateCompletion { get; } = new() { PropagateCompletion = true };

    public static RuleNameLinkOptions WithRuleNames(IImmutableSet<string> ruleNames) => new(ruleNames, null, null);

    public static RuleNameLinkOptions WithJointRuleNames(IImmutableSet<string> evaluationRuleNames, IImmutableSet<string> buildRuleNames) =>
        new(null, evaluationRuleNames, buildRuleNames);
}

public static class ProjectVersionedValueExtensions
{
    /// <summary>Applies <paramref name="selector"/> to the value while preserving its data-source versions.</summary>
    public static IProjectVersionedValue<TOut> Derive<TIn, TOut>(this IProjectVersionedValue<TIn> value, Func<TIn, TOut> selector) =>
        new ProjectVersionedValue<TOut>(selector(value.Value), value.DataSourceVersions);
}

/// <summary>Concrete, postable implementation of <see cref="IProjectRuleSource"/>, backed by a real broadcast block with rule-name filtering applied per-link.</summary>
public sealed class ProjectRuleSource : IProjectRuleSource
{
    private readonly IBroadcastBlock<IProjectVersionedValue<IProjectSubscriptionUpdate>> _broadcastBlock =
        DataflowBlockSlim.CreateBroadcastBlock<IProjectVersionedValue<IProjectSubscriptionUpdate>>();

    public IProjectRuleSourceBlock SourceBlock => new Block(_broadcastBlock);

    public bool Post(IProjectVersionedValue<IProjectSubscriptionUpdate> value) => _broadcastBlock.Post(value);

    private sealed class Block : IProjectRuleSourceBlock
    {
        private readonly IBroadcastBlock<IProjectVersionedValue<IProjectSubscriptionUpdate>> _source;

        public Block(IBroadcastBlock<IProjectVersionedValue<IProjectSubscriptionUpdate>> source) => _source = source;

        public IDisposable LinkTo(ITargetBlock<IProjectVersionedValue<IProjectSubscriptionUpdate>> target, RuleNameLinkOptions options)
        {
            var filter = new TransformManyBlock<IProjectVersionedValue<IProjectSubscriptionUpdate>, IProjectVersionedValue<IProjectSubscriptionUpdate>>(
                v => options.Matches(v.Value) ? new[] { v } : Array.Empty<IProjectVersionedValue<IProjectSubscriptionUpdate>>());

            var linkToFilter = _source.LinkTo(filter, new DataflowLinkOptions { PropagateCompletion = true });
            var linkToTarget = filter.LinkTo(target, new DataflowLinkOptions { PropagateCompletion = true });

            return new CompositeDisposable(linkToFilter, linkToTarget);
        }
    }
}

internal sealed class CompositeDisposable : IDisposable
{
    private readonly IDisposable[] _items;

    public CompositeDisposable(params IDisposable[] items) => _items = items;

    public void Dispose()
    {
        foreach (var item in _items)
        {
            item.Dispose();
        }
    }
}

