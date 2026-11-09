// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.Core;
using NuGet.Packaging;
using NuGet.Versioning;

namespace ICSharpCode.PackageManagement
{
	/// <summary>
	/// The packages an SDK-style project references, as the legacy <see cref="NuGet.IPackage"/> the dialog's view models
	/// show: id and version from the project (central versions included), everything else (title, description, authors,
	/// links, dependencies) from the nuspec of the package in the global packages folder, read with NuGet's own
	/// <see cref="PackageFolderReader"/>. A package that was never restored has no nuspec to read and is shown by id and version.
	/// </summary>
	public static class InstalledSdkPackages
	{
		public static IList<NuGet.IPackage> Read(IReadOnlyDictionary<string, string> references, string? packagesFolder)
		{
			var packages = new List<NuGet.IPackage>();
			foreach (KeyValuePair<string, string> reference in references.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)) {
				NuGet.IPackage? package = null;
				try {
					string? directory = packagesFolder == null ? null : PackageTypeIndex.FindPackageDirectory(packagesFolder, reference.Key, reference.Value);
					if (directory != null) {
						package = FromNuspec(directory);
					}
				} catch (Exception ex) when (!(ex is OutOfMemoryException)) {
					LoggingService.Debug("Could not read the nuspec of '" + reference.Key + "': " + ex.Message);
				}
				package ??= NotRestored(reference.Key, reference.Value);
				if (package != null) {
					packages.Add(package);
				}
			}
			return packages;
		}

		static NuGet.IPackage FromNuspec(string packageDirectory)
		{
			using var reader = new PackageFolderReader(packageDirectory);
			NuspecReader nuspec = reader.NuspecReader;
			static Uri? Url(string? text) => Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ? uri : null;
			return new NuGetSearchResultPackage(
				nuspec.GetId(),
				nuspec.GetVersion().ToNormalizedString(),
				string.IsNullOrWhiteSpace(nuspec.GetTitle()) ? nuspec.GetId() : nuspec.GetTitle(),
				nuspec.GetDescription(),
				nuspec.GetSummary(),
				(nuspec.GetAuthors() ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
				Url(nuspec.GetIconUrl()),
				Url(nuspec.GetLicenseUrl()),
				Url(nuspec.GetProjectUrl()),
				downloadCount: null,
				published: null,
				isListed: true,
				requireLicenseAcceptance: nuspec.GetRequireLicenseAcceptance(),
				nuspec.GetDependencyGroups().Select(group => NuGetPackageSearchService.ToDependencySet(group)).ToList());
		}

		/// <summary>The version to show for a reference whose package is not in the packages folder: a plain version, else the lower bound of a range.</summary>
		static NuGet.IPackage? NotRestored(string id, string version)
		{
			string? shown = NuGetVersion.TryParse(version, out NuGetVersion? exact)
				? exact.ToNormalizedString()
				: VersionRange.TryParse(version, out VersionRange? range) ? range.MinVersion?.ToNormalizedString() : null;
			return shown == null
				? null
				: new NuGetSearchResultPackage(id, shown, id, "Not restored yet: restore the project to see this package's details.", null, null, null, null, null,
					downloadCount: null, published: null, isListed: true, requireLicenseAcceptance: false, dependencySets: null);
		}
	}
}
