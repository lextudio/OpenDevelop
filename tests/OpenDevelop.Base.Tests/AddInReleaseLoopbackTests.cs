using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICSharpCode.AddInManager2.Model;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using Xunit;
using static OpenDevelop.Base.Tests.AddInTestSupport;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The whole GitHub Releases chain over real HTTP: a local fake of api.github.com and the release asset
/// host, the real GitHubReleaseSource and HttpClient, the release service and the real installer.
/// </summary>
[Collection(ProcessWideStateCollection.Name)]
public class AddInReleaseLoopbackTests : IDisposable
{
	readonly string directory = Path.Combine(Path.GetTempPath(), "od-loopback-" + Guid.NewGuid().ToString("N"));
	readonly FakeGitHub api = new();
	readonly FakeGitHub assets = new();

	public AddInReleaseLoopbackTests()
	{
		Directory.CreateDirectory(directory);
		InitializeServices();
	}

	public void Dispose()
	{
		api.Dispose();
		assets.Dispose();
		SD.TearDownForUnitTests();
		if (Directory.Exists(directory))
			Directory.Delete(directory, true);
	}

	/// <summary>A tiny HTTP server that answers from a table of path -> handler.</summary>
	sealed class FakeGitHub : IDisposable
	{
		readonly HttpListener listener = new();
		public Uri BaseUri { get; }
		public Dictionary<string, Action<HttpListenerRequest, HttpListenerResponse>> Routes { get; } = new();
		public List<(string Path, string? IfNoneMatch, string? Authorization)> Requests { get; } = new();

		public FakeGitHub()
		{
			int port;
			using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
			{
				probe.Start();
				port = ((IPEndPoint)probe.LocalEndpoint).Port;
			}
			BaseUri = new Uri($"http://127.0.0.1:{port}/");
			listener.Prefixes.Add(BaseUri.ToString());
			listener.Start();
			_ = Task.Run(Serve);
		}

		async Task Serve()
		{
			while (listener.IsListening)
			{
				HttpListenerContext context;
				try { context = await listener.GetContextAsync(); }
				catch (Exception) { return; }
				try
				{
					string path = context.Request.Url!.AbsolutePath;
					lock (Requests)
						Requests.Add((path + context.Request.Url.Query, context.Request.Headers["If-None-Match"], context.Request.Headers["Authorization"]));
					if (Routes.TryGetValue(path, out var route))
						route(context.Request, context.Response);
					else
						context.Response.StatusCode = 404;
				}
				catch (Exception) { context.Response.StatusCode = 500; }
				finally { context.Response.Close(); }
			}
		}

		public void Dispose()
		{
			listener.Close();
		}
	}

	static byte[] PackageBytes(string directory, string version, string name)
	{
		string path = Package(directory, name,
			("Vendor.Sample.addin", Manifest(version: version, hostRange: "0.0-99.0")),
			("Sample.dll", "dll " + version));
		return File.ReadAllBytes(path);
	}

	static string ReleasesJson(Uri downloadBase, params (string Tag, byte[] Content, bool Prerelease)[] releases)
	{
		return JsonSerializer.Serialize(releases.Select(r => new Dictionary<string, object?>
		{
			["tag_name"] = r.Tag,
			["prerelease"] = r.Prerelease,
			["draft"] = false,
			["body"] = "notes for " + r.Tag,
			["published_at"] = "2026-11-01T10:00:00Z",
			["assets"] = new[]
			{
				new Dictionary<string, object?>
				{
					["name"] = "Sample.odaddin",
					["state"] = "uploaded",
					["size"] = r.Content.Length,
					["digest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(r.Content)).ToLowerInvariant(),
					["browser_download_url"] = new Uri(downloadBase, $"owner/my-addin/releases/download/{r.Tag}/Sample.odaddin").ToString(),
				}
			},
		}));
	}

	ReleaseHarness Harness(GitHubReleaseSourceOptions options, string cacheDirectory)
	{
		var h = new ReleaseHarness(directory, "owner/my-addin");
		var source = new GitHubReleaseSource(new HttpClient(), new ReleaseCache(cacheDirectory), options);
		var service = new AddInReleaseService(source, h.Settings, h.Events, h.Setup, h.Management);
		return new ReleaseHarness(h, service);
	}

	GitHubReleaseSourceOptions Options() => new() { ApiBase = api.BaseUri, DownloadBase = assets.BaseUri };

	[Fact]
	public async Task ListsCachesRevalidatesDownloadsThroughARedirectAndInstalls()
	{
		byte[] v1 = PackageBytes(directory, "1.0.0", "v1.odaddin");
		byte[] v11 = PackageBytes(directory, "1.1.0", "v11.odaddin");
		string releases = ReleasesJson(assets.BaseUri, ("v1.0.0", v1, false));
		api.Routes["/repos/owner/my-addin/releases"] = (request, response) =>
		{
			if (request.Headers["If-None-Match"] == "\"r1\"")
			{
				response.StatusCode = 304;
				return;
			}
			response.Headers["ETag"] = "\"r1\"";
			response.ContentType = "application/json";
			byte[] body = Encoding.UTF8.GetBytes(releases);
			response.OutputStream.Write(body);
		};
		// GitHub answers an asset URL with a redirect to its storage host: the download must follow it.
		assets.Routes["/owner/my-addin/releases/download/v1.0.0/Sample.odaddin"] = (request, response) =>
		{
			response.StatusCode = 302;
			response.Headers["Location"] = new Uri(assets.BaseUri, "storage/blob-1").ToString();
		};
		assets.Routes["/storage/blob-1"] = (request, response) => response.OutputStream.Write(v1);
		var h = Harness(Options(), Path.Combine(directory, "cache"));

		await h.Service.RefreshAsync(false, default);
		var entry = Assert.Single(h.Service.GetEntries());
		Assert.Equal(ReleaseListStatus.Ok, entry.List.Status);
		Assert.Equal("v1.0.0", entry.Latest!.Tag);
		Assert.False(entry.IsInstalled);

		AddIn installed = await h.Service.InstallAsync(entry.Latest, default);

		Assert.Empty(h.Errors);
		Assert.NotNull(installed);
		Assert.Equal(AddInAction.Install, installed.Action);
		Assert.Equal("owner/my-addin", installed.Properties[OdAddinPackage.RepositoryManifestAttribute]);
		Assert.Equal("dll 1.0.0", File.ReadAllText(Path.Combine(h.Management.Temp, "Vendor.Sample", "Sample.dll")));
		Assert.All(api.Requests.Concat(assets.Requests), r => Assert.Null(r.Authorization));
		Assert.Single(api.Requests); // the install used no API quota

		// The cache is fresh: a second refresh asks nobody. A forced one revalidates with the ETag (304).
		await h.Service.RefreshAsync(false, default);
		Assert.Single(api.Requests);
		await h.Service.RefreshAsync(true, default);
		Assert.Equal(2, api.Requests.Count);
		Assert.Equal("\"r1\"", api.Requests[1].IfNoneMatch);
		Assert.Equal(ReleaseListStatus.Ok, Assert.Single(h.Service.GetEntries()).List.Status);

		// A new release appears upstream: the pending installation is older than it, so it is offered as an update.
		releases = ReleasesJson(assets.BaseUri, ("v1.0.0", v1, false), ("v1.1.0", v11, false));
		api.Routes["/repos/owner/my-addin/releases"] = (request, response) =>
		{
			response.Headers["ETag"] = "\"r2\"";
			response.ContentType = "application/json";
			response.OutputStream.Write(Encoding.UTF8.GetBytes(releases));
		};
		await h.Service.RefreshAsync(true, default);
		var updated = Assert.Single(h.Service.GetEntries());
		Assert.Equal("v1.1.0", updated.Latest!.Tag);
		Assert.True(updated.HasUpdate);
	}

	[Fact]
	public async Task ADownloadWhoseBytesDoNotMatchTheDigestIsRefusedOverRealHttp()
	{
		byte[] real = PackageBytes(directory, "1.0.0", "v1.odaddin");
		byte[] tampered = (byte[])real.Clone();
		tampered[tampered.Length / 2] ^= 0xFF;
		api.Routes["/repos/owner/my-addin/releases"] = (request, response) =>
		{
			response.ContentType = "application/json";
			response.OutputStream.Write(Encoding.UTF8.GetBytes(ReleasesJson(assets.BaseUri, ("v1.0.0", real, false))));
		};
		assets.Routes["/owner/my-addin/releases/download/v1.0.0/Sample.odaddin"] = (request, response) => response.OutputStream.Write(tampered);
		var h = Harness(Options(), Path.Combine(directory, "cache"));
		await h.Service.RefreshAsync(false, default);

		AddIn installed = await h.Service.InstallAsync(h.Service.GetEntries().Single().Latest!, default);

		Assert.Null(installed);
		Assert.Contains("SHA-256", Assert.Single(h.Errors));
		Assert.Empty(h.Management.AddedToTree);
		Assert.False(Directory.Exists(Path.Combine(h.Management.Temp, "Vendor.Sample")));
	}

	[Fact]
	public async Task AReleaseListThatPointsItsAssetAtAnotherHostIsNotOffered()
	{
		byte[] content = PackageBytes(directory, "1.0.0", "v1.odaddin");
		api.Routes["/repos/owner/my-addin/releases"] = (request, response) =>
		{
			response.ContentType = "application/json";
			// The asset URL names the API host, not the configured download host.
			response.OutputStream.Write(Encoding.UTF8.GetBytes(ReleasesJson(api.BaseUri, ("v1.0.0", content, false))));
		};
		var h = Harness(Options(), Path.Combine(directory, "cache"));

		await h.Service.RefreshAsync(false, default);

		var entry = Assert.Single(h.Service.GetEntries());
		Assert.Null(entry.Latest);
		Assert.Contains("not a download of this repository", Assert.Single(entry.List.Skipped));
		Assert.Empty(assets.Requests);
	}

	[Fact]
	public async Task GitHubUnreachableShowsTheStaleListFromTheCache()
	{
		byte[] content = PackageBytes(directory, "1.0.0", "v1.odaddin");
		bool up = true;
		api.Routes["/repos/owner/my-addin/releases"] = (request, response) =>
		{
			if (!up)
			{
				response.StatusCode = 503;
				return;
			}
			response.ContentType = "application/json";
			response.OutputStream.Write(Encoding.UTF8.GetBytes(ReleasesJson(assets.BaseUri, ("v1.0.0", content, false))));
		};
		var h = Harness(Options(), Path.Combine(directory, "cache"));
		await h.Service.RefreshAsync(false, default);
		up = false;

		await h.Service.RefreshAsync(true, default);

		var entry = Assert.Single(h.Service.GetEntries());
		Assert.Equal(ReleaseListStatus.Stale, entry.List.Status);
		Assert.Contains("503", entry.List.Message);
		Assert.Equal("v1.0.0", entry.Latest!.Tag);
	}
}
