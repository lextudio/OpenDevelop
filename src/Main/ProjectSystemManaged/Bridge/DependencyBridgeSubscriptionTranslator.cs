// Translates UnoDevelop's DependencyBridgeItem list (already gathered from live SharpDevelop
// projects or unloaded project XML — see UnoDevelopProjectTreeProvider) into an
// IProjectSubscriptionUpdate, the shape MSBuildDependencySubscriber expects from a real CPS
// design-time build. This is the missing link between UnoDevelop's evaluation data and the
// linked upstream dependency factories (slice 43 — see docs/project-system.md).
//
// IMPORTANT: MSBuildDependencySubscriber.Transform() indexes ProjectChanges[unresolvedRuleName]
// directly (not TryGetValue) for every registered factory, so every posted update MUST contain
// an entry for every factory's UnresolvedRuleName/ResolvedRuleName, even when that dependency
// type currently has zero items (with AnyChanges = false so MSBuildDependencyCollection skips it
// cheaply instead of creating an empty group).

using Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies;
using Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Subscriptions.MSBuildDependencies;

namespace Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Subscriptions;

internal static class DependencyBridgeSubscriptionTranslator
{
    private static readonly DependencyBridgeItemKind[] AllKinds =
    {
        DependencyBridgeItemKind.Assembly,
        DependencyBridgeItemKind.Project,
        DependencyBridgeItemKind.Package,
        DependencyBridgeItemKind.Framework,
        DependencyBridgeItemKind.Sdk,
        DependencyBridgeItemKind.Analyzer,
        DependencyBridgeItemKind.Com,
    };

    /// <summary>
    /// The full set of MSBuild dependency factories UnoDevelop links, discovered via a real VS MEF
    /// <see cref="ExportProvider"/> (<see cref="RealMefHost"/>) rather than a hand-maintained list —
    /// this reflects whatever <c>[Export(typeof(IMSBuildDependencyFactory))]</c>-attributed classes
    /// are actually linked into this assembly. See docs/project-system.md (Slice 44).
    /// </summary>
    public static IReadOnlyList<MSBuildDependencyFactoryBase> AllFactories { get; } =
        RealMefHost.ExportProvider.GetExportedValues<IMSBuildDependencyFactory>()
            .Cast<MSBuildDependencyFactoryBase>()
            .ToImmutableArray();

    /// <summary>
    /// Builds a full-width subscription update from the current dependency items for one project
    /// (already filtered to one configuration slice by the caller). Every registered factory's
    /// rule pair is present, even if empty — see remarks above.
    /// </summary>
    /// <param name="items">The project's current dependency items for this slice.</param>
    /// <param name="priorItemsByRuleName">
    /// Per-rule item-id-to-metadata maps from the previous call, keyed by rule name and updated in
    /// place. Required because <see cref="Subscriptions.MSBuildDependencies.MSBuildDependencyCollection"/>
    /// is stateful across calls once a <see cref="DependenciesSnapshotSession"/> (slice 47) keeps
    /// the dataflow graph alive: without a real diff, every call would report its full item set as
    /// "added" and nothing as "removed", so a dependency deleted from the project would never be
    /// removed from the accumulated snapshot. This must be the actual prior *metadata*, not just
    /// prior ids — <c>MSBuildDependencyCollection</c>'s removal handling indexes
    /// <c>Before.Items[removedItemSpec]</c> directly (not a safe lookup) to resolve a removed
    /// resolved-item's original evaluation id, so an empty/id-only <c>Before</c> snapshot throws
    /// <see cref="KeyNotFoundException"/> and silently faults the dataflow block on any removal.
    /// Pass an empty dictionary for a one-shot (non-session) build, where "everything is newly
    /// added" is correct and nothing is ever removed.
    /// </param>
    public static IProjectSubscriptionUpdate BuildUpdate(
        IReadOnlyList<DependencyBridgeItem> items,
        IDictionary<string, IImmutableDictionary<string, IImmutableDictionary<string, string>>>? priorItemsByRuleName = null)
    {
        var changes = ImmutableDictionary.CreateBuilder<string, IProjectChangeDescription>();

        foreach (var kind in AllKinds)
        {
            var factory = DependencyTreeBridgeBuilder.GetFactory(kind);
            var kindItems = items.Where(i => i.Kind == kind).ToImmutableArray();
            var itemsBySpec = kindItems.ToImmutableDictionary(i => i.Id, DependencyTreeBridgeBuilder.MergeMetadata);

            // UnoDevelop gathers already-resolved item metadata directly (no separate
            // design-time-build "resolved" phase), so the same view serves both the
            // evaluation (unresolved) and build (resolved) rule for each dependency type.
            changes[factory.UnresolvedRuleName] = BuildDescription(itemsBySpec, factory.UnresolvedRuleName, priorItemsByRuleName);
            changes[factory.ResolvedRuleName] = BuildDescription(itemsBySpec, factory.ResolvedRuleName, priorItemsByRuleName);
        }

        return new ProjectSubscriptionUpdate(changes.ToImmutable());
    }

    private static IProjectChangeDescription BuildDescription(
        IImmutableDictionary<string, IImmutableDictionary<string, string>> itemsBySpec,
        string ruleName,
        IDictionary<string, IImmutableDictionary<string, IImmutableDictionary<string, string>>>? priorItemsByRuleName)
    {
        IImmutableDictionary<string, IImmutableDictionary<string, string>> priorItemsBySpec;
        if (priorItemsByRuleName is null)
        {
            priorItemsBySpec = ImmutableDictionary<string, IImmutableDictionary<string, string>>.Empty;
        }
        else
        {
            priorItemsByRuleName.TryGetValue(ruleName, out priorItemsBySpec!);
            priorItemsBySpec ??= ImmutableDictionary<string, IImmutableDictionary<string, string>>.Empty;
            priorItemsByRuleName[ruleName] = itemsBySpec;
        }

        var currentIds = itemsBySpec.Keys.ToImmutableHashSet(StringComparer.Ordinal);
        var priorIds = priorItemsBySpec.Keys.ToImmutableHashSet(StringComparer.Ordinal);

        var addedItems = currentIds.Except(priorIds);
        var removedItems = priorIds.Except(currentIds);
        // Items present both times are reported as "changed" too — metadata (e.g. a package
        // version bump) could differ even when the item id itself didn't change, and re-reporting
        // an unchanged item as "changed" is harmless (the factory's TryUpdate just produces an
        // equivalent dependency).
        var changedItems = currentIds.Intersect(priorIds);

        var after = new ProjectChangeSnapshot(itemsBySpec);
        var before = new ProjectChangeSnapshot(priorItemsBySpec);

        var diff = new ProjectChangeDiff(
            addedItems: addedItems,
            removedItems: removedItems,
            changedItems: changedItems,
            renamedItems: ImmutableDictionary<string, string>.Empty,
            changedProperties: ImmutableHashSet<string>.Empty,
            anyChanges: addedItems.Count > 0 || removedItems.Count > 0 || changedItems.Count > 0);

        return new ProjectChangeDescription(before, after, diff);
    }

    private sealed class ProjectSubscriptionUpdate : IProjectSubscriptionUpdate
    {
        public ProjectSubscriptionUpdate(IImmutableDictionary<string, IProjectChangeDescription> projectChanges) => ProjectChanges = projectChanges;
        public IImmutableDictionary<string, IProjectChangeDescription> ProjectChanges { get; }
    }

    private sealed class ProjectChangeDescription : IProjectChangeDescription
    {
        public ProjectChangeDescription(IProjectChangeSnapshot before, IProjectChangeSnapshot after, IProjectChangeDiff difference)
        {
            Before = before;
            After = after;
            Difference = difference;
        }

        public IProjectChangeSnapshot Before { get; }
        public IProjectChangeSnapshot After { get; }
        public IProjectChangeDiff Difference { get; }
    }

    private sealed class ProjectChangeSnapshot : IProjectChangeSnapshot
    {
        public ProjectChangeSnapshot(IImmutableDictionary<string, IImmutableDictionary<string, string>> items) => Items = items;
        public IImmutableDictionary<string, IImmutableDictionary<string, string>> Items { get; }
    }

    private sealed class ProjectChangeDiff : IProjectChangeDiff
    {
        public ProjectChangeDiff(
            IImmutableSet<string> addedItems,
            IImmutableSet<string> removedItems,
            IImmutableSet<string> changedItems,
            IImmutableDictionary<string, string> renamedItems,
            IImmutableSet<string> changedProperties,
            bool anyChanges)
        {
            AddedItems = addedItems;
            RemovedItems = removedItems;
            ChangedItems = changedItems;
            RenamedItems = renamedItems;
            ChangedProperties = changedProperties;
            AnyChanges = anyChanges;
        }

        public IImmutableSet<string> AddedItems { get; }
        public IImmutableSet<string> RemovedItems { get; }
        public IImmutableSet<string> ChangedItems { get; }
        public IImmutableDictionary<string, string> RenamedItems { get; }
        public IImmutableSet<string> ChangedProperties { get; }
        public bool AnyChanges { get; }
    }
}
