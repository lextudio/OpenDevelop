// Real, modern NuGet.Client-backed package search - replaces the legacy NuGet.Core
// IPackageRepository.Search(...) call in AvailablePackagesViewModel.GetAllPackages, which cannot
// work at all against a real HTTP source on this runtime (see doc/technotes/nuget.md: its OData V2
// client needs System.Data.Services.Client, unavailable on modern .NET).
//
// The feed search itself now lives in the shared ICSharpCode.SharpDevelop.NuGet.NuGetPackageSearchEngine
// (Main/Base/Project/Src/NuGet/), used by both hosts. What stays here is only the projection this
// AddIn needs: adapting each result into the legacy NuGet.Core IPackage shape the rest of its
// pipeline (PackagesViewModel/PackageViewModel/PackageFromRepository) still expects, including the
// PackageDependencySet conversion. This method's signature is deliberately unchanged from before
// the engine was extracted, so AvailablePackagesViewModel.GetAllPackages is unaffected.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.NuGet;

using NuGet.Common;
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
			var metadata = await NuGetPackageSearchEngine
				.SearchAsync(sourceUrl, searchTerm, includePrerelease, take, NullLogger.Instance,
					throwOnSourceError: true, cancellationToken)
				.ConfigureAwait(false);

			var results = new List<NuGetSearchResultPackage>(metadata.Count);
			foreach (var package in metadata) {
				results.Add(ToPackage(package));
			}

			return results;
		}

		static NuGetSearchResultPackage ToPackage(IPackageSearchMetadata metadata)
		{
			IEnumerable<global::NuGet.PackageDependencySet> dependencySets = Enumerable.Empty<global::NuGet.PackageDependencySet>();
			try {
				dependencySets = metadata.DependencySets?.Select(ToDependencySet).ToList() ?? dependencySets;
			} catch (Exception ex) {
				LoggingService.Debug($"Could not read dependency groups for '{metadata.Identity.Id}': {ex.Message}");
			}

			return new NuGetSearchResultPackage(
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
				dependencySets);
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
