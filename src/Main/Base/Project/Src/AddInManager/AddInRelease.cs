using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.AddInManager2.Model
{
	/// <summary>
	/// One installable release of an add-in: a GitHub Release whose tag is <c>vMAJOR.MINOR.PATCH[-suffix]</c>
	/// and which carries exactly one <c>*.odaddin</c> asset (doc/technotes/addin-github-releases.md).
	/// </summary>
	public sealed class AddInRelease
	{
		public const string PackageExtension = ".odaddin";

		public AddInRelease(
			RepositoryReference repository, string tag, Version version, bool isPrerelease,
			string assetName, Uri assetUrl, long assetSize, string sha256,
			string notes, DateTimeOffset? publishedAt, Uri pageUrl)
		{
			Repository = repository ?? throw new ArgumentNullException(nameof(repository));
			Tag = tag ?? throw new ArgumentNullException(nameof(tag));
			Version = version ?? throw new ArgumentNullException(nameof(version));
			IsPrerelease = isPrerelease;
			AssetName = assetName ?? throw new ArgumentNullException(nameof(assetName));
			AssetUrl = assetUrl ?? throw new ArgumentNullException(nameof(assetUrl));
			AssetSize = assetSize;
			Sha256 = sha256 ?? throw new ArgumentNullException(nameof(sha256));
			Notes = notes ?? string.Empty;
			PublishedAt = publishedAt;
			PageUrl = pageUrl;
		}

		public RepositoryReference Repository { get; }

		/// <summary>The release tag, for example <c>v1.2.3</c>.</summary>
		public string Tag { get; }

		/// <summary>The numeric part of the tag: <c>1.2.3</c>.</summary>
		public Version Version { get; }

		/// <summary>True for a GitHub pre-release, or when the tag carries a suffix such as <c>-rc1</c>.</summary>
		public bool IsPrerelease { get; }

		/// <summary>File name of the <c>.odaddin</c> asset.</summary>
		public string AssetName { get; }

		public Uri AssetUrl { get; }

		public long AssetSize { get; }

		/// <summary>Lower-case hex SHA-256 of the asset, from the digest GitHub reports for it.</summary>
		public string Sha256 { get; }

		/// <summary>The release notes (Markdown), possibly empty.</summary>
		public string Notes { get; }

		public DateTimeOffset? PublishedAt { get; }

		/// <summary>The release's page on GitHub, or null.</summary>
		public Uri PageUrl { get; }

		/// <summary>The asset name without the extension: what the manager shows as the add-in's name.</summary>
		public string DisplayName
		{
			get { return AssetName.Substring(0, AssetName.Length - PackageExtension.Length); }
		}

		public override string ToString()
		{
			return Repository + " " + Tag;
		}
	}

	public enum ReleaseListStatus
	{
		/// <summary>Fetched from GitHub, or revalidated there (304), just now.</summary>
		Ok,
		/// <summary>Served from the on-disk cache without contacting GitHub: the cache is younger than the maximum age.</summary>
		Cached,
		/// <summary>GitHub could not be used right now; an older cached list is shown. <see cref="ReleaseListResult.Message"/> says why.</summary>
		Stale,
		/// <summary>The unauthenticated rate limit is exhausted and there is no cached list.</summary>
		RateLimited,
		/// <summary>The list could not be read and there is no cached list.</summary>
		Failed,
	}

	/// <summary>The releases of one trusted repository, plus how they were obtained.</summary>
	public sealed class ReleaseListResult
	{
		public ReleaseListResult(
			RepositoryReference repository, ReleaseListStatus status, IReadOnlyList<AddInRelease> releases,
			IReadOnlyList<string> skipped, string message, DateTimeOffset? fetchedAt, DateTimeOffset? rateLimitResetsAt)
		{
			Repository = repository ?? throw new ArgumentNullException(nameof(repository));
			Status = status;
			Releases = releases ?? Array.Empty<AddInRelease>();
			Skipped = skipped ?? Array.Empty<string>();
			Message = message ?? string.Empty;
			FetchedAt = fetchedAt;
			RateLimitResetsAt = rateLimitResetsAt;
		}

		public RepositoryReference Repository { get; }
		public ReleaseListStatus Status { get; }

		/// <summary>The installable releases, newest first.</summary>
		public IReadOnlyList<AddInRelease> Releases { get; }

		/// <summary>Why releases that exist were left out (no/duplicate asset, bad tag, no digest, ...).</summary>
		public IReadOnlyList<string> Skipped { get; }

		/// <summary>A user-presentable explanation for <see cref="ReleaseListStatus.Stale"/>, <see cref="ReleaseListStatus.RateLimited"/> and <see cref="ReleaseListStatus.Failed"/>.</summary>
		public string Message { get; }

		/// <summary>When the releases shown were fetched from GitHub.</summary>
		public DateTimeOffset? FetchedAt { get; }

		/// <summary>When the rate limit resets, if that is why GitHub was not used.</summary>
		public DateTimeOffset? RateLimitResetsAt { get; }

		public bool HasReleases
		{
			get { return Releases.Count > 0; }
		}
	}

	/// <summary>Choosing and comparing releases.</summary>
	public static class ReleaseVersioning
	{
		/// <summary>The newest release, ignoring pre-releases unless <paramref name="includePrereleases"/>; null when none.</summary>
		public static AddInRelease Latest(IEnumerable<AddInRelease> releases, bool includePrereleases)
		{
			return (releases ?? Enumerable.Empty<AddInRelease>())
				.Where(r => includePrereleases || !r.IsPrerelease)
				.OrderByDescending(r => Normalize(r.Version))
				.ThenBy(r => r.IsPrerelease) // a stable release outranks a pre-release of the same number
				.FirstOrDefault();
		}

		/// <summary>Whether <paramref name="release"/> is newer than the installed version.</summary>
		public static bool IsNewer(Version installed, AddInRelease release)
		{
			return release != null && Compare(release.Version, installed) > 0;
		}

		/// <summary>Compares two versions treating unspecified parts as zero (unlike <see cref="Version.CompareTo(Version)"/>).</summary>
		public static int Compare(Version a, Version b)
		{
			return Normalize(a).CompareTo(Normalize(b));
		}

		public static Version Normalize(Version version)
		{
			if (version == null)
				return new Version(0, 0, 0, 0);
			return new Version(
				Math.Max(version.Major, 0), Math.Max(version.Minor, 0),
				Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
		}
	}
}
