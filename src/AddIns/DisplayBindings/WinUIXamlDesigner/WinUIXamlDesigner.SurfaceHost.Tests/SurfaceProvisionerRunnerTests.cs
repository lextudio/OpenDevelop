using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

public class SurfaceProvisionerRunnerTests
{
	static CancellationToken Ct => TestContext.Current.CancellationToken;

	[Fact]
	public async Task Missing_inputs_are_a_failed_result_naming_the_file_not_an_exception()
	{
		string root = Path.Combine(Path.GetTempPath(), "od-prov-" + Guid.NewGuid().ToString("N")[..6]);
		Directory.CreateDirectory(root);
		try {
			var result = await SurfaceProvisionerRunner.RunAsync(
				new SurfaceProvisionRequest(root, Path.Combine(root, "p.csproj"), @"C:\odsurf-x\cache", @"C:\odsurf-x\stg"), null, Ct);

			Assert.Equal(SurfaceProvisionStatus.Failed, result.Status);
			Assert.False(result.IsMatched);
			Assert.Contains("SurfaceProvisioner.exe", result.Error);
		} finally {
			Directory.Delete(root, true);
		}
	}

	/// <summary>
	/// Runs the real provisioner from an unpacked LeXtudio.WinUISurface package. Needs OD_SURFACE_HOST_SDK (the package's
	/// HostSDK folder) and OD_SURFACE_PROJECT (a project that references the Windows App SDK); the cache is the short
	/// OD_SURFACE_ROOT, or C:\odsurf-test.
	/// </summary>
	[Fact]
	public async Task Real_provisioner_builds_or_reuses_a_matched_host()
	{
		string? sdk = Environment.GetEnvironmentVariable("OD_SURFACE_HOST_SDK");
		string? project = Environment.GetEnvironmentVariable("OD_SURFACE_PROJECT");
		if (string.IsNullOrEmpty(sdk) || string.IsNullOrEmpty(project) || !OperatingSystem.IsWindows())
			Assert.Skip("OD_SURFACE_HOST_SDK / OD_SURFACE_PROJECT are not set (or not Windows).");
		string root = Environment.GetEnvironmentVariable("OD_SURFACE_ROOT") ?? @"C:\odsurf-test";

		var lines = new List<string>();
		var result = await SurfaceProvisionerRunner.RunAsync(
			new SurfaceProvisionRequest(sdk, project, Path.Combine(root, "cache"), Path.Combine(root, "stg")), lines.Add, Ct);

		Assert.True(result.IsMatched, result.Error + "\n" + string.Join("\n", lines.TakeLast(15)));
		Assert.True(File.Exists(result.HostExePath));
		Assert.Matches(@"^\d+\.\d+\.\d+", result.DiscoveredVersion);
	}
}
