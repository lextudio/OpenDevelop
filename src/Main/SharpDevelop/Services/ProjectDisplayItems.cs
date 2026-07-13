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
			
			if (project is MSBuildBasedProject msbuildProject && msbuildProject.IsSdkStyleProject) {
				return GetEvaluatedProjectDisplayItems(msbuildProject);
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
		
		internal static IReadOnlyList<ProjectDisplayItem> GetEvaluatedProjectDisplayItems(MSBuildBasedProject project)
		{
			var projectDirectory = project.Directory.ToString();
			var projectFile = project.FileName.ToString();
			
			return project.GetEvaluatedProjectItems()
				.Where(item => IsDisplayItemName(item.ItemType))
				.Select(item => CreateEvaluatedDisplayItem(projectDirectory, projectFile, item))
				.Where(item => item != null)
				.Cast<ProjectDisplayItem>()
				.GroupBy(item => item.PhysicalPath, StringComparer.OrdinalIgnoreCase)
				.Select(group => group.First())
				.OrderBy(item => item.DisplayPath, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}
		
		internal static IReadOnlyList<EvaluatedProjectItem> GetEvaluatedDependencyItems(MSBuildBasedProject project)
		{
			return project.GetEvaluatedProjectItems()
				.Where(item => IsReferenceItemName(item.ItemType))
				.ToArray();
		}
		
		private static ProjectDisplayItem? CreateEvaluatedDisplayItem(string projectDirectory, string projectFile, EvaluatedProjectItem item)
		{
			var physicalPath = ResolvePhysicalPath(projectDirectory, item.EvaluatedInclude);
			if (string.IsNullOrWhiteSpace(physicalPath)
			    || !IsSupportedProjectItemPath(physicalPath)
			    || string.Equals(Path.GetFullPath(physicalPath), Path.GetFullPath(projectFile), StringComparison.OrdinalIgnoreCase)) {
				return null;
			}
			
			var link = item.GetMetadata("Link");
			var displayPath = !string.IsNullOrWhiteSpace(link)
				? link
				: Path.GetRelativePath(projectDirectory, physicalPath);
			
			return new ProjectDisplayItem(
				physicalPath,
				NormalizeDisplayPath(displayPath),
				item.GetMetadata("DependentUpon"),
				!string.IsNullOrWhiteSpace(link),
				File.Exists(physicalPath),
				null);
		}
		
		private static string ResolvePhysicalPath(string projectDirectory, string include)
		{
			if (string.IsNullOrWhiteSpace(include)) {
				return string.Empty;
			}
			
			var normalizedInclude = include.Replace('\\', Path.DirectorySeparatorChar);
			return Path.IsPathRooted(normalizedInclude)
				? Path.GetFullPath(normalizedInclude)
				: Path.GetFullPath(Path.Combine(projectDirectory, normalizedInclude));
		}

		private static bool IsReferenceItemName(string name) =>
			name is "Reference" or "ProjectReference" or "PackageReference"
				or "Analyzer" or "COMReference" or "FrameworkReference" or "SDKReference";
		
		private static bool IsDisplayItemName(string name) =>
			name is "Compile" or "None" or "Content" or "EmbeddedResource" or "Resource" or "Page" or "ApplicationDefinition";

		// Originally C#-only (ported from UnoDevelop's own C#-only UnoProjectService.cs, see this
		// file's header comment) - .vb/.fs/.vbproj/.fsproj were simply never added, so SDK-style VB
		// and F# projects showed no source file nodes at all in Solution Explorer even though their
		// Compile items were evaluated fine (see doc/technotes/roslyn.md session notes).
		static readonly string[] SupportedExtensions = {
			".cs", ".vb", ".fs",
			".xaml",
			".csproj", ".vbproj", ".fsproj",
			".props", ".targets",
			".json", ".md", ".txt", ".resx", ".xml"
		};

		private static bool IsSupportedProjectItemPath(string path)
		{
			var extension = Path.GetExtension(path);
			foreach (var supported in SupportedExtensions) {
				if (string.Equals(extension, supported, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		private static string NormalizeDisplayPath(string path)
		{
			return path.Replace(Path.DirectorySeparatorChar, '\\').Replace('/', '\\');
		}

	}
}
