using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace ICSharpCode.SharpDevelop.NuGet
{
	public sealed class NuGetPackageDependencyPreviewService
	{
		readonly ILogger logger;

		public NuGetPackageDependencyPreviewService(ILogger logger = null)
		{
			this.logger = logger ?? NullLogger.Instance;
		}

		public async Task<NuGetPackageDependencyPreview> GetDependencyPreviewAsync(
			IReadOnlyList<PackageSource> sources,
			string packageId,
			NuGetVersion version,
			NuGetFramework targetFramework,
			CancellationToken cancellationToken)
		{
			if (sources is null)
				throw new ArgumentNullException(nameof(sources));
			if (string.IsNullOrWhiteSpace(packageId))
				throw new ArgumentException("Package id cannot be empty.", nameof(packageId));
			if (version is null)
				throw new ArgumentNullException(nameof(version));

			targetFramework ??= NuGetFramework.AnyFramework;

			foreach (var source in sources)
			{
				cancellationToken.ThrowIfCancellationRequested();

				try
				{
					var repository = Repository.Factory.GetCoreV3(source);
					var dependencyInfoResource = await repository.GetResourceAsync<DependencyInfoResource>(cancellationToken).ConfigureAwait(false);
					if (dependencyInfoResource is null)
						continue;

					using (var cacheContext = new SourceCacheContext()) {
						var package = await dependencyInfoResource
							.ResolvePackage(new PackageIdentity(packageId, version), targetFramework, cacheContext, logger, cancellationToken)
							.ConfigureAwait(false);
						if (package is null)
							continue;

						return new NuGetPackageDependencyPreview(
							packageId,
							version.ToNormalizedString(),
							source.Name,
							new[] {
								new NuGetPackageDependencyGroup(
									targetFramework.GetShortFolderName(),
									package.Dependencies.Select(ToDependencyItem).ToArray())
							});
					}
				}
				catch (Exception ex)
				{
					LoggingService.Warn($"NuGet dependency preview for '{packageId}' against '{source.Name}' ({source.SourceUri}) failed: {ex}");
				}
			}

			return new NuGetPackageDependencyPreview(
				packageId,
				version.ToNormalizedString(),
				string.Empty,
				new NuGetPackageDependencyGroup[0]);
		}

		static NuGetPackageDependencyItem ToDependencyItem(global::NuGet.Packaging.Core.PackageDependency dependency)
		{
			return new NuGetPackageDependencyItem(
				dependency.Id,
				dependency.VersionRange?.ToNormalizedString() ?? string.Empty);
		}
	}
}
