using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace ICSharpCode.SharpDevelop.NuGet
{
	public sealed class NuGetPackageUpdateService
	{
		readonly ILogger logger;

		public NuGetPackageUpdateService(ILogger logger = null)
		{
			this.logger = logger ?? NullLogger.Instance;
		}

		public async Task<IReadOnlyList<NuGetPackageUpdateResult>> GetUpdatesAsync(
			IReadOnlyList<PackageSource> sources,
			IReadOnlyList<SdkStylePackageReference> installedPackages,
			bool includePrerelease,
			CancellationToken cancellationToken)
		{
			if (sources is null)
				throw new ArgumentNullException(nameof(sources));
			if (installedPackages is null)
				throw new ArgumentNullException(nameof(installedPackages));

			var updates = new List<NuGetPackageUpdateResult>();
			foreach (var package in installedPackages)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (!NuGetVersion.TryParse(package.Version, out var currentVersion))
					continue;

				var update = await GetUpdateAsync(sources, package.Id, currentVersion, includePrerelease, cancellationToken).ConfigureAwait(false);
				if (update is not null)
					updates.Add(update);
			}

			return updates
				.OrderBy(update => update.Id, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}

		async Task<NuGetPackageUpdateResult> GetUpdateAsync(
			IReadOnlyList<PackageSource> sources,
			string packageId,
			NuGetVersion currentVersion,
			bool includePrerelease,
			CancellationToken cancellationToken)
		{
			NuGetVersion latestVersion = null;
			string latestSourceName = string.Empty;

			foreach (var source in sources)
			{
				cancellationToken.ThrowIfCancellationRequested();

				try
				{
					var repository = Repository.Factory.GetCoreV3(source);
					var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
					if (metadataResource is null)
						continue;

					using (var cacheContext = new SourceCacheContext()) {
						var metadata = await metadataResource
							.GetMetadataAsync(packageId, includePrerelease, includeUnlisted: false, cacheContext, logger, cancellationToken)
							.ConfigureAwait(false);
						var sourceLatest = metadata
							.Select(item => item.Identity.Version)
							.Where(version => includePrerelease || !version.IsPrerelease)
							.OrderByDescending(version => version)
							.FirstOrDefault();

						if (sourceLatest is not null && (latestVersion is null || sourceLatest > latestVersion))
						{
							latestVersion = sourceLatest;
							latestSourceName = source.Name;
						}
					}
				}
				catch (Exception ex)
				{
					LoggingService.Warn($"NuGet update check for '{packageId}' against '{source.Name}' ({source.SourceUri}) failed: {ex}");
				}
			}

			if (latestVersion is null || latestVersion <= currentVersion)
				return null;

			return new NuGetPackageUpdateResult(
				packageId,
				currentVersion.ToNormalizedString(),
				latestVersion.ToNormalizedString(),
				latestSourceName);
		}
	}
}
