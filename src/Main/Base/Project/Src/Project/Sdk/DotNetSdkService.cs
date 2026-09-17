using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Project.Sdk
{
	/// <summary>
	/// Discovers installed .NET SDK roots (system installs, OpenDevelop's own bundled/dev SDK, and
	/// user-added custom paths) and resolves which one the user has selected to build/debug/test
	/// project files with. This is the single authoritative source that MinimalMSBuildEngine,
	/// DapSession, and MtpServerProcess should all consult, instead of each independently guessing
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

		/// <summary>The "dotnet" host executable's file name on this platform ("dotnet.exe" on
		/// Windows). <see cref="File.Exists(string)"/> does no PATHEXT-style extension resolution
		/// the way process launching does, so every root-description check below must use this
		/// exact name - checking for a bare "dotnet" makes every SDK root candidate File.Exists
		/// check fail on Windows, which silently disabled this entire service (DiscoverSdks()
		/// always returning empty) for every Windows install, dev or packaged alike.</summary>
		static readonly string DotnetExeFileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

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

		/// <summary>Validates a user-selected DOTNET_ROOT and describes the SDK it contains.</summary>
		public static bool TryDescribeCustomRoot(string rootPath, out DotNetSdkInfo sdk, out string error)
		{
			sdk = null;
			error = null;
			if (string.IsNullOrWhiteSpace(rootPath)) {
				error = "No folder was selected.";
				return false;
			}

			string normalized = NormalizeRoot(rootPath);
			if (normalized == null || !Directory.Exists(normalized)) {
				error = $"The folder does not exist:\n{rootPath}";
				return false;
			}
			if (!File.Exists(Path.Combine(normalized, DotnetExeFileName))) {
				error = $"The selected folder is not a .NET SDK root because it does not contain the dotnet executable:\n{normalized}";
				return false;
			}
			string sdkDirectory = Path.Combine(normalized, "sdk");
			if (!Directory.Exists(sdkDirectory)) {
				error = $"The selected folder contains dotnet, but it does not contain an sdk folder:\n{normalized}";
				return false;
			}

			try {
				sdk = TryDescribeRoot(normalized, "Custom", DotNetSdkOrigin.Custom);
			} catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				error = $"The SDK folder could not be read:\n{normalized}\n\n{ex.Message}";
				return false;
			}
			if (sdk == null) {
				error = $"No stable SDK version was found under:\n{sdkDirectory}";
				return false;
			}
			return true;
		}

		/// <summary>
		/// Enumerates every SDK root we can find: well-known system install locations, the SDK
		/// this OpenDevelop process itself was launched under (via launch.sh's DOTNET_ROOT), and
		/// any custom paths the user has added. Duplicate roots (same resolved directory) are
		/// collapsed to a single entry.
		/// </summary>
		public static IReadOnlyList<DotNetSdkInfo> DiscoverSdks()
		{
			return DiscoverSdks(CustomRoots);
		}

		internal static IReadOnlyList<DotNetSdkInfo> DiscoverSdks(IEnumerable<string> customRoots)
		{
			if (customRoots == null)
				throw new ArgumentNullException(nameof(customRoots));
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
			//
			// "dotnet\x64" (and, symmetrically, "dotnet\arm64") is where the .NET SDK installer puts
			// a side-by-side SDK of a DIFFERENT architecture than the machine's native one - e.g.
			// `winget install Microsoft.DotNet.SDK.10 --architecture x64` on this ARM64 machine
			// installed to "Program Files\dotnet\x64\sdk\...", not under the native
			// "Program Files\dotnet\sdk\..." picked up by the plain candidate above. This is exactly
			// the root ResolveEffectiveSdkForInProcessHosting() needs when the native SDK's
			// architecture doesn't match this process's own (see [[nugetsdkresolver-arch-mismatch]]) -
			// without probing it explicitly, a side-by-side install like that is invisible to
			// DiscoverSdks() entirely, even though `dotnet --list-sdks` (which walks a registered-
			// install-location manifest, not just these fixed paths) does find it. See
			// MSBuildInternals.NoCompatibleInProcessSdkFound for the consumer that needed this.
			string programFilesDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
			string[] systemCandidates = {
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/.dotnet",
				programFilesDotnet,
				Path.Combine(programFilesDotnet, "x64"),
				Path.Combine(programFilesDotnet, "arm64"),
				Path.Combine(programFilesDotnet, "x86"),
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

			foreach (var custom in customRoots)
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
				string candidate = Path.Combine(dir, DotnetExeFileName);
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
			string dotnetExe = Path.Combine(rootPath, DotnetExeFileName);
			string sdkDir = Path.Combine(rootPath, "sdk");
			if (!File.Exists(dotnetExe) || !Directory.Exists(sdkDir))
				return null;

			// Order by numeric version (stable releases only, like ResolveEffectiveSdk's version
			// comparison below): plain ordinal sorting puts "9.0.305" above "10.0.200" ('1' < '9'),
			// which wrongly picked an ancient SDK and failed every net10.0 build with NETSDK1045.
			var versions = Directory.GetDirectories(sdkDir)
				.Select(Path.GetFileName)
				.Where(v => v != null && !v.Contains('-'))
				.OrderBy(v => Version.TryParse(v, out var ver) ? ver : new Version(0, 0))
				.Select(v => v)
				.ToList();
			if (versions.Count == 0)
				return null;

			return new DotNetSdkInfo {
				Label = $"{label} (.NET SDK {versions[versions.Count - 1]})",
				RootPath = rootPath,
				DotnetExecutablePath = dotnetExe,
				InstalledSdkVersions = versions,
				HighestSdkVersion = versions[versions.Count - 1],
				Origin = origin,
				Architecture = DetectHostArchitecture(dotnetExe)
			};
		}

		/// <summary>
		/// Reads a PE file's Machine field straight out of its header (offset given by the
		/// e_lfanew pointer at 0x3C, PE signature + IMAGE_FILE_HEADER.Machine 4 bytes later) rather
		/// than trusting file size/timestamp or assuming AnyCPU - the .NET host executable itself,
		/// and some of the assemblies an SDK ships (e.g. Microsoft.Build.NuGetSdkResolver.dll,
		/// built ReadyToRun), are architecture-specific even though most managed SDK assemblies are
		/// portable IL. Returns null for an unrecognized/unreadable machine value rather than
		/// guessing - callers must treat that as "unknown", not as a match.
		/// </summary>
		public static Architecture? DetectHostArchitecture(string peFilePath)
		{
			try {
				using var stream = File.OpenRead(peFilePath);
				using var reader = new BinaryReader(stream);
				if (stream.Length < 0x40)
					return null;
				stream.Position = 0x3C;
				int peHeaderOffset = reader.ReadInt32();
				if (peHeaderOffset <= 0 || peHeaderOffset + 6 > stream.Length)
					return null;
				stream.Position = peHeaderOffset + 4;
				ushort machine = reader.ReadUInt16();
				switch (machine) {
					case 0x8664: // IMAGE_FILE_MACHINE_AMD64
						return System.Runtime.InteropServices.Architecture.X64;
					case 0xAA64: // IMAGE_FILE_MACHINE_ARM64
						return System.Runtime.InteropServices.Architecture.Arm64;
					case 0x014c: // IMAGE_FILE_MACHINE_I386
						return System.Runtime.InteropServices.Architecture.X86;
					case 0x01c4: // IMAGE_FILE_MACHINE_ARMNT
						return System.Runtime.InteropServices.Architecture.Arm;
					default:
						return null;
				}
			} catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				return null;
			}
		}

		/// <summary>
		/// Resolves the SDK to actually use: the user's stored selection if it still exists on
		/// disk, otherwise the system default (falling back to a bare "dotnet"/PATH resolution if
		/// even that can't be found - matching the previous fallback behavior).
		/// </summary>
		public static DotNetSdkInfo ResolveEffectiveSdk()
		{
			return ResolveEffectiveSdk(DiscoverSdks());
		}

		internal static DotNetSdkInfo ResolveEffectiveSdk(IReadOnlyList<DotNetSdkInfo> discovered)
		{
			if (discovered == null)
				throw new ArgumentNullException(nameof(discovered));
			string selected = SelectedSdkRootPath;
			if (!string.IsNullOrEmpty(selected)) {
				var match = discovered.FirstOrDefault(s => string.Equals(s.RootPath, NormalizeRoot(selected), StringComparison.OrdinalIgnoreCase));
				if (match != null)
					return match;
			}

			// Prefer the highest-versioned System-origin SDK, not just the first one discovered:
			// DiscoverSdks() puts ResolvePathDotnetRoot()'s result (the literal "dotnet" on PATH)
			// first specifically so that a plain FirstOrDefault would prefer it - but that silently
			// breaks if PATH resolution comes back empty/differently in a given process's inherited
			// environment (e.g. a child process launched with a trimmed/altered PATH), in which case
			// FirstOrDefault falls through to whatever fixed candidate (like "~/.dotnet") happens to
			// come next in the well-known-paths list, even when that's a much older SDK than another
			// perfectly good one sitting right next to it (observed: an ancient standalone .NET 8 SDK
			// under ~/.dotnet winning over a current Homebrew .NET 10 install, silently breaking
			// evaluation of any net10.0+-targeted project - Microsoft.NET.Sdk from an old SDK doesn't
			// understand newer TFMs/item-default conventions correctly). Version-sort instead so the
			// outcome doesn't depend on PATH-resolution succeeding or on candidate enumeration order.
			var systemDefault = discovered
				.Where(s => s.Origin == DotNetSdkOrigin.System)
				.OrderByDescending(s => Version.TryParse(s.HighestSdkVersion?.Split('-')[0], out var v) ? v : new Version(0, 0))
				.FirstOrDefault();
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
		/// Like <see cref="ResolveEffectiveSdk()"/>, but restricted to SDKs whose host architecture
		/// matches this OpenDevelop process's own (<see cref="RuntimeInformation.ProcessArchitecture"/>).
		/// Use this - never <see cref="ResolveEffectiveSdk()"/> - for anything the IDE loads directly
		/// into its own process (the embedded MSBuild engine's SdkResolvers, in particular): an
		/// out-of-process "dotnet build" child can run under a different architecture than this
		/// process just fine (it's a separate OS process), but a resolver DLL loaded in-process
		/// cannot - a mismatched one is a ReadyToRun image the OS loader rejects outright, not a
		/// slower/JIT fallback. Returns null when no discovered SDK matches this process's
		/// architecture at all, which callers must treat as "no usable SDK" and surface to the user
		/// rather than falling back to a mismatched one that will crash every SDK-style project load.
		/// </summary>
		public static DotNetSdkInfo ResolveEffectiveSdkForInProcessHosting()
		{
			return ResolveEffectiveSdkForInProcessHosting(DiscoverSdks());
		}

		internal static DotNetSdkInfo ResolveEffectiveSdkForInProcessHosting(IReadOnlyList<DotNetSdkInfo> discovered)
		{
			if (discovered == null)
				throw new ArgumentNullException(nameof(discovered));

			var processArchitecture = RuntimeInformation.ProcessArchitecture;
			var compatible = discovered.Where(s => s.Architecture == processArchitecture).ToList();
			if (compatible.Count == 0)
				return null;

			string selected = SelectedSdkRootPath;
			if (!string.IsNullOrEmpty(selected)) {
				var match = compatible.FirstOrDefault(s => string.Equals(s.RootPath, NormalizeRoot(selected), StringComparison.OrdinalIgnoreCase));
				if (match != null)
					return match;
			}

			var systemDefault = compatible
				.Where(s => s.Origin == DotNetSdkOrigin.System)
				.OrderByDescending(s => Version.TryParse(s.HighestSdkVersion?.Split('-')[0], out var v) ? v : new Version(0, 0))
				.FirstOrDefault();
			if (systemDefault != null)
				return systemDefault;

			return compatible
				.OrderByDescending(s => Version.TryParse(s.HighestSdkVersion?.Split('-')[0], out var v) ? v : new Version(0, 0))
				.First();
		}

		/// <summary>
		/// Resolves a "dotnet" host executable matching a specific target architecture - which need
		/// NOT be this process's own (<see cref="RuntimeInformation.ProcessArchitecture"/>). Unlike
		/// <see cref="ResolveEffectiveSdkForInProcessHosting"/>, this is for launching an
		/// out-of-process CHILD (a separate OS process, e.g. an out-of-process designer host
		/// adopting a self-contained app's runtime graph): the .NET installer's side-by-side layout
		/// (<c>%ProgramFiles%\dotnet</c>, plus <c>\x64</c>/<c>\arm64</c>/<c>\x86</c> siblings for
		/// other architectures - see <see cref="DiscoverSdks"/>'s systemCandidates) commonly makes
		/// more than one architecture's SDK available on the same machine, e.g. an ARM64 Windows
		/// install carries an x64 side install for exactly this kind of cross-architecture launch.
		/// Returns null when no installed SDK of that architecture is found.
		/// </summary>
		public static string ResolveDotnetHostForArchitecture(Architecture targetArchitecture)
		{
			return DiscoverSdks()
				.Where(s => s.Architecture == targetArchitecture)
				.OrderByDescending(s => Version.TryParse(s.HighestSdkVersion?.Split('-')[0], out var v) ? v : new Version(0, 0))
				.Select(s => s.DotnetExecutablePath)
				.FirstOrDefault();
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
			// The installed application bundles OpenDevelop.Addin.SdkResolver beside a copy of
			// the .NET resolvers. Prefer that combined folder so external addin projects can use
			// Sdk="OpenDevelop.Addin.Sdk" without fetching a NuGet SDK package.
			var bundledResolvers = Path.Combine(AppContext.BaseDirectory, "SdkResolvers");
			result["MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET"] = Directory.Exists(bundledResolvers)
				? bundledResolvers
				: Path.Combine(sdkVersionDir, "SdkResolvers");
			result["MSBUILD_NUGET_PATH"] = sdkVersionDir;
			return result;
		}
	}
}
