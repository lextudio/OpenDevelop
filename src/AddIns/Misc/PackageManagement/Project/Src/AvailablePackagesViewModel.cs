// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;

using NuGet;

namespace ICSharpCode.PackageManagement
{
	public class AvailablePackagesViewModel : PackagesViewModel
	{
		IPackageRepository repository;
		
		public AvailablePackagesViewModel(
			IPackageManagementSolution solution,
			IPackageManagementEvents packageManagementEvents,
			IRegisteredPackageRepositories registeredPackageRepositories,
			IPackageViewModelFactory packageViewModelFactory,
			ITaskFactory taskFactory)
			: base(
				solution,
				packageManagementEvents,
				registeredPackageRepositories, 
				packageViewModelFactory, 
				taskFactory)
		{
			IsSearchable = true;
			ShowPackageSources = true;
			ShowPrerelease = true;

			this.packageManagementEvents = packageManagementEvents;
			RegisterEvents();
		}
		
		void RegisterEvents()
		{
			packageManagementEvents.ParentPackageInstalled += OnPackageChanged;
			packageManagementEvents.ParentPackageUninstalled += OnPackageChanged;
			packageManagementEvents.ParentPackagesUpdated += OnPackageChanged;
		}
		
		protected override void OnDispose()
		{
			packageManagementEvents.ParentPackageInstalled -= OnPackageChanged;
			packageManagementEvents.ParentPackageUninstalled -= OnPackageChanged;
			packageManagementEvents.ParentPackagesUpdated -= OnPackageChanged;
		}
		
		protected override void UpdateRepositoryBeforeReadPackagesTaskStarts()
		{
			try {
				repository = RegisteredPackageRepositories.ActiveRepository;
			} catch (Exception ex) {
				repository = null;
				errorMessage = ex.Message;
			}
		}
		
		protected override IQueryable<IPackage> GetAllPackages(string searchCriteria)
		{
			if (repository == null) {
				throw new ApplicationException(errorMessage);
			}

			// The legacy NuGet.Core repository object is still used for callers elsewhere that need
			// its identity/equality (PackageFromRepository), but never for .Search(...) - its OData
			// V2 client can't run on this runtime for any real HTTP source (see
			// doc/technotes/nuget.md). Its .Source getter is ALSO unsafe to call for an http(s)
			// DataServicePackageRepository: on first access it lazily initializes NuGet.Core's own
			// static NuGet.ProxyCache, which constructs a legacy NuGet.Settings using the same
			// Windows-only named-Mutex syntax as the doc's crash #2 - so the source URL is read from
			// the plain PackageSource instead, never touching repository.Source.
			var sourceUrl = RegisteredPackageRepositories.ActivePackageSource?.Source;
			var packages = NuGetPackageSearchService
				.SearchAsync(sourceUrl, searchCriteria, IncludePrerelease, take: 200, System.Threading.CancellationToken.None)
				.GetAwaiter()
				.GetResult()
				.Cast<IPackage>()
				.AsQueryable();

			if (IncludePrerelease) {
				return packages.Where(package => package.IsAbsoluteLatestVersion);
			}
			return packages.Where(package => package.IsLatestVersion);
		}
		
		/// <summary>
		/// Order packages by most downloaded first.
		/// </summary>
		protected override IQueryable<IPackage> OrderPackages(IQueryable<IPackage> packages)
		{
			return packages.OrderByDescending(package => package.DownloadCount);
		}
		
		protected override IEnumerable<IPackage> GetFilteredPackagesBeforePagingResults(IQueryable<IPackage> allPackages)
		{
			if (IncludePrerelease) {
				return base.GetFilteredPackagesBeforePagingResults(allPackages)
					.DistinctLast<IPackage>(PackageEqualityComparer.Id);
			}
			return base.GetFilteredPackagesBeforePagingResults(allPackages)
				.Where(package => package.IsReleaseVersion())
				.DistinctLast<IPackage>(PackageEqualityComparer.Id);
		}

	}
}
