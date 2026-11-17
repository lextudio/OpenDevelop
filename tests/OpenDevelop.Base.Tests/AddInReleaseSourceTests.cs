using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICSharpCode.AddInManager2.Model;
using Xunit;

namespace OpenDevelop.Base.Tests;

public class AddInReleaseSourceTests : IDisposable
{
	static readonly Uri DownloadBase = new("https://github.com/");
	readonly string directory = Path.Combine(Path.GetTempPath(), "od-release-source-" + Guid.NewGuid().ToString("N"));
	DateTimeOffset now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

	public void Dispose()
	{
		if (Directory.Exists(directory))
			Directory.Delete(directory, true);
	}

	// ---- helpers ------------------------------------------------------------------------------------------

	static RepositoryReference Repo(string text = "owner/my-addin") => RepositoryReference.Parse(text);

	static string Sha256Of(byte[] data) => "sha256:" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

	static object Asset(string name, string digest, long size = 4, string url = null, string state = "uploaded", string repo = "owner/my-addin", string tag = "v1.0.0") => new Dictionary<string, object?>
	{
		["name"] = name,
		["state"] = state,
		["size"] = size,
		["digest"] = digest,
		["browser_download_url"] = url ?? $"https://github.com/{repo}/releases/download/{tag}/{name}",
	};

	static Dictionary<string, object?> Release(string tag, bool prerelease = false, bool draft = false, string body = "notes", params object[] assets) => new()
	{
		["tag_name"] = tag,
		["prerelease"] = prerelease,
		["draft"] = draft,
		["body"] = body,
		["published_at"] = "2026-11-01T10:00:00Z",
		["html_url"] = $"https://github.com/owner/my-addin/releases/tag/{tag}",
		["assets"] = assets,
	};

	static string Json(params object[] releases) => JsonSerializer.Serialize(releases);

	static string GoodList() => Json(
		Release("v1.0.0", assets: Asset("My Addin.odaddin", Sha256Of(new byte[] { 1, 2, 3, 4 }), tag: "v1.0.0")),
		Release("v1.10.0", assets: Asset("My Addin.odaddin", Sha256Of(new byte[] { 9, 9, 9, 9 }), tag: "v1.10.0")),
		Release("v1.9.0", assets: Asset("My Addin.odaddin", Sha256Of(new byte[] { 5, 5, 5, 5 }), tag: "v1.9.0")),
		Release("v2.0.0-rc1", assets: Asset("My Addin.odaddin", Sha256Of(new byte[] { 7, 7, 7, 7 }), tag: "v2.0.0-rc1")));

	sealed class FakeHandler : HttpMessageHandler
	{
		public List<HttpRequestMessage> Requests { get; } = new();
		public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Requests.Add(request);
			return Task.FromResult(Respond(request));
		}
	}

	GitHubReleaseSource Source(FakeHandler handler) =>
		new(new HttpClient(handler), new ReleaseCache(directory), new GitHubReleaseSourceOptions { DownloadBase = DownloadBase }, () => now);

	static HttpResponseMessage Ok(string body, string etag = "\"abc\"")
	{
		var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
		response.Headers.TryAddWithoutValidation("ETag", etag);
		return response;
	}

	// ---- RepositoryReference ------------------------------------------------------------------------------

	[Theory]
	[InlineData("owner/my-addin", "owner/my-addin")]
	[InlineData("  owner/my-addin  ", "owner/my-addin")]
	[InlineData("https://github.com/owner/my-addin", "owner/my-addin")]
	[InlineData("https://github.com/owner/my-addin/", "owner/my-addin")]
	[InlineData("https://github.com/owner/my-addin.git", "owner/my-addin")]
	[InlineData("Some-Org/Repo.Name_1", "Some-Org/Repo.Name_1")]
	public void RepositoryReference_AcceptsOwnerRepoAndAPastedGitHubUrl(string input, string expected)
	{
		Assert.True(RepositoryReference.TryParse(input, out var repository, out var error), error);
		Assert.Equal(expected, repository.FullName);
	}

	[Theory]
	[InlineData("")]
	[InlineData("owner")]
	[InlineData("a/b/c")]
	[InlineData("-owner/repo")]
	[InlineData("owner/..")]
	[InlineData("owner/.")]
	[InlineData("owner/re po")]
	[InlineData("owner/repo?x=1")]
	[InlineData("http://github.com/owner/repo")]
	[InlineData("https://gitlab.com/owner/repo")]
	[InlineData("https://github.com/owner")]
	[InlineData("../owner/repo")]
	public void RepositoryReference_RejectsEverythingElse(string input)
	{
		Assert.False(RepositoryReference.TryParse(input, out var repository, out var error));
		Assert.Null(repository);
		Assert.False(string.IsNullOrEmpty(error));
	}

	[Fact]
	public void RepositoryReference_IsCaseInsensitiveButKeepsTheSpelling()
	{
		var a = Repo("Owner/My-Addin");
		var b = Repo("owner/my-addin");
		Assert.Equal(a, b);
		Assert.Equal(a.GetHashCode(), b.GetHashCode());
		Assert.Equal("Owner/My-Addin", a.ToString());
	}

	// ---- parser -------------------------------------------------------------------------------------------

	[Fact]
	public void Parser_ReturnsTheNewestFirstAndFlagsPrereleases()
	{
		var (releases, skipped) = GitHubReleaseParser.Parse(GoodList(), Repo(), DownloadBase);

		Assert.Empty(skipped);
		Assert.Equal(new[] { "v2.0.0-rc1", "v1.10.0", "v1.9.0", "v1.0.0" }, releases.Select(r => r.Tag));
		Assert.True(releases[0].IsPrerelease);
		Assert.All(releases.Skip(1), r => Assert.False(r.IsPrerelease));
		Assert.Equal(new Version(1, 10, 0), releases[1].Version);
		Assert.Equal("My Addin", releases[1].DisplayName);
		Assert.Equal(Sha256Of(new byte[] { 9, 9, 9, 9 })[7..], releases[1].Sha256);
		Assert.Equal("notes", releases[1].Notes);
		Assert.NotNull(releases[1].PublishedAt);
	}

	[Fact]
	public void Parser_TreatsATagSuffixAsAPrereleaseEvenWhenGitHubDoesNot()
	{
		string json = Json(Release("v1.0.0-beta.2", prerelease: false, assets: Asset("A.odaddin", Sha256Of(new byte[4]), tag: "v1.0.0-beta.2")));

		var (releases, _) = GitHubReleaseParser.Parse(json, Repo(), DownloadBase);

		Assert.True(Assert.Single(releases).IsPrerelease);
	}

	[Fact]
	public void Parser_IgnoresDraftsSilently()
	{
		string json = Json(Release("v1.0.0", draft: true, assets: Asset("A.odaddin", Sha256Of(new byte[4]))));

		var (releases, skipped) = GitHubReleaseParser.Parse(json, Repo(), DownloadBase);

		Assert.Empty(releases);
		Assert.Empty(skipped);
	}

	public static IEnumerable<object[]> UnusableReleases()
	{
		string good = Sha256Of(new byte[4]);
		yield return new object[] { "no asset", Json(Release("v1.0.0")), "no .odaddin asset" };
		yield return new object[] { "only another asset", Json(Release("v1.0.0", assets: Asset("notes.txt", good))), "no .odaddin asset" };
		yield return new object[] { "two packages", Json(Release("v1.0.0", assets: new[] { Asset("A.odaddin", good), Asset("B.odaddin", good) })), "2 .odaddin assets" };
		yield return new object[] { "tag without v", Json(Release("1.0.0", assets: Asset("A.odaddin", good))), "not of the form" };
		yield return new object[] { "tag not a version", Json(Release("vNext", assets: Asset("A.odaddin", good))), "not of the form" };
		yield return new object[] { "tag with one number", Json(Release("v1", assets: Asset("A.odaddin", good))), "not of the form" };
		yield return new object[] { "no digest", Json(Release("v1.0.0", assets: Asset("A.odaddin", null))), "no SHA-256 digest" };
		yield return new object[] { "wrong digest algorithm", Json(Release("v1.0.0", assets: Asset("A.odaddin", "sha1:" + new string('a', 40)))), "no SHA-256 digest" };
		yield return new object[] { "short digest", Json(Release("v1.0.0", assets: Asset("A.odaddin", "sha256:abcd"))), "no SHA-256 digest" };
		yield return new object[] { "zero size", Json(Release("v1.0.0", assets: Asset("A.odaddin", good, size: 0))), "valid size" };
		yield return new object[] { "asset still uploading", Json(Release("v1.0.0", assets: Asset("A.odaddin", good, state: "starter"))), "not fully uploaded" };
		yield return new object[] { "other repository", Json(Release("v1.0.0", assets: Asset("A.odaddin", good, repo: "evil/other"))), "not a download of this repository" };
		yield return new object[] { "other host", Json(Release("v1.0.0", assets: Asset("A.odaddin", good, url: "https://evil.example/owner/my-addin/releases/download/v1.0.0/A.odaddin"))), "not a download of this repository" };
		yield return new object[] { "plain http", Json(Release("v1.0.0", assets: Asset("A.odaddin", good, url: "http://github.com/owner/my-addin/releases/download/v1.0.0/A.odaddin"))), "not a download of this repository" };
		yield return new object[] { "path in the name", Json(Release("v1.0.0", assets: Asset("../A.odaddin", good, url: "https://github.com/owner/my-addin/releases/download/v1.0.0/A.odaddin"))), "not a plain file name" };
	}

	[Theory]
	[MemberData(nameof(UnusableReleases))]
	public void Parser_SkipsAReleaseItCannotInstallSafely_AndSaysWhy(string label, string json, string reason)
	{
		var (releases, skipped) = GitHubReleaseParser.Parse(json, Repo(), DownloadBase);

		Assert.True(releases.Count == 0, label);
		Assert.Contains(reason, Assert.Single(skipped));
	}

	[Fact]
	public void Parser_KeepsTheGoodReleasesWhenOthersAreSkipped()
	{
		string json = Json(
			Release("v1.0.0", assets: Asset("A.odaddin", Sha256Of(new byte[4]))),
			Release("v1.1.0"));

		var (releases, skipped) = GitHubReleaseParser.Parse(json, Repo(), DownloadBase);

		Assert.Equal("v1.0.0", Assert.Single(releases).Tag);
		Assert.Contains("v1.1.0", Assert.Single(skipped));
	}

	[Fact]
	public void Parser_RejectsABodyThatIsNotAnArray()
	{
		Assert.Throws<JsonException>(() => GitHubReleaseParser.Parse("{\"message\":\"Not Found\"}", Repo(), DownloadBase));
		Assert.ThrowsAny<JsonException>(() => GitHubReleaseParser.Parse("<html>", Repo(), DownloadBase));
	}

	[Theory]
	[InlineData("https://github.com/owner/my-addin/releases/download/v1.0.0/A.odaddin", true)]
	[InlineData("https://GITHUB.com/Owner/My-Addin/releases/download/v1.0.0/A.odaddin", true)]
	[InlineData("https://github.com/owner/my-addin/releases/download/v1.0.0/A.odaddin?token=x", false)]
	[InlineData("https://user:pw@github.com/owner/my-addin/releases/download/v1.0.0/A.odaddin", false)]
	[InlineData("https://github.com/owner/my-addin/releases/download/v1.0.0/A.odaddin#frag", false)]
	[InlineData("https://github.com/owner/my-addin/releases/download/v1.0.0%2f..%2f..%2fA.odaddin", false)]
	[InlineData("https://github.com/owner/my-addin/releases/download/", false)]
	[InlineData("https://github.com/owner/my-addin-evil/releases/download/v1.0.0/A.odaddin", false)]
	[InlineData("https://github.com/owner/my-addin/archive/v1.0.0.zip", false)]
	[InlineData("https://github.com:8443/owner/my-addin/releases/download/v1.0.0/A.odaddin", false)]
	public void IsAssetUrlAllowed_AcceptsOnlyADownloadOfTheSameRepositoryOnTheConfiguredHost(string url, bool allowed)
	{
		Assert.Equal(allowed, GitHubReleaseParser.IsAssetUrlAllowed(new Uri(url), Repo(), DownloadBase));
	}

	// ---- versioning ---------------------------------------------------------------------------------------

	[Fact]
	public void Latest_IgnoresPrereleasesUnlessAsked_AndComparesNumerically()
	{
		var (releases, _) = GitHubReleaseParser.Parse(GoodList(), Repo(), DownloadBase);

		Assert.Equal("v1.10.0", ReleaseVersioning.Latest(releases, includePrereleases: false).Tag);
		Assert.Equal("v2.0.0-rc1", ReleaseVersioning.Latest(releases, includePrereleases: true).Tag);
		Assert.Null(ReleaseVersioning.Latest(Array.Empty<AddInRelease>(), true));
		Assert.Null(ReleaseVersioning.Latest(null, true));
	}

	[Fact]
	public void IsNewer_TreatsUnspecifiedPartsAsZero()
	{
		var (releases, _) = GitHubReleaseParser.Parse(GoodList(), Repo(), DownloadBase);
		var v110 = releases.Single(r => r.Tag == "v1.10.0");

		Assert.True(ReleaseVersioning.IsNewer(new Version(1, 9), v110));
		Assert.True(ReleaseVersioning.IsNewer(new Version(1, 9, 5, 0), v110));
		Assert.False(ReleaseVersioning.IsNewer(new Version(1, 10), v110));
		Assert.False(ReleaseVersioning.IsNewer(new Version(1, 10, 0, 0), v110));
		Assert.False(ReleaseVersioning.IsNewer(new Version(2, 0), v110));
		Assert.False(ReleaseVersioning.IsNewer(new Version(1, 0), null));
	}

	// ---- listing: cache, conditional requests, rate limit -------------------------------------------------

	[Fact]
	public async Task GetReleases_FetchesOnceSendsNoCredentials_AndServesTheNextCallFromTheCache()
	{
		var handler = new FakeHandler { Respond = _ => Ok(GoodList()) };
		var source = Source(handler);

		var first = await source.GetReleasesAsync(Repo(), false, default);
		now += TimeSpan.FromHours(23);
		var second = await source.GetReleasesAsync(Repo(), false, default);

		Assert.Equal(ReleaseListStatus.Ok, first.Status);
		Assert.Equal(4, first.Releases.Count);
		Assert.Equal(ReleaseListStatus.Cached, second.Status);
		Assert.Equal(4, second.Releases.Count);
		var request = Assert.Single(handler.Requests);
		Assert.Equal("https://api.github.com/repos/owner/my-addin/releases?per_page=30", request.RequestUri!.ToString());
		Assert.Null(request.Headers.Authorization);
		Assert.False(request.Headers.Contains("Cookie"));
		Assert.Contains(request.Headers.UserAgent, p => p.Product!.Name == "OpenDevelop-AddInManager");
		Assert.Contains("application/vnd.github+json", request.Headers.Accept.Select(a => a.MediaType));
	}

	[Fact]
	public async Task GetReleases_RevalidatesAnExpiredCacheWithTheETag_AndAnswer304KeepsTheList()
	{
		var handler = new FakeHandler { Respond = _ => Ok(GoodList(), "\"v1\"") };
		var source = Source(handler);
		await source.GetReleasesAsync(Repo(), false, default);

		now += TimeSpan.FromHours(25);
		handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
		var revalidated = await source.GetReleasesAsync(Repo(), false, default);
		now += TimeSpan.FromHours(1);
		var afterwards = await source.GetReleasesAsync(Repo(), false, default);

		Assert.Equal(ReleaseListStatus.Ok, revalidated.Status);
		Assert.Equal(4, revalidated.Releases.Count);
		Assert.Equal("\"v1\"", handler.Requests[1].Headers.IfNoneMatch.Single().ToString());
		// The 304 refreshed the cache's age, so an hour later nothing is requested.
		Assert.Equal(ReleaseListStatus.Cached, afterwards.Status);
		Assert.Equal(2, handler.Requests.Count);
	}

	[Fact]
	public async Task GetReleases_ForceRefreshAsksGitHubEvenWhenTheCacheIsFresh()
	{
		var handler = new FakeHandler { Respond = _ => Ok(GoodList()) };
		var source = Source(handler);
		await source.GetReleasesAsync(Repo(), false, default);

		var forced = await source.GetReleasesAsync(Repo(), true, default);

		Assert.Equal(ReleaseListStatus.Ok, forced.Status);
		Assert.Equal(2, handler.Requests.Count);
	}

	static HttpResponseMessage RateLimited(DateTimeOffset reset)
	{
		var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"API rate limit exceeded\"}") };
		response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
		response.Headers.TryAddWithoutValidation("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString());
		return response;
	}

	[Fact]
	public async Task GetReleases_WhenRateLimitedWithACache_ShowsTheStaleListAndWhenItResets()
	{
		var handler = new FakeHandler { Respond = _ => Ok(GoodList()) };
		var source = Source(handler);
		await source.GetReleasesAsync(Repo(), false, default);
		now += TimeSpan.FromHours(30);
		var reset = now.AddMinutes(25);
		handler.Respond = _ => RateLimited(reset);

		var result = await source.GetReleasesAsync(Repo(), false, default);

		Assert.Equal(ReleaseListStatus.Stale, result.Status);
		Assert.Equal(4, result.Releases.Count);
		Assert.Equal(reset.ToUnixTimeSeconds(), result.RateLimitResetsAt!.Value.ToUnixTimeSeconds());
		Assert.Contains("60 requests per hour", result.Message);
	}

	[Fact]
	public async Task GetReleases_WhenRateLimitedWithoutACache_IsRateLimitedNotAnEmptySuccess()
	{
		var reset = now.AddMinutes(40);
		var handler = new FakeHandler { Respond = _ => RateLimited(reset) };

		var result = await Source(handler).GetReleasesAsync(Repo(), false, default);

		Assert.Equal(ReleaseListStatus.RateLimited, result.Status);
		Assert.Empty(result.Releases);
		Assert.Equal(reset.ToUnixTimeSeconds(), result.RateLimitResetsAt!.Value.ToUnixTimeSeconds());
	}

	[Fact]
	public async Task GetReleases_TreatsA429WithRetryAfterAsRateLimited()
	{
		var handler = new FakeHandler
		{
			Respond = _ =>
			{
				var response = new HttpResponseMessage((HttpStatusCode)429);
				response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
				return response;
			}
		};

		var result = await Source(handler).GetReleasesAsync(Repo(), false, default);

		Assert.Equal(ReleaseListStatus.RateLimited, result.Status);
		Assert.Equal(now.AddSeconds(120), result.RateLimitResetsAt);
	}

	[Fact]
	public async Task GetReleases_AForbiddenThatIsNotTheRateLimitIsAPlainFailure()
	{
		var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) };

		var result = await Source(handler).GetReleasesAsync(Repo(), false, default);

		Assert.Equal(ReleaseListStatus.Failed, result.Status);
		Assert.Null(result.RateLimitResetsAt);
		Assert.Contains("403", result.Message);
	}

	[Fact]
	public async Task GetReleases_A404IsFailedAndNeverServedFromAnOlderCache()
	{
		var handler = new FakeHandler { Respond = _ => Ok(GoodList()) };
		var source = Source(handler);
		await source.GetReleasesAsync(Repo(), false, default);
		now += TimeSpan.FromHours(30);
		handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

		var result = await source.GetReleasesAsync(Repo(), false, default);

		Assert.Equal(ReleaseListStatus.Failed, result.Status);
		Assert.Empty(result.Releases);
		Assert.Contains("owner/my-addin", result.Message);
	}

	[Fact]
	public async Task GetReleases_ANetworkErrorShowsTheStaleListOrFails()
	{
		var handler = new FakeHandler { Respond = _ => Ok(GoodList()) };
		var source = Source(handler);
		var withoutCache = await Source(new FakeHandler { Respond = _ => throw new HttpRequestException("offline") })
			.GetReleasesAsync(Repo("other/repo"), false, default);
		await source.GetReleasesAsync(Repo(), false, default);
		now += TimeSpan.FromHours(30);
		handler.Respond = _ => throw new HttpRequestException("offline");

		var withCache = await source.GetReleasesAsync(Repo(), false, default);

		Assert.Equal(ReleaseListStatus.Failed, withoutCache.Status);
		Assert.Contains("offline", withoutCache.Message);
		Assert.Equal(ReleaseListStatus.Stale, withCache.Status);
		Assert.Equal(4, withCache.Releases.Count);
		Assert.Null(withCache.RateLimitResetsAt);
	}

	[Fact]
	public async Task GetReleases_ABodyThatIsNotAReleaseListFailsAndIsNotCached()
	{
		var handler = new FakeHandler { Respond = _ => Ok("{\"message\":\"surprise\"}") };
		var source = Source(handler);

		var first = await source.GetReleasesAsync(Repo(), false, default);
		var second = await source.GetReleasesAsync(Repo(), false, default);

		Assert.Equal(ReleaseListStatus.Failed, first.Status);
		Assert.Equal(ReleaseListStatus.Failed, second.Status);
		Assert.Equal(2, handler.Requests.Count);
	}

	[Fact]
	public async Task GetReleases_ACorruptCacheFileIsIgnored()
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "owner__my-addin.json"), "{ not json");
		var handler = new FakeHandler { Respond = _ => Ok(GoodList()) };

		var result = await Source(handler).GetReleasesAsync(Repo(), false, default);

		Assert.Equal(ReleaseListStatus.Ok, result.Status);
		Assert.Single(handler.Requests);
		Assert.Empty(handler.Requests[0].Headers.IfNoneMatch);
	}

	[Fact]
	public async Task GetReleases_PropagatesCancellation()
	{
		var source = Source(new FakeHandler { Respond = _ => Ok(GoodList()) });
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetReleasesAsync(Repo(), false, cts.Token));
	}

	[Fact]
	public async Task GetReleases_KeepsEachRepositorysCacheSeparate()
	{
		var handler = new FakeHandler { Respond = _ => Ok(GoodList()) };
		var source = Source(handler);

		await source.GetReleasesAsync(Repo("owner/my-addin"), false, default);
		var other = await source.GetReleasesAsync(Repo("owner/other-addin"), false, default);

		Assert.Equal(ReleaseListStatus.Ok, other.Status);
		Assert.Equal(2, handler.Requests.Count);
	}

	// ---- download -----------------------------------------------------------------------------------------

	static AddInRelease ReleaseFor(byte[] content, string digest = null, long? size = null, string url = null, string repo = "owner/my-addin")
	{
		return new AddInRelease(
			Repo(repo), "v1.0.0", new Version(1, 0, 0), false, "My Addin.odaddin",
			new Uri(url ?? $"https://github.com/{repo}/releases/download/v1.0.0/My%20Addin.odaddin"),
			size ?? content.Length, (digest ?? Sha256Of(content))["sha256:".Length..], "", null, null);
	}

	[Fact]
	public async Task Download_WritesAVerifiedFile()
	{
		byte[] content = { 10, 20, 30, 40, 50 };
		var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) } };
		Directory.CreateDirectory(directory);
		string target = Path.Combine(directory, "My Addin.odaddin");

		await Source(handler).DownloadAsync(ReleaseFor(content), target, default);

		Assert.Equal(content, File.ReadAllBytes(target));
		Assert.False(File.Exists(target + ".part"));
		Assert.Null(handler.Requests.Single().Headers.Authorization);
	}

	[Fact]
	public async Task Download_RefusesADigestMismatch_AndLeavesNothingBehind()
	{
		byte[] content = { 10, 20, 30, 40, 50 };
		var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 10, 20, 30, 40, 51 }) } };
		Directory.CreateDirectory(directory);
		string target = Path.Combine(directory, "My Addin.odaddin");

		var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Source(handler).DownloadAsync(ReleaseFor(content), target, default));

		Assert.Contains("SHA-256", ex.Message);
		Assert.False(File.Exists(target));
		Assert.False(File.Exists(target + ".part"));
	}

	[Fact]
	public async Task Download_DoesNotReplaceAnExistingFileWhenVerificationFails()
	{
		byte[] content = { 1, 2, 3, 4 };
		Directory.CreateDirectory(directory);
		string target = Path.Combine(directory, "My Addin.odaddin");
		File.WriteAllText(target, "previous");
		var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 4, 3, 2, 1 }) } };

		await Assert.ThrowsAsync<InvalidDataException>(() => Source(handler).DownloadAsync(ReleaseFor(content), target, default));

		Assert.Equal("previous", File.ReadAllText(target));
	}

	[Theory]
	[InlineData(3)] // shorter than reported
	[InlineData(9)] // longer than reported
	public async Task Download_RefusesASizeThatDiffersFromTheReportedOne(int actualLength)
	{
		byte[] content = { 1, 2, 3, 4, 5, 6 };
		var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[actualLength]) } };
		Directory.CreateDirectory(directory);
		string target = Path.Combine(directory, "My Addin.odaddin");

		await Assert.ThrowsAsync<InvalidDataException>(() => Source(handler).DownloadAsync(ReleaseFor(content), target, default));

		Assert.False(File.Exists(target));
		Assert.False(File.Exists(target + ".part"));
	}

	[Fact]
	public async Task Download_RefusesAnAssetOutsideTheRepositoryBeforeAnyRequest()
	{
		byte[] content = { 1, 2, 3, 4 };
		var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) } };
		Directory.CreateDirectory(directory);
		string target = Path.Combine(directory, "My Addin.odaddin");
		var elsewhere = ReleaseFor(content, url: "https://evil.example/owner/my-addin/releases/download/v1.0.0/My%20Addin.odaddin");

		await Assert.ThrowsAsync<InvalidDataException>(() => Source(handler).DownloadAsync(elsewhere, target, default));

		Assert.Empty(handler.Requests);
		Assert.False(File.Exists(target));
	}

	[Fact]
	public async Task Download_RefusesAnAbsurdReportedSize()
	{
		byte[] content = { 1, 2, 3, 4 };
		var handler = new FakeHandler();
		var huge = ReleaseFor(content, size: (1L << 30) + 1);

		await Assert.ThrowsAsync<InvalidDataException>(() => Source(handler).DownloadAsync(huge, Path.Combine(directory, "x.odaddin"), default));

		Assert.Empty(handler.Requests);
	}

	[Fact]
	public async Task Download_AFailedRequestLeavesNothingBehind()
	{
		byte[] content = { 1, 2, 3, 4 };
		var handler = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
		Directory.CreateDirectory(directory);
		string target = Path.Combine(directory, "My Addin.odaddin");

		await Assert.ThrowsAsync<HttpRequestException>(() => Source(handler).DownloadAsync(ReleaseFor(content), target, default));

		Assert.Empty(Directory.GetFiles(directory));
	}
}
