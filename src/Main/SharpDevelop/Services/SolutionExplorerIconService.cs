using System.IO;
using System.Windows.Media.Imaging;

using ICSharpCode.Core.Presentation;

namespace ICSharpCode.SharpDevelop.Services;

internal static class SolutionExplorerIconService
{
    public static BitmapSource GetIcon(SolutionExplorerNodeModel node)
    {
        return PresentationResourceService.GetBitmapSource(GetIconKey(node.Kind, node.FullPath, node.IsDirectory));
    }

    private static string GetIconKey(SolutionExplorerNodeKind kind, string path, bool isDirectory)
    {
        return kind switch
        {
            SolutionExplorerNodeKind.Solution => "Icons.16x16.SolutionIcon",
            SolutionExplorerNodeKind.Project or SolutionExplorerNodeKind.ProjectReference => "Icons.16x16.NewProjectIcon",
            SolutionExplorerNodeKind.DependenciesFolder
                or SolutionExplorerNodeKind.ReferencesFolder
                or SolutionExplorerNodeKind.Reference => "Icons.16x16.Reference",
            SolutionExplorerNodeKind.PackagesFolder
                or SolutionExplorerNodeKind.PackageReference => "Icons.16x16.Reference",
            SolutionExplorerNodeKind.Folder or SolutionExplorerNodeKind.GhostFolder => "Icons.16x16.ClosedFolderBitmap",
            SolutionExplorerNodeKind.File
                or SolutionExplorerNodeKind.LinkedFile
                or SolutionExplorerNodeKind.MissingFile
                or SolutionExplorerNodeKind.GhostFile => GetFileIconKey(path),
            _ => isDirectory ? "Icons.16x16.ClosedFolderBitmap" : "Icons.16x16.MiscFiles"
        };
    }

    private static string GetFileIconKey(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csproj" or ".vbproj" or ".fsproj" => "Icons.16x16.NewProjectIcon",
            ".sln" or ".slnx" => "Icons.16x16.SolutionIcon",
            _ => "Icons.16x16.MiscFiles"
        };
    }
}
