using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

/// <summary>
/// A tripwire for a measured limitation. The Surface's frame mode renders in the Light theme whatever SURFACE_THEME says (bundled 2.2.0
/// and a matched 2.4.0 host both: byte-identical frames, and the surface logs "Light theme" either way; upstream's SetTheme is a no-op),
/// so the child refuses design/theme instead of restarting the surface to no visible effect. If a newer surface starts honouring the
/// variable this test fails: that is the signal to implement design/theme by restarting the surface with SURFACE_THEME.
/// Needs OD_SURFACE_EXE (and optionally OD_SURFACE_ARGS), like the other real-surface tests.
/// </summary>
public class SurfaceThemeObservationTests
{
	const string Xaml = "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">"
		+ "<StackPanel Padding=\"16\" Spacing=\"8\"><TextBlock Text=\"theme\"/><Button Content=\"b\"/><CheckBox Content=\"c\" IsChecked=\"True\"/></StackPanel></Page>";

	static async Task<string> FrameHashAsync(string exe, string arguments, string? theme, CancellationToken cancellationToken)
	{
		string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
		var psi = new ProcessStartInfo(exe, arguments) {
			UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
			WorkingDirectory = Path.GetDirectoryName(exe)!,
		};
		psi.Environment["SURFACE_AUTH_TOKEN"] = token;
		if (theme == null) psi.Environment.Remove("SURFACE_THEME"); else psi.Environment["SURFACE_THEME"] = theme;
		using var process = Process.Start(psi)!;
		_ = process.StandardError.ReadToEndAsync(cancellationToken);
		try {
			int? port = null;
			while (port == null)
				port = SurfaceLaunch.TryParsePort(await process.StandardOutput.ReadLineAsync(cancellationToken)
					?? throw new InvalidOperationException("The surface exited before announcing a port."));
			using var tcp = new TcpClient();
			await tcp.ConnectAsync("127.0.0.1", port.Value, cancellationToken);
			await using var connection = await SurfaceConnection.ConnectAsync(tcp.GetStream(), token, TimeSpan.FromSeconds(30), cancellationToken);
			var result = await connection.LoadXamlAsync(Xaml, 480, 320, 1.0, false, null, cancellationToken);
			return Convert.ToHexString(SHA256.HashData(result.Frame!.Png))[..12];
		} finally {
			if (!process.HasExited)
				process.Kill(true);
		}
	}

	[Fact]
	public async Task A_dark_launch_still_renders_the_same_frame_as_a_light_one_so_theme_changes_stay_refused()
	{
		string? exe = Environment.GetEnvironmentVariable("OD_SURFACE_EXE");
		if (string.IsNullOrEmpty(exe) || !OperatingSystem.IsWindows())
			Assert.Skip("OD_SURFACE_EXE is not set (or not Windows).");
		string arguments = Environment.GetEnvironmentVariable("OD_SURFACE_ARGS") ?? "";
		var ct = TestContext.Current.CancellationToken;

		string light = await FrameHashAsync(exe, arguments, "Light", ct);
		string dark = await FrameHashAsync(exe, arguments, "Dark", ct);

		Assert.True(light == dark,
			"The surface now renders a different frame for SURFACE_THEME=Dark. Implement design/theme in the Surface child by starting a new "
			+ "surface with SURFACE_THEME (a theme is fixed at startup), replaying the document into it, and retiring the old one.");
	}
}
