#nullable enable
// R6c (see doc/technotes/solution-explorer.md): builds the WPF-bindable node tree shown by
// ProjectBrowserPad, directly from SharpDevelop's native ISolution/IProject model. This is new
// code (UnoDevelop's tree builder walks its own WinUI-only UnoSolutionModel, so there was nothing
// to port) - it is deliberately simpler than the CPS MutableProjectTree bridge
// (SharpDevelopProjectTreeProvider) built for R6b: a flat file/folder tree is all the MVP tree view
// needs, and building it directly off IProject.Items avoids a second, redundant walk of the CPS tree.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ICSharpCode.SharpDevelop.Project;
using Microsoft.VisualStudio.ProjectSystem;

namespace ICSharpCode.SharpDevelop.Services;

internal static class ProjectBrowserTreeBuilder
{
    public static ProjectBrowserNodeModel? BuildSolutionTree(ISolution? solution, bool showAllFiles, bool refreshGitStatus = true)
    {
        if (solution is null)
        {
            return null;
        }

        if (refreshGitStatus) {
            RefreshGitStatus(GetGitStatusRoots(solution));
        }

        var root = new ProjectBrowserNodeModel(
            solution.Name,
            solution.FileName.ToString(),
            isDirectory: false,
            ProjectBrowserNodeKind.Solution,
            boundItem: solution,
            isExpanded: true);

        foreach (var project in solution.Projects.CreateSnapshot().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            root.Children.Add(BuildProjectNode(project, showAllFiles));
        }

        return root;
    }

    internal static string[] GetGitStatusRoots(ISolution solution)
    {
        return new[] { Path.GetDirectoryName(solution.FileName.ToString()) }
            .Concat(solution.Projects.CreateSnapshot().Select(project => Path.GetDirectoryName(project.FileName.ToString())))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    internal static void RefreshGitStatus(IEnumerable<string> roots)
    {
        GitStatusService.ClearCache();
        foreach (var root in roots) {
            GitStatusService.Refresh(root);
        }
    }

    private static ProjectBrowserNodeModel BuildProjectNode(IProject project, bool showAllFiles)
    {
        var projectNode = new ProjectBrowserNodeModel(
            project.Name,
            project.FileName.ToString(),
            isDirectory: false,
            ProjectBrowserNodeKind.Project,
            boundItem: project as ISolutionItem,
            projectPathHint: project.FileName.ToString(),
            isExpanded: true);

        var cpsTree = new SharpDevelopProjectTreeProvider(project).BuildTree();
        foreach (var child in cpsTree.Children)
        {
            var childNode = ConvertProjectTreeNode(child, project.FileName.ToString(), showAllFiles);
            if (childNode != null) {
                projectNode.Children.Add(childNode);
            }
        }

        AppendContributorNodes(projectNode, project.FileName.ToString());

        SortChildren(projectNode);
        return projectNode;
    }

    // Addin-contributed virtual nodes (IProjectTreeContributor, e.g. the Stride addin's "Assets"
    // subtree backed by the project's .sdpkg - see doc/technotes/stride-game-studio.md). Each
    // contributor runs guarded so a broken addin can never break the whole Solution Explorer.
    private static void AppendContributorNodes(ProjectBrowserNodeModel projectNode, string projectPath)
    {
        foreach (var contributor in ProjectTreeContributorRegistry.GetContributors())
        {
            try
            {
                if (!contributor.CanContribute(projectPath))
                    continue;
                foreach (var contribution in contributor.GetContributions(projectPath))
                {
                    var node = ConvertContribution(contribution, projectPath);
                    if (node != null) {
                        projectNode.Children.Add(node);
                    }
                }
            }
            catch (Exception ex)
            {
                ICSharpCode.Core.LoggingService.Warn("Project tree contributor failed for " + projectPath + ": " + ex);
            }
        }
    }

    private static ProjectBrowserNodeModel? ConvertContribution(ProjectBrowserContribution contribution, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(contribution.Caption))
            return null;

        var kind = contribution.IsFolder ? ProjectBrowserNodeKind.Folder : ProjectBrowserNodeKind.File;
        var node = new ProjectBrowserNodeModel(
            contribution.Caption,
            contribution.FullPath ?? string.Empty,
            contribution.IsFolder,
            kind,
            boundItem: null,
            boundProjectTree: null,
            projectPathHint: projectPath,
            includeHint: null,
            isExpanded: false);

        foreach (var child in contribution.Children)
        {
            var childNode = ConvertContribution(child, projectPath);
            if (childNode != null) {
                node.Children.Add(childNode);
            }
        }

        SortChildren(node);
        return node;
    }
    
    private static ProjectBrowserNodeModel? ConvertProjectTreeNode(IProjectTree tree, string projectPath, bool showAllFiles)
    {
        if (!showAllFiles && tree.Flags.Contains(ProjectTreeFlags.Common.VisibleOnlyInShowAllFiles)) {
            return null;
        }
        
        var node = new ProjectBrowserNodeModel(
            tree.Caption,
            tree.FilePath ?? string.Empty,
            tree.IsFolder,
            GetNodeKind(tree),
            boundItem: null,
            boundProjectTree: tree,
            projectPathHint: projectPath,
            includeHint: GetIncludeHint(tree, projectPath),
            isExpanded: IsExpandedByDefault(tree));
        
        foreach (var child in tree.Children)
        {
            var childNode = ConvertProjectTreeNode(child, projectPath, showAllFiles);
            if (childNode != null) {
                node.Children.Add(childNode);
            }
        }
        
        SortChildren(node);
        return node;
    }
    
    // Shared with UnoDevelop's CpsTreeConverter (see doc/technotes/solution-explorer.md) - this used
    // to check File.Exists on disk per node instead of CPS's own FileSystemEntity/
    // IncludeInProjectCandidate flags, which meant a ghost (ready-to-include) file was
    // indistinguishable from a normal one here (it exists on disk, so File.Exists was true) - the
    // shared resolver now recognizes it as GhostFile like UnoDevelop's tree always did.
    private static ProjectBrowserNodeKind GetNodeKind(IProjectTree tree) =>
        ProjectBrowserTreeKindResolver.ResolveKind(tree);
    
    private static string? GetIncludeHint(IProjectTree tree, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(tree.FilePath))
            return null;
        
        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(projectDirectory))
            return tree.FilePath;
        
        return Path.GetRelativePath(projectDirectory, tree.FilePath);
    }
    
    private static bool IsExpandedByDefault(IProjectTree tree)
    {
        return tree.Flags.Contains(ProjectTreeFlags.Common.DependenciesFolder)
            || tree.Flags.Contains(ProjectTreeFlags.Common.ReferencesFolder)
            || tree.Flags.Contains(ProjectTreeFlags.Common.PackagesFolder);
    }

    private static int GetSortOrder(ProjectBrowserNodeKind kind) => kind switch
    {
        ProjectBrowserNodeKind.DependenciesFolder => -2,
        ProjectBrowserNodeKind.ReferencesFolder => -2,
        ProjectBrowserNodeKind.PackagesFolder => -2,
        ProjectBrowserNodeKind.Folder or ProjectBrowserNodeKind.GhostFolder => -1,
        _ => 0,
    };

    private static void SortChildren(ProjectBrowserNodeModel node)
    {
        node.Children.Sort((a, b) =>
        {
            int order = GetSortOrder(a.Kind).CompareTo(GetSortOrder(b.Kind));
            if (order != 0) return order;

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var child in node.Children)
        {
            SortChildren(child);
        }
    }
}
