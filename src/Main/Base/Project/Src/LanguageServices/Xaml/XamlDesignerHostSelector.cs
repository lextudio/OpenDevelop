namespace ICSharpCode.SharpDevelop.LanguageServices.Xaml;

/// <summary>Maps evaluated project runtime identity to a deployed child host. The result is
/// deliberately a file name only: no UI-framework assembly crosses into the IDE process.</summary>
public static class XamlDesignerHostSelector
{
	public static string? GetHostAssemblyName(XamlFrameworkContext context) => context.Runtime switch {
		XamlRuntimeKind.LibreWpf => "WpfDesign.SurfaceHost.dll",
		XamlRuntimeKind.MicrosoftWpf => "MicrosoftWpfPreview.Host.dll",
		XamlRuntimeKind.Uno => "WinUIXamlDesigner.UnoHost.dll",
		XamlRuntimeKind.MicrosoftWinUI => "WinUIXamlDesigner.MicrosoftHost.dll",
		_ => null
	};
}
