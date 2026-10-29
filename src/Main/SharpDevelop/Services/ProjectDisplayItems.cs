#nullable enable
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

			var projectDirectory = Path.GetDirectoryName(project.FileName?.ToString());
			var projectFile = project.FileName.ToString();
			var nestingProviders = GetNestingProviders(projectFile);
			var items = project.Items.CreateSnapshot()
				.OfType<FileProjectItem>()
				.Where(item => !IsReferenceItemName(item.ItemType.ItemName))
				.Where(item => IsSupportedProjectItemPath(projectFile, item.FileName.ToString(), nestingProviders))
				// Real MSBuild evaluation legitimately declares some generated sources as ordinary
				// Compile items with no Visible="false" metadata at all (e.g. Uno.Resizetizer's
				// obj/**/unoresizetizer/*.g.cs) - a real Solution Explorer hides anything physically
				// under bin/obj/.git/.vs regardless of metadata, as a hardcoded convention, not just
				// via Visible. Match that here so item-list resolution doesn't leak build output.
				.Where(item => string.IsNullOrEmpty(projectDirectory) || !IsExcludedProjectPath(item.FileName.ToString(), projectDirectory))
				.Select(item => new ProjectDisplayItem(
					item.FileName.ToString(),
					NormalizeDisplayPath(item.VirtualName),
					item.DependentUpon,
					item.IsLink,
					File.Exists(item.FileName.ToString()),
					item))
				// A single physical file can legitimately register under more than one MSBuild item
				// type/head evaluation (e.g. a .xaml file surfacing as both a Page item and a
				// None/Content item) - each becomes a separate entry here, and since tree-building
				// adds file leaves unconditionally (unlike folders, which dedupe by name), that showed
				// up as the same file listed twice at the same tree level. One node per physical file,
				// keeping the first item type MSBuild reports for it, matches what a real Solution
				// Explorer shows regardless of how many item types happen to reference the same file.
				.GroupBy(item => item.PhysicalPath, StringComparer.OrdinalIgnoreCase)
				.Select(group => group.First())
				.OrderBy(item => item.DisplayPath, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			return ApplyFileNesting(items, projectFile, nestingProviders);
		}

		private static bool IsExcludedProjectPath(string path, string projectDirectory)
		{
			var relativePath = Path.GetRelativePath(projectDirectory, path);
			var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return segments.Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(segment, ".vs", StringComparison.OrdinalIgnoreCase));
		}
		
		internal static IReadOnlyList<ProjectDisplayItem> GetEvaluatedProjectDisplayItems(MSBuildBasedProject project)
		{
			var projectDirectory = project.Directory.ToString();
			var projectFile = project.FileName.ToString();
			var nestingProviders = GetNestingProviders(projectFile);

			var items = project.GetEvaluatedProjectItems()
				.Where(item => IsDisplayItemName(item.ItemType))
				.Select(item => CreateEvaluatedDisplayItem(projectDirectory, projectFile, item, nestingProviders))
				.Where(item => item != null)
				.Cast<ProjectDisplayItem>()
				.GroupBy(item => item.PhysicalPath, StringComparer.OrdinalIgnoreCase)
				.Select(group => group.First())
				.OrderBy(item => item.DisplayPath, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			return ApplyFileNesting(items, projectFile, nestingProviders);
		}

		// CPS also applies conventional nesting when the SDK does not emit an explicit
		// DependentUpon value. In particular, modern WPF SDK projects normally expose
		// MainWindow.xaml.cs with no DirectMetadata at all; VS still nests it under the
		// matching MainWindow.xaml. Preserve explicit MSBuild metadata, then apply the
		// same high-value conventions only when the parent really exists in the project.
		private static IReadOnlyList<ProjectDisplayItem> ApplyFileNesting(ProjectDisplayItem[] items, string projectFile, IReadOnlyList<IProjectFileNestingProvider> nestingProviders)
		{
			var displayPaths = new HashSet<string>(items.Select(item => item.DisplayPath), StringComparer.OrdinalIgnoreCase);
			return items.Select(item => {
				if (!string.IsNullOrWhiteSpace(item.DependentUpon))
					return item;
				var parentPath = GetConventionParentPath(item.DisplayPath)
					?? GetResourceParentPath(item.DisplayPath, displayPaths)
					?? GetAddinParentPath(nestingProviders, projectFile, item.DisplayPath, displayPaths);
				if (parentPath is null || !displayPaths.Contains(parentPath))
					return item;
				return item with { DependentUpon = parentPath.Split('\\', '/').Last() };
			}).ToArray();
		}

		private static string? GetConventionParentPath(string displayPath)
		{
			if (displayPath.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)
			    || displayPath.EndsWith(".xaml.vb", StringComparison.OrdinalIgnoreCase))
				return Path.ChangeExtension(displayPath, null);
			if (displayPath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
				return displayPath.Substring(0, displayPath.Length - ".Designer.cs".Length) + ".cs";
			if (displayPath.EndsWith(".Designer.vb", StringComparison.OrdinalIgnoreCase))
				return displayPath.Substring(0, displayPath.Length - ".Designer.vb".Length) + ".vb";
			return null;
		}

		private static string? GetAddinParentPath(IReadOnlyList<IProjectFileNestingProvider> providers, string projectFile, string displayPath, IReadOnlySet<string> displayPaths)
		{
			foreach (var provider in providers) {
				try {
					var parent = provider.GetParentDisplayPath(projectFile, displayPath, displayPaths);
					if (!string.IsNullOrWhiteSpace(parent))
						return parent;
				} catch (Exception ex) {
					ICSharpCode.Core.LoggingService.Warn("Project file nesting provider failed: " + ex);
				}
			}
			return null;
		}

		// A .resx nests under the code file with the same base name - Form1.resx under Form1.cs
		// (or .vb/.fs) - matching VS's WinForms/designer resource nesting, so a form shows one
		// node with its .Designer.cs and .resx beneath it. The sibling's extension is the project
		// language, which this convention cannot assume, so probe the actual project items.
		private static string? GetResourceParentPath(string displayPath, HashSet<string> displayPaths)
		{
			if (!displayPath.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
				return null;
			string baseName = displayPath.Substring(0, displayPath.Length - ".resx".Length);
			foreach (string extension in new[] { ".cs", ".vb", ".fs" }) {
				string candidate = baseName + extension;
				if (displayPaths.Contains(candidate))
					return candidate;
			}
			return null;
		}

		internal static IReadOnlyList<EvaluatedProjectItem> GetEvaluatedDependencyItems(MSBuildBasedProject project)
		{
			return project.GetEvaluatedProjectItems()
				.Where(item => IsReferenceItemName(item.ItemType))
				.ToArray();
		}
		
		private static ProjectDisplayItem? CreateEvaluatedDisplayItem(string projectDirectory, string projectFile, EvaluatedProjectItem item, IReadOnlyList<IProjectFileNestingProvider> nestingProviders)
		{
			var physicalPath = ResolvePhysicalPath(projectDirectory, item.EvaluatedInclude);
			if (string.IsNullOrWhiteSpace(physicalPath)
			    || !IsSupportedProjectItemPath(projectFile, physicalPath, nestingProviders)
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

		private static bool IsSupportedProjectItemPath(string projectFile, string path, IReadOnlyList<IProjectFileNestingProvider> nestingProviders)
		{
			var extension = Path.GetExtension(path);
			foreach (var supported in SupportedExtensions) {
				if (string.Equals(extension, supported, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			foreach (var provider in nestingProviders) {
				try {
					if (AppliesToProject(provider, projectFile) && provider.IsSupportedProjectItem(projectFile, path))
						return true;
				} catch (Exception ex) {
					ICSharpCode.Core.LoggingService.Warn("Project file nesting provider failed: " + ex);
				}
			}
			return false;
		}

		/// <summary>Resolved once per item list, not once per item: the registry allocates and sorts on
		/// every call, so a per-item lookup costs a solution-wide project one set of allocations per
		/// file it displays.</summary>
		private static IProjectFileNestingProvider[] GetNestingProviders(string projectFile)
		{
			return ProjectFileNestingProviderRegistry.GetProviders()
				.Where(provider => AppliesToProject(provider, projectFile))
				.ToArray();
		}

		private static bool AppliesToProject(IProjectFileNestingProvider provider, string projectFile)
		{
			try {
				return provider.AppliesToProject(projectFile);
			} catch (Exception ex) {
				ICSharpCode.Core.LoggingService.Warn("Project file nesting provider failed: " + ex);
				return false;
			}
		}

		private static string NormalizeDisplayPath(string path)
		{
			return path.Replace(Path.DirectorySeparatorChar, '\\').Replace('/', '\\');
		}

	}
}
