// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.SharpDevelop;
using Modern = NuGet.Configuration;

namespace ICSharpCode.PackageManagement
{
	/// <summary>
	/// The feeds the dialog works with (package health, updates): the source the dialog is browsing, or every enabled source when
	/// that is the aggregate. They are taken from NuGet's own configuration (<see cref="Modern.PackageSourceProvider"/>)
	/// rather than rebuilt from the legacy source list, so credentials and the protocol version come along.
	/// </summary>
	static class PackageFeeds
	{
		public static IReadOnlyList<Modern.PackageSource> Current()
		{
			var repositories = PackageManagementServices.RegisteredPackageRepositories;
			NuGet.PackageSource? active = repositories.ActivePackageSource;
			IEnumerable<NuGet.PackageSource> browsing = active == null || active.IsAggregate()
				? repositories.PackageSources.GetEnabledPackageSources()
				: new[] { active };
			var names = new HashSet<string>(browsing.Select(source => source.Name), StringComparer.OrdinalIgnoreCase);
			Modern.ISettings settings = Modern.Settings.LoadDefaultSettings(SD.ProjectService.CurrentSolution?.Directory.ToString());
			return new Modern.PackageSourceProvider(settings).LoadPackageSources()
				.Where(source => source.IsEnabled && names.Contains(source.Name))
				.ToList();
		}
	}
}
