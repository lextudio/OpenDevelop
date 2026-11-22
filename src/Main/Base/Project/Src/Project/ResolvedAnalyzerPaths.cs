// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>
	/// The <c>Analyzer</c> items MSBuild resolved for a project, recorded by the reference resolution that
	/// already runs ResolveReferences for it (so no extra MSBuild process): package analyzers and source
	/// generators, and the targeting pack's generators (Regex, LibraryImport, ...). Evaluation alone never
	/// has them. The language service needs the generators, or its compilation lacks every generated member.
	/// </summary>
	public static class ResolvedAnalyzerPaths
	{
		static readonly ConcurrentDictionary<string, IReadOnlyList<string>> byProject = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// <c>OD_RESOLVE_ANALYZERS=0</c> turns the collection off (the language service then sees only
		/// declared Analyzer items, as before): an escape hatch, and the A/B switch for measuring its cost.
		/// </summary>
		public static bool Enabled { get; } = Environment.GetEnvironmentVariable("OD_RESOLVE_ANALYZERS") != "0";

		/// <summary>
		/// Records what one resolution returned. <paramref name="targetFramework"/> is the slice the caller asked
		/// for: null for the project as a whole (its default inner build), a TFM for one slice of a
		/// multi-targeted project; analyzers differ per TFM, so slices never share an entry.
		/// </summary>
		public static void Record(string projectFile, string targetFramework, IEnumerable<string> analyzers)
		{
			if (string.IsNullOrEmpty(projectFile))
				return;
			byProject[Key(projectFile, targetFramework)] = Enabled ? Filter(analyzers).ToArray() : Array.Empty<string>();
		}

		public static void Record(string projectFile, IEnumerable<string> analyzers) => Record(projectFile, null, analyzers);

		/// <summary>The recorded analyzers, or null when this project (slice) was not resolved in this process.</summary>
		public static IReadOnlyList<string> TryGet(string projectFile, string targetFramework = null)
			=> projectFile != null && byProject.TryGetValue(Key(projectFile, targetFramework), out var analyzers) ? analyzers : null;

		static string Key(string projectFile, string targetFramework) => projectFile + "|" + (targetFramework ?? string.Empty);

		/// <summary>
		/// Drops the SDK's own code-quality analyzers (CAxxxx/IDExxxx rules under
		/// <c>Sdks/Microsoft.NET.Sdk/analyzers</c>): they produce diagnostics only, no generated code, and
		/// loading them for every project is the expensive part of running analyzers in the host.
		/// </summary>
		public static IEnumerable<string> Filter(IEnumerable<string> analyzers)
			=> (analyzers ?? Enumerable.Empty<string>())
				.Where(path => !string.IsNullOrWhiteSpace(path) && !IsSdkCodeQualityAnalyzer(path))
				.Distinct(StringComparer.OrdinalIgnoreCase);

		public static bool IsSdkCodeQualityAnalyzer(string path)
			=> Normalize(path).IndexOf("/Sdks/Microsoft.NET.Sdk/analyzers/", StringComparison.OrdinalIgnoreCase) >= 0;

		// MSBuild reports the SDK's analyzers as ".../Sdks/Microsoft.NET.Sdk/targets/../analyzers/...".
		static string Normalize(string path)
		{
			try {
				path = System.IO.Path.GetFullPath(path);
			} catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is System.IO.PathTooLongException) {
			}
			return path.Replace('\\', '/');
		}
	}
}
