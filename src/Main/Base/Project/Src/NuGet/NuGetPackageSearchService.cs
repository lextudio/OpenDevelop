using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;

namespace ICSharpCode.SharpDevelop.NuGet
{
    /// <summary>
    /// Multi-source package search projected into the presentation-shaped
    /// <see cref="NuGetSearchResult"/> record (docs/nuget-manager.md slice 3). The actual feed search
    /// lives in the shared <see cref="NuGetPackageSearchEngine"/>; this is only the projection layer -
    /// see that class for why the projection is per-host rather than shared.
    /// </summary>
    public sealed class NuGetPackageSearchService
    {
        readonly ILogger _logger;

        public NuGetPackageSearchService(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// Searches every given source and returns a deduplicated (by package id, first source
        /// wins — sources are expected in priority order, see
        /// <see cref="NuGetPackageSourceCatalog.LoadEnabledSources"/>), alphabetically sorted list.
        /// A source that fails to respond (network error, misconfigured URL, ...) is skipped with
        /// a logged warning rather than failing the whole search — one bad feed shouldn't block
        /// results from the others.
        /// </summary>
        public async Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
            IReadOnlyList<PackageSource> sources,
            string searchTerm,
            bool includePrerelease,
            int take,
            CancellationToken cancellationToken)
        {
            // Which source each result came from isn't carried on IPackageSearchMetadata, and
            // NuGetSearchResult surfaces it to the user - so resolve it per source here rather than
            // asking the shared engine to invent a result type that carries provenance.
            var resultsById = new Dictionary<string, NuGetSearchResult>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in sources ?? throw new ArgumentNullException(nameof(sources)))
            {
                var metadata = await NuGetPackageSearchEngine.SearchAsync(
                    new[] { source },
                    searchTerm,
                    includePrerelease,
                    take,
                    _logger,
                    throwOnSourceError: false,
                    cancellationToken).ConfigureAwait(false);

                foreach (var result in metadata)
                {
                    if (resultsById.ContainsKey(result.Identity.Id))
                        continue;

                    resultsById[result.Identity.Id] = new NuGetSearchResult(
                        result.Identity.Id,
                        result.Identity.Version.ToNormalizedString(),
                        result.Description,
                        result.DownloadCount,
                        result.IconUrl?.ToString(),
                        source.Name);
                }
            }

            return resultsById.Values
                .OrderBy(result => result.Id, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .ToArray();
        }
    }
}
