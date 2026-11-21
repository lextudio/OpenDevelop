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
using System.Text.RegularExpressions;

using ICSharpCode.ILSpyX.TreeView;

namespace ICSharpCode.AndroidSdkManager
{
	/// <summary>
	/// Groups the flat package list from sdkmanager into the two trees shown by the
	/// Platforms and Tools tabs.
	///
	/// sdkmanager reports one flat list ordered by package id, so neither the order of the first encounter nor the id
	/// order is a sensible tree order: "platforms;android-9" sorts after "platforms;android-34" as text, and the Tools
	/// families would be interleaved with bare leaves. Both trees are therefore built from explicit orderings:
	/// the Platforms tab lists API levels newest first, each holding its platform, sources, system images and add-ons;
	/// the Tools tab lists the known tool families in a fixed order, then any other family, each newest version first.
	/// </summary>
	public static class SdkPackageTreeBuilder
	{
		/// <summary>"android-34", "android-37.0" - the level of platform, sources and system-image packages.</summary>
		static readonly Regex AndroidLevelRegex = new Regex(@"android-(\d+(?:\.\d+)?)", RegexOptions.Compiled);

		/// <summary>Splits "37.0.0 rc2" into its numeric prefix "37.0.0" and remainder " rc2".</summary>
		static readonly Regex VersionPrefixRegex = new Regex(@"^(\d+(?:\.\d+)*)(.*)$", RegexOptions.Compiled);

		/// <summary>"platforms;android-CANARY", "system-images;android-canary-20260909;..." - preview channels have no
		/// numeric level, but they are packages too and must not vanish from the tree.</summary>
		static readonly Regex AndroidPreviewRegex = new Regex(@"android-([A-Za-z][A-Za-z0-9-]*)", RegexOptions.Compiled);

		/// <summary>"addon-google_apis-google-15" carries no "android-", only the API level at the end.</summary>
		static readonly Regex AddOnLevelRegex = new Regex(@"google-(\d+(?:\.\d+)?)$", RegexOptions.Compiled);

		static readonly Dictionary<int, string> ApiLevelNames = new Dictionary<int, string> {
			{ 35, "Android 15.0" },
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

		/// <summary>
		/// The Tools tab's families in display order. sdkmanager emits a family as the part of the package id before the
		/// first ';'; ids without one ("emulator", "platform-tools") are families of their own. Families not listed here
		/// still get a group, appended after these in id order, so no package is ever a bare row or silently dropped.
		/// </summary>
		static readonly (string Family, string Label)[] ToolFamilies = {
			("build-tools", "Android SDK Build-Tools"),
			("platform-tools", "Android SDK Platform-Tools"),
			("cmdline-tools", "Android SDK Command-line Tools"),
			("emulator", "Android Emulator"),
			("ndk", "NDK (Side by side)"),
			("ndk-bundle", "NDK (legacy bundle)"),
			("cmake", "CMake"),
			("skiaparser", "Skia parser"),
			("build", "Build tools (legacy)"),
			("extras", "Extras"),
			("patcher", "Extras"),
		};

		/// <summary>Order inside one API level: the platform itself, then sources, system images and add-ons.</summary>
		static int PlatformKindRank(string id)
		{
			if (id.StartsWith("platforms;", StringComparison.OrdinalIgnoreCase)) return 0;
			if (id.StartsWith("sources;", StringComparison.OrdinalIgnoreCase)) return 1;
			if (id.StartsWith("system-images;", StringComparison.OrdinalIgnoreCase)) return 2;
			if (id.StartsWith("add-ons;", StringComparison.OrdinalIgnoreCase)) return 3;
			return 4;
		}

		/// <summary>Group that holds platform-ish packages whose id carries no recognisable level, so none can vanish.</summary>
		const string UnclassifiedGroupLabel = "Other";

		public static SharpTreeNode BuildPlatformsRoot(IEnumerable<SdkPackage> packages)
		{
			var root = new SharpTreeNode { LazyLoading = false };
			var platformPackages = packages.Where(IsPlatformPackage).ToList();
			var byLevel = platformPackages
				.Select(package => new { Package = package, Level = GetApiLevel(package.Id) })
				.Where(entry => entry.Level != null)
				.GroupBy(entry => entry.Level, StringComparer.Ordinal)
				// Preview channels are newer than any released level, so they come first; both sets are newest first.
				.OrderBy(group => IsNumericLevel(group.Key))
				.ThenByDescending(group => group.Key, Comparer<string>.Create(CompareLevels));

			foreach (var level in byLevel) {
				var group = new SdkGroupNode(PlatformLabel(level.Key));
				// The version column of a platform package is its revision (2, 3, ...), not the platform version, so
				// the id decides the order inside a level: the plain platform first ("platforms;android-34" is a
				// prefix of its -ext and -beta siblings), then the extensions, then sources and system images.
				foreach (var package in level.Select(entry => entry.Package)
					.OrderBy(package => PlatformKindRank(package.Id))
					.ThenBy(package => package.Id, StringComparer.OrdinalIgnoreCase))
					group.Children.Add(new SdkPackageTreeNode(package));
				root.Children.Add(group);
			}

			// A platform-ish package whose level no regex recognises (a future add-on id, a renamed system image) still
			// has to be reachable: BuildToolsRoot skips platform families, so dropping it here would hide it entirely.
			var unclassified = platformPackages
				.Where(package => GetApiLevel(package.Id) == null)
				.OrderBy(package => PlatformKindRank(package.Id))
				.ThenBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (unclassified.Count != 0) {
				var group = new SdkGroupNode(UnclassifiedGroupLabel);
				foreach (var package in unclassified)
					group.Children.Add(new SdkPackageTreeNode(package));
				root.Children.Add(group);
			}

			return root;
		}

		public static SharpTreeNode BuildToolsRoot(IEnumerable<SdkPackage> packages)
		{
			var root = new SharpTreeNode { LazyLoading = false };
			var groups = new Dictionary<string, List<SdkPackage>>(StringComparer.OrdinalIgnoreCase);

			foreach (var package in packages) {
				if (IsPlatformPackage(package))
					continue;
				var label = ToolGroupLabel(FamilyOf(package.Id));
				if (!groups.TryGetValue(label, out var items))
					groups.Add(label, items = new List<SdkPackage>());
				items.Add(package);
			}

			foreach (var group in groups.OrderBy(entry => ToolGroupOrder(entry.Key)).ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)) {
				var node = new SdkGroupNode(group.Key);
				foreach (var package in group.Value.OrderBy(package => package, NewestFirstComparer.Instance))
					node.Children.Add(new SdkPackageTreeNode(package));
				root.Children.Add(node);
			}

			return root;
		}

		static string FamilyOf(string id)
		{
			var separator = id.IndexOf(';');
			return separator < 0 ? id : id.Substring(0, separator);
		}

		static string ToolGroupLabel(string family)
		{
			foreach (var known in ToolFamilies) {
				if (string.Equals(known.Family, family, StringComparison.OrdinalIgnoreCase))
					return known.Label;
			}
			return family;
		}

		static int ToolGroupOrder(string label)
		{
			for (int i = 0; i < ToolFamilies.Length; i++) {
				if (string.Equals(ToolFamilies[i].Label, label, StringComparison.OrdinalIgnoreCase))
					return i;
			}
			return int.MaxValue;
		}

		static bool IsPlatformPackage(SdkPackage package)
		{
			return package.Id.StartsWith("platforms;", StringComparison.OrdinalIgnoreCase)
				|| package.Id.StartsWith("sources;", StringComparison.OrdinalIgnoreCase)
				|| package.Id.StartsWith("system-images;", StringComparison.OrdinalIgnoreCase)
				|| package.Id.StartsWith("add-ons;", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>The API level of a platform-ish package, or null when the id carries none.</summary>
		static string GetApiLevel(string id)
		{
			var match = AndroidLevelRegex.Match(id);
			if (!match.Success)
				match = AddOnLevelRegex.Match(id);
			if (!match.Success)
				match = AndroidPreviewRegex.Match(id);
			if (!match.Success)
				return null;
			return NormalizeLevel(match.Groups[1].Value);
		}

		/// <summary>A numeric level ("34", "37.2") versus a preview channel ("CANARY", "canary-20260909").</summary>
		static bool IsNumericLevel(string level)
		{
			foreach (char c in level) {
				if (!char.IsDigit(c) && c != '.')
					return false;
			}
			return level.Length != 0;
		}

		/// <summary>"37.0" and "37" are the same API level.</summary>
		static string NormalizeLevel(string level)
		{
			return level.EndsWith(".0", StringComparison.Ordinal) ? level.Substring(0, level.Length - 2) : level;
		}

		static string PlatformLabel(string level)
		{
			if (!IsNumericLevel(level))
				return "Preview (" + level + ")";

			if (int.TryParse(level.Split('.')[0], out int major) && ApiLevelNames.TryGetValue(major, out var name))
				return name + " (API " + level + ")";
			return "API Level " + level;
		}

		/// <summary>Numeric-aware comparison of two API level strings ("9" &lt; "10", "37" &lt; "37.2").</summary>
		static int CompareLevels(string left, string right)
		{
			if (!IsNumericLevel(left) || !IsNumericLevel(right))
				return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);

			var leftParts = left.Split('.');
			var rightParts = right.Split('.');
			int count = Math.Max(leftParts.Length, rightParts.Length);
			for (int i = 0; i < count; i++) {
				int leftValue = i < leftParts.Length && int.TryParse(leftParts[i], out var l) ? l : 0;
				int rightValue = i < rightParts.Length && int.TryParse(rightParts[i], out var r) ? r : 0;
				if (leftValue != rightValue)
					return leftValue.CompareTo(rightValue);
			}
			return 0;
		}

		/// <summary>Newest version first, then package id so equal versions are stable.</summary>
		sealed class NewestFirstComparer : IComparer<SdkPackage>
		{
			public static readonly NewestFirstComparer Instance = new NewestFirstComparer();

			public int Compare(SdkPackage x, SdkPackage y)
			{
				if (ReferenceEquals(x, y))
					return 0;
				if (x == null)
					return 1;
				if (y == null)
					return -1;

				int versions = CompareVersionText(NewestVersion(x), NewestVersion(y));
				if (versions != 0)
					return -versions;
				return string.Compare(x.Id, y.Id, StringComparison.OrdinalIgnoreCase);
			}

			static string NewestVersion(SdkPackage package)
			{
				// An update's available version is newer than the installed one, so it decides the order.
				var version = string.CompareOrdinal(package.AvailableVersion, package.InstalledVersion) > 0
					? package.AvailableVersion
					: package.InstalledVersion;
				if (!string.IsNullOrEmpty(version))
					return version;

				// Packages whose id ends in a version ("build-tools;34.0.0") keep their order even when sdkmanager
				// reported no version cell for them.
				var separator = package.Id.IndexOf(';');
				return separator < 0 ? string.Empty : package.Id.Substring(separator + 1);
			}

			/// <summary>
			/// Compares dotted numeric versions segment by segment. A version with a non-numeric remainder
			/// ("37.0.0 rc2", "34.0.0-alpha01") is a pre-release of the plain version, so it sorts BELOW it: that keeps
			/// build-tools;37.0.0 ahead of 37.0.0-rc2 and 37.0.0-rc1.
			/// </summary>
			static int CompareVersionText(string left, string right)
			{
				if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
					return string.CompareOrdinal(left, right);

				var leftMatch = VersionPrefixRegex.Match(left.Trim());
				var rightMatch = VersionPrefixRegex.Match(right.Trim());
				if (!leftMatch.Success || !rightMatch.Success)
					return string.CompareOrdinal(left, right);

				var leftNumbers = leftMatch.Groups[1].Value.Split('.');
				var rightNumbers = rightMatch.Groups[1].Value.Split('.');
				int count = Math.Max(leftNumbers.Length, rightNumbers.Length);
				for (int i = 0; i < count; i++) {
					long l = i < leftNumbers.Length && long.TryParse(leftNumbers[i], out var parsedLeft) ? parsedLeft : 0;
					long r = i < rightNumbers.Length && long.TryParse(rightNumbers[i], out var parsedRight) ? parsedRight : 0;
					if (l != r)
						return l.CompareTo(r);
				}

				var leftSuffix = leftMatch.Groups[2].Value.Trim();
				var rightSuffix = rightMatch.Groups[2].Value.Trim();
				if (leftSuffix.Length == 0 || rightSuffix.Length == 0)
					return rightSuffix.Length.CompareTo(leftSuffix.Length); // the plain version wins
				return string.CompareOrdinal(leftSuffix, rightSuffix);
			}
		}
	}
}
