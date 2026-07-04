// Clean-room reimplementation. See docs/project-system.md and memory/opensource-cps-shim.md.
// Surface reconstructed from MIT dotnet/project-system usage only.

using System.Collections.Generic;
using Microsoft.VisualStudio.ProjectSystem.Properties;

namespace Microsoft.VisualStudio.ProjectSystem;

/// <summary>
/// An immutable node in the CPS project tree (Solution Explorer hierarchy).
/// </summary>
public interface IProjectTree
{
    /// <summary>Display label shown in Solution Explorer.</summary>
    string Caption { get; }

    /// <summary>Absolute path to the file/folder this node represents, or null.</summary>
    string? FilePath { get; }

    /// <summary>Capability flags that describe what this node is and supports.</summary>
    ProjectTreeFlags Flags { get; }

    /// <summary>Parent node, or null if this is the root.</summary>
    IProjectTree? Parent { get; }

    /// <summary>Direct children of this node.</summary>
    IEnumerable<IProjectTree> Children { get; }

    /// <summary>The root of the tree this node belongs to.</summary>
    IProjectTree Root { get; }

    /// <summary>True when this node represents a folder (virtual or on-disk).</summary>
    bool IsFolder { get; }

    /// <summary>True when this node is the tree root.</summary>
    bool IsRoot { get; }

    /// <summary>Properties shown in the Properties window, or null.</summary>
    IRule? BrowseObjectProperties { get; }

    /// <summary>Icon shown when the node is collapsed (or always, for leaf nodes).</summary>
    ProjectImageMoniker? Icon { get; }

    /// <summary>Icon shown when the node is expanded.</summary>
    ProjectImageMoniker? ExpandedIcon { get; }

    /// <summary>Whether this node should be visible in the tree.</summary>
    bool Visible { get; }

    /// <summary>Sort key; lower values appear first.</summary>
    int DisplayOrder { get; }

    // ── Immutable-tree mutation: each returns a new tree ────────────────────

    /// <summary>
    /// Returns a new parent node with <paramref name="child"/> added.
    /// The returned <paramref name="child"/> has its <see cref="Parent"/> set to the new parent.
    /// Callers typically navigate via .Parent to recover the updated parent.
    /// </summary>
    IProjectTree Add(IProjectTree child);

    /// <summary>Returns a new parent node with <paramref name="child"/> removed.</summary>
    IProjectTree Remove(IProjectTree child);

    /// <summary>Returns a new node with the specified properties replaced.</summary>
    IProjectTree SetProperties(
        string? caption = null,
        string? filePath = null,
        IRule? browseObjectProperties = null,
        ProjectImageMoniker? icon = null,
        ProjectImageMoniker? expandedIcon = null,
        bool? visible = null,
        ProjectTreeFlags? flags = null,
        int? displayOrder = null);
}

/// <summary>
/// Extended tree node — adds <see cref="IsProjectItem"/>.
/// </summary>
public interface IProjectTree2 : IProjectTree
{
    /// <summary>True when this node maps to an MSBuild project item.</summary>
    bool IsProjectItem { get; }
}

/// <summary>
/// Tree node that represents a specific MSBuild project item.
/// </summary>
public interface IProjectItemTree : IProjectTree
{
    /// <summary>The MSBuild item context (type + identity).</summary>
    IProjectPropertiesContext Item { get; }

    /// <summary>True when the item is a linked file (physical path outside the project directory).</summary>
    bool IsLinked { get; }

    /// <summary>The property sheet, if any.</summary>
    IPropertySheet? PropertySheet { get; }
}

/// <summary>
/// Combined: project-item node with the IProjectTree2 extension.
/// </summary>
public interface IProjectItemTree2 : IProjectItemTree, IProjectTree2
{
}
