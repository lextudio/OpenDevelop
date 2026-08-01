using System;
using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.ClassDiagram;

public sealed class ClassDiagramDisplayBinding : IDisplayBinding
{
    public bool CanCreateContentForFile(FileName fileName) =>
        Path.GetExtension(fileName).Equals(".cd", StringComparison.OrdinalIgnoreCase);

    public IViewContent CreateContentForFile(OpenedFile file) => new ClassDiagramViewContent(file);

    public bool IsPreferredBindingForFile(FileName fileName) => CanCreateContentForFile(fileName);

    public double AutoDetectFileContent(FileName fileName, Stream fileContent, string detectedMimeType) =>
        CanCreateContentForFile(fileName) ? 1 : 0;
}
