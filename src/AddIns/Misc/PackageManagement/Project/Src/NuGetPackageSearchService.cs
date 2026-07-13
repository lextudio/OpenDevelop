// Real, modern NuGet.Client-backed package search - replaces the legacy NuGet.Core
// IPackageRepository.Search(...) call in AvailablePackagesViewModel.GetAllPackages, which cannot
// work at all against a real HTTP source on this runtime (see doc/technotes/nuget.md: its OData V2
// client needs System.Data.Services.Client, unavailable on modern .NET). Works uniformly for local
// folder feeds and http(s) v2/v3 feeds via Repository.Factory.GetCoreV3, same approach as
// UnoDevelop's NuGetPackageSearchService.cs (the precedent this was modeled on).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;

using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace ICSharpCode.PackageManagement
{
	public static class NuGetPackageSearchService
	{
		public static async Task<IReadOnlyList<NuGetSearchResultPackage>> SearchAsync(
			string sourceUrl,
			string searchTerm,
			bool includePrerelease,
			int take,
			CancellationToken cancellationToken)
		{
			var results = new List<NuGetSearchResultPackage>();
			try {
				var repository = Repository.Factory.GetCoreV3(sourceUrl);
				var searchResource = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);
				if (searchResource == null) {
					return results;
				}

				var filter = new SearchFilter(includePrerelease);
				var metadata = await searchResource
					.SearchAsync(searchTerm ?? string.Empty, filter, skip: 0, take: take, NullLogger.Instance, cancellationToken)
					.ConfigureAwait(false);

				foreach (var package in metadata) {
					results.Add(await ToPackageAsync(package, cancellationToken).ConfigureAwait(false));
				}
			} catch (Exception ex) {
				LoggingService.Warn($"NuGet search against '{sourceUrl}' failed: {ex}");
				throw;
			}

			return results;
		}

		static Task<NuGetSearchResultPackage> ToPackageAsync(IPackageSearchMetadata metadata, CancellationToken cancellationToken)
		{
			IEnumerable<global::NuGet.PackageDependencySet> dependencySets = Enumerable.Empty<global::NuGet.PackageDependencySet>();
			try {
				dependencySets = metadata.DependencySets?.Select(ToDependencySet).ToList() ?? dependencySets;
			} catch (Exception ex) {
				LoggingService.Debug($"Could not read dependency groups for '{metadata.Identity.Id}': {ex.Message}");
			}

			return Task.FromResult(new NuGetSearchResultPackage(
				metadata.Identity.Id,
				metadata.Identity.Version.ToNormalizedString(),
				metadata.Title,
				metadata.Description,
				metadata.Summary,
				metadata.Authors?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
				metadata.IconUrl,
				metadata.LicenseUrl,
				metadata.ProjectUrl,
				metadata.DownloadCount,
				metadata.Published,
				metadata.IsListed,
				metadata.RequireLicenseAcceptance,
				dependencySets));
		}

		static global::NuGet.PackageDependencySet ToDependencySet(NuGet.Packaging.PackageDependencyGroup group)
		{
			var dependencies = group.Packages.Select(dependency => {
				var range = dependency.VersionRange;
				var versionSpec = new global::NuGet.VersionSpec {
					MinVersion = range?.MinVersion != null ? new global::NuGet.SemanticVersion(range.MinVersion.ToNormalizedString()) : null,
					IsMinInclusive = range?.IsMinInclusive ?? true,
					MaxVersion = range?.MaxVersion != null ? new global::NuGet.SemanticVersion(range.MaxVersion.ToNormalizedString()) : null,
					IsMaxInclusive = range?.IsMaxInclusive ?? false,
				};
				return new global::NuGet.PackageDependency(dependency.Id, versionSpec);
			}).ToList();

			return new global::NuGet.PackageDependencySet(null, dependencies);
		}
	}
}
