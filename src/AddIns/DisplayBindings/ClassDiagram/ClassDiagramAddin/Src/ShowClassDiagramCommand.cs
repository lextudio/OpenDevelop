using System;
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
        var sourceFiles = project.Items.OfType<FileProjectItem>()
            .Select(item => item.FileName.ToString())
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
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
