using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.AddInManager2.Model
{
	/// <summary>Where add-in releases come from and how they are fetched.</summary>
	public interface IAddInReleaseSource
	{
		/// <summary>
		/// The installable releases of a trusted repository. Never throws for an unreachable or rate-limited
		/// GitHub: the outcome is in <see cref="ReleaseListResult.Status"/>. Only cancellation throws.
		/// </summary>
		Task<ReleaseListResult> GetReleasesAsync(RepositoryReference repository, bool forceRefresh, CancellationToken cancellationToken);

		/// <summary>
		/// Downloads the release's <c>.odaddin</c> to <paramref name="destinationFile"/> and verifies its size and
		/// SHA-256 against what GitHub reported; on any mismatch nothing is left at the destination and
		/// <see cref="InvalidDataException"/> is thrown.
		/// </summary>
		Task DownloadAsync(AddInRelease release, string destinationFile, CancellationToken cancellationToken);
	}

	public sealed class GitHubReleaseSourceOptions
	{
		/// <summary>The REST API root. Overridden only by tests, against a local fake.</summary>
		public Uri ApiBase { get; set; } = new Uri("https://api.github.com/");

		/// <summary>The root every asset URL must live under. Overridden only by tests.</summary>
		public Uri DownloadBase { get; set; } = new Uri("https://github.com/");

		/// <summary>A cached release list younger than this is used without contacting GitHub.</summary>
		public TimeSpan MaxCacheAge { get; set; } = TimeSpan.FromHours(24);

		/// <summary>Largest asset the installer will download.</summary>
		public long MaxAssetBytes { get; set; } = 1L << 30;
	}

	/// <summary>
	/// Lists and downloads add-in releases from public GitHub repositories, without authentication.
	/// Unauthenticated access is 60 API requests per hour per IP and a 304 still counts, so the on-disk
	/// <see cref="ReleaseCache"/> is what protects the quota; assets are downloaded from their
	/// <c>browser_download_url</c>, which is not an API call.
	/// </summary>
	public sealed class GitHubReleaseSource : IAddInReleaseSource
	{
		const string UserAgentProduct = "OpenDevelop-AddInManager";
		const int ReleasesPerRequest = 30;

		readonly HttpClient http;
		readonly ReleaseCache cache;
		readonly GitHubReleaseSourceOptions options;
		readonly Func<DateTimeOffset> clock;

		public GitHubReleaseSource(HttpClient http, ReleaseCache cache, GitHubReleaseSourceOptions options = null, Func<DateTimeOffset> clock = null)
		{
			this.http = http ?? throw new ArgumentNullException(nameof(http));
			this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
			this.options = options ?? new GitHubReleaseSourceOptions();
			this.clock = clock ?? (() => DateTimeOffset.UtcNow);
		}

		public async Task<ReleaseListResult> GetReleasesAsync(RepositoryReference repository, bool forceRefresh, CancellationToken cancellationToken)
		{
			if (repository == null)
				throw new ArgumentNullException(nameof(repository));
			DateTimeOffset now = clock();
			CachedReleaseList cached = cache.TryRead(repository);
			ReleaseListResult fromCache = null;
			if (cached != null) {
				fromCache = TryParse(repository, cached, ReleaseListStatus.Cached, null, null);
				if (fromCache == null)
					cached = null; // unreadable cache: behave as if there was none
			}
			if (!forceRefresh && cached != null && now - cached.FetchedAt >= TimeSpan.Zero && now - cached.FetchedAt < options.MaxCacheAge)
				return fromCache;

			HttpResponseMessage response;
			try {
				using (var request = CreateRequest(HttpMethod.Get, new Uri(options.ApiBase,
					"repos/" + repository.Owner + "/" + repository.Name + "/releases?per_page=" + ReleasesPerRequest.ToString(CultureInfo.InvariantCulture)))) {
					request.Headers.Accept.ParseAdd("application/vnd.github+json");
					request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
					if (cached != null && !string.IsNullOrEmpty(cached.ETag))
						request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
					response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
				}
			} catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
				throw;
			} catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException || ex is IOException) {
				return Unavailable(repository, cached, "GitHub could not be reached (" + ex.Message + ").", null);
			}

			using (response) {
				switch (response.StatusCode) {
					case HttpStatusCode.OK:
						return await HandleOkAsync(repository, response, cached, now, cancellationToken).ConfigureAwait(false);
					case HttpStatusCode.NotModified when cached != null:
						cache.Write(repository, new CachedReleaseList { ETag = cached.ETag, FetchedAt = now, Body = cached.Body });
						return TryParse(repository, new CachedReleaseList { ETag = cached.ETag, FetchedAt = now, Body = cached.Body }, ReleaseListStatus.Ok, null, null)
							?? fromCache;
					case HttpStatusCode.NotFound:
						return new ReleaseListResult(repository, ReleaseListStatus.Failed, null, null,
							"GitHub has no public repository " + repository.FullName + ", or it has no releases.", null, null);
				}
				if (IsRateLimited(response, now, out DateTimeOffset? resetsAt)) {
					string when = resetsAt.HasValue ? " It resets at " + resetsAt.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) + "." : string.Empty;
					string message = "GitHub's limit of 60 requests per hour for anonymous access is used up." + when;
					return cached != null
						? Unavailable(repository, cached, message, resetsAt)
						: new ReleaseListResult(repository, ReleaseListStatus.RateLimited, null, null, message, null, resetsAt);
				}
				return Unavailable(repository, cached,
					"GitHub answered " + (int)response.StatusCode + " " + response.ReasonPhrase + ".", null);
			}
		}

		async Task<ReleaseListResult> HandleOkAsync(
			RepositoryReference repository, HttpResponseMessage response, CachedReleaseList cached, DateTimeOffset now, CancellationToken cancellationToken)
		{
			string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			var fresh = new CachedReleaseList { ETag = response.Headers.ETag?.ToString(), FetchedAt = now, Body = body };
			ReleaseListResult result = TryParse(repository, fresh, ReleaseListStatus.Ok, null, null);
			if (result == null)
				return Unavailable(repository, cached, "GitHub's answer for " + repository.FullName + " was not a release list.", null);
			cache.Write(repository, fresh);
			return result;
		}

		ReleaseListResult Unavailable(RepositoryReference repository, CachedReleaseList cached, string message, DateTimeOffset? resetsAt)
		{
			if (cached != null) {
				ReleaseListResult stale = TryParse(repository, cached, ReleaseListStatus.Stale,
					message + " Showing the list fetched " + cached.FetchedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + ".", resetsAt);
				if (stale != null)
					return stale;
			}
			return new ReleaseListResult(repository, ReleaseListStatus.Failed, null, null, message, null, resetsAt);
		}

		ReleaseListResult TryParse(RepositoryReference repository, CachedReleaseList list, ReleaseListStatus status, string message, DateTimeOffset? resetsAt)
		{
			try {
				var (releases, skipped) = GitHubReleaseParser.Parse(list.Body, repository, options.DownloadBase);
				return new ReleaseListResult(repository, status, releases, skipped, message, list.FetchedAt, resetsAt);
			} catch (JsonException) {
				return null;
			}
		}

		static bool IsRateLimited(HttpResponseMessage response, DateTimeOffset now, out DateTimeOffset? resetsAt)
		{
			resetsAt = null;
			if (response.StatusCode != HttpStatusCode.Forbidden && (int)response.StatusCode != 429)
				return false;
			bool exhausted = false;
			if (response.Headers.TryGetValues("x-ratelimit-remaining", out IEnumerable<string> remaining)
				&& remaining.FirstOrDefault() == "0") {
				exhausted = true;
				if (response.Headers.TryGetValues("x-ratelimit-reset", out IEnumerable<string> reset)
					&& long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
					&& seconds > 0 && seconds < 253402300799L)
					resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
			}
			if (response.Headers.RetryAfter != null) {
				exhausted = true;
				if (resetsAt == null) {
					if (response.Headers.RetryAfter.Delta.HasValue)
						resetsAt = now + response.Headers.RetryAfter.Delta.Value;
					else if (response.Headers.RetryAfter.Date.HasValue)
						resetsAt = response.Headers.RetryAfter.Date.Value;
				}
			}
			return exhausted;
		}

		public async Task DownloadAsync(AddInRelease release, string destinationFile, CancellationToken cancellationToken)
		{
			if (release == null)
				throw new ArgumentNullException(nameof(release));
			if (string.IsNullOrEmpty(destinationFile))
				throw new ArgumentException("A destination file is required.", nameof(destinationFile));
			if (!GitHubReleaseParser.IsAssetUrlAllowed(release.AssetUrl, release.Repository, options.DownloadBase))
				throw new InvalidDataException("Refusing to download " + release.AssetUrl + ": it is not a release asset of " + release.Repository + ".");
			if (release.AssetSize <= 0 || release.AssetSize > options.MaxAssetBytes)
				throw new InvalidDataException("The asset size " + release.AssetSize + " is outside the allowed range.");

			string partial = destinationFile + ".part";
			try {
				using (var request = CreateRequest(HttpMethod.Get, release.AssetUrl))
				using (HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false)) {
					response.EnsureSuccessStatusCode();
					using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
					using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
					using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)) {
						var buffer = new byte[81920];
						long total = 0;
						int read;
						while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0) {
							total += read;
							if (total > release.AssetSize)
								throw new InvalidDataException("The download is larger than the " + release.AssetSize + " bytes GitHub reported.");
							hash.AppendData(buffer, 0, read);
							await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
						}
						if (total != release.AssetSize)
							throw new InvalidDataException("The download is " + total + " bytes, but GitHub reported " + release.AssetSize + ".");
						string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
						if (!string.Equals(actual, release.Sha256, StringComparison.Ordinal))
							throw new InvalidDataException("The download's SHA-256 (" + actual + ") does not match the digest GitHub reported (" + release.Sha256 + ").");
					}
				}
				File.Move(partial, destinationFile, true);
			} catch {
				TryDelete(partial);
				throw;
			}
		}

		static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
		{
			var request = new HttpRequestMessage(method, uri);
			request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgentProduct, "1.0"));
			return request;
		}

		static void TryDelete(string file)
		{
			try {
				File.Delete(file);
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
		}
	}
}
