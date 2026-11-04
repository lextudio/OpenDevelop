using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Debugger.AddIn.Ios
{
	/// <summary>
	/// What it takes to debug a .NET 11+ CoreCLR app in the iOS Simulator with the DAP adapter
	/// (doc/technotes/uno-mobile-designer.md, section 4c): the app is started stopped, its runtime
	/// keeps its transport pipes in the app container's tmp directory, and the adapter loads the
	/// app's own mscordbi and DAC once they are retargeted to macOS.
	/// </summary>
	public static class IosSimulatorDebugSupport
	{
		public const string RuntimeIdentifier = "iossimulator-arm64";

		static readonly Regex IosTargetFramework = new Regex(@"^net(\d+)\.\d+-ios", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		/// <summary>True for net11.0-ios and later: earlier iOS TFMs run on Mono, which this debugger cannot debug.</summary>
		public static bool IsDebuggableTargetFramework(string targetFramework)
		{
			var match = IosTargetFramework.Match(targetFramework ?? string.Empty);
			return match.Success && int.Parse(match.Groups[1].Value) >= 11;
		}

		/// <summary>The bundle a simulator build produces: bin/&lt;Configuration&gt;/&lt;tfm&gt;/iossimulator-arm64/&lt;name&gt;.app.</summary>
		public static string FindAppBundle(string projectDirectory, string configuration, string targetFramework, string assemblyName)
		{
			var outputDirectory = Path.Combine(projectDirectory, "bin", configuration, targetFramework, RuntimeIdentifier);
			var expected = Path.Combine(outputDirectory, assemblyName + ".app");
			if (Directory.Exists(expected))
				return expected;
			return Directory.Exists(outputDirectory) ? Directory.GetDirectories(outputDirectory, "*.app").FirstOrDefault() : null;
		}

		/// <summary>
		/// A directory with the app's libmscordbi and libmscordaccore that a macOS process can load.
		/// The bundle's copies are built for the iOS simulator platform; the host's macOS copies of the
		/// same runtime build do not match the iOS runtime's layout. Cached by content, so every app on
		/// one runtime build shares one copy.
		/// </summary>
		public static async Task<string> PrepareDebuggerLibrariesAsync(string appBundle, CancellationToken cancellationToken)
		{
			var dbi = Path.Combine(appBundle, "libmscordbi.dylib");
			var dac = Path.Combine(appBundle, "libmscordaccore.dylib");
			if (!File.Exists(dbi) || !File.Exists(dac))
				throw new FileNotFoundException("The app bundle carries no debugger libraries (libmscordbi.dylib, libmscordaccore.dylib). Debugging needs a Debug build on .NET 11 or later: " + appBundle);

			string hash;
			using (var sha = SHA256.Create()) {
				var bytes = File.ReadAllBytes(dbi).Concat(File.ReadAllBytes(dac)).ToArray();
				hash = Convert.ToHexString(sha.ComputeHash(bytes)).Substring(0, 16).ToLowerInvariant();
			}
			var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "OpenDevelop", "ios-simulator-dbi");
			var directory = Path.Combine(cacheRoot, hash);
			if (Directory.Exists(directory))
				return directory;

			var staging = directory + ".tmp-" + Environment.ProcessId;
			if (Directory.Exists(staging))
				Directory.Delete(staging, true);
			Directory.CreateDirectory(staging);
			foreach (var source in new[] { dbi, dac }) {
				var target = Path.Combine(staging, Path.GetFileName(source));
				await RunAsync("/usr/bin/xcrun", new[] { "vtool", "-set-build-version", "macos", "15.0", "15.0", "-replace", "-output", target, source }, cancellationToken).ConfigureAwait(false);
				// The simulator build names its own directory "$ORIGIN", which dyld does not expand
				if ((await RunAsync("/usr/bin/otool", new[] { "-l", target }, cancellationToken).ConfigureAwait(false)).Contains("path $ORIGIN "))
					await RunAsync("/usr/bin/xcrun", new[] { "install_name_tool", "-rpath", "$ORIGIN", "@loader_path", target }, cancellationToken).ConfigureAwait(false);
				await RunAsync("/usr/bin/codesign", new[] { "--force", "--sign", "-", target }, cancellationToken).ConfigureAwait(false);
			}
			File.WriteAllText(Path.Combine(staging, ".complete"), appBundle);
			// The final directory only ever appears through this one move, already complete, and another IDE process
			// may be attaching from it: never replace it. Losing the race to publish means it is there to reuse.
			try {
				if (!Directory.Exists(directory))
					Directory.Move(staging, directory);
			} catch (IOException) when (Directory.Exists(directory)) {
			}
			if (Directory.Exists(staging))
				Directory.Delete(staging, true);
			return directory;
		}

		/// <summary>The bundle's CFBundleIdentifier (its Info.plist is often binary, which plutil reads).</summary>
		public static async Task<string> ReadBundleIdAsync(string appBundle, CancellationToken cancellationToken)
		{
			var bundleId = (await RunAsync("/usr/bin/plutil", new[] { "-extract", "CFBundleIdentifier", "raw", "-o", "-", Path.Combine(appBundle, "Info.plist") }, cancellationToken).ConfigureAwait(false)).Trim();
			if (bundleId.Length == 0)
				throw new InvalidOperationException("The app bundle has no CFBundleIdentifier: " + appBundle);
			return bundleId;
		}

		/// <summary>Boots the simulator (a cold boot can take a minute) and installs the app.</summary>
		public static async Task BootAndInstallAsync(string udid, string appBundle, CancellationToken cancellationToken)
		{
			await RunSimctlAsync(cancellationToken, "bootstatus", udid, "-b").ConfigureAwait(false);
			await RunSimctlAsync(cancellationToken, "install", udid, appBundle).ConfigureAwait(false);
		}

		/// <summary>Starts the app; with <paramref name="stopped"/> it waits stopped (SIGSTOP) before its runtime loads.</summary>
		public static async Task<int> LaunchAsync(string udid, string bundleId, bool stopped, CancellationToken cancellationToken)
		{
			var arguments = new List<string> { "launch", "--terminate-running-process" };
			if (stopped)
				arguments.Add("--wait-for-debugger");
			arguments.Add(udid);
			arguments.Add(bundleId);
			var output = await RunSimctlAsync(cancellationToken, arguments.ToArray()).ConfigureAwait(false);
			return ParseLaunchedProcessId(output, bundleId);
		}

		/// <summary>Best effort: ends the app, e.g. one left stopped for a debugger that will not come.</summary>
		public static async Task TerminateAsync(string udid, string bundleId)
		{
			try {
				await RunSimctlAsync(CancellationToken.None, "terminate", udid, bundleId).ConfigureAwait(false);
			} catch (Exception) {
				// The simulator may be gone already; the original failure is what gets reported
			}
		}

		/// <summary>simctl launch prints "&lt;bundle id&gt;: &lt;pid&gt;".</summary>
		public static int ParseLaunchedProcessId(string output, string bundleId)
		{
			var match = Regex.Match(output ?? string.Empty, Regex.Escape(bundleId) + @":\s*(\d+)");
			if (!match.Success)
				throw new InvalidOperationException("simctl launch did not report a process id: " + output);
			return int.Parse(match.Groups[1].Value);
		}

		/// <summary>The app's data container tmp directory, where its runtime creates clr-debug-pipe-*.</summary>
		public static async Task<string> GetContainerTempDirectoryAsync(string udid, string bundleId, CancellationToken cancellationToken)
		{
			var container = (await RunSimctlAsync(cancellationToken, "get_app_container", udid, bundleId, "data").ConfigureAwait(false)).Trim();
			if (!Directory.Exists(container))
				throw new DirectoryNotFoundException("simctl did not report the app's data container: " + container);
			return Path.Combine(container, "tmp") + "/";
		}

		static Task<string> RunSimctlAsync(CancellationToken cancellationToken, params string[] arguments)
		{
			return RunAsync("/usr/bin/xcrun", new[] { "simctl" }.Concat(arguments).ToArray(), cancellationToken);
		}

		static async Task<string> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
		{
			var startInfo = new ProcessStartInfo(fileName) {
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			foreach (var argument in arguments)
				startInfo.ArgumentList.Add(argument);
			using (var process = Process.Start(startInfo) ?? throw new InvalidOperationException(fileName + " could not be started.")) {
				var stdout = process.StandardOutput.ReadToEndAsync();
				var stderr = process.StandardError.ReadToEndAsync();
				try {
					await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					try { process.Kill(true); } catch (Exception) { }
					throw;
				}
				var output = await stdout.ConfigureAwait(false);
				var error = await stderr.ConfigureAwait(false);
				if (process.ExitCode != 0)
					throw new InvalidOperationException(Path.GetFileName(fileName) + " " + string.Join(" ", arguments) + " failed (exit " + process.ExitCode + ")." + Environment.NewLine + output + error);
				return output;
			}
		}
	}
}
