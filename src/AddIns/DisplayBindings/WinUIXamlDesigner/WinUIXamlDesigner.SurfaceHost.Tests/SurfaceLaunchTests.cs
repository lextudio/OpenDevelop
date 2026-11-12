using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

public class SurfaceLaunchTests
{
	[Theory]
	[InlineData("SURFACE_PORT=63315", 63315)]
	[InlineData("SURFACE_PORT=1", 1)]
	[InlineData("SURFACE_PORT=65535", 65535)]
	public void Port_announcement_is_parsed(string line, int port) => Assert.Equal(port, SurfaceLaunch.TryParsePort(line));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("[Surface] Listening on 127.0.0.1:63315")]
	[InlineData("SURFACE_PORT=")]
	[InlineData("SURFACE_PORT=0")]
	[InlineData("SURFACE_PORT=65536")]
	[InlineData("SURFACE_PORT=-5")]
	[InlineData("SURFACE_PORT=12abc")]
	[InlineData(" SURFACE_PORT=80")]
	public void Anything_else_is_not_a_port(string? line) => Assert.Null(SurfaceLaunch.TryParsePort(line));

	[Fact]
	public void Deep_roots_are_rejected_before_launch()
	{
		string deep = Path.Combine(Path.GetTempPath(), new string('d', SurfaceLaunch.MaxRootLength));
		var ex = Assert.Throws<PathTooLongException>(() => SurfaceLaunch.EnsureShortRoot(deep));
		Assert.Contains("filename too long", ex.Message);
		SurfaceLaunch.EnsureShortRoot(Path.GetTempPath().Length < 100 ? Path.GetTempPath() : Path.GetPathRoot(Path.GetTempPath())!);
	}

	[Fact]
	public void Run_copy_is_a_private_recursive_copy_and_the_cache_entry_is_untouched()
	{
		string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "odsurf-test-" + Guid.NewGuid().ToString("N")[..6]);
		string host = Path.Combine(root, "host");
		Directory.CreateDirectory(Path.Combine(host, "sub"));
		File.WriteAllText(Path.Combine(host, "Surface.exe"), "exe");
		File.WriteAllText(Path.Combine(host, "Surface.designtime.pri"), "pri");
		File.WriteAllText(Path.Combine(host, "sub", "a.dll"), "a");
		try {
			string run1 = SurfaceLaunch.PrepareRunCopy(host, Path.Combine(root, "runs"));
			string run2 = SurfaceLaunch.PrepareRunCopy(host, Path.Combine(root, "runs"));

			Assert.NotEqual(run1, run2);
			Assert.Equal("a", File.ReadAllText(Path.Combine(run1, "sub", "a.dll")));
			// The launcher overwrites the run's PRI in place; the pristine entry must not change.
			File.WriteAllText(Path.Combine(run1, "Surface.designtime.pri"), "changed");
			Assert.Equal("pri", File.ReadAllText(Path.Combine(host, "Surface.designtime.pri")));
			Assert.Equal("--user-pri \"" + Path.Combine(run1, "Surface.designtime.pri") + "\"", SurfaceLaunch.MatchedHostArguments(run1));
		} finally {
			Directory.Delete(root, true);
		}
	}
}
