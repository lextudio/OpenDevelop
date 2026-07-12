// R6c (see doc/technotes/solution-explorer.md): builds the WPF-bindable node tree shown by
// SolutionExplorerPad, directly from SharpDevelop's native ISolution/IProject model. This is new
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

internal static class SolutionExplorerTreeBuilder
{
    public static SolutionExplorerNodeModel? BuildSolutionTree(ISolution? solution)
    {
        if (solution is null)
        {
            return null;
        }

        var root = new SolutionExplorerNodeModel(
            solution.Name,
            solution.FileName.ToString(),
            isDirectory: false,
            SolutionExplorerNodeKind.Solution,
            boundItem: solution,
            isExpanded: true);

        foreach (var project in solution.Projects.CreateSnapshot().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            root.Children.Add(BuildProjectNode(project));
        }

        return root;
    }

    private static SolutionExplorerNodeModel BuildProjectNode(IProject project)
    {
        var projectNode = new SolutionExplorerNodeModel(
            project.Name,
            project.FileName.ToString(),
            isDirectory: false,
            SolutionExplorerNodeKind.Project,
            boundItem: project as ISolutionItem,
            projectPathHint: project.FileName.ToString(),
            isExpanded: true);

        var cpsTree = new SharpDevelopProjectTreeProvider(project).BuildTree();
        foreach (var child in cpsTree.Children)
        {
            projectNode.Children.Add(ConvertProjectTreeNode(child, project.FileName.ToString()));
        }

        SortChildren(projectNode);
        return projectNode;
    }
    
    private static SolutionExplorerNodeModel ConvertProjectTreeNode(IProjectTree tree, string projectPath)
    {
        var node = new SolutionExplorerNodeModel(
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
            node.Children.Add(ConvertProjectTreeNode(child, projectPath));
        }
        
        SortChildren(node);
        return node;
    }
    
    private static SolutionExplorerNodeKind GetNodeKind(IProjectTree tree)
    {
        if (tree.Flags.Contains(ProjectTreeFlags.Common.DependenciesFolder))
            return SolutionExplorerNodeKind.DependenciesFolder;
        if (tree.Flags.Contains(ProjectTreeFlags.Common.PackagesFolder))
            return SolutionExplorerNodeKind.PackagesFolder;
        if (tree.Flags.Contains(ProjectTreeFlags.Common.ReferencesFolder))
            return SolutionExplorerNodeKind.ReferencesFolder;
        if (tree.Flags.Contains(ProjectTreeFlags.Common.PackageReference))
            return SolutionExplorerNodeKind.PackageReference;
        if (tree.Flags.Contains(ProjectTreeFlags.Common.ProjectReference))
            return SolutionExplorerNodeKind.ProjectReference;
        if (tree.Flags.Contains(ProjectTreeFlags.Common.Reference))
            return SolutionExplorerNodeKind.Reference;
        if (tree.Flags.Contains(ProjectTreeFlags.Common.LinkedFile))
            return SolutionExplorerNodeKind.LinkedFile;
        if (tree.IsFolder)
            return tree.Flags.Contains(ProjectTreeFlags.Common.IncludeInProjectCandidate)
                ? SolutionExplorerNodeKind.GhostFolder
                : SolutionExplorerNodeKind.Folder;
        if (!string.IsNullOrWhiteSpace(tree.FilePath) && !File.Exists(tree.FilePath))
            return SolutionExplorerNodeKind.MissingFile;
        return SolutionExplorerNodeKind.File;
    }
    
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

    private static int GetSortOrder(SolutionExplorerNodeKind kind) => kind switch
    {
        SolutionExplorerNodeKind.DependenciesFolder => -2,
        SolutionExplorerNodeKind.ReferencesFolder => -2,
        SolutionExplorerNodeKind.PackagesFolder => -2,
        SolutionExplorerNodeKind.Folder or SolutionExplorerNodeKind.GhostFolder => -1,
        _ => 0,
    };

    private static void SortChildren(SolutionExplorerNodeModel node)
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
