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

        var projectDirectory = project.Directory.ToString();
        var folders = new Dictionary<string, SolutionExplorerNodeModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in ProjectDisplayItems.GetProjectDisplayItems(project))
        {
            var segments = item.DisplayPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var parent = projectNode;
            var pathSoFar = string.Empty;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                pathSoFar = pathSoFar.Length == 0 ? segments[i] : pathSoFar + "\\" + segments[i];
                if (!folders.TryGetValue(pathSoFar, out var folderNode))
                {
                    folderNode = new SolutionExplorerNodeModel(
                        segments[i],
                        Path.Combine(projectDirectory, pathSoFar),
                        isDirectory: true,
                        SolutionExplorerNodeKind.Folder,
                        projectPathHint: project.FileName.ToString());
                    folders.Add(pathSoFar, folderNode);
                    parent.Children.Add(folderNode);
                }

                parent = folderNode;
            }

            var kind = !item.Exists
                ? SolutionExplorerNodeKind.MissingFile
                : item.IsLinked
                    ? SolutionExplorerNodeKind.LinkedFile
                    : SolutionExplorerNodeKind.File;

            parent.Children.Add(new SolutionExplorerNodeModel(
                segments[^1],
                item.PhysicalPath,
                isDirectory: false,
                kind,
                boundItem: null,
                projectPathHint: project.FileName.ToString(),
                includeHint: item.ProjectItem?.Include));
        }

        SortChildren(projectNode);
        return projectNode;
    }

    private static void SortChildren(SolutionExplorerNodeModel node)
    {
        node.Children.Sort((a, b) =>
        {
            var aIsFolder = a.Kind is SolutionExplorerNodeKind.Folder or SolutionExplorerNodeKind.GhostFolder;
            var bIsFolder = b.Kind is SolutionExplorerNodeKind.Folder or SolutionExplorerNodeKind.GhostFolder;
            if (aIsFolder != bIsFolder)
            {
                return aIsFolder ? -1 : 1;
            }

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var child in node.Children)
        {
            SortChildren(child);
        }
    }
}
