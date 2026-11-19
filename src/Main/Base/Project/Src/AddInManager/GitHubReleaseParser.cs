using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ICSharpCode.Core;
using NuGet.Versioning;
using System.Text.RegularExpressions;

namespace ICSharpCode.AddInManager2.Model
{
	/// <summary>
	/// Turns the body of <c>GET /repos/{owner}/{repo}/releases</c> into <see cref="AddInRelease"/>s.
	/// A release that cannot be installed safely is left out and reported in the skipped list, never guessed at.
	/// </summary>
	public static class GitHubReleaseParser
	{
		static readonly Regex AssetNamePattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._ -]*\.odaddin$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
		static readonly Regex DigestPattern = new Regex(@"^sha256:(?<hex>[0-9A-Fa-f]{64})$", RegexOptions.CultureInvariant);

		/// <param name="json">The response body: a JSON array of releases.</param>
		/// <param name="repository">The repository that was queried.</param>
		/// <param name="downloadBase">
		/// Where release assets must live (<c>https://github.com/</c>): an asset URL is accepted only under
		/// <c>{downloadBase}{owner}/{repo}/releases/download/</c> of the same repository.
		/// </param>
		public static (IReadOnlyList<AddInRelease> Releases, IReadOnlyList<string> Skipped) Parse(
			string json, RepositoryReference repository, Uri downloadBase)
		{
			if (repository == null)
				throw new ArgumentNullException(nameof(repository));
			if (downloadBase == null)
				throw new ArgumentNullException(nameof(downloadBase));
			var releases = new List<AddInRelease>();
			var skipped = new List<string>();
			using (JsonDocument document = JsonDocument.Parse(json)) {
				if (document.RootElement.ValueKind != JsonValueKind.Array)
					throw new JsonException("The release list is not a JSON array.");
				foreach (JsonElement element in document.RootElement.EnumerateArray()) {
					string label = GetString(element, "tag_name") ?? "(release without a tag)";
					if (GetBool(element, "draft"))
						continue;
					string reason = TryParseRelease(element, repository, downloadBase, out AddInRelease release);
					if (release != null)
						releases.Add(release);
					else
						skipped.Add(label + ": " + reason);
				}
			}
			releases.Sort((a, b) => {
				int byVersion = VersionComparer.Default.Compare(b.SemanticVersion, a.SemanticVersion);
				return byVersion != 0 ? byVersion : a.IsPrerelease.CompareTo(b.IsPrerelease);
			});
			return (releases, skipped);
		}

		static string TryParseRelease(JsonElement element, RepositoryReference repository, Uri downloadBase, out AddInRelease release)
		{
			release = null;
			string tag = GetString(element, "tag_name");
			if (tag == null)
				return "no tag name";
			// The tag is a v followed by a Semantic Version 2.0.0 (MAJOR.MINOR.PATCH[-prerelease][+build]), strictly.
			if (tag.Length < 2 || tag[0] != 'v' || !SemanticVersion.TryParse(tag.Substring(1), out SemanticVersion version))
				return "the tag is not a v followed by a Semantic Version (vMAJOR.MINOR.PATCH[-prerelease][+build])";
			bool isPrerelease = GetBool(element, "prerelease") || version.IsPrerelease;

			var packages = new List<JsonElement>();
			if (element.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array) {
				foreach (JsonElement asset in assets.EnumerateArray()) {
					string name = GetString(asset, "name");
					if (name != null && name.EndsWith(AddInRelease.PackageExtension, StringComparison.OrdinalIgnoreCase))
						packages.Add(asset);
				}
			}
			if (packages.Count == 0)
				return "no " + AddInRelease.PackageExtension + " asset";
			if (packages.Count > 1)
				return packages.Count + " " + AddInRelease.PackageExtension + " assets; exactly one is required";

			JsonElement package = packages[0];
			string assetName = GetString(package, "name");
			if (!AssetNamePattern.IsMatch(assetName))
				return "the asset name '" + assetName + "' is not a plain file name";
			string state = GetString(package, "state");
			if (state != null && !string.Equals(state, "uploaded", StringComparison.OrdinalIgnoreCase))
				return "the asset is not fully uploaded (state '" + state + "')";

			string digest = GetString(package, "digest");
			Match digestMatch = digest == null ? Match.Empty : DigestPattern.Match(digest);
			if (!digestMatch.Success)
				return "GitHub reports no SHA-256 digest for the asset, so it cannot be verified";

			if (!package.TryGetProperty("size", out JsonElement sizeElement) || !sizeElement.TryGetInt64(out long size) || size <= 0)
				return "the asset has no valid size";

			string urlText = GetString(package, "browser_download_url");
			if (!Uri.TryCreate(urlText, UriKind.Absolute, out Uri assetUrl))
				return "the asset has no download URL";
			if (!IsAssetUrlAllowed(assetUrl, repository, downloadBase))
				return "the asset URL is not a download of this repository";

			DateTimeOffset? published = null;
			string publishedText = GetString(element, "published_at");
			if (publishedText != null && DateTimeOffset.TryParse(publishedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
				published = parsed;
			Uri pageUrl = null;
			string pageText = GetString(element, "html_url");
			if (pageText != null && Uri.TryCreate(pageText, UriKind.Absolute, out Uri page)
				&& (page.Scheme == Uri.UriSchemeHttps || page.Scheme == Uri.UriSchemeHttp))
				pageUrl = page;

			release = new AddInRelease(
				repository, tag, version, isPrerelease, assetName, assetUrl, size,
				digestMatch.Groups["hex"].Value.ToLowerInvariant(), GetString(element, "body"), published, pageUrl);
			return null;
		}

		/// <summary>
		/// An asset must come from <c>{downloadBase}{owner}/{repo}/releases/download/...</c> of the repository
		/// the user trusted, so a crafted response cannot send the installer to another host or repository.
		/// </summary>
		public static bool IsAssetUrlAllowed(Uri assetUrl, RepositoryReference repository, Uri downloadBase)
		{
			if (assetUrl == null || repository == null || downloadBase == null)
				return false;
			if (!string.IsNullOrEmpty(assetUrl.UserInfo) || !string.IsNullOrEmpty(assetUrl.Query) || !string.IsNullOrEmpty(assetUrl.Fragment))
				return false;
			if (!string.Equals(assetUrl.Scheme, downloadBase.Scheme, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(assetUrl.Host, downloadBase.Host, StringComparison.OrdinalIgnoreCase)
				|| assetUrl.Port != downloadBase.Port)
				return false;
			string basePath = downloadBase.AbsolutePath;
			if (!basePath.EndsWith("/", StringComparison.Ordinal))
				basePath += "/";
			string required = basePath + repository.Owner + "/" + repository.Name + "/releases/download/";
			string path = assetUrl.AbsolutePath;
			// A dot segment would have been resolved away by Uri, but a percent-encoded separator would not.
			return path.StartsWith(required, StringComparison.OrdinalIgnoreCase)
				&& path.Length > required.Length
				&& path.IndexOf("%2f", StringComparison.OrdinalIgnoreCase) < 0
				&& path.IndexOf("%5c", StringComparison.OrdinalIgnoreCase) < 0;
		}

		static string GetString(JsonElement element, string name)
		{
			if (element.ValueKind == JsonValueKind.Object
				&& element.TryGetProperty(name, out JsonElement value)
				&& value.ValueKind == JsonValueKind.String)
				return value.GetString();
			return null;
		}

		static bool GetBool(JsonElement element, string name)
		{
			return element.ValueKind == JsonValueKind.Object
				&& element.TryGetProperty(name, out JsonElement value)
				&& value.ValueKind == JsonValueKind.True;
		}
	}
}
