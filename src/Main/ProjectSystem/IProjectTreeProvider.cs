// Clean-room reimplementation. See docs/project-system.md.

namespace Microsoft.VisualStudio.ProjectSystem;

/// <summary>
/// Minimal CPS IProjectTreeProvider surface used by dotnet/project-system.
/// </summary>
public interface IProjectTreeProvider
{
    /// <summary>Returns the absolute file path for the given tree node, or null.</summary>
    string? GetPath(IProjectTree node);

    /// <summary>
    /// Returns the relative directory under which new items should be added when
    /// the user initiates an Add New Item on <paramref name="node"/>, or null if
    /// the node does not support that operation.
    /// </summary>
    string? GetAddNewItemDirectory(IProjectTree node);

    /// <summary>
    /// Finds the node in the subtree rooted at <paramref name="root"/> whose
    /// <see cref="IProjectTree.FilePath"/> matches <paramref name="path"/>,
    /// or null if not found.
    /// </summary>
    IProjectTree? FindByPath(IProjectTree root, string path);
}
