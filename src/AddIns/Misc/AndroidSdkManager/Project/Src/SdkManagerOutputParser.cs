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

using System;
using System.Collections.Generic;
using System.Linq;

namespace ICSharpCode.AndroidSdkManager
{
	/// <summary>
	/// Parses the three pipe-delimited tables printed by `sdkmanager --list --verbose`
	/// ("Installed packages:", "Available Packages:", "Available Updates:") into a merged
	/// package-id -> SdkPackage map.
	/// </summary>
	public static class SdkManagerOutputParser
	{
		enum Section
		{
			None,
			Installed,
			Available,
			Updates
		}

		public static IReadOnlyList<SdkPackage> Parse(string output)
		{
			var packages = new Dictionary<string, SdkPackage>(StringComparer.OrdinalIgnoreCase);
			if (string.IsNullOrEmpty(output))
				return packages.Values.ToList();

			var section = Section.None;
			foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n')) {
				var line = rawLine.TrimEnd();
				if (line.Length == 0)
					continue;

				var trimmed = line.Trim();
				if (trimmed.StartsWith("Installed packages", StringComparison.OrdinalIgnoreCase)) {
					section = Section.Installed;
					continue;
				}
				if (trimmed.StartsWith("Available Packages", StringComparison.OrdinalIgnoreCase)) {
					section = Section.Available;
					continue;
				}
				if (trimmed.StartsWith("Available Updates", StringComparison.OrdinalIgnoreCase)) {
					section = Section.Updates;
					continue;
				}
				if (trimmed.EndsWith(":") || IsSeparatorOrHeaderRow(trimmed) || section == Section.None)
					continue;

				var cells = trimmed.Split('|').Select(c => c.Trim()).ToArray();
				if (cells.Length < 2 || string.IsNullOrEmpty(cells[0]))
					continue;

				switch (section) {
					case Section.Installed:
						ParseInstalledRow(packages, cells);
						break;
					case Section.Available:
						ParseAvailableRow(packages, cells);
						break;
					case Section.Updates:
						ParseUpdateRow(packages, cells);
						break;
				}
			}

			return packages.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
		}

		static bool IsSeparatorOrHeaderRow(string trimmed)
		{
			if (trimmed.All(c => c == '-' || c == '|' || char.IsWhiteSpace(c)))
				return true;
			return trimmed.StartsWith("Path ", StringComparison.OrdinalIgnoreCase)
				|| trimmed.StartsWith("Path|", StringComparison.OrdinalIgnoreCase)
				|| trimmed.StartsWith("ID ", StringComparison.OrdinalIgnoreCase)
				|| trimmed.StartsWith("ID|", StringComparison.OrdinalIgnoreCase);
		}

		static void ParseInstalledRow(Dictionary<string, SdkPackage> packages, string[] cells)
		{
			var id = cells[0];
			var package = GetOrAdd(packages, id);
			package.IsInstalled = true;
			package.InstalledVersion = cells.Length > 1 ? cells[1] : string.Empty;
			package.DisplayName = cells.Length > 2 && !string.IsNullOrEmpty(cells[2]) ? cells[2] : id;
		}

		static void ParseAvailableRow(Dictionary<string, SdkPackage> packages, string[] cells)
		{
			var id = cells[0];
			var package = GetOrAdd(packages, id);
			package.AvailableVersion = cells.Length > 1 ? cells[1] : string.Empty;
			if (string.IsNullOrEmpty(package.DisplayName))
				package.DisplayName = cells.Length > 2 && !string.IsNullOrEmpty(cells[2]) ? cells[2] : id;
		}

		static void ParseUpdateRow(Dictionary<string, SdkPackage> packages, string[] cells)
		{
			var id = cells[0];
			var package = GetOrAdd(packages, id);
			package.HasUpdate = true;
			if (cells.Length > 2)
				package.AvailableVersion = cells[2];
		}

		static SdkPackage GetOrAdd(Dictionary<string, SdkPackage> packages, string id)
		{
			if (!packages.TryGetValue(id, out var package)) {
				package = new SdkPackage { Id = id, DisplayName = id };
				packages.Add(id, package);
			}
			return package;
		}
	}
}
