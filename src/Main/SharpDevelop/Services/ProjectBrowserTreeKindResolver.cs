using Microsoft.VisualStudio.ProjectSystem;

namespace ICSharpCode.SharpDevelop.Services;

/// <summary>
/// CPS <see cref="ProjectTreeFlags"/> -> <see cref="ProjectBrowserNodeKind"/> mapping, shared by
/// both hosts' CPS-tree-to-UI-node conversion (UnoDevelop's CpsTreeConverter, OpenDevelop's
/// ProjectBrowserTreeBuilder - see doc/technotes/solution-explorer.md). Canonically the more
/// refined of the two prior implementations: it distinguishes a ghost/ready-to-include file
/// (<see cref="ProjectTreeFlags.Common.IncludeInProjectCandidate"/>) from a missing one (present in
/// the project but absent from disk, via the *lack* of
/// <see cref="ProjectTreeFlags.Common.FileSystemEntity"/>) using CPS's own flags, rather than an
/// extra <c>File.Exists</c> disk check per node.
/// </summary>
internal static class ProjectBrowserTreeKindResolver
{
    public static ProjectBrowserNodeKind ResolveKind(IProjectTree node)
    {
        var f = node.Flags;

        if (f.Contains(ProjectTreeFlags.Common.ProjectRoot))
            return ProjectBrowserNodeKind.Project;

        if (f.Contains(ProjectTreeFlags.Common.DependenciesFolder))
            return ProjectBrowserNodeKind.DependenciesFolder;

        if (f.Contains(ProjectTreeFlags.Common.ReferencesFolder))
            return ProjectBrowserNodeKind.ReferencesFolder;

        if (f.Contains(ProjectTreeFlags.Common.PackagesFolder))
            return ProjectBrowserNodeKind.PackagesFolder;

        if (f.Contains(ProjectTreeFlags.Common.PackageReference))
            return ProjectBrowserNodeKind.PackageReference;

        if (f.Contains(ProjectTreeFlags.Common.Reference))
        {
            return f.Contains(ProjectTreeFlags.Common.ProjectReference)
                ? ProjectBrowserNodeKind.ProjectReference
                : ProjectBrowserNodeKind.Reference;
        }

        if (f.Contains(ProjectTreeFlags.Common.Folder) ||
            f.Contains(ProjectTreeFlags.Common.VirtualFolder))
            return f.Contains(ProjectTreeFlags.Common.IncludeInProjectCandidate)
                ? ProjectBrowserNodeKind.GhostFolder
                : ProjectBrowserNodeKind.Folder;

        if (f.Contains(ProjectTreeFlags.Common.SourceFile))
        {
            if (f.Contains(ProjectTreeFlags.Common.IncludeInProjectCandidate))
                return ProjectBrowserNodeKind.GhostFile;

            if (!f.Contains(ProjectTreeFlags.Common.FileSystemEntity))
                return ProjectBrowserNodeKind.MissingFile;

            if (f.Contains(ProjectTreeFlags.Common.LinkedFile))
                return ProjectBrowserNodeKind.LinkedFile;

            return ProjectBrowserNodeKind.File;
        }

        return ProjectBrowserNodeKind.Unknown;
    }
}
