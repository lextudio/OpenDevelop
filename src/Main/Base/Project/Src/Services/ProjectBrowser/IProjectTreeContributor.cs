#nullable enable
using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.Services;

/// <summary>
/// Addin-extensible source of extra Solution Explorer project-subtree nodes beyond what the
/// project's own MSBuild items describe (<stride>/sources/tools/Stride.OpenDevelop.AddIn/stride-game-studio.md "Projects pad /
/// Solution Explorer spec for a Stride project"). A contributor is registered by an addin at
/// load (Autostart command) into <see cref="ProjectTreeContributorRegistry"/> and consulted by
/// <see cref="ICSharpCode.SharpDevelop.Services.ProjectBrowserTreeBuilder"/> when building a
/// project subtree. Kept in Base so both the App tree builder and addins see it without an App
/// reference.
/// </summary>
public interface IProjectTreeContributor
{
    /// <summary>Whether this contributor wants to add nodes under the given project.</summary>
    bool CanContribute(string projectFilePath);

    /// <summary>The virtual subtree to attach under the project node (e.g. a Stride "Assets"
    /// subtree backed by the project's .sdpkg). Nodes with a <see cref="ProjectBrowserContribution.FullPath"/>
    /// that is a real file are opened by double-click through the normal workbench file-open
    /// path (display bindings); pure-virtual nodes (null path) are containers only.</summary>
    IReadOnlyList<ProjectBrowserContribution> GetContributions(string projectFilePath);
}

/// <summary>A single virtual node contributed to a project's Solution Explorer subtree.</summary>
public sealed class ProjectBrowserContribution
{
    /// <summary>Display name shown in the tree.</summary>
    public string Caption { get; set; } = "";

    /// <summary>On-disk path (for file-like nodes); null for pure-virtual containers.</summary>
    public string? FullPath { get; set; }

    /// <summary>True for folders/containers; false for file-like nodes.</summary>
    public bool IsFolder { get; set; }

    /// <summary>Child nodes.</summary>
    public IReadOnlyList<ProjectBrowserContribution> Children { get; set; } = Array.Empty<ProjectBrowserContribution>();
}

/// <summary>Static registry for <see cref="IProjectTreeContributor"/>s. Addins register at load
/// (Autostart command) instead of relying on MEF, because OpenDevelopMefHost only scans the main
/// assembly, not addin assemblies.</summary>
public static class ProjectTreeContributorRegistry
{
    static readonly List<IProjectTreeContributor> contributors = new();

    public static void Register(IProjectTreeContributor contributor)
    {
        lock (contributors)
        {
            if (!contributors.Contains(contributor))
                contributors.Add(contributor);
        }
    }

    public static IReadOnlyList<IProjectTreeContributor> GetContributors()
    {
        lock (contributors)
        {
            return contributors.ToArray();
        }
    }
}