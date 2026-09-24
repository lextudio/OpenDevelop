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
	[InlineData("<Project Sdk=\"Uno.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>", XamlFrameworkKind.Uno, XamlRuntimeKind.Uno, XamlRuntimeKind.Uno)]
	[InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Uno.WinUI\" /></ItemGroup></Project>", XamlFrameworkKind.Uno, XamlRuntimeKind.Uno, XamlRuntimeKind.Uno)]
	// WinUI reports the SAME runtime on every platform, deliberately. An earlier detector mapped
	// WindowsAppSDK/UseWinUI projects to the Uno runtime off Windows, so a native WinUI page was
	// silently rendered by the Uno/ProGPU compatibility host; commit c7520ef288 ("Serve WinUI and
	// WinForms projects strictly by their own designer backends") removed that, because a project
	// being unrunnable here should surface as an unavailable host rather than as a page quietly
	// served by a different runtime. This expectation therefore has no platform switch - do not
	// "fix" it back to Uno.
	[InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.WindowsAppSDK\" /></ItemGroup></Project>", XamlFrameworkKind.WinUI, XamlRuntimeKind.MicrosoftWinUI, XamlRuntimeKind.MicrosoftWinUI)]
	[InlineData("<Project Sdk=\"LibreWPF.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>", XamlFrameworkKind.Wpf, XamlRuntimeKind.LibreWpf, XamlRuntimeKind.LibreWpf)]
	// WPF follows the same rule: a plain Microsoft.NET.Sdk WPF project is Microsoft WPF on every
	// OS. It used to be re-identified as LibreWPF off Windows, which designed a Microsoft WPF
	// project with another runtime's controls; the designer now reports the OS as unsupported
	// instead. LibreWPF comes only from explicit evidence (LibreWPF.Sdk) in the project file.
	[InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>", XamlFrameworkKind.Wpf, XamlRuntimeKind.MicrosoftWpf, XamlRuntimeKind.MicrosoftWpf)]
	[InlineData("<Project Sdk=\"Microsoft.NET.Sdk\" />", XamlFrameworkKind.Unknown, XamlRuntimeKind.Unknown, XamlRuntimeKind.Unknown)]
	public void DetectProjectFile_UsesOrderedFrameworkEvidence(string projectXml, XamlFrameworkKind expected, XamlRuntimeKind expectedRuntimeOnWindows, XamlRuntimeKind expectedRuntimeElsewhere)
	{
		var directory = Directory.CreateTempSubdirectory("od-xaml-detect-");
		try {
			var project = Path.Combine(directory.FullName, "Sample.csproj");
			File.WriteAllText(project, projectXml);
			var result = XamlFrameworkDetector.DetectProjectFile(project);
			Assert.Equal(expected, result.Kind);
			Assert.Equal(
				OperatingSystem.IsWindows() ? expectedRuntimeOnWindows : expectedRuntimeElsewhere,
				result.Runtime);
		} finally { directory.Delete(true); }
	}
}
