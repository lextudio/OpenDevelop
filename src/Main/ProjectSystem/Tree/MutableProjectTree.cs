// Clean-room implementation. See docs/project-system.md.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.ProjectSystem.Properties;

namespace Microsoft.VisualStudio.ProjectSystem;

/// <summary>
/// Mutable builder for CPS tree nodes. After construction, expose via IProjectTree.
/// This is the UnoDevelop-internal type; upstream project-system code sees only IProjectTree.
/// </summary>
public sealed class MutableProjectTree : IProjectTree2
{
    private readonly List<MutableProjectTree> _children = new();

    public MutableProjectTree(string caption, string? filePath = null)
    {
        Caption = caption;
        FilePath = filePath;
    }

    // ── IProjectTree ─────────────────────────────────────────────────────────

    public string Caption { get; set; }
    public string? FilePath { get; set; }
    public ProjectTreeFlags Flags { get; set; } = ProjectTreeFlags.Empty;
    public IProjectTree? Parent { get; private set; }
    public IEnumerable<IProjectTree> Children => _children;
    public IProjectTree Root => Parent?.Root ?? this;
    public bool IsFolder => Flags.Contains(ProjectTreeFlags.Common.Folder) ||
                            Flags.Contains(ProjectTreeFlags.Common.VirtualFolder);
    public bool IsRoot => Parent is null;
    public IRule? BrowseObjectProperties { get; set; }
    public ProjectImageMoniker? Icon { get; set; }
    public ProjectImageMoniker? ExpandedIcon { get; set; }
    public bool Visible { get; set; } = true;
    public int DisplayOrder { get; set; }

    // ── IProjectTree2 ────────────────────────────────────────────────────────

    public bool IsProjectItem { get; set; }

    // ── Builder API ──────────────────────────────────────────────────────────

    /// <summary>Direct mutable access to children for tree builders.</summary>
    public IReadOnlyList<MutableProjectTree> MutableChildren => _children;

    public MutableProjectTree AddChild(MutableProjectTree child)
    {
        if (child.Parent is not null)
            throw new InvalidOperationException("Node already has a parent.");
        child.Parent = this;
        _children.Add(child);
        return this;
    }

    public bool RemoveChild(MutableProjectTree child)
    {
        if (_children.Remove(child))
        {
            child.Parent = null;
            return true;
        }
        return false;
    }

    public void ClearChildren()
    {
        foreach (var c in _children) c.Parent = null;
        _children.Clear();
    }

    // ── IProjectTree immutable-tree mutation ──────────────────────────────────

    /// <summary>
    /// Returns a shallow copy of this node with <paramref name="child"/> appended.
    /// The child's Parent is set to the new copy. Callers navigate via .Parent.
    /// </summary>
    public IProjectTree Add(IProjectTree child)
    {
        var copy = ShallowCopy();
        var mutableChild = child as MutableProjectTree ?? new MutableProjectTree(child.Caption, child.FilePath)
        {
            Flags = child.Flags,
            BrowseObjectProperties = child.BrowseObjectProperties,
            Icon = child.Icon,
            ExpandedIcon = child.ExpandedIcon,
            Visible = child.Visible,
            DisplayOrder = child.DisplayOrder,
        };
        mutableChild.Parent = copy;
        copy._children.Add(mutableChild);
        // Return the child (with Parent set to copy) so callers can do .Add(c).Parent
        return mutableChild;
    }

    public IProjectTree Remove(IProjectTree child)
    {
        var copy = ShallowCopy();
        copy._children.RemoveAll(c => c == child || c.Caption == child.Caption);
        foreach (var c in copy._children) c.Parent = copy;
        return copy;
    }

    public IProjectTree SetProperties(
        string? caption = null,
        string? filePath = null,
        IRule? browseObjectProperties = null,
        ProjectImageMoniker? icon = null,
        ProjectImageMoniker? expandedIcon = null,
        bool? visible = null,
        ProjectTreeFlags? flags = null,
        int? displayOrder = null)
    {
        var copy = ShallowCopy();
        if (caption is not null) copy.Caption = caption;
        if (filePath is not null) copy.FilePath = filePath;
        if (browseObjectProperties is not null) copy.BrowseObjectProperties = browseObjectProperties;
        if (icon is not null) copy.Icon = icon;
        if (expandedIcon is not null) copy.ExpandedIcon = expandedIcon;
        if (visible is not null) copy.Visible = visible.Value;
        if (flags is not null) copy.Flags = flags.Value;
        if (displayOrder is not null) copy.DisplayOrder = displayOrder.Value;
        return copy;
    }

    private MutableProjectTree ShallowCopy()
    {
        var copy = new MutableProjectTree(Caption, FilePath)
        {
            Flags = Flags,
            BrowseObjectProperties = BrowseObjectProperties,
            Icon = Icon,
            ExpandedIcon = ExpandedIcon,
            Visible = Visible,
            DisplayOrder = DisplayOrder,
            IsProjectItem = IsProjectItem,
        };
        foreach (var c in _children)
        {
            copy._children.Add(c);
            c.Parent = copy;
        }
        return copy;
    }

    public override string ToString() => Caption;
}

/// <summary>
/// Mutable tree node that also implements IProjectItemTree2 (maps to an MSBuild item).
/// </summary>
public sealed class MutableProjectItemTree : IProjectItemTree2
{
    private readonly List<MutableProjectItemTree> _children = new();

    public MutableProjectItemTree(string caption, IProjectPropertiesContext item, string? filePath = null)
    {
        Caption = caption;
        Item = item;
        FilePath = filePath;
    }

    // ── IProjectTree ─────────────────────────────────────────────────────────
    public string Caption { get; set; }
    public string? FilePath { get; set; }
    public ProjectTreeFlags Flags { get; set; } = ProjectTreeFlags.Empty;
    public IProjectTree? Parent { get; private set; }
    public IEnumerable<IProjectTree> Children => _children.Cast<IProjectTree>();
    public IProjectTree Root => Parent?.Root ?? this;
    public bool IsFolder => false;
    public bool IsRoot => Parent is null;
    public IRule? BrowseObjectProperties { get; set; }
    public ProjectImageMoniker? Icon { get; set; }
    public ProjectImageMoniker? ExpandedIcon { get; set; }
    public bool Visible { get; set; } = true;
    public int DisplayOrder { get; set; }

    // ── IProjectTree2 ────────────────────────────────────────────────────────
    public bool IsProjectItem => true;

    // ── IProjectItemTree ─────────────────────────────────────────────────────
    public IProjectPropertiesContext Item { get; }
    public bool IsLinked { get; set; }
    public IPropertySheet? PropertySheet { get; set; }

    // ── Builder API ──────────────────────────────────────────────────────────
    public IReadOnlyList<MutableProjectItemTree> MutableChildren => _children;

    public MutableProjectItemTree AddChild(MutableProjectItemTree child)
    {
        if (child.Parent is not null)
            throw new InvalidOperationException("Node already has a parent.");
        child.Parent = this;
        _children.Add(child);
        return this;
    }

    // ── IProjectTree immutable-tree mutation ──────────────────────────────────

    public IProjectTree Add(IProjectTree child)
    {
        var mutableChild = child as MutableProjectItemTree ?? new MutableProjectItemTree(child.Caption, Item, child.FilePath)
        {
            Flags = child.Flags,
            BrowseObjectProperties = child.BrowseObjectProperties,
            Icon = child.Icon,
            ExpandedIcon = child.ExpandedIcon,
            Visible = child.Visible,
            DisplayOrder = child.DisplayOrder,
        };
        mutableChild.Parent = this;
        _children.Add(mutableChild);
        return mutableChild;
    }

    public IProjectTree Remove(IProjectTree child)
    {
        _children.RemoveAll(c => c == child || c.Caption == child.Caption);
        foreach (var c in _children) c.Parent = this;
        return this;
    }

    public IProjectTree SetProperties(
        string? caption = null,
        string? filePath = null,
        IRule? browseObjectProperties = null,
        ProjectImageMoniker? icon = null,
        ProjectImageMoniker? expandedIcon = null,
        bool? visible = null,
        ProjectTreeFlags? flags = null,
        int? displayOrder = null)
    {
        if (caption is not null) Caption = caption;
        if (filePath is not null) FilePath = filePath;
        if (browseObjectProperties is not null) BrowseObjectProperties = browseObjectProperties;
        if (icon is not null) Icon = icon;
        if (expandedIcon is not null) ExpandedIcon = expandedIcon;
        if (visible is not null) Visible = visible.Value;
        if (flags is not null) Flags = flags.Value;
        if (displayOrder is not null) DisplayOrder = displayOrder.Value;
        return this;
    }

    public override string ToString() => Caption;
}
