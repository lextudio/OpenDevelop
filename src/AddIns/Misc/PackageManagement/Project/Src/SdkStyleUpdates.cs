// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.SharpDevelop.NuGet;
using NuGet.Frameworks;

namespace ICSharpCode.PackageManagement
{
	/// <summary>
	/// The newer versions available for the packages an SDK-style project references, asked of the feeds the dialog works
	/// with through NuGet.Protocol (<see cref="NuGetPackageUpdateService"/>), as the legacy package objects the Updates tab
	/// shows. The legacy engine asked a placeholder repository that knows no packages, so this tab was always empty.
	/// </summary>
	static class SdkStyleUpdates
	{
		public static IList<NuGet.IPackage> Get(
			IEnumerable<NuGet.IPackage> installed, string? searchTerms, bool includePrerelease, IReadOnlyList<NuGetFramework>? projectFrameworks = null, CancellationToken cancellationToken = default)
		{
			var references = installed
				.Where(package => string.IsNullOrWhiteSpace(searchTerms)
					|| package.Id.Contains(searchTerms, StringComparison.OrdinalIgnoreCase)
					|| (package.Title ?? string.Empty).Contains(searchTerms, StringComparison.OrdinalIgnoreCase))
				.Select(package => new SdkStylePackageReference(package.Id, package.Version.ToString()))
				.ToList();
			// Called from the tab's background task, so waiting here blocks no UI thread.
			var updates = new NuGetPackageUpdateService()
				.GetUpdatesAsync(PackageFeeds.Current(), references, includePrerelease, cancellationToken, projectFrameworks)
				.GetAwaiter().GetResult();
			return updates
				.Where(update => update.Metadata != null)
				.Select(update => (NuGet.IPackage)NuGetPackageSearchService.ToPackage(update.Metadata!))
				.ToList();
		}
	}
}
