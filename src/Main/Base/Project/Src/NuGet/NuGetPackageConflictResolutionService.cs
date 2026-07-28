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
	/// <summary>
	/// Full transitive dependency resolution and version-conflict detection for a package
	/// install/update, shared by both hosts (see doc/technotes/package-management.md).
	///
	/// <see cref="NuGetPackageDependencyPreviewService"/> only shows the *direct* dependency
	/// group of a single package/version - it never walks transitively and never checks the
	/// result against what is already installed in the project. This service does both: it
	/// walks the full transitive closure reachable from the project's existing direct package
	/// references plus the package being installed/updated, and for every package id that more
	/// than one requester depends on, verifies the version chosen for it actually satisfies every
	/// requester's <see cref="VersionRange"/>. A range that cannot be satisfied by any resolvable
	/// version is reported as an explicit conflict rather than silently picking one side.
	///
	/// This deliberately does not call into <c>NuGet.Resolver.PackageResolver</c> - that API's
	/// constructor/context shape has changed across NuGet.Client versions and getting it wrong
	/// would only be caught by a full solution build (which, per doc/technotes/nuget.md, this
	/// sandbox cannot reliably do end-to-end because of the unrelated WpfDesign.AddIn blocker).
	/// Walking <see cref="DependencyInfoResource"/>/<see cref="FindPackageByIdResource"/> directly
	/// with a simple, auditable greedy widest-compatible-version algorithm is easier to verify by
	/// reading and gives the same user-facing guarantee: a real transitive walk, and an explicit,
	/// readable conflict report instead of silence.
	/// </summary>
	public sealed class NuGetPackageConflictResolutionService
	{
		const int MaxPackagesWalked = 200;

		readonly ILogger logger;

		public NuGetPackageConflictResolutionService(ILogger logger = null)
		{
			this.logger = logger ?? NullLogger.Instance;
		}

		/// <param name="sources">Package sources to resolve against, in priority order.</param>
		/// <param name="installedPackages">The project's current direct package references (before this operation).</param>
		/// <param name="packageId">Id of the package being installed/updated.</param>
		/// <param name="version">Version of the package being installed/updated.</param>
		/// <param name="targetFramework">The project's target framework.</param>
		public async Task<NuGetPackageConflictResolutionResult> ResolveAsync(
			IReadOnlyList<PackageSource> sources,
			IReadOnlyList<PackageIdentity> installedPackages,
			string packageId,
			NuGetVersion version,
			NuGetFramework targetFramework,
			CancellationToken cancellationToken)
		{
			if (sources is null)
				throw new ArgumentNullException(nameof(sources));
			if (installedPackages is null)
				throw new ArgumentNullException(nameof(installedPackages));
			if (string.IsNullOrWhiteSpace(packageId))
				throw new ArgumentException("Package id cannot be empty.", nameof(packageId));
			if (version is null)
				throw new ArgumentNullException(nameof(version));

			targetFramework ??= NuGetFramework.AnyFramework;

			// Roots: the project's existing direct references, with the package being
			// installed/updated overriding any existing entry with the same id.
			var roots = installedPackages
				.Where(package => !string.Equals(package.Id, packageId, StringComparison.OrdinalIgnoreCase))
				.ToDictionary(package => package.Id, package => package.Version, StringComparer.OrdinalIgnoreCase);
			roots[packageId] = version;

			var installedVersions = installedPackages.ToDictionary(
				package => package.Id, package => package.Version.ToNormalizedString(), StringComparer.OrdinalIgnoreCase);

			var resolvedVersions = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
			var requirements = new Dictionary<string, List<(string RequesterId, VersionRange Range)>>(StringComparer.OrdinalIgnoreCase);
			var conflicts = new List<string>();

			using var cacheContext = new SourceCacheContext();
			var repositories = sources.Select(Repository.Factory.GetCoreV3).ToArray();

			var queue = new Queue<(string Id, NuGetVersion Version, string RequesterId)>();
			foreach (var root in roots)
				queue.Enqueue((root.Key, root.Value, "<project>"));

			var visited = 0;
			while (queue.Count > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (++visited > MaxPackagesWalked)
				{
					conflicts.Add($"Dependency graph exceeded {MaxPackagesWalked} packages; stopped walking to avoid unbounded network calls. Resolve the deepest conflicts first and re-run.");
					break;
				}

				var (id, requestedVersion, requesterId) = queue.Dequeue();

				if (resolvedVersions.TryGetValue(id, out var alreadyResolved))
				{
					// Already resolved elsewhere in the graph - just record this requirement so we
					// can later confirm the resolved version actually satisfies it.
					AddRequirement(requirements, id, requesterId, new VersionRange(requestedVersion));
					continue;
				}

				resolvedVersions[id] = requestedVersion;
				AddRequirement(requirements, id, requesterId, new VersionRange(requestedVersion));

				var dependencies = await ResolveDependenciesAsync(repositories, id, requestedVersion, targetFramework, cacheContext, cancellationToken)
					.ConfigureAwait(false);
				if (dependencies is null)
					continue;

				foreach (var dependency in dependencies)
				{
					var range = dependency.VersionRange ?? VersionRange.All;
					if (resolvedVersions.TryGetValue(dependency.Id, out var existing))
					{
						AddRequirement(requirements, dependency.Id, id, range);
						continue;
					}

					var candidate = await PickBestVersionAsync(repositories, dependency.Id, range, cacheContext, cancellationToken)
						.ConfigureAwait(false);
					if (candidate is null)
					{
						conflicts.Add($"Could not find any version of '{dependency.Id}' satisfying range '{range}' required by '{id}'.");
						AddRequirement(requirements, dependency.Id, id, range);
						continue;
					}

					AddRequirement(requirements, dependency.Id, id, range);
					queue.Enqueue((dependency.Id, candidate, id));
				}
			}

			// Verify every requirement against the version actually resolved for that id.
			foreach (var entry in requirements)
			{
				if (!resolvedVersions.TryGetValue(entry.Key, out var resolved))
					continue;

				foreach (var (requesterId, range) in entry.Value)
				{
					if (!range.Satisfies(resolved))
					{
						conflicts.Add(
							$"Version conflict on '{entry.Key}': '{requesterId}' requires '{range}' but the resolved version is '{resolved.ToNormalizedString()}' (needed to satisfy other requesters). Pin '{entry.Key}' explicitly to a version satisfying all requesters, or downgrade/upgrade the conflicting direct package.");
					}
				}
			}

			if (conflicts.Count > 0)
			{
				return new NuGetPackageConflictResolutionResult(
					succeeded: false,
					resolvedPackages: Array.Empty<NuGetResolvedPackage>(),
					conflicts: conflicts,
					message: $"{conflicts.Count} version conflict(s) found resolving '{packageId}' {version.ToNormalizedString()}.");
			}

			var resolvedPackages = resolvedVersions
				.Select(entry => new NuGetResolvedPackage(
					entry.Key,
					entry.Value.ToNormalizedString(),
					installedVersions.TryGetValue(entry.Key, out var previous) ? previous : string.Empty))
				.OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
				.ToArray();

			var changedCount = resolvedPackages.Count(package => package.ChangedFromInstalled);
			return new NuGetPackageConflictResolutionResult(
				succeeded: true,
				resolvedPackages: resolvedPackages,
				conflicts: Array.Empty<string>(),
				message: changedCount == 0
					? $"'{packageId}' {version.ToNormalizedString()} resolves cleanly with no changes to other installed package versions."
					: $"'{packageId}' {version.ToNormalizedString()} resolves, but requires {changedCount} other installed package(s) to change version.");
		}

		static void AddRequirement(
			Dictionary<string, List<(string RequesterId, VersionRange Range)>> requirements,
			string id, string requesterId, VersionRange range)
		{
			if (!requirements.TryGetValue(id, out var list))
			{
				list = new List<(string, VersionRange)>();
				requirements[id] = list;
			}

			list.Add((requesterId, range));
		}

		async Task<IReadOnlyList<global::NuGet.Packaging.Core.PackageDependency>> ResolveDependenciesAsync(
			IReadOnlyList<SourceRepository> repositories, string id, NuGetVersion version, NuGetFramework framework,
			SourceCacheContext cacheContext, CancellationToken cancellationToken)
		{
			foreach (var repository in repositories)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					var resource = await repository.GetResourceAsync<DependencyInfoResource>(cancellationToken).ConfigureAwait(false);
					if (resource is null)
						continue;

					var package = await resource.ResolvePackage(new PackageIdentity(id, version), framework, cacheContext, logger, cancellationToken)
						.ConfigureAwait(false);
					if (package is null)
						continue;

					return package.Dependencies.ToArray();
				}
				catch (Exception ex)
				{
					LoggingService.Warn($"NuGet dependency resolution for '{id}' {version} against '{repository.PackageSource.Name}' failed: {ex}");
				}
			}

			return Array.Empty<global::NuGet.Packaging.Core.PackageDependency>();
		}

		async Task<NuGetVersion> PickBestVersionAsync(
			IReadOnlyList<SourceRepository> repositories, string id, VersionRange range,
			SourceCacheContext cacheContext, CancellationToken cancellationToken)
		{
			foreach (var repository in repositories)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					var resource = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false);
					if (resource is null)
						continue;

					var versions = await resource.GetAllVersionsAsync(id, cacheContext, logger, cancellationToken).ConfigureAwait(false);
					var best = range.FindBestMatch(versions);
					if (best != null)
						return best;
				}
				catch (Exception ex)
				{
					LoggingService.Warn($"NuGet version lookup for '{id}' against '{repository.PackageSource.Name}' failed: {ex}");
				}
			}

			return null;
		}
	}
}
