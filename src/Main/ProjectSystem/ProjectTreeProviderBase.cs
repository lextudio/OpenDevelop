// Clean-room reimplementation. See docs/project-system.md.

namespace Microsoft.VisualStudio.ProjectSystem;

/// <summary>
/// Base implementation of <see cref="IProjectTreeProvider"/> with sensible defaults.
/// Derived classes implement <see cref="BuildTree"/> to produce the root node.
/// </summary>
public abstract class ProjectTreeProviderBase : IProjectTreeProvider
{
    // ── IProjectTreeProvider ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public virtual string? GetPath(IProjectTree node) => node.FilePath;

    /// <inheritdoc/>
    public virtual string? GetAddNewItemDirectory(IProjectTree node)
    {
        if (node.IsFolder && node.FilePath is { } path)
            return path;
        // For a file node, return its parent folder
        if (node.Parent is { } parent)
            return GetAddNewItemDirectory(parent);
        return null;
    }

    /// <inheritdoc/>
    public virtual IProjectTree? FindByPath(IProjectTree root, string path) =>
        root.FindByFilePath(path);

    // ── Builder contract ─────────────────────────────────────────────────────

    /// <summary>
    /// Build (or rebuild) the tree root. Called whenever the underlying project data changes.
    /// </summary>
    public abstract MutableProjectTree BuildTree();
}
