// ISolutionExplorerService implementation for OpenDevelop, using SharpDevelop's own native
// IProject/MSBuildBasedProject APIs (project.Items.Add/Remove + project.Save()) rather than raw
// .csproj XML manipulation. UnoDevelop's UnoProjectService.cs implements the same interface by
// directly parsing/rewriting XML, because UnoDevelop replaced SharpDevelop's IProjectService with
// its own model (UnoSolutionModel/UnoProjectModel) - OpenDevelop kept the original SharpDevelop
// IProjectService (SDProjectService) and its real MSBuildBasedProject, so it can just call that
// API directly instead of re-deriving XML mutation from scratch. See doc/technotes/solution-explorer.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.SharpDevelop.Services
{
	internal sealed class SharpDevelopSolutionExplorerService : ISolutionExplorerService
	{
		public string CreateFolder(string targetDirectory, string baseName = "NewFolder")
		{
			var path = ResolveUniquePath(targetDirectory, baseName, extension: null, isDirectory: true);
			Directory.CreateDirectory(path);
			return path;
		}

		public string CreateFile(string targetDirectory, string baseName = "NewFile", string extension = ".cs", string? initialContent = "// New file\n")
		{
			var path = ResolveUniquePath(targetDirectory, baseName, extension, isDirectory: false);
			File.WriteAllText(path, initialContent ?? string.Empty);
			AddFileToOwningProject(path);
			return path;
		}

		public IReadOnlyList<string> ImportExistingFiles(string targetDirectory, IEnumerable<string> sourcePaths)
		{
			var imported = new List<string>();
			foreach (var sourcePath in sourcePaths) {
				if (!File.Exists(sourcePath))
					continue;

				var destination = ResolveUniquePath(targetDirectory, Path.GetFileNameWithoutExtension(sourcePath), Path.GetExtension(sourcePath), isDirectory: false);
				File.Copy(sourcePath, destination);
				AddFileToOwningProject(destination);
				imported.Add(destination);
			}
			return imported;
		}

		public string ImportExistingFolder(string targetDirectory, string sourceDirectory)
		{
			var destination = ResolveUniquePath(targetDirectory, Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), extension: null, isDirectory: true);
			CopyDirectoryRecursive(sourceDirectory, destination);
			foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)) {
				AddFileToOwningProject(file);
			}
			return destination;
		}

		public string RenameItem(string sourcePath, bool isDirectory, string newName)
		{
			var directory = Path.GetDirectoryName(sourcePath) ?? throw new ArgumentException("sourcePath has no parent directory", nameof(sourcePath));
			var targetPath = Path.Combine(directory, isDirectory ? newName : newName);

			if (isDirectory) {
				Directory.Move(sourcePath, targetPath);
			} else {
				File.Move(sourcePath, targetPath);
			}

			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(sourcePath));
			if (project != null) {
				foreach (var item in project.Items.CreateSnapshot().OfType<FileProjectItem>().ToList()) {
					var itemPath = item.FileName.ToString();
					if (isDirectory) {
						if (!itemPath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
							continue;
						var relative = itemPath.Substring(sourcePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
						RetargetProjectItem(project, item, Path.Combine(targetPath, relative));
					} else if (string.Equals(itemPath, sourcePath, StringComparison.OrdinalIgnoreCase)) {
						RetargetProjectItem(project, item, targetPath);
					}
				}
				project.Save();
			}

			return targetPath;
		}

		public void DeleteItem(string sourcePath, bool isDirectory)
		{
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(sourcePath));
			if (project != null) {
				foreach (var item in project.Items.CreateSnapshot().OfType<FileProjectItem>().ToList()) {
					var itemPath = item.FileName.ToString();
					var matches = isDirectory
						? itemPath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase)
						: string.Equals(itemPath, sourcePath, StringComparison.OrdinalIgnoreCase);
					if (matches)
						project.Items.Remove(item);
				}
				project.Save();
			}

			if (isDirectory) {
				if (Directory.Exists(sourcePath))
					Directory.Delete(sourcePath, recursive: true);
			} else if (File.Exists(sourcePath)) {
				File.Delete(sourcePath);
			}
		}

		public bool TryIncludeItemInProject(string itemPath, out string includedItemName)
		{
			includedItemName = Path.GetFileName(itemPath);
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(itemPath));
			if (project == null)
				return false;

			if (project.Items.OfType<FileProjectItem>().Any(item => string.Equals(item.FileName.ToString(), itemPath, StringComparison.OrdinalIgnoreCase)))
				return true; // already included

			AddFileToOwningProject(itemPath);
			return true;
		}

		public bool TryExcludeItemFromProject(string itemPath, bool isDirectory, out string excludedItemName)
		{
			excludedItemName = Path.GetFileName(itemPath);
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(itemPath));
			if (project == null)
				return false;

			var removedAny = false;
			foreach (var item in project.Items.CreateSnapshot().OfType<FileProjectItem>().ToList()) {
				var itemFullPath = item.FileName.ToString();
				var matches = isDirectory
					? itemFullPath.StartsWith(itemPath, StringComparison.OrdinalIgnoreCase)
					: string.Equals(itemFullPath, itemPath, StringComparison.OrdinalIgnoreCase);
				if (matches) {
					project.Items.Remove(item);
					removedAny = true;
				}
			}

			if (removedAny)
				project.Save();
			return removedAny;
		}

		public bool TryRemoveItemFromProject(string itemPath, bool isDirectory, out string removedItemName, string? projectPathHint = null, string? includeHint = null)
		{
			// For a plain file/folder node, "Remove from project" and "Exclude from project" mean
			// the same thing (the physical item stays on disk) - MVP does not yet distinguish them.
			return TryExcludeItemFromProject(itemPath, isDirectory, out removedItemName);
		}

		public bool TryRemoveReference(string? projectPathHint, string include, SolutionExplorerNodeKind kind, out string removedName)
		{
			removedName = include;
			var project = ResolveProjectByPathHint(projectPathHint);
			if (project == null)
				return false;

			var itemTypeName = kind switch {
				SolutionExplorerNodeKind.PackageReference => "PackageReference",
				_ => "Reference",
			};

			var toRemove = project.Items
				.Where(item => string.Equals(item.ItemType.ItemName, itemTypeName, StringComparison.OrdinalIgnoreCase))
				.Where(item => string.Equals(item.Include, include, StringComparison.OrdinalIgnoreCase))
				.ToList();

			if (toRemove.Count == 0)
				return false;

			foreach (var item in toRemove)
				project.Items.Remove(item);
			project.Save();
			return true;
		}

		public bool TryRemoveProject(string projectPath, out string removedProjectName)
		{
			removedProjectName = Path.GetFileNameWithoutExtension(projectPath);
			var solution = SD.ProjectService.CurrentSolution;
			var project = solution?.Projects.FirstOrDefault(p => string.Equals(p.FileName.ToString(), projectPath, StringComparison.OrdinalIgnoreCase));
			if (project == null)
				return false;

			project.ParentFolder.Items.Remove(project);
			return true;
		}

		public bool TrySetStartupProject(string projectPath, out IProject? project)
		{
			var solution = SD.ProjectService.CurrentSolution;
			project = solution?.Projects.FirstOrDefault(p => string.Equals(p.FileName.ToString(), projectPath, StringComparison.OrdinalIgnoreCase));
			if (project == null || solution == null)
				return false;

			solution.StartupProject = project;
			return true;
		}

		private static void RetargetProjectItem(IProject project, FileProjectItem item, string newPath)
		{
			project.Items.Remove(item);
			var newItem = new FileProjectItem(project, item.ItemType, newPath) {
				DependentUpon = item.DependentUpon,
			};
			project.Items.Add(newItem);
		}

		private static IProject? ResolveProjectByPathHint(string? projectPathHint)
		{
			var solution = SD.ProjectService.CurrentSolution;
			if (solution == null)
				return null;
			if (string.IsNullOrEmpty(projectPathHint))
				return SD.ProjectService.CurrentProject;
			return solution.Projects.FirstOrDefault(p => string.Equals(p.FileName.ToString(), projectPathHint, StringComparison.OrdinalIgnoreCase));
		}

		private static void AddFileToOwningProject(string filePath)
		{
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(filePath))
				?? SD.ProjectService.CurrentProject;
			if (project == null)
				return; // no open project - the file exists on disk but isn't tracked yet.

			if (project.Items.OfType<FileProjectItem>().Any(item => string.Equals(item.FileName.ToString(), filePath, StringComparison.OrdinalIgnoreCase)))
				return;

			var itemType = ResolveItemTypeForExtension(Path.GetExtension(filePath));
			var item = new FileProjectItem(project, itemType, filePath);
			project.Items.Add(item);
			project.Save();
		}

		private static ItemType ResolveItemTypeForExtension(string extension)
		{
			return extension.ToLowerInvariant() switch {
				".cs" => ItemType.Compile,
				".xaml" => ItemType.Page,
				".resx" => ItemType.EmbeddedResource,
				_ => ItemType.None,
			};
		}

		private static void CopyDirectoryRecursive(string sourceDirectory, string destinationDirectory)
		{
			Directory.CreateDirectory(destinationDirectory);
			foreach (var file in Directory.EnumerateFiles(sourceDirectory)) {
				File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)));
			}
			foreach (var subDirectory in Directory.EnumerateDirectories(sourceDirectory)) {
				CopyDirectoryRecursive(subDirectory, Path.Combine(destinationDirectory, Path.GetFileName(subDirectory)));
			}
		}

		private static string ResolveUniquePath(string targetDirectory, string baseName, string? extension, bool isDirectory)
		{
			Directory.CreateDirectory(targetDirectory);
			var suffix = 0;
			while (true) {
				var candidateName = suffix == 0 ? baseName : $"{baseName}{suffix}";
				var candidatePath = Path.Combine(targetDirectory, extension is null ? candidateName : candidateName + extension);
				var exists = isDirectory ? Directory.Exists(candidatePath) : File.Exists(candidatePath);
				if (!exists)
					return candidatePath;
				suffix++;
			}
		}
	}
}
