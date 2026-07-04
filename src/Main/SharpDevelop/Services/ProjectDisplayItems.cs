// Extracted from UnoDevelop's UnoProjectService.cs (see doc/technotes/solution-explorer.md) -
// only the bits SharpDevelopProjectTreeProvider actually needs: turning a SharpDevelop IProject's
// evaluated MSBuild items into a flat, display-ready list. UnoProjectService itself is UnoDevelop's
// own IProjectService/ISolution/IProject reimplementation (a different concern entirely - OpenDevelop
// already has its own native SharpDevelop IProjectService/ISolution/IProject from the original
// codebase), so it was not copied wholesale.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.SharpDevelop.Services
{
	internal static class ProjectDisplayItems
	{
		internal sealed record ProjectDisplayItem(string PhysicalPath, string DisplayPath, string? DependentUpon = null, bool IsLinked = false, bool Exists = true, ProjectItem? ProjectItem = null);

		// UnoDevelop note kept for context: "UnoProjectModel.CreateProjectItem wraps *every*
		// evaluated MSBuild item as a FileProjectItem, so references/packages also surface here."
		// The same is true of SharpDevelop's own IProject.Items - references/packages are rendered
		// separately under the References and Packages folders, so exclude them from the file tree,
		// otherwise a ProjectReference to an out-of-tree .csproj shows up as a linked file and a
		// "<Reference Include='System.Xml'/>" (extension ".Xml") is mistaken for a missing .xml file.
		internal static IReadOnlyList<ProjectDisplayItem> GetProjectDisplayItems(IProject project)
		{
			if (project is null) {
				return Array.Empty<ProjectDisplayItem>();
			}

			return project.Items.CreateSnapshot()
				.OfType<FileProjectItem>()
				.Where(item => !IsReferenceItemName(item.ItemType.ItemName))
				.Where(item => IsSupportedProjectItemPath(item.FileName.ToString()))
				.Select(item => new ProjectDisplayItem(
					item.FileName.ToString(),
					NormalizeDisplayPath(item.VirtualName),
					item.DependentUpon,
					item.IsLink,
					File.Exists(item.FileName.ToString()),
					item))
				.OrderBy(item => item.DisplayPath, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}

		private static bool IsReferenceItemName(string name) =>
			name is "Reference" or "ProjectReference" or "PackageReference"
				or "Analyzer" or "COMReference" or "FrameworkReference";

		private static bool IsSupportedProjectItemPath(string path)
		{
			var extension = Path.GetExtension(path);
			return string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(extension, ".props", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(extension, ".targets", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(extension, ".resx", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase);
		}

		private static string NormalizeDisplayPath(string path)
		{
			return path.Replace(Path.DirectorySeparatorChar, '\\').Replace('/', '\\');
		}
	}
}
