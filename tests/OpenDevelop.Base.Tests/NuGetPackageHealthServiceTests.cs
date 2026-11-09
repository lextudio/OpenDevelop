using System.Net;
using System.Text;
using ICSharpCode.SharpDevelop.NuGet;
using Xunit;
using NuGetSource = NuGet.Configuration.PackageSource;

namespace OpenDevelop.Base.Tests;

// The deprecation and vulnerability data of a package version, read through NuGet.Protocol's metadata resource
// from a feed. The feed here is a small in-process NuGet v3 server that serves a service index and registration
// pages, so NuGet.Protocol's own parsing is what is under test, not a mock of it.
public sealed class NuGetPackageHealthServiceTests : IDisposable
{
	readonly HttpListener listener = new HttpListener();
	readonly string baseUrl;
	readonly CancellationTokenSource stop = new CancellationTokenSource();
	readonly Task serving;
	public List<string> Requests { get; } = new List<string>();

	public NuGetPackageHealthServiceTests()
	{
		var port = FreePort();
		baseUrl = $"http://localhost:{port}/";
		listener.Prefixes.Add(baseUrl);
		listener.Start();
		serving = Task.Run(Serve);
	}

	public void Dispose()
	{
		stop.Cancel();
		listener.Close();
		try { serving.Wait(2000); } catch (AggregateException) { }
	}

	static int FreePort()
	{
		var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		return port;
	}

	string ServiceIndex => $$"""
		{"version":"3.0.0","resources":[
		  {"@id":"{{baseUrl}}reg/","@type":"RegistrationsBaseUrl/3.6.0"},
		  {"@id":"{{baseUrl}}reg/","@type":"RegistrationsBaseUrl"}
		]}
		""";

	// id -> registration index JSON for one version
	string Registration(string id, string version, string extraCatalogFields) => $$"""
		{"@id":"{{baseUrl}}reg/{{id.ToLowerInvariant()}}/index.json","count":1,"items":[
		  {"@id":"{{baseUrl}}reg/{{id.ToLowerInvariant()}}/page/{{version}}/{{version}}.json","count":1,"lower":"{{version}}","upper":"{{version}}","items":[
		    {"@id":"{{baseUrl}}reg/{{id.ToLowerInvariant()}}/{{version}}.json","catalogEntry":{
		       "@id":"{{baseUrl}}catalog/{{id.ToLowerInvariant()}}.{{version}}.json","id":"{{id}}","version":"{{version}}",
		       "authors":"a","description":"d","listed":true,"packageContent":"{{baseUrl}}pkg/{{id.ToLowerInvariant()}}.{{version}}.nupkg"{{extraCatalogFields}}},
		     "packageContent":"{{baseUrl}}pkg/{{id.ToLowerInvariant()}}.{{version}}.nupkg"}
		  ]}
		]}
		""";

	readonly Dictionary<string, string> pages = new Dictionary<string, string>();

	void Serve()
	{
		while (!stop.IsCancellationRequested) {
			HttpListenerContext context;
			try { context = listener.GetContext(); } catch { return; }
			var path = context.Request.Url!.AbsolutePath;
			lock (Requests) Requests.Add(path);
			string? body = path == "/index.json" ? ServiceIndex : pages.GetValueOrDefault(path);
			var bytes = Encoding.UTF8.GetBytes(body ?? "");
			context.Response.StatusCode = body == null ? 404 : 200;
			context.Response.ContentType = "application/json";
			context.Response.OutputStream.Write(bytes, 0, bytes.Length);
			context.Response.Close();
		}
	}

	NuGetSource Feed => new NuGetSource(baseUrl + "index.json", "TestFeed");

	void Serves(string id, string version, string extraCatalogFields) =>
		pages[$"/reg/{id.ToLowerInvariant()}/index.json"] = Registration(id, version, extraCatalogFields);

	const string DeprecatedAndVulnerable = """
		,"deprecation":{"message":"Use Other.Pack","reasons":["Legacy","CriticalBugs"],"alternatePackage":{"id":"Other.Pack","range":"[2.0.0, )"}},
		 "vulnerabilities":[{"advisoryUrl":"https://github.com/advisories/GHSA-aaaa","severity":"3"},{"advisoryUrl":"https://github.com/advisories/GHSA-bbbb","severity":"1"}]
		""";

	[Fact]
	public async Task ReadsDeprecationAndVulnerabilitiesOfAVersion()
	{
		Serves("Old.Pack", "1.0.0", DeprecatedAndVulnerable);

		var health = await NuGetPackageHealthService.GetAsync(new[] { Feed }, "Old.Pack", "1.0.0");

		Assert.True(health.HasWarnings);
		Assert.True(health.IsDeprecated);
		Assert.Equal(new[] { "Legacy", "CriticalBugs" }, health.DeprecationReasons);
		Assert.Equal("Use Other.Pack", health.DeprecationMessage);
		Assert.StartsWith("Other.Pack", health.AlternatePackage);
		Assert.Equal(new[] { "Critical", "Moderate" }, health.Vulnerabilities.Select(v => v.Severity).ToArray());
		Assert.Equal("https://github.com/advisories/GHSA-aaaa", health.Vulnerabilities[0].AdvisoryUrl.ToString());
		var text = health.Describe();
		Assert.Contains("Deprecated (Legacy, CriticalBugs): Use Other.Pack", text);
		Assert.Contains("Use instead: Other.Pack", text);
		Assert.Contains("Known vulnerability, Critical severity: https://github.com/advisories/GHSA-aaaa", text);
	}

	[Fact]
	public async Task AVersionWithNothingWrongHasNoWarnings()
	{
		Serves("Fine.Pack", "1.0.0", "");

		var health = await NuGetPackageHealthService.GetAsync(new[] { Feed }, "Fine.Pack", "1.0.0");

		Assert.False(health.HasWarnings);
		Assert.Equal("", health.Describe());
	}

	[Fact]
	public async Task OnlyVulnerabilitiesAreReportedWhenThePackageIsNotDeprecated()
	{
		Serves("Vuln.Pack", "1.0.0", ",\"vulnerabilities\":[{\"advisoryUrl\":\"https://github.com/advisories/GHSA-cccc\",\"severity\":\"2\"}]");

		var health = await NuGetPackageHealthService.GetAsync(new[] { Feed }, "Vuln.Pack", "1.0.0");

		Assert.False(health.IsDeprecated);
		Assert.Equal("High", Assert.Single(health.Vulnerabilities).Severity);
		Assert.StartsWith("Known vulnerability, High severity", health.Describe());
	}

	[Fact]
	public async Task AnUnknownPackageIsNotAFinding()
	{
		var health = await NuGetPackageHealthService.GetAsync(new[] { Feed }, "Nobody.Knows", "1.0.0");

		Assert.Same(NuGetPackageHealth.Healthy, health);
	}

	[Fact]
	public async Task AFeedThatIsDownIsNotAFindingAndTheNextFeedIsAsked()
	{
		Serves("Old.Pack", "1.0.0", DeprecatedAndVulnerable);
		var dead = new NuGetSource($"http://localhost:{FreePort()}/index.json", "Dead");

		var health = await NuGetPackageHealthService.GetAsync(new[] { dead, Feed }, "Old.Pack", "1.0.0");

		Assert.True(health.IsDeprecated);
		Assert.Same(NuGetPackageHealth.Healthy, await NuGetPackageHealthService.GetAsync(new[] { dead }, "Old.Pack", "1.0.0"));
	}

	[Theory]
	[InlineData("[1.0,2.0)")]
	[InlineData("1.*")]
	[InlineData("")]
	public async Task AVersionThatIsNotAPlainVersionIsNotLookedUp(string version)
	{
		Serves("Old.Pack", "1.0.0", DeprecatedAndVulnerable);

		var health = await NuGetPackageHealthService.GetAsync(new[] { Feed }, "Old.Pack", version);

		Assert.Same(NuGetPackageHealth.Healthy, health);
		lock (Requests) Assert.Empty(Requests);
	}

	[Fact]
	public async Task ALocalFolderFeedHasNoSuchDataAndIsNotAnError()
	{
		var folder = Directory.CreateTempSubdirectory("health-local-feed-").FullName;
		try {
			var health = await NuGetPackageHealthService.GetAsync(new[] { new NuGetSource(folder, "Local") }, "Old.Pack", "1.0.0");
			Assert.False(health.HasWarnings);
		} finally {
			Directory.Delete(folder, recursive: true);
		}
	}

	[Theory]
	[InlineData(0, "Low")]
	[InlineData(1, "Moderate")]
	[InlineData(2, "High")]
	[InlineData(3, "Critical")]
	[InlineData(-1, "Unknown")]
	[InlineData(9, "Unknown")]
	public void SeverityNumbersAreNamedAsNuGetNamesThem(int number, string name)
	{
		// internal: reached through the vulnerability list in the first test; this pins the mapping itself
		var method = typeof(NuGetPackageHealthService).GetMethod("SeverityName", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
		Assert.Equal(name, method.Invoke(null, new object[] { number }));
	}
}
