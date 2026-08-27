// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Solution Explorer "Assets" subtree for a Stride game project (doc/technotes/stride-game-studio.md
// "Projects pad / Solution Explorer spec for a Stride project"): the .sdpkg is the single source
// of truth for assets, so this contributes VIRTUAL nodes (not MSBuild items) parsed straight from
// the package. File-like nodes carry the asset's real on-disk path so a double-click routes
// through the normal workbench open path into the owning asset-editor display binding.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ICSharpCode.SharpDevelop.Services;

namespace ICSharpCode.StrideGameStudio
{
	/// <summary>
	/// Contributes a virtual "Assets" subtree under a Stride game project, derived from the
	/// project's <c>.sdpkg</c> asset package. Registered via
	/// <see cref="RegisterStrideProjectTreeContributorCommand"/> on <c>/SharpDevelop/Autostart</c>.
	/// </summary>
	public sealed class StrideProjectTreeContributor : IProjectTreeContributor
	{
		public bool CanContribute(string projectFilePath)
		{
			var directory = Path.GetDirectoryName(projectFilePath);
			return !string.IsNullOrEmpty(directory)
				&& Directory.Exists(directory)
				&& Directory.EnumerateFiles(directory, "*.sdpkg", SearchOption.TopDirectoryOnly).Any();
		}

		public IReadOnlyList<ProjectBrowserContribution> GetContributions(string projectFilePath)
		{
			var directory = Path.GetDirectoryName(projectFilePath);
			if (string.IsNullOrEmpty(directory))
				return Array.Empty<ProjectBrowserContribution>();

			var packageFile = Directory.EnumerateFiles(directory, "*.sdpkg", SearchOption.TopDirectoryOnly).FirstOrDefault();
			if (packageFile == null)
				return Array.Empty<ProjectBrowserContribution>();

			try
			{
				var assetFolders = ParseAssetFolders(packageFile);
				var children = new List<ProjectBrowserContribution>();
				foreach (var folderPath in assetFolders)
				{
					var node = BuildFolderNode(directory, folderPath);
					if (node != null)
						children.Add(node);
				}

				// No declared asset folders (or none on disk yet) - still show an empty Assets
				// root so the tree is stable and the concept is visible.
				return new[]
				{
					new ProjectBrowserContribution
					{
						Caption = "Assets",
						IsFolder = true,
						Children = children
					}
				};
			}
			catch (Exception ex)
			{
				// Never break the whole Solution Explorer because a package failed to parse.
				ICSharpCode.Core.LoggingService.Warn("[StrideGameStudio] .sdpkg tree contribution failed: " + ex);
				return Array.Empty<ProjectBrowserContribution>();
			}
		}

		/// <summary>Lightweight .sdpkg parse - only the AssetFolders block is needed for the tree.
		/// Full package/session loading is deliberately avoided here (it is heavy and belongs to
		/// the asset editors, see StridePackageView).</summary>
		static string[] ParseAssetFolders(string packageFile)
		{
			var folders = new List<string>();
			var inAssetFolders = false;
			foreach (var rawLine in File.ReadLines(packageFile))
			{
				var line = rawLine.Trim();
				if (line == "AssetFolders:")
				{
					inAssetFolders = true;
					continue;
				}

				if (inAssetFolders)
				{
					// Stop at the next top-level YAML key (no indentation).
					if (line.Length > 0 && !char.IsWhiteSpace(rawLine[0]) && !line.StartsWith("-", StringComparison.Ordinal))
						break;

					const string pathMarker = "Path:";
					var idx = line.IndexOf(pathMarker, StringComparison.Ordinal);
					if (idx < 0)
						continue;

					var value = line[(idx + pathMarker.Length)..].Trim();
					// Strip the leading "!dir " tag if present.
					if (value.StartsWith("!dir", StringComparison.Ordinal))
						value = value["!dir".Length..].Trim();
					value = value.Trim('\'', '"');
					if (value.Length > 0)
						folders.Add(value);
				}
			}

			return folders.ToArray();
		}

		/// <summary>Recursively builds a folder/file contribution for one .sdpkg asset-folder
		/// path (resolved relative to the package file). Skips obj/bin.</summary>
		static ProjectBrowserContribution? BuildFolderNode(string packageDirectory, string folderPath)
		{
			var fullPath = Path.IsPathRooted(folderPath)
				? folderPath
				: Path.GetFullPath(Path.Combine(packageDirectory, folderPath));
			if (!Directory.Exists(fullPath))
				return null;

			var node = new ProjectBrowserContribution
			{
				Caption = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
				IsFolder = true,
				FullPath = fullPath,
				Children = BuildChildren(fullPath)
			};
			return node;
		}

		static List<ProjectBrowserContribution> BuildChildren(string directory)
		{
			var children = new List<ProjectBrowserContribution>();
			foreach (var subDir in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
			{
				var name = Path.GetFileName(subDir);
				if (string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase))
					continue;
				children.Add(new ProjectBrowserContribution
				{
					Caption = name,
					IsFolder = true,
					FullPath = subDir,
					Children = BuildChildren(subDir)
				});
			}

			foreach (var file in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
			{
				children.Add(new ProjectBrowserContribution
				{
					Caption = Path.GetFileName(file),
					IsFolder = false,
					FullPath = file
				});
			}

			return children;
		}
	}
}