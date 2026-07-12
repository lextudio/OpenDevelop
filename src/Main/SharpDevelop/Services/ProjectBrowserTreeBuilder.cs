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
    public static ProjectBrowserNodeModel? BuildSolutionTree(ISolution? solution, bool showAllFiles)
    {
        if (solution is null)
        {
            return null;
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

        SortChildren(projectNode);
        return projectNode;
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
    
    private static ProjectBrowserNodeKind GetNodeKind(IProjectTree tree)
    {
        if (tree.Flags.Contains(ProjectTreeFlags.Common.DependenciesFolder))
            return ProjectBrowserNodeKind.DependenciesFolder;
        if (tree.Flags.Contains(ProjectTreeFlags.Common.PackagesFolder))
            return ProjectBrowserNodeKind.PackagesFolder;
        if (tree.Flags.Contains(ProjectTreeFlags.Common.ReferencesFolder))
            return ProjectBrowserNodeKind.ReferencesFolder;
        if (tree.Flags.Contains(ProjectTreeFlags.Common.PackageReference))
            return ProjectBrowserNodeKind.PackageReference;
        if (tree.Flags.Contains(ProjectTreeFlags.Common.ProjectReference))
            return ProjectBrowserNodeKind.ProjectReference;
        if (tree.Flags.Contains(ProjectTreeFlags.Common.Reference))
            return ProjectBrowserNodeKind.Reference;
        if (tree.Flags.Contains(ProjectTreeFlags.Common.LinkedFile))
            return ProjectBrowserNodeKind.LinkedFile;
        if (tree.IsFolder)
            return tree.Flags.Contains(ProjectTreeFlags.Common.IncludeInProjectCandidate)
                ? ProjectBrowserNodeKind.GhostFolder
                : ProjectBrowserNodeKind.Folder;
        if (!string.IsNullOrWhiteSpace(tree.FilePath) && !File.Exists(tree.FilePath))
            return ProjectBrowserNodeKind.MissingFile;
        return ProjectBrowserNodeKind.File;
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
