// Clean-room reimplementation of Microsoft.VisualStudio.ProjectSystem.ProjectTreeFlags.
// Reconstructed from the MIT-licensed dotnet/project-system usage and public API docs only.
// Do not port from the proprietary CPS SDK assemblies. See memory opensource-cps-shim.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Microsoft.VisualStudio.ProjectSystem;

/// <summary>
/// An immutable set of string "capability" flags attached to an <c>IProjectTree</c> node.
/// Flags describe what a node is and what it supports (e.g. it is a folder, it maps to a file on
/// disk, it can host Add Item, it is only visible in Show All Files). Commands and tree logic key
/// off these flags rather than off a fixed node-type enum, which is what makes the CPS tree
/// extensible. Flag membership is case-insensitive and order-insensitive.
/// </summary>
public readonly struct ProjectTreeFlags : IEquatable<ProjectTreeFlags>, IEnumerable<string>
{
    private readonly ImmutableHashSet<string>? _set;

    private ProjectTreeFlags(ImmutableHashSet<string> set) => _set = set;

    private static readonly ImmutableHashSet<string> EmptySet =
        ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The empty flag set.</summary>
    public static ProjectTreeFlags Empty => new(EmptySet);

    private ImmutableHashSet<string> Set => _set ?? EmptySet;

    /// <summary>True when no flags are present.</summary>
    public bool IsEmpty => Set.Count == 0;

    /// <summary>Creates a flag set containing the single <paramref name="flag"/>.</summary>
    public static ProjectTreeFlags Create(string flag)
    {
        if (string.IsNullOrEmpty(flag))
            throw new ArgumentException("Flag must be a non-empty string.", nameof(flag));
        return new ProjectTreeFlags(EmptySet.Add(flag));
    }

    /// <summary>Creates a flag set from the given <paramref name="flags"/>.</summary>
    public static ProjectTreeFlags Create(params string[] flags) =>
        new(EmptySet.Union(flags ?? Array.Empty<string>()));

    /// <summary>Identity overload — returns <paramref name="flags"/> unchanged.
    /// Matches the CPS API where <c>Create(ProjectTreeFlags)</c> is a no-op.</summary>
    public static ProjectTreeFlags Create(ProjectTreeFlags flags) => flags;

    /// <summary>Returns the union of two flag sets.</summary>
    public static ProjectTreeFlags Union(ProjectTreeFlags first, ProjectTreeFlags second) =>
        first.Union(second);

    /// <summary>Returns a set with <paramref name="flag"/> added.</summary>
    public ProjectTreeFlags Add(string flag)
    {
        if (string.IsNullOrEmpty(flag))
            throw new ArgumentException("Flag must be a non-empty string.", nameof(flag));
        return new ProjectTreeFlags(Set.Add(flag));
    }

    /// <summary>Returns a set with all flags from <paramref name="other"/> added.</summary>
    public ProjectTreeFlags Add(ProjectTreeFlags other) => Union(other);

    /// <summary>Returns a set with <paramref name="flag"/> removed.</summary>
    public ProjectTreeFlags Remove(string flag) => new(Set.Remove(flag));

    /// <summary>Returns the union of this set and <paramref name="other"/>.</summary>
    public ProjectTreeFlags Union(ProjectTreeFlags other) => new(Set.Union(other.Set));

    /// <summary>Returns the union of this set and a single <paramref name="flag"/>.</summary>
    public ProjectTreeFlags Union(string flag) => Add(flag);

    /// <summary>Returns this set with every flag in <paramref name="other"/> removed.</summary>
    public ProjectTreeFlags Except(ProjectTreeFlags other) => new(Set.Except(other.Set));

    /// <summary>Returns the intersection of this set and <paramref name="other"/>.</summary>
    public ProjectTreeFlags Intersect(ProjectTreeFlags other) => new(Set.Intersect(other.Set));

    /// <summary>True when <paramref name="flag"/> is present.</summary>
    public bool Contains(string flag) => Set.Contains(flag);

    /// <summary>True when every flag in <paramref name="flags"/> is present.</summary>
    public bool Contains(ProjectTreeFlags flags) => Set.IsSupersetOf(flags.Set);

    /// <summary>Alias for <see cref="Contains(ProjectTreeFlags)"/>, matching CPS naming.</summary>
    public bool HasFlag(ProjectTreeFlags flags) => Contains(flags);

    /// <summary>True when any flag in <paramref name="flags"/> is present.</summary>
    public bool ContainsAny(ProjectTreeFlags flags) => Set.Overlaps(flags.Set);

    public static implicit operator ProjectTreeFlags(string flag) => Create(flag);

    public static ProjectTreeFlags operator +(ProjectTreeFlags flags, string flag) => flags.Add(flag);

    public static ProjectTreeFlags operator +(ProjectTreeFlags first, ProjectTreeFlags second) => first.Union(second);

    public bool Equals(ProjectTreeFlags other) => Set.SetEquals(other.Set);

    public override bool Equals(object? obj) => obj is ProjectTreeFlags other && Equals(other);

    public override int GetHashCode()
    {
        // Order-independent hash so equal sets hash equally.
        var hash = 0;
        foreach (var flag in Set)
            hash ^= StringComparer.OrdinalIgnoreCase.GetHashCode(flag);
        return hash;
    }

    public static bool operator ==(ProjectTreeFlags left, ProjectTreeFlags right) => left.Equals(right);

    public static bool operator !=(ProjectTreeFlags left, ProjectTreeFlags right) => !left.Equals(right);

    public IEnumerator<string> GetEnumerator() => Set.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => string.Join(" ", Set.OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

    // ── Top-level shorthands (mirrors CPS static properties on the struct itself) ──────────────
    // Some upstream code accesses these directly as ProjectTreeFlags.Reference rather than
    // ProjectTreeFlags.Common.Reference — both paths must work.

    public static ProjectTreeFlags Reference                    { get; } = Create("Reference");
    public static ProjectTreeFlags ResolvedReference            { get; } = Create("ResolvedReference");
    public static ProjectTreeFlags BrokenReference              { get; } = Create("BrokenReference");
    public static ProjectTreeFlags SharedProjectImportReference { get; } = Create("SharedProjectImportReference");
    public static ProjectTreeFlags VirtualFolder                { get; } = Create("VirtualFolder");
    public static ProjectTreeFlags Folder                       { get; } = Create("Folder");
    public static ProjectTreeFlags ReferencesFolder             { get; } = Create("ReferencesFolder");
    public static ProjectTreeFlags DependenciesFolder           { get; } = Create("DependenciesFolder");
    public static ProjectTreeFlags PackagesFolder               { get; } = Create("PackagesFolder");
    public static ProjectTreeFlags PackageReference             { get; } = Create("PackageReference");
    public static ProjectTreeFlags ProjectReference             { get; } = Create("ProjectReference");
    public static ProjectTreeFlags LinkedFile                   { get; } = Create("LinkedFile");
    public static ProjectTreeFlags BubbleUp                     { get; } = Create("BubbleUp");
    public static ProjectTreeFlags FileSystemEntity             { get; } = Create("FileSystemEntity");

    /// <summary>
    /// Well-known flags defined by CPS. Names and meanings mirror the documented CPS values so that
    /// upstream code referencing <c>ProjectTreeFlags.Common.*</c> binds unchanged.
    /// </summary>
    public static class Common
    {
        public static ProjectTreeFlags ProjectRoot { get; } = Create("ProjectRoot");
        public static ProjectTreeFlags Folder { get; } = Create("Folder");
        public static ProjectTreeFlags VirtualFolder { get; } = Create("VirtualFolder");
        public static ProjectTreeFlags BubbleUp { get; } = Create("BubbleUp");
        public static ProjectTreeFlags FileSystemEntity { get; } = Create("FileSystemEntity");
        public static ProjectTreeFlags SourceFile { get; } = Create("SourceFile");
        public static ProjectTreeFlags ReferencesFolder { get; } = Create("ReferencesFolder");
        public static ProjectTreeFlags DependenciesFolder { get; } = Create("DependenciesFolder");
        public static ProjectTreeFlags PackagesFolder { get; } = Create("PackagesFolder");
        public static ProjectTreeFlags PackageReference { get; } = Create("PackageReference");
        public static ProjectTreeFlags ProjectReference { get; } = Create("ProjectReference");
        public static ProjectTreeFlags LinkedFile { get; } = Create("LinkedFile");
        public static ProjectTreeFlags AppDesignerFolder { get; } = Create("AppDesignerFolder");
        public static ProjectTreeFlags IncludeInProjectCandidate { get; } = Create("IncludeInProjectCandidate");
        public static ProjectTreeFlags VisibleOnlyInShowAllFiles { get; } = Create("VisibleOnlyInShowAllFiles");
        public static ProjectTreeFlags DisableAddItemFolder { get; } = Create("DisableAddItemFolder");
        public static ProjectTreeFlags DisableAddItemRecursiveFolder { get; } = Create("DisableAddItemRecursiveFolder");
        public static ProjectTreeFlags SharedItemsImportFile { get; } = Create("SharedItemsImportFile");
        public static ProjectTreeFlags SharedProjectImportReference { get; } = Create("SharedProjectImportReference");
        public static ProjectTreeFlags Reference { get; } = Create("Reference");
        public static ProjectTreeFlags ResolvedReference { get; } = Create("ResolvedReference");
        public static ProjectTreeFlags BrokenReference { get; } = Create("BrokenReference");
    }
}
