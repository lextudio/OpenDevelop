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
using System.Text.RegularExpressions;

using ICSharpCode.TreeView;

namespace ICSharpCode.AndroidSdkManager
{
	/// <summary>
	/// Groups the flat package list from sdkmanager into the two trees shown by the
	/// Platforms and Tools tabs, mirroring the classic Android SDK Manager UI's grouping.
	/// </summary>
	public static class SdkPackageTreeBuilder
	{
		static readonly Regex ApiLevelRegex = new Regex(@"android-(\d+)", RegexOptions.Compiled);

		static readonly Dictionary<int, string> ApiLevelNames = new Dictionary<int, string> {
			{ 34, "Android 14.0" },
			{ 33, "Android 13.0 – Tiramisu" },
			{ 32, "Android 12L" },
			{ 31, "Android 12.0 – Sv2" },
			{ 30, "Android 11.0 – R" },
			{ 29, "Android 10.0 – Q" },
			{ 28, "Android 9.0 – Pie" },
			{ 27, "Android 8.1 – Oreo" },
			{ 26, "Android 8.0 – Oreo" },
			{ 25, "Android 7.1 – Nougat" },
			{ 24, "Android 7.0 – Nougat" },
			{ 23, "Android 6.0 – Marshmallow" },
			{ 22, "Android 5.1 – Lollipop" },
			{ 21, "Android 5.0 – Lollipop" },
		};

		static readonly Dictionary<string, string> ToolGroupNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
			{ "build-tools", "Android SDK Build-Tools" },
			{ "ndk", "NDK (Side by side)" },
			{ "cmdline-tools", "Android SDK Command-line Tools" },
			{ "extras", "Extras" },
			{ "patcher", "Extras" },
		};

		public static SharpTreeNode BuildPlatformsRoot(IEnumerable<SdkPackage> packages)
		{
			var root = new SharpTreeNode { LazyLoading = false };
			var groups = new SortedDictionary<int, SdkGroupNode>(Comparer<int>.Create((a, b) => b.CompareTo(a)));

			foreach (var package in packages) {
				if (!IsPlatformPackage(package))
					continue;

				var level = GetApiLevel(package.Id);
				if (!level.HasValue)
					continue;

				if (!groups.TryGetValue(level.Value, out var group)) {
					var label = ApiLevelNames.TryGetValue(level.Value, out var name)
						? name + " (API " + level.Value + ")"
						: "API Level " + level.Value;
					group = new SdkGroupNode(label);
					groups.Add(level.Value, group);
					root.Children.Add(group);
				}
				group.Children.Add(new SdkPackageTreeNode(package));
			}

			return root;
		}

		public static SharpTreeNode BuildToolsRoot(IEnumerable<SdkPackage> packages)
		{
			var root = new SharpTreeNode { LazyLoading = false };
			var groups = new Dictionary<string, SdkGroupNode>(StringComparer.OrdinalIgnoreCase);

			foreach (var package in packages) {
				if (IsPlatformPackage(package))
					continue;

				var kind = package.Id.Split(';')[0];
				if (ToolGroupNames.TryGetValue(kind, out var groupLabel)) {
					if (!groups.TryGetValue(groupLabel, out var group)) {
						group = new SdkGroupNode(groupLabel);
						groups.Add(groupLabel, group);
						root.Children.Add(group);
					}
					group.Children.Add(new SdkPackageTreeNode(package));
				} else {
					root.Children.Add(new SdkPackageTreeNode(package));
				}
			}

			return root;
		}

		static bool IsPlatformPackage(SdkPackage package)
		{
			return package.Id.StartsWith("platforms;", StringComparison.OrdinalIgnoreCase)
				|| package.Id.StartsWith("sources;", StringComparison.OrdinalIgnoreCase)
				|| package.Id.StartsWith("system-images;", StringComparison.OrdinalIgnoreCase)
				|| package.Id.StartsWith("add-ons;", StringComparison.OrdinalIgnoreCase);
		}

		static int? GetApiLevel(string id)
		{
			var match = ApiLevelRegex.Match(id);
			if (!match.Success)
				return null;
			return int.Parse(match.Groups[1].Value);
		}
	}
}
