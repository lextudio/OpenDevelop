// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

using NuGet.Frameworks;

namespace ICSharpCode.PackageManagement
{
	/// <summary>The target frameworks an SDK-style project builds for, from its evaluated properties.</summary>
	public static class ProjectTargetFrameworks
	{
		/// <summary>
		/// Each moniker of <paramref name="targetFrameworks"/> (semicolon separated); when that is empty, the single
		/// <paramref name="targetFramework"/>. Unrecognised monikers are dropped, so an unknown result is an empty list.
		/// </summary>
		public static IReadOnlyList<NuGetFramework> Parse(string? targetFrameworks, string? targetFramework)
		{
			string monikers = string.IsNullOrWhiteSpace(targetFrameworks) ? targetFramework ?? string.Empty : targetFrameworks;
			return monikers
				.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(moniker => NuGetFramework.Parse(moniker))
				.Where(framework => !framework.IsUnsupported)
				.ToList();
		}
	}
}
