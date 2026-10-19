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
				error = $"No SDK version was found under:\n{sdkDirectory}";
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
			var names = Directory.GetDirectories(sdkDir)
				.Select(Path.GetFileName)
				.Where(v => v != null)
				.ToList();
			var versions = names
				.Where(v => !v.Contains('-'))
				.OrderBy(v => Version.TryParse(v, out var ver) ? ver : new Version(0, 0))
				.ToList();
			// Pre-releases are kept apart: they never become the IDE's SDK, but a project targeting a
			// framework no stable SDK supports yet (net11.0 before .NET 11 ships) can only be built with one.
			var previews = names
				.Where(v => v.Contains('-') && Version.TryParse(v.Split('-')[0], out _))
				.OrderBy(v => v, PrereleaseVersionComparer.Instance)
				.ToList();
			if (versions.Count == 0 && previews.Count == 0)
				return null;

			string highest = versions.Count > 0 ? versions[versions.Count - 1] : previews[previews.Count - 1];
			return new DotNetSdkInfo {
				Label = versions.Count > 0
					? $"{label} (.NET SDK {highest})"
					: $"{label} (.NET SDK {highest}, preview only)",
				RootPath = rootPath,
				DotnetExecutablePath = dotnetExe,
				InstalledSdkVersions = versions,
				PreviewSdkVersions = previews,
				HighestSdkVersion = highest,
				Origin = origin,
				Architecture = DetectHostArchitecture(dotnetExe)
			};
		}

		/// <summary>
		/// Reads a native executable's architecture straight out of its own header rather than
		/// trusting file size/timestamp or assuming AnyCPU - the .NET host executable itself, and
		/// some of the assemblies an SDK ships (e.g. Microsoft.Build.NuGetSdkResolver.dll, built
		/// ReadyToRun), are architecture-specific even though most managed SDK assemblies are
		/// portable IL. Returns null for an unrecognized/unreadable image rather than guessing -
		/// callers must treat that as "unknown", not as a match.
		///
		/// All three host formats have to be understood, not just PE: this originally read the PE
		/// IMAGE_FILE_HEADER.Machine field only, so on macOS (where "dotnet" is a Mach-O binary)
		/// and Linux (ELF) every discovered SDK came back with a null Architecture. That made
		/// <see cref="ResolveEffectiveSdkForInProcessHosting()"/> - which keeps only SDKs whose
		/// Architecture equals this process's - return null on every non-Windows machine, so
		/// MSBuildInternals.InitializeMSBuildEnvironment() bailed out at
		/// NoCompatibleInProcessSdkFound before it ever pointed the NuGet SDK-resolver manifest at
		/// a real resolver. The shipped manifest then still held the NuGet package's own relative
		/// &lt;Path&gt;, naming a DLL this app deliberately does not bundle, and every SDK-style
		/// project load failed with "Could not load SDK Resolver. A manifest file exists, but the
		/// path to the SDK Resolver DLL file could not be found."
		/// </summary>
		public static Architecture? DetectHostArchitecture(string peFilePath)
		{
			try {
				using var stream = File.OpenRead(peFilePath);
				using var reader = new BinaryReader(stream);
				if (stream.Length < 0x40)
					return null;
				uint magic = reader.ReadUInt32();
				switch (magic) {
					case 0xFEEDFACF: // MH_MAGIC_64 (little-endian host - every Mach-O we care about)
					case 0xFEEDFACE: // MH_MAGIC (32-bit)
						return MachOArchitecture(reader.ReadUInt32());
					case 0xBEBAFECA: // FAT_MAGIC, stored big-endian - a universal binary
					case 0xBFBAFECA: // FAT_MAGIC_64
						return FatMachOArchitecture(stream, reader);
					case 0x464C457F: // "\x7FELF"
						return ElfArchitecture(stream, reader);
				}
				stream.Position = 0x3C;
				int peHeaderOffset = reader.ReadInt32();
				if (peHeaderOffset <= 0 || peHeaderOffset + 6 > stream.Length)
					return null;
				stream.Position = peHeaderOffset;
				if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
					return null;
				return PEArchitecture(reader.ReadUInt16());
			} catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is EndOfStreamException) {
				return null;
			}
		}

		static Architecture? PEArchitecture(ushort machine)
		{
			switch (machine) {
				case 0x8664: // IMAGE_FILE_MACHINE_AMD64
					return Architecture.X64;
				case 0xAA64: // IMAGE_FILE_MACHINE_ARM64
					return Architecture.Arm64;
				case 0x014c: // IMAGE_FILE_MACHINE_I386
					return Architecture.X86;
				case 0x01c4: // IMAGE_FILE_MACHINE_ARMNT
					return Architecture.Arm;
				default:
					return null;
			}
		}

		static Architecture? MachOArchitecture(uint cpuType)
		{
			switch (cpuType) {
				case 0x01000007: // CPU_TYPE_X86_64
					return Architecture.X64;
				case 0x0100000C: // CPU_TYPE_ARM64
					return Architecture.Arm64;
				case 0x00000007: // CPU_TYPE_X86
					return Architecture.X86;
				case 0x0000000C: // CPU_TYPE_ARM
					return Architecture.Arm;
				default:
					return null;
			}
		}

		/// <summary>
		/// A universal binary contains several slices; report this process's own architecture when
		/// one of them provides it (that is the slice macOS would actually run here), otherwise the
		/// first slice we recognize.
		/// </summary>
		static Architecture? FatMachOArchitecture(Stream stream, BinaryReader reader)
		{
			// The fat header and its arch entries are big-endian regardless of the slices.
			uint count = ReadUInt32BigEndian(reader);
			if (count == 0 || count > 64)
				return null;
			Architecture? first = null;
			for (uint i = 0; i < count; i++) {
				if (stream.Position + 4 > stream.Length)
					break;
				var arch = MachOArchitecture(ReadUInt32BigEndian(reader));
				if (arch != null && arch == RuntimeInformation.ProcessArchitecture)
					return arch;
				if (first == null)
					first = arch;
				// Skip the rest of the fat_arch entry (cpusubtype, offset, size, align).
				stream.Position += 16;
			}
			return first;
		}

		static Architecture? ElfArchitecture(Stream stream, BinaryReader reader)
		{
			stream.Position = 5;
			bool littleEndian = reader.ReadByte() != 2; // EI_DATA: 2 = ELFDATA2MSB
			stream.Position = 0x12;
			ushort machine = reader.ReadUInt16();
			if (!littleEndian)
				machine = (ushort)((machine >> 8) | (machine << 8));
			switch (machine) {
				case 0x3E: // EM_X86_64
					return Architecture.X64;
				case 0xB7: // EM_AARCH64
					return Architecture.Arm64;
				case 0x03: // EM_386
					return Architecture.X86;
				case 0x28: // EM_ARM
					return Architecture.Arm;
				default:
					return null;
			}
		}

		static uint ReadUInt32BigEndian(BinaryReader reader)
		{
			return ((uint)reader.ReadByte() << 24) | ((uint)reader.ReadByte() << 16)
				| ((uint)reader.ReadByte() << 8) | reader.ReadByte();
		}

		/// <summary>
		/// Resolves the SDK to actually use: the user's stored selection if it still exists on
		/// disk and is supported (.NET 10+), otherwise the highest supported system default.
		/// Returns <c>null</c> when no supported SDK exists; callers must report that, not fall back.
		/// </summary>
		public static DotNetSdkInfo ResolveEffectiveSdk()
		{
			return ResolveEffectiveSdk(DiscoverSdks(), SelectedSdkRootPath);
		}

		internal static DotNetSdkInfo ResolveEffectiveSdk(IReadOnlyList<DotNetSdkInfo> discovered)
		{
			return ResolveEffectiveSdk(discovered, SelectedSdkRootPath);
		}

		/// <summary>
		/// Resolves the SDK to actually use from an already-discovered set, for a given preferred
		/// root. A preference is honoured only when it names a supported SDK; otherwise a supported
		/// one is chosen, and <c>null</c> when there is none - there is deliberately no fallback to
		/// an older major version, which is what silently produced the .NET 9 evaluation behind
		/// issue #19.
		/// </summary>
		public static DotNetSdkInfo ResolveEffectiveSdk(IReadOnlyList<DotNetSdkInfo> discovered, string preferredRoot)
		{
			if (discovered == null)
				throw new ArgumentNullException(nameof(discovered));
			string selected = preferredRoot;
			if (!string.IsNullOrEmpty(selected)) {
				var match = discovered.FirstOrDefault(s => IsSupportedSdk(s) && SameRoot(s.RootPath, selected));
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
			// System-origin SDKs win, but a supported Bundled/Custom one is still better than none
			// (the IDE's own SDK 10 beside a system SDK 9 is a valid setup), so rank rather than filter.
			var systemDefault = discovered
				.Where(IsSupportedSdk)
				.OrderByDescending(s => s.Origin == DotNetSdkOrigin.System)
				.ThenByDescending(s => Version.TryParse(s.HighestSdkVersion?.Split('-')[0], out var v) ? v : new Version(0, 0))
				.FirstOrDefault();
			if (systemDefault != null)
				return systemDefault;

			// Deliberately no synthetic bare-"dotnet" fallback any more: PATH would resolve straight
			// back to the unsupported SDK, which is the silent .NET 9 evaluation of issue #19.
			return null;
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
			return ResolveEffectiveSdkForInProcessHosting(DiscoverSdks(), SelectedSdkRootPath);
		}

		internal static DotNetSdkInfo ResolveEffectiveSdkForInProcessHosting(IReadOnlyList<DotNetSdkInfo> discovered)
		{
			return ResolveEffectiveSdkForInProcessHosting(discovered, SelectedSdkRootPath);
		}

		/// <summary>
		/// Like <see cref="ResolveEffectiveSdkForInProcessHosting()"/>, from an already-discovered
		/// set and for a given preferred root: the candidates are restricted to SDKs this process's
		/// architecture can load in-process <em>and</em> that are supported, and the result is null
		/// when none is - never a mismatched or too-old SDK that would crash or confuse evaluation.
		/// </summary>
		public static DotNetSdkInfo ResolveEffectiveSdkForInProcessHosting(IReadOnlyList<DotNetSdkInfo> discovered, string preferredRoot)
		{
			if (discovered == null)
				throw new ArgumentNullException(nameof(discovered));

			var processArchitecture = RuntimeInformation.ProcessArchitecture;
			var compatible = discovered.Where(s => s.Architecture == processArchitecture && IsSupportedSdk(s)).ToList();
			if (compatible.Count == 0)
				return null;

			string selected = preferredRoot;
			if (!string.IsNullOrEmpty(selected)) {
				var match = compatible.FirstOrDefault(s => SameRoot(s.RootPath, selected));
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
		/// What the machine's .NET SDK situation looks like for this IDE, as plain facts a caller can
		/// report. Deliberately separate from the reporting so the decision can be unit-tested
		/// without an IDE, an output pad or a live property service.
		/// </summary>
		public sealed class DotNetSdkAvailability
		{
			/// <summary>An SDK this process's own architecture can load in-process, or null.</summary>
			public DotNetSdkInfo InProcessSdk { get; init; }

			/// <summary>The discovered root holding a supported .NET SDK (10 or newer).</summary>
			public DotNetSdkInfo SupportedSdkRoot { get; init; }

			/// <summary>The supported SDK version found in <see cref="SupportedSdkRoot"/>, or null.</summary>
			public string SupportedSdkVersion { get; init; }

			/// <summary>Whether Tools &gt; Options &gt; .NET SDK names a root at all.</summary>
			public bool PreferredRootSet { get; init; }

			/// <summary>The preferred root as discovered, or null when it is set but no longer installed.</summary>
			public DotNetSdkInfo PreferredSdk { get; init; }

			/// <summary>True when a preferred root is set but nothing at that path was discovered.</summary>
			public bool PreferredRootMissing => PreferredRootSet && PreferredSdk == null;

			/// <summary>
			/// True when nothing here needs the user's attention. Note this is about the facts above
			/// only: a healthy result still does not promise a build will succeed, which is why the
			/// report says so rather than treating "healthy" as "fine".
			/// </summary>
			public bool IsHealthy => InProcessSdk != null && SupportedSdkRoot != null && !PreferredRootMissing && !PreferredRootUnsupported;

			/// <summary>True when the preferred root exists but holds no supported SDK, or one of another architecture than this process, so it is ignored.</summary>
			public bool PreferredRootUnsupported => PreferredSdk != null
				&& (!IsSupportedSdk(PreferredSdk) || PreferredSdk.Architecture != RuntimeInformation.ProcessArchitecture);
		}

		/// <summary>
		/// Describes the .NET SDK situation for a set of already-discovered roots.
		/// </summary>
		/// <param name="discovered">Roots from <see cref="DiscoverSdks()"/>.</param>
		/// <param name="preferredRoot">The root named under Tools &gt; Options &gt; .NET SDK, or null/empty.</param>
		/// <param name="noCompatibleInProcessSdkFound">
		/// The definitive post-evaluation signal from <c>MSBuildInternals</c>: when set, in-process
		/// project evaluation has already failed for real, whatever discovery says.
		/// </param>
		public static DotNetSdkAvailability DescribeAvailability(
			IReadOnlyList<DotNetSdkInfo> discovered,
			string preferredRoot,
			bool noCompatibleInProcessSdkFound = false)
		{
			if (discovered == null)
				throw new ArgumentNullException(nameof(discovered));

			var inProcess = noCompatibleInProcessSdkFound
				? null
				: ResolveEffectiveSdkForInProcessHosting(discovered, preferredRoot);

			var supportedSdkRoot = discovered.FirstOrDefault(IsSupportedSdk);

			bool preferredSet = !string.IsNullOrEmpty(preferredRoot);
			var preferred = preferredSet
				? discovered.FirstOrDefault(s => SameRoot(s.RootPath, preferredRoot))
				: null;

			return new DotNetSdkAvailability {
				InProcessSdk = inProcess,
				SupportedSdkRoot = supportedSdkRoot,
				SupportedSdkVersion = supportedSdkRoot?.InstalledSdkVersions.LastOrDefault(IsSupportedSdkVersion),
				PreferredRootSet = preferredSet,
				PreferredSdk = preferred
			};
		}

		/// <summary>Major version of an SDK version string ("10.0.401" -&gt; 10), or -1 if unparsable.</summary>
		public static int SdkMajorVersion(string version)
		{
			if (string.IsNullOrEmpty(version))
				return -1;
			int dot = version.IndexOf('.');
			return int.TryParse(dot < 0 ? version : version.Substring(0, dot), out var major) ? major : -1;
		}

		static bool IsSupportedSdk(DotNetSdkInfo sdk)
		{
			return sdk != null && sdk.InstalledSdkVersions.Any(IsSupportedSdkVersion);
		}

		static bool IsSupportedSdkVersion(string version)
		{
			return SdkMajorVersion(version) >= 10;
		}

		/// <summary>Whether two SDK root paths name the same directory, ignoring a trailing separator.</summary>
		public static bool SameRoot(string left, string right)
		{
			if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
				return false;
			// Normalise both sides (as the old NormalizeRoot comparison did) so mixed separators,
			// "..", and relative stored preferences still match.
			string l = NormalizeRoot(left) ?? left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			string r = NormalizeRoot(right) ?? right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			return string.Equals(l, r, StringComparison.OrdinalIgnoreCase);
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
			// Roots with a stable SDK only: a pre-release-only root (a dotnetup RC) would otherwise
			// outrank every stable one by version, and need not carry the runtime the child uses. Such
			// roots were not discovered at all before pre-release SDKs were tracked; this keeps that.
			return DiscoverSdks()
				.Where(s => s.Architecture == targetArchitecture && s.InstalledSdkVersions.Count > 0)
				.OrderByDescending(s => Version.TryParse(s.HighestSdkVersion?.Split('-')[0], out var v) ? v : new Version(0, 0))
				.Select(s => s.DotnetExecutablePath)
				.FirstOrDefault();
		}

		/// <summary>The SDK root, and the exact SDK version in it, to build and run one project with.</summary>
		public sealed class SdkChoice
		{
			public DotNetSdkInfo Sdk { get; init; }

			/// <summary>The SDK version folder to use, e.g. "10.0.401" or "11.0.100-rc.1.26425.128".</summary>
			public string Version { get; init; }

			/// <summary>True when this is the IDE's effective SDK at its highest version - the choice every project got before.</summary>
			public bool IsDefault { get; init; }

			public bool IsPreview => Version != null && Version.Contains('-');
		}

		/// <summary>
		/// The .NET major version a target framework moniker needs ("net11.0" and "net11.0-windows" -&gt; 11,
		/// "netcoreapp3.1" -&gt; 3), or null for one no .NET SDK version gates (net48, netstandard2.0, empty).
		/// </summary>
		public static int? TargetFrameworkMajor(string targetFramework)
		{
			if (string.IsNullOrWhiteSpace(targetFramework))
				return null;
			var match = System.Text.RegularExpressions.Regex.Match(targetFramework.Trim(), @"^net(?:coreapp)?(\d+)\.\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			return match.Success && int.TryParse(match.Groups[1].Value, out var major) ? major : null;
		}

		/// <summary>
		/// The SDK to build and run a project with that targets <paramref name="targetFrameworks"/>.
		/// The IDE's effective SDK whenever it is new enough - the common case, unchanged. Only when the
		/// project needs a newer .NET major than that (net11.0 on an SDK 10 setup) does it look further:
		/// first for a STABLE SDK of that major in any discovered root, then - since a framework can be
		/// targeted before its SDK ships - for a PRE-RELEASE one. With none, the effective SDK is kept
		/// and the build reports NETSDK1045 itself. This choice is for the out-of-process build and the
		/// launched program only; the IDE's in-process MSBuild stays on the effective SDK.
		/// </summary>
		public static SdkChoice ResolveSdkForTargetFrameworks(IReadOnlyList<DotNetSdkInfo> discovered, string preferredRoot, IEnumerable<string> targetFrameworks)
		{
			if (discovered == null)
				throw new ArgumentNullException(nameof(discovered));
			var effective = ResolveEffectiveSdk(discovered, preferredRoot);
			// No supported (.NET 10+) SDK at all: no choice, exactly as before - callers report it. Never
			// fall back to an older stable SDK or to a pre-release one here (that is issue #19's trap).
			if (effective == null)
				return null;
			SdkChoice Default() => new SdkChoice { Sdk = effective, Version = effective.HighestSdkVersion, IsDefault = true };

			// From here every candidate is newer than the effective SDK, which is itself .NET 10+.
			int? required = (targetFrameworks ?? Enumerable.Empty<string>()).Select(TargetFrameworkMajor).Max();
			if (required == null || SdkMajorVersion(effective.HighestSdkVersion) >= required)
				return Default();

			var stable = discovered
				.SelectMany(s => s.InstalledSdkVersions.Select(v => (Sdk: s, Version: v)))
				.Where(c => SdkMajorVersion(c.Version) >= required && System.Version.TryParse(c.Version, out _))
				.OrderByDescending(c => SameRoot(c.Sdk.RootPath, preferredRoot))
				.ThenByDescending(c => c.Sdk.Origin == DotNetSdkOrigin.System)
				.ThenByDescending(c => System.Version.Parse(c.Version))
				.FirstOrDefault();
			if (stable.Sdk != null)
				return new SdkChoice { Sdk = stable.Sdk, Version = stable.Version };

			var preview = discovered
				.SelectMany(s => s.PreviewSdkVersions.Select(v => (Sdk: s, Version: v)))
				.Where(c => SdkMajorVersion(c.Version) >= required)
				.OrderByDescending(c => SameRoot(c.Sdk.RootPath, preferredRoot))
				.ThenByDescending(c => c.Sdk.Origin == DotNetSdkOrigin.System)
				.ThenByDescending(c => c.Version, PrereleaseVersionComparer.Instance)
				.FirstOrDefault();
			if (preview.Sdk != null)
				return new SdkChoice { Sdk = preview.Sdk, Version = preview.Version };

			return Default();
		}

		/// <summary>
		/// Orders pre-release SDK versions the way they were released: by the numeric version, then by
		/// the label's dot-separated parts, numerically where both are numbers ("preview.10" after
		/// "preview.7", which an ordinal string compare gets wrong) and ordinally otherwise ("rc" after
		/// "preview").
		/// </summary>
		public sealed class PrereleaseVersionComparer : IComparer<string>
		{
			public static readonly PrereleaseVersionComparer Instance = new PrereleaseVersionComparer();

			public int Compare(string x, string y)
			{
				if (ReferenceEquals(x, y)) return 0;
				if (x == null) return -1;
				if (y == null) return 1;
				string[] xs = x.Split(new[] { '-' }, 2), ys = y.Split(new[] { '-' }, 2);
				Version.TryParse(xs[0], out var xv);
				Version.TryParse(ys[0], out var yv);
				int byVersion = Comparer<Version>.Default.Compare(xv, yv);
				if (byVersion != 0)
					return byVersion;
				string[] xl = xs.Length > 1 ? xs[1].Split('.') : Array.Empty<string>();
				string[] yl = ys.Length > 1 ? ys[1].Split('.') : Array.Empty<string>();
				for (int i = 0; i < Math.Min(xl.Length, yl.Length); i++) {
					int part = int.TryParse(xl[i], out var xn) && int.TryParse(yl[i], out var yn)
						? xn.CompareTo(yn)
						: string.CompareOrdinal(xl[i], yl[i]);
					if (part != 0)
						return part;
				}
				return xl.Length.CompareTo(yl.Length);
			}
		}

		// ResolveSdkForProject runs once per project on paths that must stay cheap (the one-process
		// build and batched reference resolution ask it for every project of a solution), and
		// DiscoverSdks touches the disk for every root. Reuse one discovery for a short while, keyed
		// on the settings it reads, so a changed selection or custom root is seen immediately.
		static readonly object discoveryCacheLock = new object();
		// Monotonic milliseconds (Environment.TickCount64), so a wall-clock change cannot keep an entry alive.
		static (long At, string Key, IReadOnlyList<DotNetSdkInfo> Sdks) discoveryCache;
		const long DiscoveryCacheLifetimeMilliseconds = 5000;

		static IReadOnlyList<DotNetSdkInfo> DiscoverSdksCached(string selectedRoot, IReadOnlyList<string> customRoots)
		{
			string key = (selectedRoot ?? "") + "\n" + string.Join("\n", customRoots);
			lock (discoveryCacheLock) {
				if (discoveryCache.Sdks != null && discoveryCache.Key == key
				    && Environment.TickCount64 - discoveryCache.At < DiscoveryCacheLifetimeMilliseconds)
					return discoveryCache.Sdks;
			}
			var sdks = DiscoverSdks(customRoots);
			lock (discoveryCacheLock)
				discoveryCache = (Environment.TickCount64, key, sdks);
			return sdks;
		}

		/// <summary><see cref="ResolveSdkForTargetFrameworks"/> for a loaded project (its TargetFramework / TargetFrameworks).</summary>
		public static SdkChoice ResolveSdkForProject(IProject project)
		{
			var frameworks = new List<string>();
			if (project is MSBuildBasedProject msbuild) {
				string single = msbuild.GetEvaluatedProperty("TargetFramework");
				if (!string.IsNullOrWhiteSpace(single))
					frameworks.Add(single);
				string multiple = msbuild.GetEvaluatedProperty("TargetFrameworks");
				if (!string.IsNullOrWhiteSpace(multiple))
					frameworks.AddRange(multiple.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()));
			}
			string selected = SelectedSdkRootPath;
			return ResolveSdkForTargetFrameworks(DiscoverSdksCached(selected, CustomRoots), selected, frameworks);
		}

		/// <summary>
		/// Builds the same set of environment variables launch.sh sets for this app's own process,
		/// but pointed at the given SDK - so a build/debug/test child process gets a fully
		/// deterministic, self-consistent SDK/MSBuild toolset regardless of what the parent
		/// process itself inherited.
		/// </summary>
		public static IReadOnlyDictionary<string, string> GetEnvironmentVariablesFor(DotNetSdkInfo sdk)
		{
			return GetEnvironmentVariablesFor(sdk, sdk?.HighestSdkVersion);
		}

		/// <summary>Like <see cref="GetEnvironmentVariablesFor(DotNetSdkInfo)"/>, for a specific SDK version folder in that root.</summary>
		public static IReadOnlyDictionary<string, string> GetEnvironmentVariablesFor(DotNetSdkInfo sdk, string sdkVersion)
		{
			var result = new Dictionary<string, string>();
			if (sdk?.RootPath == null || sdkVersion == null)
				return result;

			string sdkVersionDir = Path.Combine(sdk.RootPath, "sdk", sdkVersion);
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
