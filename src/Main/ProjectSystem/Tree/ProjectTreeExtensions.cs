// Clean-room reimplementation. See docs/project-system.md.
// Provides the IProjectTree extension methods used by dotnet/project-system.
// The upstream IProjectTreeExtensions.cs pulls in IDependency / IProjectCatalogSnapshot
// and other deep CPS dependencies; this shim covers only the methods the tree builders need.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.VisualStudio.ProjectSystem;

public static class ProjectTreeExtensions
{
    /// <summary>
    /// Returns the first direct child whose <see cref="IProjectTree.Caption"/> equals
    /// <paramref name="caption"/> (case-insensitive), or null.
    /// </summary>
    public static IProjectTree? FindChildWithCaption(this IProjectTree tree, string caption) =>
        tree.Children.FirstOrDefault(
            c => string.Equals(c.Caption, caption, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the first direct child whose <see cref="IProjectTree.Flags"/> contains
    /// all flags in <paramref name="flags"/>, or null.
    /// </summary>
    public static IProjectTree? FindChildWithFlags(this IProjectTree tree, ProjectTreeFlags flags)
    {
        foreach (var child in tree.Children)
        {
            if (child.Flags.Contains(flags))
                return child;
        }
        return null;
    }

    /// <summary>
    /// Returns all direct children whose flags contain all flags in <paramref name="flags"/>.
    /// </summary>
    public static IEnumerable<IProjectTree> FindChildrenWithFlags(
        this IProjectTree tree, ProjectTreeFlags flags) =>
        tree.Children.Where(c => c.Flags.Contains(flags));

    /// <summary>
    /// Searches the subtree (depth-first) for a node with the given <paramref name="filePath"/>.
    /// </summary>
    public static IProjectTree? FindByFilePath(this IProjectTree root, string filePath)
    {
        if (string.Equals(root.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            return root;
        foreach (var child in root.Children)
        {
            var found = child.FindByFilePath(filePath);
            if (found is not null) return found;
        }
        return null;
    }

    // ── ProjectTreeFlags helpers (mirrors ProjectTreeFlagsExtensions from project-system) ──────

    public static bool IsProjectRoot(this ProjectTreeFlags flags) =>
        flags.Contains(ProjectTreeFlags.Common.ProjectRoot);

    public static bool IsIncludedInProject(this ProjectTreeFlags flags) =>
        !flags.Contains(ProjectTreeFlags.Common.IncludeInProjectCandidate);

    public static bool IsMissingOnDisk(this ProjectTreeFlags flags) =>
        !flags.Contains(ProjectTreeFlags.Common.FileSystemEntity);

    public static bool IsFolder(this ProjectTreeFlags flags) =>
        flags.Contains(ProjectTreeFlags.Common.Folder);

    // ── Depth-first traversal ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates all descendants (depth-first) of this node, not including the node itself.
    /// </summary>
    public static IEnumerable<IProjectTree> GetSelfAndDescendants(this IProjectTree root)
    {
        yield return root;
        foreach (var child in root.Children)
        foreach (var desc in child.GetSelfAndDescendants())
            yield return desc;
    }
}
