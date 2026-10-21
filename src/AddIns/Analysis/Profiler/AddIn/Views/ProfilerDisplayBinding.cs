using System.IO;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Workbench;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>Opens <c>.nettrace</c> captures in the Profiler's report view.</summary>
public sealed class ProfilerDisplayBinding : IDisplayBinding
{
    public bool IsPreferredBindingForFile(FileName fileName) => CanCreateContentForFile(fileName);

    public bool CanCreateContentForFile(FileName fileName) =>
        Path.GetExtension(fileName.ToString()).Equals(".nettrace", StringComparison.OrdinalIgnoreCase);

    public double AutoDetectFileContent(FileName fileName, Stream fileContent, string detectedMimeType) => 1;

    public IViewContent CreateContentForFile(OpenedFile file) => new ProfilerReportViewContent(file);
}
