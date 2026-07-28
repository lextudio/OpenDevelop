using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace ICSharpCode.SharpDevelop.NuGet
{
	/// <summary>
	/// Native, reduced-scope equivalent of OpenDevelop's Package Manager Console (see
	/// <c>src/AddIns/Misc/PackageManagement/PowerShell</c> and <c>Cmdlets</c> for the original -
	/// a real embedded Windows PowerShell host with <c>Install-Package</c>/<c>Update-Package</c>/
	/// <c>Uninstall-Package</c>/<c>Get-Package</c> cmdlets). Hosting <c>System.Management.Automation</c>
	/// interactively inside Uno-Skia on macOS/Linux (custom <c>PSHost</c>, runspace, console I/O
	/// redirection, tab completion, cross-plat native SDK packaging) is out of scope for this
	/// session - see doc/technotes/package-management.md for why. This class is the scoped-down,
	/// honestly-real substitute: a small line-oriented command language covering the same everyday
	/// verbs, going through the exact same shared services (<see cref="NuGetProjectPackageOperationService"/>,
	/// <see cref="NuGetPackageConflictResolutionService"/>, <see cref="NuGetPackageUpdateService"/>,
	/// <see cref="SdkStylePackageReferenceEditor"/>) that back the graphical package manager, so a
	/// scripted install and a UI install produce identical, conflict-checked results.
	///
	/// Supported commands (one per line):
	///   list                              - list installed packages
	///   install &lt;id&gt; [version]            - install/update to the given version, or latest if omitted
	///   update &lt;id&gt; [version]             - same as install, for an already-installed package
	///   uninstall &lt;id&gt;                    - remove a package reference
	///   help                              - list commands
	/// </summary>
	public sealed class PackageConsoleCommandProcessor
	{
		readonly string projectFileName;
		readonly IReadOnlyList<PackageSource> sources;
		readonly NuGetFramework targetFramework;
		readonly Func<string, string, bool, string, Task<bool>> confirmLicenseAsync;
		readonly NuGetProjectPackageOperationService operationService;
		readonly ILogger logger;

		/// <param name="confirmLicenseAsync">
		/// Callback invoked as (packageId, version, requireLicenseAcceptance, licenseUrl) => accepted;
		/// only called when the package's metadata declares requireLicenseAcceptance. A host with no
		/// UI (batch/CI scripting) can pass a callback that always returns false for
		/// requireLicenseAcceptance packages, refusing to silently accept on a user's behalf.
		/// </param>
		public PackageConsoleCommandProcessor(
			string projectFileName,
			IReadOnlyList<PackageSource> sources,
			NuGetFramework targetFramework,
			Func<string, string, bool, string, Task<bool>> confirmLicenseAsync,
			ILogger logger = null)
		{
			this.projectFileName = projectFileName ?? throw new ArgumentNullException(nameof(projectFileName));
			this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
			this.targetFramework = targetFramework ?? NuGetFramework.AnyFramework;
			this.confirmLicenseAsync = confirmLicenseAsync ?? throw new ArgumentNullException(nameof(confirmLicenseAsync));
			this.logger = logger ?? NullLogger.Instance;
			operationService = new NuGetProjectPackageOperationService();
		}

		public async Task<string> ExecuteAsync(string commandLine, CancellationToken cancellationToken)
		{
			if (string.IsNullOrWhiteSpace(commandLine))
				return string.Empty;

			var tokens = commandLine.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
			var verb = tokens[0].ToLowerInvariant();

			try
			{
				return verb switch
				{
					"help" => Help(),
					"list" => List(),
					"install" => await InstallOrUpdateAsync(tokens, cancellationToken).ConfigureAwait(false),
					"update" => await InstallOrUpdateAsync(tokens, cancellationToken).ConfigureAwait(false),
					"uninstall" => await UninstallAsync(tokens, cancellationToken).ConfigureAwait(false),
					_ => $"Unknown command '{tokens[0]}'. Type 'help' for a list of commands.",
				};
			}
			catch (Exception ex)
			{
				LoggingService.Warn($"Package console command '{commandLine}' failed: {ex}");
				return $"Error: {ex.Message}";
			}
		}

		static string Help()
		{
			return string.Join(Environment.NewLine, new[]
			{
				"list                          list installed packages",
				"install <id> [version]        install a package (latest version if omitted)",
				"update <id> [version]         update an installed package (latest if omitted)",
				"uninstall <id>                remove a package reference",
			});
		}

		string List()
		{
			var packages = new SdkStylePackageReferenceEditor(projectFileName).GetPackageReferences();
			if (packages.Count == 0)
				return "No installed packages.";

			return string.Join(Environment.NewLine, packages.Select(package => $"{package.Id} {package.Version}"));
		}

		async Task<string> InstallOrUpdateAsync(string[] tokens, CancellationToken cancellationToken)
		{
			if (tokens.Length < 2)
				return "Usage: install <id> [version]";

			var packageId = tokens[1];
			NuGetVersion version;
			string licenseUrl = null;
			bool requiresLicense = false;

			if (tokens.Length >= 3 && NuGetVersion.TryParse(tokens[2], out var explicitVersion))
			{
				version = explicitVersion;
			}
			else
			{
				var latest = await FindLatestAsync(packageId, cancellationToken).ConfigureAwait(false);
				if (latest is null)
					return $"Could not find package '{packageId}' on any configured source.";

				version = latest.Identity.Version;
				requiresLicense = latest.RequireLicenseAcceptance;
				licenseUrl = latest.LicenseUrl?.ToString();
			}

			if (requiresLicense && !await confirmLicenseAsync(packageId, version.ToNormalizedString(), true, licenseUrl).ConfigureAwait(false))
				return $"Install of '{packageId}' {version.ToNormalizedString()} cancelled: license not accepted.";

			var (conflicts, operation) = await operationService.AddPackageReferenceWithConflictCheckAsync(
				projectFileName, sources, targetFramework, packageId, version, restore: true, cancellationToken).ConfigureAwait(false);

			if (!conflicts.Succeeded)
				return $"Conflict resolving '{packageId}' {version.ToNormalizedString()}:{Environment.NewLine}{string.Join(Environment.NewLine, conflicts.Conflicts)}";

			if (!operation.Changed)
				return $"'{packageId}' is already up to date.";

			return operation.RestoreSucceeded
				? $"Installed '{packageId}' {version.ToNormalizedString()}{(operation.RestoreRequested ? " and restored the project." : ".")}"
				: $"Installed '{packageId}' {version.ToNormalizedString()}, but restore failed with exit code {operation.RestoreExitCode}: {operation.RestoreError}";
		}

		async Task<string> UninstallAsync(string[] tokens, CancellationToken cancellationToken)
		{
			if (tokens.Length < 2)
				return "Usage: uninstall <id>";

			var packageId = tokens[1];
			var operation = await operationService.RemovePackageReferenceAsync(projectFileName, packageId, restore: true, cancellationToken)
				.ConfigureAwait(false);

			if (!operation.Changed)
				return $"'{packageId}' is not installed.";

			return operation.RestoreSucceeded
				? $"Uninstalled '{packageId}'{(operation.RestoreRequested ? " and restored the project." : ".")}"
				: $"Uninstalled '{packageId}', but restore failed with exit code {operation.RestoreExitCode}: {operation.RestoreError}";
		}

		async Task<IPackageSearchMetadata> FindLatestAsync(string packageId, CancellationToken cancellationToken)
		{
			IPackageSearchMetadata best = null;
			foreach (var source in sources)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					var repository = Repository.Factory.GetCoreV3(source);
					var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
					if (metadataResource is null)
						continue;

					using var cacheContext = new SourceCacheContext();
					var metadata = await metadataResource
						.GetMetadataAsync(packageId, includePrerelease: false, includeUnlisted: false, cacheContext, logger, cancellationToken)
						.ConfigureAwait(false);
					var candidate = metadata.OrderByDescending(item => item.Identity.Version).FirstOrDefault();
					if (candidate is not null && (best is null || candidate.Identity.Version > best.Identity.Version))
						best = candidate;
				}
				catch (Exception ex)
				{
					LoggingService.Warn($"Package console lookup for '{packageId}' against '{source.Name}' failed: {ex}");
				}
			}

			return best;
		}
	}
}
