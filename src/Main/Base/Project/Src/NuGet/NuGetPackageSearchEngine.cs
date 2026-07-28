using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace ICSharpCode.SharpDevelop.NuGet
{
    /// <summary>
    /// The actual NuGet feed search, shared by both hosts (see doc/technotes/nuget.md): builds a
    /// <c>NuGet.Protocol</c> repository per source, resolves its <see cref="PackageSearchResource"/>,
    /// and runs the search - the real NuGet.Client search API, the same one
    /// <c>dotnet package search</c>/VS use, not a hand-rolled HTTP client against the NuGet v3 API.
    ///
    /// Deliberately returns raw <see cref="IPackageSearchMetadata"/> rather than a
    /// presentation-shaped type: the two hosts genuinely need different projections of the same
    /// results - UnoDevelop maps to a plain <see cref="NuGetSearchResult"/> record, while
    /// OpenDevelop's PackageManagement AddIn maps to a legacy <c>NuGet.Core</c> <c>IPackage</c>
    /// adapter its (unported) `PackagesViewModel`/`PackageFromRepository` pipeline still expects.
    /// An earlier attempt to share one method returning one projection type had to be reverted for
    /// exactly that reason; sharing the engine and splitting only the projection is what works.
    /// </summary>
    public static class NuGetPackageSearchEngine
    {
        /// <summary>
        /// Searches every given source in order and returns the union, deduplicated by package id
        /// (first source wins - sources are expected in priority order, see
        /// <see cref="NuGetPackageSourceCatalog.LoadEnabledSources"/>) in encounter order, so a
        /// caller that wants the feed's own relevance/download ranking still gets it. Deduplication
        /// is a no-op for a single source, since <see cref="PackageSearchResource"/> already returns
        /// at most one entry per package id.
        /// </summary>
        /// <param name="throwOnSourceError">
        /// When false (multi-source default), a source that fails to respond (network error,
        /// misconfigured URL, ...) is skipped with a logged warning rather than failing the whole
        /// search - one bad feed shouldn't block results from the others. When true (what a
        /// single-source caller wants), the failure propagates so the UI can surface the reason
        /// instead of silently showing an empty result list.
        /// </param>
        public static async Task<IReadOnlyList<IPackageSearchMetadata>> SearchAsync(
            IReadOnlyList<PackageSource> sources,
            string searchTerm,
            bool includePrerelease,
            int take,
            ILogger logger = null,
            bool throwOnSourceError = false,
            CancellationToken cancellationToken = default)
        {
            if (sources is null)
                throw new ArgumentNullException(nameof(sources));
            if (searchTerm is null)
                throw new ArgumentNullException(nameof(searchTerm));

            logger ??= NullLogger.Instance;
            var filter = new SearchFilter(includePrerelease);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<IPackageSearchMetadata>();

            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IEnumerable<IPackageSearchMetadata> metadata;
                try
                {
                    var repository = Repository.Factory.GetCoreV3(source);
                    var searchResource = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);
                    if (searchResource is null)
                        continue;

                    metadata = await searchResource
                        .SearchAsync(searchTerm, filter, skip: 0, take: take, logger, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"NuGet search against '{source.Name}' ({source.SourceUri}) failed: {ex}");
                    if (throwOnSourceError)
                        throw;
                    continue;
                }

                foreach (var result in metadata)
                {
                    if (seenIds.Add(result.Identity.Id))
                        results.Add(result);
                }
            }

            return results;
        }

        /// <summary>
        /// Convenience overload for a single source given as a URL/path string (a local folder feed,
        /// or an http(s) v2/v3 feed) - <see cref="Repository.Factory"/> handles all of those
        /// uniformly. Defaults <c>throwOnSourceError</c> to true: with only one source there is no
        /// "other feed" left to fall back to, so a failure is worth surfacing.
        /// </summary>
        public static Task<IReadOnlyList<IPackageSearchMetadata>> SearchAsync(
            string sourceUrl,
            string searchTerm,
            bool includePrerelease,
            int take,
            ILogger logger = null,
            bool throwOnSourceError = true,
            CancellationToken cancellationToken = default)
        {
            if (sourceUrl is null)
                throw new ArgumentNullException(nameof(sourceUrl));

            return SearchAsync(
                new[] { new PackageSource(sourceUrl) },
                searchTerm ?? string.Empty,
                includePrerelease,
                take,
                logger,
                throwOnSourceError,
                cancellationToken);
        }
    }
}
