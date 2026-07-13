// Minimal IPackageRepository standing in for a real HTTP(S) source when constructing the legacy
// NuGet.Core repository object for that source throws. On this runtime, legacy NuGet.Core's own
// repository construction for a remote source touches machine-wide package cache locking that uses
// Windows-only named-Mutex/lock-file syntax (a "Global\\<hash>" name interpreted as a path) - a
// third instance of the same family of bug as the two crashes in doc/technotes/nuget.md, this time
// in repository *construction* itself rather than settings or the OData search client.
//
// This repository is only ever used for its .Source (identity/equality, passed through to
// SdkStylePackageReferenceService for "dotnet restore --source") - actual search goes through
// NuGetPackageSearchService (real NuGet.Protocol), and actual package content fetch goes through
// "dotnet restore" (a separate, modern process), so GetPackages/AddPackage/RemovePackage are never
// exercised by the search+install path this addin's dialog drives.

using System;
using System.Collections.Generic;
using System.Linq;

using NuGet;

namespace ICSharpCode.PackageManagement
{
	public sealed class NuGetRemoteSourceRepository : IPackageRepository
	{
		public NuGetRemoteSourceRepository(string source)
		{
			Source = source;
		}

		public string Source { get; }

		public PackageSaveModes PackageSaveMode { get; set; } = PackageSaveModes.Nupkg;

		public bool SupportsPrereleasePackages => true;

		public IQueryable<IPackage> GetPackages() => Enumerable.Empty<IPackage>().AsQueryable();

		public void AddPackage(IPackage package)
		{
			throw new NotSupportedException($"'{Source}' is a remote source handled by NuGetPackageSearchService/dotnet restore, not the legacy in-process engine.");
		}

		public void RemovePackage(IPackage package)
		{
			throw new NotSupportedException($"'{Source}' is a remote source handled by NuGetPackageSearchService/dotnet restore, not the legacy in-process engine.");
		}
	}
}
