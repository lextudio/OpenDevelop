using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Project.Sdk
{
	/// <summary>
	/// Discovers installed .NET SDK roots (system installs, OpenDevelop's own bundled/dev SDK, and
	/// user-added custom paths) and resolves which one the user has selected to build/debug/test
	/// project files with. This is the single authoritative source that MinimalMSBuildEngine,
	/// DapSession, and VsTestAdapter should all consult, instead of each independently guessing
	/// (which is exactly how OpenDevelop ended up with three inconsistent resolution strategies and
	/// the NETSDK1045 confusion this replaces).
	///
	/// Default behavior: no selection stored (empty SelectedSdkRootPath) means "use the system
	/// SDK" - i.e. whatever "dotnet" resolves to on PATH/standard install locations - not the
	/// bundled dev SDK this app itself happens to be running under.
	/// </summary>
	public static class DotNetSdkService
	{
		const string SelectedSdkRootPathKey = "SharpDevelop.Sdk.SelectedRootPath";
		const string CustomRootsKey = "SharpDevelop.Sdk.CustomRoots";

		public static string SelectedSdkRootPath {
			get { return PropertyService.Get(SelectedSdkRootPathKey, string.Empty); }
			set { PropertyService.Set(SelectedSdkRootPathKey, value ?? string.Empty); }
		}

		public static IReadOnlyList<string> CustomRoots {
			get { return PropertyService.GetList<string>(CustomRootsKey); }
		}

		public static void AddCustomRoot(string rootPath)
		{
			if (string.IsNullOrEmpty(rootPath))
				return;
			var roots = CustomRoots.ToList();
			if (!roots.Contains(rootPath, StringComparer.OrdinalIgnoreCase)) {
				roots.Add(rootPath);
				PropertyService.SetList(CustomRootsKey, roots);
			}
		}

		public static void RemoveCustomRoot(string rootPath)
		{
			var roots = CustomRoots.Where(r => !string.Equals(r, rootPath, StringComparison.OrdinalIgnoreCase)).ToList();
			PropertyService.SetList(CustomRootsKey, roots);
		}

		/// <summary>
		/// Enumerates every SDK root we can find: well-known system install locations, the SDK
		/// this OpenDevelop process itself was launched under (via launch.sh's DOTNET_ROOT), and
		/// any custom paths the user has added. Duplicate roots (same resolved directory) are
		/// collapsed to a single entry.
		/// </summary>
		public static IReadOnlyList<DotNetSdkInfo> DiscoverSdks()
		{
			var results = new List<DotNetSdkInfo>();
			var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			void TryAdd(string rootPath, string label, DotNetSdkOrigin origin)
			{
				if (string.IsNullOrEmpty(rootPath))
					return;
				var normalized = NormalizeRoot(rootPath);
				if (normalized == null || !seenRoots.Add(normalized))
					return;
				var info = TryDescribeRoot(normalized, label, origin);
				if (info != null)
					results.Add(info);
			}

			// Bundled: the SDK this OpenDevelop process itself is running under (launch.sh sets
			// DOTNET_ROOT before "dotnet run" starts the app), so it's always discoverable even
			// though it usually lives at a non-standard path (e.g. a cloned librewpf checkout)
			// that the system-install candidates below would never find.
			string bundledRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
			TryAdd(bundledRoot, "OpenDevelop Bundled SDK", DotNetSdkOrigin.Bundled);

			// Whatever bare "dotnet" resolves to via PATH is what every terminal/script on this
			// machine means by "the system SDK" - added first so ResolveEffectiveSdk's "first
			// System entry" fallback matches that, not just whichever well-known path happens to
			// be first in the list below (machines commonly have several System-origin SDKs
			// installed side by side - e.g. an old installer-script copy under ~/.dotnet alongside
			// a newer Homebrew one - and only one of them is what "dotnet" on PATH actually means).
			TryAdd(ResolvePathDotnetRoot(), "System (PATH default)", DotNetSdkOrigin.System);

			// Other well-known system install locations, for visibility/selection even when they
			// aren't the PATH default (same candidates MinimalMSBuildEngine used to probe
			// one-at-a-time and stop at the first hit; here we want all of them).
			string[] systemCandidates = {
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/.dotnet",
				"/usr/local/share/dotnet",
				"/opt/homebrew/opt/dotnet/libexec",
			};
			foreach (var candidate in systemCandidates)
				TryAdd(candidate, "System", DotNetSdkOrigin.System);

			// /usr/local/bin/dotnet is typically a symlink into one of the above; resolve it to
			// its real target so it collapses into the same entry instead of appearing twice.
			const string usrLocalBinDotnet = "/usr/local/bin/dotnet";
			if (File.Exists(usrLocalBinDotnet)) {
				try {
					var resolved = new FileInfo(usrLocalBinDotnet).ResolveLinkTarget(true)?.FullName;
					if (resolved != null)
						TryAdd(Path.GetDirectoryName(resolved), "System", DotNetSdkOrigin.System);
				} catch (IOException) {
					// Not a symlink, or link target doesn't exist - ignore.
				}
			}

			foreach (var custom in CustomRoots)
				TryAdd(custom, "Custom", DotNetSdkOrigin.Custom);

			return results;
		}

		/// <summary>
		/// Finds the "dotnet" executable that a plain "dotnet" command would run (searching PATH
		/// like a shell would) and fully resolves any symlink chain to the real DOTNET_ROOT for
		/// "the system SDK".
		/// </summary>
		static string ResolvePathDotnetRoot()
		{
			string pathVar = Environment.GetEnvironmentVariable("PATH");
			if (string.IsNullOrEmpty(pathVar))
				return null;
			foreach (var dir in pathVar.Split(Path.PathSeparator)) {
				if (string.IsNullOrEmpty(dir))
					continue;
				string candidate = Path.Combine(dir, "dotnet");
				if (!File.Exists(candidate))
					continue;
				string resolved;
				try {
					resolved = new FileInfo(candidate).ResolveLinkTarget(true)?.FullName ?? candidate;
				} catch (IOException) {
					resolved = candidate;
				}
				string root = Path.GetDirectoryName(resolved);
				if (Directory.Exists(Path.Combine(root, "sdk")))
					return root;
				// Homebrew's formula layout splits the package: the "dotnet" binary symlink
				// resolves into <Cellar>/<version>/bin/dotnet, but the actual SDK/runtime tree
				// (with "sdk/", "shared/", etc.) lives in the sibling <Cellar>/<version>/libexec.
				string siblingLibexec = Path.Combine(Path.GetDirectoryName(root) ?? "", "libexec");
				if (Directory.Exists(Path.Combine(siblingLibexec, "sdk")))
					return siblingLibexec;
				return root;
			}
			return null;
		}

		static string NormalizeRoot(string rootPath)
		{
			try {
				return Path.GetFullPath(rootPath).TrimEnd('/', '\\');
			} catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException || ex is NotSupportedException) {
				return null;
			}
		}

		static DotNetSdkInfo TryDescribeRoot(string rootPath, string label, DotNetSdkOrigin origin)
		{
			string dotnetExe = Path.Combine(rootPath, "dotnet");
			string sdkDir = Path.Combine(rootPath, "sdk");
			if (!File.Exists(dotnetExe) || !Directory.Exists(sdkDir))
				return null;

			var versions = Directory.GetDirectories(sdkDir)
				.Select(Path.GetFileName)
				.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (versions.Count == 0)
				return null;

			return new DotNetSdkInfo {
				Label = $"{label} (.NET SDK {versions[versions.Count - 1]})",
				RootPath = rootPath,
				DotnetExecutablePath = dotnetExe,
				InstalledSdkVersions = versions,
				HighestSdkVersion = versions[versions.Count - 1],
				Origin = origin
			};
		}

		/// <summary>
		/// Resolves the SDK to actually use: the user's stored selection if it still exists on
		/// disk, otherwise the system default (falling back to a bare "dotnet"/PATH resolution if
		/// even that can't be found - matching the previous fallback behavior).
		/// </summary>
		public static DotNetSdkInfo ResolveEffectiveSdk()
		{
			var discovered = DiscoverSdks();
			string selected = SelectedSdkRootPath;
			if (!string.IsNullOrEmpty(selected)) {
				var match = discovered.FirstOrDefault(s => string.Equals(s.RootPath, NormalizeRoot(selected), StringComparison.OrdinalIgnoreCase));
				if (match != null)
					return match;
			}

			var systemDefault = discovered.FirstOrDefault(s => s.Origin == DotNetSdkOrigin.System);
			if (systemDefault != null)
				return systemDefault;

			return new DotNetSdkInfo {
				Label = "PATH (unresolved)",
				RootPath = null,
				DotnetExecutablePath = "dotnet",
				HighestSdkVersion = null,
				Origin = DotNetSdkOrigin.System
			};
		}

		/// <summary>
		/// Builds the same set of environment variables launch.sh sets for this app's own process,
		/// but pointed at the given SDK - so a build/debug/test child process gets a fully
		/// deterministic, self-consistent SDK/MSBuild toolset regardless of what the parent
		/// process itself inherited.
		/// </summary>
		public static IReadOnlyDictionary<string, string> GetEnvironmentVariablesFor(DotNetSdkInfo sdk)
		{
			var result = new Dictionary<string, string>();
			if (sdk?.RootPath == null || sdk.HighestSdkVersion == null)
				return result;

			string sdkVersionDir = Path.Combine(sdk.RootPath, "sdk", sdk.HighestSdkVersion);
			result["DOTNET_ROOT"] = sdk.RootPath;
			result["DOTNET_HOST_PATH"] = sdk.DotnetExecutablePath;
			result["MSBuildSDKsPath"] = Path.Combine(sdkVersionDir, "Sdks");
			result["MSBuildExtensionsPath"] = sdkVersionDir;
			result["MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET"] = Path.Combine(sdkVersionDir, "SdkResolvers");
			result["MSBUILD_NUGET_PATH"] = sdkVersionDir;
			return result;
		}
	}
}
