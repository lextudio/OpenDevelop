using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.ClassDiagram;

public sealed class ShowClassDiagramCommand : AbstractMenuCommand
{
    public override void Run()
    {
        var project = SD.ProjectService.CurrentProject;
        if (project is null)
            return;

        var fileName = FileName.Create(Path.Combine(project.Directory, project.Name + ".cd"));
        var sourceFiles = ClassDiagramProjectSources.GetSourceFiles(project);
        var document = ClassDiagramDocument.Create(sourceFiles);

        FileUtility.ObservedSave(
            new NamedFileOperationDelegate(target => SaveAndOpen(project, target, document)),
            fileName,
            FileErrorPolicy.ProvideAlternative);
    }

    static void SaveAndOpen(IProject project, FileName fileName, ClassDiagramDocument document)
    {
        document.Save(fileName);
        if (!project.Items.OfType<FileProjectItem>().Any(item => item.FileName == fileName)) {
            var item = new FileProjectItem(project, ItemType.Content) { FileName = fileName };
            ProjectService.AddProjectItem(project, item);
            project.Save();
        }
        FileService.OpenFile(fileName);
    }
}

internal static class ClassDiagramProjectSources
{
    public static IReadOnlyList<string> GetSourceFiles(IProject project)
    {
        if (project is MSBuildBasedProject msbuildProject && msbuildProject.IsSdkStyleProject) {
            var projectDirectory = project.Directory.ToString();
            return msbuildProject.GetEvaluatedProjectItems()
                .Where(item => string.Equals(item.ItemType, "Compile", StringComparison.OrdinalIgnoreCase))
                .Select(item => ResolvePath(projectDirectory, item.EvaluatedInclude))
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path) && !IsBuildOutput(path, projectDirectory))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return project.Items.OfType<FileProjectItem>()
            .Select(item => item.FileName.ToString())
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static string ResolvePath(string projectDirectory, string include)
    {
        if (string.IsNullOrWhiteSpace(include))
            return string.Empty;
        var normalized = include.Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(normalized)
            ? normalized
            : Path.Combine(projectDirectory, normalized));
    }

    static bool IsBuildOutput(string path, string projectDirectory)
    {
        var relative = Path.GetRelativePath(projectDirectory, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }
}
