namespace OpenDevelop.XamlDom.Tests;

/// <summary>Which WPF implementation this host runs the shared tests against.</summary>
static class TestHost
{
	public const string Name = "LibreWPF";
	public const bool IsLibreWpf = true;
	public static bool IsWindows => OperatingSystem.IsWindows();
}
