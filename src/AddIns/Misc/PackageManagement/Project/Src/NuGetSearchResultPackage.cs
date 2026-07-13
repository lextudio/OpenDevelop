// Adapts a modern NuGet.Client search result (NuGet.Protocol.Core.Types.IPackageSearchMetadata)
// into the legacy NuGet.Core IPackage shape the rest of this addin's pipeline
// (PackagesViewModel/PackageViewModel/PackageFromRepository) still expects. See
// NuGetPackageSearchService.cs and doc/technotes/nuget.md for why search needs this at all
// (legacy NuGet.Core's own remote-search client is unusable on this runtime).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

using NuGet;
using NuGetVersioning = NuGet.Versioning;

namespace ICSharpCode.PackageManagement
{
	public sealed class NuGetSearchResultPackage : IPackage
	{
		public NuGetSearchResultPackage(
			string id,
			string version,
			string title,
			string description,
			string summary,
			IEnumerable<string> authors,
			Uri iconUrl,
			Uri licenseUrl,
			Uri projectUrl,
			long? downloadCount,
			DateTimeOffset? published,
			bool isListed,
			bool requireLicenseAcceptance,
			IEnumerable<PackageDependencySet> dependencySets)
		{
			Id = id;
			Version = new SemanticVersion(NuGetVersioning.NuGetVersion.Parse(version).ToNormalizedString());
			Title = title;
			Description = description ?? string.Empty;
			Summary = summary ?? string.Empty;
			AuthorsList = authors?.ToList() ?? new List<string>();
			IconUrl = iconUrl;
			LicenseUrl = licenseUrl;
			ProjectUrl = projectUrl;
			DownloadCount = downloadCount.HasValue ? (int)Math.Min(downloadCount.Value, int.MaxValue) : -1;
			Published = published;
			Listed = isListed;
			IsLatestVersion = true;
			IsAbsoluteLatestVersion = true;
			RequireLicenseAcceptance = requireLicenseAcceptance;
			DependencySetsList = dependencySets?.ToList() ?? new List<PackageDependencySet>();
		}

		public string Id { get; }
		public SemanticVersion Version { get; }
		public string Title { get; }
		public Uri IconUrl { get; }
		public Uri LicenseUrl { get; }
		public Uri ProjectUrl { get; }
		public bool RequireLicenseAcceptance { get; }
		public string Description { get; }
		public string Summary { get; }
		public string Language { get; }
		public string Tags { get; }
		public Uri ReportAbuseUrl { get; }
		public Uri GalleryUrl { get; }
		public int DownloadCount { get; }
		public int RatingsCount { get; }
		public double Rating { get; }
		public DateTime? LastUpdated { get; }
		public bool IsLatestVersion { get; }
		public DateTimeOffset? Published { get; }
		public string ReleaseNotes { get; }
		public string Copyright { get; }
		public bool IsAbsoluteLatestVersion { get; }
		public bool Listed { get; }
		public bool HasDependencies => DependencySetsList.Any(set => set.Dependencies.Any());
		public Version MinClientVersion { get; }
		public bool DevelopmentDependency { get; }

		List<string> AuthorsList { get; }
		public IEnumerable<string> Authors => AuthorsList;
		public IEnumerable<string> Owners => Enumerable.Empty<string>();

		List<PackageDependencySet> DependencySetsList { get; }
		public IEnumerable<PackageDependencySet> DependencySets => DependencySetsList;

		public IEnumerable<IPackageAssemblyReference> AssemblyReferences => Enumerable.Empty<IPackageAssemblyReference>();
		public IEnumerable<FrameworkAssemblyReference> FrameworkAssemblies => Enumerable.Empty<FrameworkAssemblyReference>();
		public ICollection<PackageReferenceSet> PackageAssemblyReferences { get; } = new List<PackageReferenceSet>();

		public IEnumerable<IPackageFile> GetFiles() => Enumerable.Empty<IPackageFile>();

		public IEnumerable<FrameworkName> GetSupportedFrameworks() => Enumerable.Empty<FrameworkName>();

		public Stream GetStream() => null;

		public void ExtractContents(IFileSystem fileSystem, string extractPath)
		{
			// Search results are metadata-only (see doc/technotes/nuget.md): actual package
			// content is fetched by "dotnet restore" (SdkStylePackageReferenceService.InstallPackage),
			// not by the legacy in-process engine, so this is never called on the install path
			// this addin's dialog exercises.
			throw new NotSupportedException("Search results only carry metadata; package content is fetched by 'dotnet restore', not this API.");
		}

		public override string ToString() => $"{Id} {Version}";

		public override bool Equals(object obj)
		{
			return obj is IPackage rhs && Id == rhs.Id && Version == rhs.Version;
		}

		public override int GetHashCode() => (Id, Version?.ToString()).GetHashCode();
	}
}
