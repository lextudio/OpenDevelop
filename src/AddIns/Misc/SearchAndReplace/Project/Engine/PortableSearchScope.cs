#nullable enable

using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.SearchAndReplace.Portable;

public sealed record PortableSearchScope(
	PortableSearchScopeKind Kind,
	string? Directory,
	IReadOnlyList<string> FilePaths)
{
	public static PortableSearchScope ForDirectory(string directory) =>
		new(PortableSearchScopeKind.Directory, directory, []);

	public static PortableSearchScope ForFiles(PortableSearchScopeKind kind, IEnumerable<string> filePaths) =>
		new(kind, null, filePaths.Distinct(System.StringComparer.OrdinalIgnoreCase).ToArray());

	public bool IsDirectory => Kind == PortableSearchScopeKind.Directory;
}
