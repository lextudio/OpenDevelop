using System.IO;
using System.Windows.Media.Imaging;

using ICSharpCode.Core.Presentation;

namespace ICSharpCode.SharpDevelop.Services;

internal static class ProjectBrowserIconService
{
    public static BitmapSource GetIcon(ProjectBrowserNodeModel node)
    {
        return PresentationResourceService.GetBitmapSource(GetIconKey(node));
    }

    private static string GetIconKey(ProjectBrowserNodeModel node)
    {
        return node.Kind switch
        {
            ProjectBrowserNodeKind.Solution => "Icons.16x16.SolutionIcon",
            ProjectBrowserNodeKind.Project or ProjectBrowserNodeKind.ProjectReference => "Icons.16x16.NewProjectIcon",
            // Dependencies/References/Packages are all modeled as a handful of shared
            // ProjectBrowserNodeKind values (see SharpDevelopProjectTreeProvider.
            // GetDependencyGroupFlag), so "Assemblies"/"Analyzers"/"Frameworks"/etc. can only be
            // told apart by their display Name, not by Kind alone.
            ProjectBrowserNodeKind.DependenciesFolder
                or ProjectBrowserNodeKind.ReferencesFolder => GetDependencyGroupIconKey(node.Name),
            ProjectBrowserNodeKind.Reference => "Icons.16x16.Reference",
            ProjectBrowserNodeKind.PackagesFolder
                or ProjectBrowserNodeKind.PackageReference => "Icons.16x16.Library",
            ProjectBrowserNodeKind.Folder or ProjectBrowserNodeKind.GhostFolder => "Icons.16x16.ClosedFolderBitmap",
            ProjectBrowserNodeKind.File
                or ProjectBrowserNodeKind.LinkedFile
                or ProjectBrowserNodeKind.MissingFile
                or ProjectBrowserNodeKind.GhostFile => GetFileIconKey(node.FullPath),
            _ => node.IsDirectory ? "Icons.16x16.ClosedFolderBitmap" : "Icons.16x16.MiscFiles"
        };
    }

    private static string GetDependencyGroupIconKey(string name)
    {
        return name switch
        {
            "Assemblies" => "Icons.16x16.Assembly",
            "Analyzers" => "Icons.16x16.Analyzers",
            "Frameworks" or "SDKs" => "Icons.16x16.Frameworks",
            "Projects" => "Icons.16x16.Application",
            "COM" => "Icons.16x16.Component",
            _ => "Icons.16x16.Reference"
        };
    }

    private static string GetFileIconKey(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csproj" or ".vbproj" or ".fsproj" => "Icons.16x16.NewProjectIcon",
            ".sln" or ".slnx" => "Icons.16x16.SolutionFolderSwitch",
            ".cs" => "Icons.16x16.CSSourceFile",
            ".xaml" => "Icons.16x16.XMLFile",
            ".json" => "Icons.16x16.JSONFile",
            ".xml" or ".config" => "Icons.16x16.XMLFile",
            ".htm" or ".html" => "Icons.16x16.HTMLFile",
            ".css" => "Icons.16x16.StyleSheet",
            ".js" => "Icons.16x16.JSScript",
            ".md" or ".markdown" => "Icons.16x16.MarkdownFile",
            ".sql" => "Icons.16x16.SQLFile",
            ".resx" => "Icons.16x16.ResourceSymbols",
            ".settings" => "Icons.16x16.SettingsFile",
            ".txt" => "Icons.16x16.TextFile",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".cur" or ".bmp" or ".svg" => "Icons.16x16.Image",
            ".cshtml" or ".razor" => "Icons.16x16.CSRazorFile",
            ".aspx" or ".ascx" => "Icons.16x16.ASPXFile",
            ".master" => "Icons.16x16.MasterPage",
            ".skin" => "Icons.16x16.SkinFile",
            ".manifest" => "Icons.16x16.Manifest",
            ".dll" or ".exe" => "Icons.16x16.BinaryFile",
            ".vb" => "Icons.16x16.VBFile",
            ".fs" => "Icons.16x16.FSFile",
            ".cpp" or ".c" => "Icons.16x16.CPPSourceFile",
            ".h" or ".hpp" => "Icons.16x16.CPPHeaderFile",
            _ => "Icons.16x16.MiscFiles"
        };
    }
}
