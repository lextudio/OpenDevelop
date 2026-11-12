using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

public class SurfaceProvisionResultTests
{
	// Shape taken from a real `SurfaceProvisioner --build` run on tests/fixtures/WinUIDesignerFixture/Microsoft.
	const string Built = """
		{"contractVersion":1,"status":"built","success":true,"error":null,"cancelled":false,"discoveredVersion":"2.4.0",
		"versionSource":2,"winUiComponent":"2.3.6","hostDir":"C:\\wxp\\cache\\host","hostExePath":"C:\\wxp\\cache\\host\\Surface.exe",
		"mergedPriPath":"C:\\wxp\\cache\\host\\Surface.designtime.pri","cacheHit":false,"usedFallback":false,
		"managedMatched":true,"nativeMatched":true,"matchedHost":true,"totalSeconds":211.77}
		""";

	[Fact]
	public void Built_and_cached_hosts_are_matched()
	{
		var built = SurfaceProvisionResult.Parse(Built);
		Assert.Equal(SurfaceProvisionStatus.Built, built.Status);
		Assert.True(built.IsMatched);
		Assert.Equal("2.4.0", built.DiscoveredVersion);
		Assert.Equal(@"C:\wxp\cache\host\Surface.exe", built.HostExePath);

		var cached = SurfaceProvisionResult.Parse(Built.Replace("\"built\"", "\"cached\"").Replace("\"cacheHit\":false", "\"cacheHit\":true"));
		Assert.Equal(SurfaceProvisionStatus.Cached, cached.Status);
		Assert.True(cached.CacheHit);
		Assert.True(cached.IsMatched);
	}

	[Fact]
	public void Degraded_is_never_matched_even_with_a_host_path()
	{
		var r = SurfaceProvisionResult.Parse(Built.Replace("\"built\"", "\"degraded\"").Replace("\"success\":true", "\"success\":false"));
		Assert.Equal(SurfaceProvisionStatus.Degraded, r.Status);
		Assert.False(r.IsMatched);
	}

	[Fact]
	public void Built_that_does_not_claim_success_is_a_failure()
	{
		var r = SurfaceProvisionResult.Parse(Built.Replace("\"success\":true", "\"success\":false"));
		Assert.Equal(SurfaceProvisionStatus.Failed, r.Status);
		Assert.False(r.IsMatched);
	}

	[Fact]
	public void A_matched_flag_alone_is_not_enough_without_a_pri()
	{
		var r = SurfaceProvisionResult.Parse(Built.Replace("\"mergedPriPath\":\"C:\\\\wxp\\\\cache\\\\host\\\\Surface.designtime.pri\",", ""));
		Assert.False(r.IsMatched);
	}

	[Theory]
	[InlineData("")]
	[InlineData("not json")]
	[InlineData("[]")]
	[InlineData("{\"contractVersion\":2,\"status\":\"built\",\"success\":true}")]
	[InlineData("{\"contractVersion\":1,\"status\":\"mystery\",\"success\":true}")]
	public void Unusable_output_is_a_failure_not_an_exception(string stdout)
	{
		var r = SurfaceProvisionResult.Parse(stdout);
		Assert.Equal(SurfaceProvisionStatus.Failed, r.Status);
		Assert.False(r.IsMatched);
	}

	[Fact]
	public void Failure_keeps_the_originating_error()
	{
		var r = SurfaceProvisionResult.Parse(
			"{\"contractVersion\":1,\"status\":\"failed\",\"success\":false,\"error\":\"Could not discover a WindowsAppSDK version.\",\"cancelled\":false}");
		Assert.Equal("Could not discover a WindowsAppSDK version.", r.Error);
		Assert.False(r.Cancelled);
	}
}
