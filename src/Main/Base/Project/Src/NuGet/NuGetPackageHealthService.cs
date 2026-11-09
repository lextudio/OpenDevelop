// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace ICSharpCode.SharpDevelop.NuGet
{
    /// <summary>One known vulnerability of a package version, as the feed reports it.</summary>
    public sealed class NuGetPackageVulnerability
    {
        public NuGetPackageVulnerability(string severity, Uri advisoryUrl)
        {
            Severity = severity;
            AdvisoryUrl = advisoryUrl;
        }

        /// <summary>Low, Moderate, High, Critical (or Unknown).</summary>
        public string Severity { get; }

        public Uri AdvisoryUrl { get; }
    }

    /// <summary>What a feed says is wrong with one version of a package: deprecation and known vulnerabilities.</summary>
    public sealed class NuGetPackageHealth
    {
        public static readonly NuGetPackageHealth Healthy = new NuGetPackageHealth(false, Array.Empty<string>(), null, null, Array.Empty<NuGetPackageVulnerability>());

        public NuGetPackageHealth(
            bool isDeprecated, IReadOnlyList<string> deprecationReasons, string? deprecationMessage, string? alternatePackage,
            IReadOnlyList<NuGetPackageVulnerability> vulnerabilities)
        {
            IsDeprecated = isDeprecated;
            DeprecationReasons = deprecationReasons;
            DeprecationMessage = deprecationMessage;
            AlternatePackage = alternatePackage;
            Vulnerabilities = vulnerabilities;
        }

        public bool IsDeprecated { get; }

        /// <summary>For example Legacy, CriticalBugs, Other (NuGet's own reason names).</summary>
        public IReadOnlyList<string> DeprecationReasons { get; }

        public string? DeprecationMessage { get; }

        /// <summary>The package the author recommends instead, with its version range when the feed gives one.</summary>
        public string? AlternatePackage { get; }

        public IReadOnlyList<NuGetPackageVulnerability> Vulnerabilities { get; }

        public bool HasWarnings => IsDeprecated || Vulnerabilities.Count > 0;

        /// <summary>The warning as text for a details pane, one finding per line; empty when there is nothing to warn about.</summary>
        public string Describe()
        {
            var lines = new List<string>();
            if (IsDeprecated) {
                string reasons = DeprecationReasons.Count > 0 ? " (" + string.Join(", ", DeprecationReasons) + ")" : "";
                lines.Add("Deprecated" + reasons + (string.IsNullOrWhiteSpace(DeprecationMessage) ? "" : ": " + DeprecationMessage.Trim()));
                if (!string.IsNullOrWhiteSpace(AlternatePackage)) {
                    lines.Add("Use instead: " + AlternatePackage);
                }
            }
            foreach (NuGetPackageVulnerability vulnerability in Vulnerabilities) {
                lines.Add("Known vulnerability, " + vulnerability.Severity + " severity: " + vulnerability.AdvisoryUrl);
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Asks a package's feed for the deprecation and vulnerability information of one version. The data comes
    /// from NuGet.Protocol's metadata resource (the same registration data Visual Studio and <c>dotnet list package
    /// --vulnerable</c> read), so a feed that does not carry it, such as a local folder, simply reports nothing.
    /// </summary>
    public static class NuGetPackageHealthService
    {
        /// <summary>
        /// The health of <paramref name="id"/> <paramref name="version"/> according to the first of
        /// <paramref name="sources"/> that knows the package; <see cref="NuGetPackageHealth.Healthy"/> when none does,
        /// when the version is not a plain version, or when every source fails (a feed being down is not a finding).
        /// </summary>
        public static async Task<NuGetPackageHealth> GetAsync(
            IReadOnlyList<PackageSource> sources, string id, string version, CancellationToken cancellationToken = default)
        {
            if (!NuGetVersion.TryParse(version, out NuGetVersion? parsed)) {
                return NuGetPackageHealth.Healthy;
            }
            var identity = new PackageIdentity(id, parsed);
            using var cache = new SourceCacheContext();
            foreach (PackageSource source in sources) {
                cancellationToken.ThrowIfCancellationRequested();
                try {
                    SourceRepository repository = Repository.Factory.GetCoreV3(source);
                    PackageMetadataResource? resource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
                    if (resource == null) {
                        continue;
                    }
                    IPackageSearchMetadata? metadata = await resource
                        .GetMetadataAsync(identity, cache, NullLogger.Instance, cancellationToken).ConfigureAwait(false);
                    if (metadata == null) {
                        continue;
                    }
                    return await ReadAsync(metadata).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    LoggingService.Debug($"NuGet health lookup for '{id}' {version} against '{source.Name}' failed: {ex.Message}");
                }
            }
            return NuGetPackageHealth.Healthy;
        }

        static async Task<NuGetPackageHealth> ReadAsync(IPackageSearchMetadata metadata)
        {
            PackageDeprecationMetadata? deprecation = await metadata.GetDeprecationMetadataAsync().ConfigureAwait(false);
            var vulnerabilities = (metadata.Vulnerabilities ?? Enumerable.Empty<PackageVulnerabilityMetadata>())
                .Where(vulnerability => vulnerability.AdvisoryUrl != null)
                .Select(vulnerability => new NuGetPackageVulnerability(SeverityName(vulnerability.Severity), vulnerability.AdvisoryUrl))
                .ToList();
            string? alternate = deprecation?.AlternatePackage == null
                ? null
                : deprecation.AlternatePackage.PackageId + (deprecation.AlternatePackage.Range == null ? "" : " " + deprecation.AlternatePackage.Range);
            return new NuGetPackageHealth(
                deprecation != null,
                deprecation?.Reasons?.ToList() ?? new List<string>(),
                deprecation?.Message,
                alternate,
                vulnerabilities);
        }

        /// <summary>NuGet's severity numbers: 0 Low, 1 Moderate, 2 High, 3 Critical.</summary>
        internal static string SeverityName(int severity) => severity switch {
            0 => "Low",
            1 => "Moderate",
            2 => "High",
            3 => "Critical",
            _ => "Unknown"
        };
    }
}
