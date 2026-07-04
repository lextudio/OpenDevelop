// Wires UnoDevelop's evaluation data through the real linked CPS dependency dataflow pipeline
// (MSBuildDependencySubscriber + DependenciesSnapshotProvider, slices 41/42) instead of the
// imperative per-call DependencyTreeBridgeBuilder path.
//
// Since slice 47, the dataflow graph itself (DependenciesSnapshotSession) is kept alive per project
// across calls rather than rebuilt from scratch every refresh — only new evaluation data is posted
// into the existing graph. A session is discarded and rebuilt if a project's target framework set
// changes (the graph's slice topology is fixed at construction). See docs/project-system.md
// (Slices 43, 47).

using Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Snapshot;

namespace Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies;

public static class SharpDevelopDependenciesSnapshotFactory
{
    private static readonly Dictionary<string, DependenciesSnapshotSession> SessionsByProjectPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a <see cref="DependenciesSnapshot"/> for one project from its already-gathered
    /// dependency items, grouped by target framework (a null/empty key means "no TFM slicing").
    /// Returns null if no snapshot was produced within <paramref name="timeout"/> (falls back to
    /// the existing <see cref="DependencyTreeBridgeBuilder"/> path in that case). Reuses this
    /// project's existing dataflow session (slice 47) when its target framework set is unchanged.
    /// </summary>
    internal static async Task<DependenciesSnapshot?> BuildSnapshotAsync(
        string projectPath,
        IReadOnlyDictionary<string, IReadOnlyList<DependencyBridgeItem>> itemsByTargetFramework,
        TimeSpan? timeout = null)
    {
        if (itemsByTargetFramework.Count == 0)
        {
            return null;
        }

        if (SessionsByProjectPath.TryGetValue(projectPath, out var session)
            && !session.TargetFrameworks.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(itemsByTargetFramework.Keys.OrderBy(t => t, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            await session.DisposeAsync();
            SessionsByProjectPath.Remove(projectPath);
            session = null;
        }

        if (session is null)
        {
            session = await DependenciesSnapshotSession.CreateAsync(projectPath, itemsByTargetFramework.Keys.ToImmutableArray());
            SessionsByProjectPath[projectPath] = session;
        }

        return await session.RefreshAsync(itemsByTargetFramework, timeout);
    }

    /// <summary>
    /// Disposes and forgets any session whose project path isn't in <paramref name="activeProjectPaths"/>
    /// — the mechanism for reclaiming sessions for projects removed/unloaded from the solution or a
    /// closed solution (pass an empty collection). Detecting "this specific project was removed" via
    /// SharpDevelop's project events isn't reliable (there's no dedicated whole-project-removed event,
    /// only per-item add/remove — see docs/project-system.md, Slice 48), so this instead prunes
    /// against the current live project set after each full Solution Explorer rebuild.
    /// </summary>
    internal static async Task PruneSessionsExceptAsync(IReadOnlyCollection<string> activeProjectPaths)
    {
        var stalePaths = SessionsByProjectPath.Keys.Except(activeProjectPaths, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        foreach (var path in stalePaths)
        {
            if (SessionsByProjectPath.Remove(path, out var session))
            {
                await session.DisposeAsync();
            }
        }
    }

    /// <summary>Disposes every session — call when the solution closes.</summary>
    internal static Task ClearAllAsync() => PruneSessionsExceptAsync(Array.Empty<string>());

    /// <summary>
    /// Builds the dependencies <see cref="MutableProjectTree"/> node for one project via the real
    /// dataflow pipeline (<see cref="BuildSnapshotAsync"/>), using the same
    /// <see cref="DependenciesTreeBuilder"/>/<see cref="DependencyTreeBridgeBuilder.BridgeTreeOperations"/>
    /// tree-construction path the imperative <see cref="DependencyTreeBridgeBuilder"/> already uses,
    /// so the result is a drop-in replacement wherever that path's <c>MutableProjectTree</c> is
    /// consumed. Returns null if no snapshot was produced (caller should fall back to
    /// <see cref="DependencyTreeBridgeBuilder.BuildDependenciesTree"/>). See docs/project-system.md
    /// (Slice 46).
    /// </summary>
    public static async Task<MutableProjectTree?> BuildTreeAsync(
        string projectPath,
        IReadOnlyDictionary<string, IReadOnlyList<DependencyBridgeItem>> itemsByTargetFramework,
        TimeSpan? timeout = null)
    {
        var snapshot = await BuildSnapshotAsync(projectPath, itemsByTargetFramework, timeout);
        if (snapshot is null)
        {
            return null;
        }

        var unconfiguredProject = new DependencyTreeBridgeBuilder.BridgeUnconfiguredProject(projectPath);
        var builder = new DependenciesTreeBuilder(unconfiguredProject)
        {
            TreeConstruction = new DependencyTreeBridgeBuilder.BridgeTreeOperations()
        };

        return (MutableProjectTree)await builder.BuildTreeAsync(null, snapshot, CancellationToken.None);
    }
}
