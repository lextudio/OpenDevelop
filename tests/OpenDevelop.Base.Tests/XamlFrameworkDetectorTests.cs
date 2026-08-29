using ICSharpCode.SharpDevelop.LanguageServices.Xaml;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class XamlFrameworkDetectorTests
{
	[Theory]
	[InlineData(XamlRuntimeKind.LibreWpf, "WpfDesign.SurfaceHost.dll")]
	[InlineData(XamlRuntimeKind.MicrosoftWpf, "MicrosoftWpfPreview.Host.dll")]
	[InlineData(XamlRuntimeKind.Uno, "WinUIXamlDesigner.UnoHost.dll")]
	[InlineData(XamlRuntimeKind.MicrosoftWinUI, "WinUIXamlDesigner.MicrosoftHost.dll")]
	[InlineData(XamlRuntimeKind.Unknown, null)]
	public void HostSelection_KeepsRuntimeFamiliesIsolated(XamlRuntimeKind runtime, string? expected)
	{
		var context = new XamlFrameworkContext(XamlFrameworkKind.Wpf, runtime, "", "");
		Assert.Equal(expected, XamlDesignerHostSelector.GetHostAssemblyName(context));
	}

	[Theory]
	[InlineData("<Project Sdk=\"Uno.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>", XamlFrameworkKind.Uno, XamlRuntimeKind.Uno)]
	[InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Uno.WinUI\" /></ItemGroup></Project>", XamlFrameworkKind.Uno, XamlRuntimeKind.Uno)]
	[InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.WindowsAppSDK\" /></ItemGroup></Project>", XamlFrameworkKind.WinUI, XamlRuntimeKind.MicrosoftWinUI)]
	[InlineData("<Project Sdk=\"LibreWPF.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>", XamlFrameworkKind.Wpf, XamlRuntimeKind.LibreWpf)]
	[InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>", XamlFrameworkKind.Wpf, XamlRuntimeKind.MicrosoftWpf)]
	[InlineData("<Project Sdk=\"Microsoft.NET.Sdk\" />", XamlFrameworkKind.Unknown, XamlRuntimeKind.Unknown)]
	public void DetectProjectFile_UsesOrderedFrameworkEvidence(string projectXml, XamlFrameworkKind expected, XamlRuntimeKind expectedRuntime)
	{
		var directory = Directory.CreateTempSubdirectory("od-xaml-detect-");
		try {
			var project = Path.Combine(directory.FullName, "Sample.csproj");
			File.WriteAllText(project, projectXml);
			var result = XamlFrameworkDetector.DetectProjectFile(project);
			Assert.Equal(expected, result.Kind);
			Assert.Equal(expectedRuntime, result.Runtime);
		} finally { directory.Delete(true); }
	}
}
