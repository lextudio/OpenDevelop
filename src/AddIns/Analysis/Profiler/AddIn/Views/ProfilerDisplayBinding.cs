using System.IO;
using System.Text.RegularExpressions;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Workbench;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>
/// Opens <c>.nettrace</c> captures and memory dumps in the Profiler's report view. A dump is chosen by name here (the usual
/// extensions, and the bare <c>core</c> or <c>core.1234</c> of a Linux core file); the file's header is what decides whether it
/// really is one when the report loads.
/// </summary>
public sealed partial class ProfilerDisplayBinding : IDisplayBinding
{
	static readonly string[] Extensions = [".nettrace", ".dmp", ".mdmp", ".hdmp"];

	public bool IsPreferredBindingForFile(FileName fileName) => CanCreateContentForFile(fileName);

	public bool CanCreateContentForFile(FileName fileName)
	{
		var path = fileName.ToString();
		return Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
			|| CoreFileName().IsMatch(Path.GetFileName(path));
	}

	public double AutoDetectFileContent(FileName fileName, Stream fileContent, string detectedMimeType) => 1;

	public IViewContent CreateContentForFile(OpenedFile file) => new ProfilerReportViewContent(file);

	[GeneratedRegex(@"^core(\.\d+)?$", RegexOptions.IgnoreCase)]
	private static partial Regex CoreFileName();
}
