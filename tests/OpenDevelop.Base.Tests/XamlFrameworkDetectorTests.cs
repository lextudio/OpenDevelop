using ICSharpCode.SharpDevelop.LanguageServices.Xaml;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class XamlFrameworkDetectorTests
{
	[Theory]
	[InlineData("<Project Sdk=\"Uno.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>", XamlFrameworkKind.Uno)]
	[InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Uno.WinUI\" /></ItemGroup></Project>", XamlFrameworkKind.Uno)]
	[InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.WindowsAppSDK\" /></ItemGroup></Project>", XamlFrameworkKind.WinUI)]
	[InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>", XamlFrameworkKind.Wpf)]
	[InlineData("<Project Sdk=\"Microsoft.NET.Sdk\" />", XamlFrameworkKind.Unknown)]
	public void DetectProjectFile_UsesOrderedFrameworkEvidence(string projectXml, XamlFrameworkKind expected)
	{
		var directory = Directory.CreateTempSubdirectory("od-xaml-detect-");
		try {
			var project = Path.Combine(directory.FullName, "Sample.csproj");
			File.WriteAllText(project, projectXml);
			Assert.Equal(expected, XamlFrameworkDetector.DetectProjectFile(project).Kind);
		} finally { directory.Delete(true); }
	}
}
