using System.Diagnostics;
using System.Net.Sockets;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

/// <summary>
/// Drives the real upstream Surface.exe. Skipped unless OD_SURFACE_EXE names a built Surface.exe (from a short
/// path: see <see cref="SurfaceLaunch.MaxRootLength"/>). Optional OD_SURFACE_ARGS carries its arguments, e.g. the
/// matched host's <c>--user-pri</c> from <see cref="SurfaceLaunch.MatchedHostArguments"/>.
/// </summary>
public class SurfaceRealProcessTests
{
	const string Xaml = """
		<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
		  <StackPanel Padding="16"><TextBlock Text="od"/><Button Content="b"/></StackPanel>
		</Page>
		""";

	[Fact]
	public async Task Real_surface_renders_a_png_frame()
	{
		string? exe = Environment.GetEnvironmentVariable("OD_SURFACE_EXE");
		if (string.IsNullOrEmpty(exe) || !OperatingSystem.IsWindows())
			Assert.Skip("OD_SURFACE_EXE is not set (or not Windows).");
		SurfaceLaunch.EnsureShortRoot(Path.GetDirectoryName(exe)!);

		string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
		var psi = new ProcessStartInfo(exe, Environment.GetEnvironmentVariable("OD_SURFACE_ARGS") ?? "") {
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = Path.GetDirectoryName(exe)!,
		};
		psi.Environment["SURFACE_AUTH_TOKEN"] = token;
		using var process = Process.Start(psi)!;
		_ = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		try {
			int? port = null;
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
			while (port == null)
				port = SurfaceLaunch.TryParsePort(await process.StandardOutput.ReadLineAsync(cts.Token)
					?? throw new InvalidOperationException("Surface exited before announcing a port."));

			using var tcp = new TcpClient();
			await tcp.ConnectAsync("127.0.0.1", port.Value, cts.Token);
			await using var conn = await SurfaceConnection.ConnectAsync(tcp.GetStream(), token, TimeSpan.FromSeconds(30), cts.Token);
			var result = await conn.LoadXamlAsync(Xaml, 480, 320, 1.0, false, null, cts.Token);

			Assert.Null(result.Error);
			Assert.True(result.Frame!.Png.Length > 100);
			Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, result.Frame.Png[..4]);
		} finally {
			if (!process.HasExited)
				process.Kill(true);
		}
	}
}
