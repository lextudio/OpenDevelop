namespace OpenDevelop.XamlDom.Tests;

/// <summary>Which WPF implementation this host runs the shared tests against.</summary>
static class TestHost
{
	public const string Name = "Microsoft WPF";
	public const bool IsLibreWpf = false;
	public static bool IsWindows => true; // Microsoft WPF exists only on Windows
}
