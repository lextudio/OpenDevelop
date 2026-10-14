using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>The project images an image-source picker offers (Visual Studio's Image.Source
/// drop-down): files with an image extension, written as the relative URI a XAML file resolves
/// against its own folder - forward slashes, <c>../</c> for a parent folder.</summary>
public static class ProjectImagePaths
{
	static readonly HashSet<string> imageExtensions = new(StringComparer.OrdinalIgnoreCase) {
		".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff"
	};

	public static bool IsImageFile(string path) => imageExtensions.Contains(Path.GetExtension(path));

	/// <summary>The image files among <paramref name="files"/> relative to
	/// <paramref name="xamlFile"/>'s folder, distinct and in ordinal order.</summary>
	public static IReadOnlyList<string> RelativeTo(string xamlFile, IEnumerable<string> files)
	{
		var folder = Path.GetDirectoryName(Path.GetFullPath(xamlFile)) ?? "";
		return files.Where(file => !string.IsNullOrEmpty(file) && IsImageFile(file))
			.Select(file => Path.GetRelativePath(folder, Path.GetFullPath(file)).Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/'))
			.Distinct(StringComparer.Ordinal)
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToArray();
	}
}
