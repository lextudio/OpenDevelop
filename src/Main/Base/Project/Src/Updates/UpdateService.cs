// OpenDevelop's update checker, modeled on ILSpy's UpdateService
// (externals/ilspy/ILSpy/Updates/UpdateService.cs). ILSpy polls a static updates.xml on
// github.io; OpenDevelop has no such hosted feed, so this implementation queries the GitHub
// Releases API of the project's own repository (lextudio/OpenDevelop) for the latest release
// instead. The version shape and the CheckForUpdatesIfEnabledAsync / CheckForUpdatesAsync /
// GetLatestVersionAsync API surface mirror ILSpy's so the two stay drop-in comparable.
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.ILSpy.Updates;

namespace ICSharpCode.SharpDevelop.Updates
{
	internal static class UpdateService
	{
		// GitHub's latest-release endpoint for OpenDevelop's own repository.
		static readonly Uri DefaultUpdateUrl = new Uri("https://api.github.com/repos/lextudio/OpenDevelop/releases/latest");

#if DEBUG
		// Tests may point the checker at a local/fixture endpoint; production builds always
		// resolve against the real repository above.
		public static Uri UpdateUrl = DefaultUpdateUrl;
#else
		static readonly Uri UpdateUrl = DefaultUpdateUrl;
#endif

		public static AvailableVersionInfo LatestAvailableVersion { get; private set; }

		public static async Task<AvailableVersionInfo> GetLatestVersionAsync(CancellationToken cancellationToken = default)
		{
			using var client = new HttpClient();
			client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenDevelop-Updater");
			client.Timeout = TimeSpan.FromSeconds(15);

			string data;
			try {
				data = await client.GetStringAsync(UpdateUrl, cancellationToken).ConfigureAwait(false);
			} catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) {
				// GitHub returns 404 from /releases/latest while the repository has no published
				// release yet - that is "nothing to update to", not a check failure.
				LatestAvailableVersion = null;
				return null;
			}

			using var doc = JsonDocument.Parse(data);
			JsonElement root = doc.RootElement;

			string tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
			string downloadUrl = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
			if (string.IsNullOrEmpty(tag))
				throw new InvalidOperationException("GitHub latest-release response contained no tag_name.");

			var version = ParseVersion(tag);
			LatestAvailableVersion = new AvailableVersionInfo { Version = version, DownloadUrl = downloadUrl };
			return LatestAvailableVersion;
		}

		/// <summary>
		/// Parses a GitHub release tag into a comparable <see cref="Version"/>. Handles a leading
		/// "v", a plain "1.2.3", and prerelease suffixes such as "1.2.3-beta2" (the suffix is
		/// dropped; OpenDevelop only compares released versions for the stable update channel).
		/// </summary>
		internal static Version ParseVersion(string tag)
		{
			string value = tag.Trim();
			if (value.Length > 0 && (value[0] == 'v' || value[0] == 'V'))
				value = value.Substring(1);

			int dash = value.IndexOf('-');
			if (dash >= 0)
				value = value.Substring(0, dash);

			// GitHub may tag with a short form like "5.2" - normalize to four components so
			// Version comparison against RevisionClass's Major.Minor.Build.Revision stays valid.
			string[] parts = value.Split('.');
			string[] normalized = new string[4];
			for (int i = 0; i < normalized.Length; i++)
				normalized[i] = i < parts.Length && int.TryParse(parts[i], out _) ? parts[i] : "0";

			return new Version(string.Join(".", normalized));
		}

		/// <summary>
		/// If automatic update checking is enabled, checks for updates. Returns the download URL if
		/// an update is available, null otherwise (including "no check was performed"). Mirrors
		/// ILSpy's weekly check cadence via <see cref="UpdateSettings.LastSuccessfulUpdateCheck"/>.
		/// </summary>
		public static async Task<string> CheckForUpdatesIfEnabledAsync(UpdateSettings settings, CancellationToken cancellationToken = default)
		{
			if (!settings.AutomaticUpdateCheckEnabled)
				return null;

			// perform update check if we never did one before; or if the last check wasn't in the
			// past 7 days (same policy as ILSpy's UpdateService)
			if (settings.LastSuccessfulUpdateCheck == null
				|| settings.LastSuccessfulUpdateCheck < DateTime.UtcNow.AddDays(-7)
				|| settings.LastSuccessfulUpdateCheck > DateTime.UtcNow)
			{
				return await CheckForUpdateInternalAsync(settings, cancellationToken).ConfigureAwait(false);
			}

			return null;
		}

		public static Task<string> CheckForUpdatesAsync(UpdateSettings settings, CancellationToken cancellationToken = default)
		{
			return CheckForUpdateInternalAsync(settings, cancellationToken);
		}

		static async Task<string> CheckForUpdateInternalAsync(UpdateSettings settings, CancellationToken cancellationToken)
		{
			try {
				var v = await GetLatestVersionAsync(cancellationToken).ConfigureAwait(false);
				settings.LastSuccessfulUpdateCheck = DateTime.UtcNow;
				return v.Version > AppUpdateService.CurrentVersion ? v.DownloadUrl : null;
			} catch (Exception ex) {
				// ignore errors getting the version info (offline, GitHub rate limit, malformed tag...)
				LoggingService.Debug("Update check failed: " + ex.Message);
				return null;
			}
		}
	}
}
