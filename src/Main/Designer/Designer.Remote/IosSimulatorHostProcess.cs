// A designer host that runs inside an iOS Simulator device. Shared by every designer that previews
// on a simulator (MAUI, Uno, ...): it installs the app bundle, launches it with the loopback port
// and token it must call back on, and keeps the DDP connection and the app's lifetime in one place.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using StreamJsonRpc;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>
	/// The child cannot be spawned directly: it lives in a simulator device. <see cref="PrepareAsync"/>
	/// boots the device and installs the bundle; <see cref="StartConnectionAsync"/> then launches the
	/// app with <c>--port N --token T</c> through a small watcher script whose lifetime stands for the
	/// app's (Xcode 27's <c>simctl launch</c> returns as soon as the app is launched, so it cannot
	/// represent it), and completes the authenticated handshake. The token, not the process id, is
	/// the identity check: the process this class tracks is the script, not the app.
	/// </summary>
	public sealed class IosSimulatorHostProcess : DesignerHostProcessClient
	{
		readonly string executablePath;
		readonly string udid;
		readonly string bundleId;
		readonly string displayName;
		readonly string scriptPath;

		/// <param name="appBundlePath">The built <c>.app</c> directory.</param>
		/// <param name="udid">The simulator device to run in.</param>
		/// <param name="bundleId">The app's bundle identifier (see <see cref="ReadBundleId"/>).</param>
		/// <param name="displayName">Names the host in error messages ("MAUI", "Uno").</param>
		public IosSimulatorHostProcess(string appBundlePath, string udid, string bundleId, string displayName = "designer")
			: base(TimeSpan.FromMinutes(1))
		{
			if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("iOS simulators run on macOS only.");
			if (string.IsNullOrWhiteSpace(appBundlePath)) throw new ArgumentException("An app bundle path is required.", nameof(appBundlePath));
			// These go into the watcher script unquoted, so only characters that cannot change its meaning.
			RequireSafeIdentifier(udid, nameof(udid));
			RequireSafeIdentifier(bundleId, nameof(bundleId));
			executablePath = Path.Combine(appBundlePath, ReadPlistString(appBundlePath, "CFBundleExecutable", Path.GetFileNameWithoutExtension(appBundlePath)));
			this.udid = udid;
			this.bundleId = bundleId;
			this.displayName = displayName;
			scriptPath = Path.Combine(Path.GetTempPath(), "designer-ios-host-" + Guid.NewGuid().ToString("N") + ".sh");
		}

		public Task StartConnectionAsync(CancellationToken cancellationToken) => StartAsync(cancellationToken);

		// Only feeds the base class's existence check; the app is launched by the script below.
		protected override string GetChildDllPath() => executablePath;

		protected override string DotnetHostPath => "/bin/sh";

		protected override TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(120);

		protected override TimeSpan ConnectTimeout => TimeSpan.FromSeconds(90);

		protected override string BuildCommandLine(string childDll, int port, string token)
		{
			File.WriteAllText(scriptPath, LaunchScript(udid, bundleId, displayName, port, token));
			// The base class hands this to /bin/sh as one argument string: quote the path (TMPDIR may contain spaces).
			return "\"" + scriptPath + "\"";
		}

		/// <summary>The watcher script: launch the app with this attempt's port and token, wait for it
		/// to appear, then stay alive for as long as it runs.</summary>
		public static string LaunchScript(string udid, string bundleId, string displayName, int port, string token)
		{
			return "set -e\n"
				+ "/usr/bin/xcrun simctl launch --terminate-running-process " + udid + " " + bundleId + " --port " + port + " --token " + token + "\n"
				+ WatchScript(displayName, port, token, 40);
		}

		/// <summary>The part of the watcher that follows the launch. simctl's printed pid is not the app's
		/// process id (Xcode 27), so the lifetime is watched by finding this attempt's own
		/// <c>--port N --token T</c> in the process list. The pattern is written as <c>[-]-port ...</c>:
		/// as a regular expression it still matches the real command line, but the grep's own command
		/// line (which contains the pattern text) no longer does, so the watcher cannot see itself.
		/// Exits 1 with "not running" when the app does not appear within <paramref name="waitSeconds"/>.</summary>
		public static string WatchScript(string displayName, int port, string token, int waitSeconds)
		{
			string name = System.Text.RegularExpressions.Regex.Replace(displayName ?? string.Empty, "[^A-Za-z0-9._:-]", string.Empty);
			return string.Join("\n", new[] {
				"PAT=\"[-]-port " + port + " --token " + token + "\"",
				"i=0",
				"while [ $i -lt " + waitSeconds + " ]; do",
				"  if /bin/ps -Aww -o command 2>/dev/null | /usr/bin/grep -q -e \"$PAT\"; then break; fi",
				"  i=$((i+1)); sleep 1",
				"done",
				"if ! /bin/ps -Aww -o command 2>/dev/null | /usr/bin/grep -q -e \"$PAT\"; then",
				"  echo \"" + name + " iOS host: not running\" >&2",
				"  exit 1",
				"fi",
				"while /bin/ps -Aww -o command 2>/dev/null | /usr/bin/grep -q -e \"$PAT\"; do sleep 1; done",
				"",
			});
		}

		protected override async Task OnConnectedAsync(JsonRpc rpc, string token, CancellationToken cancellationToken)
		{
			var handshake = await rpc.InvokeWithParameterObjectAsync<HostHandshake>("initialize",
				new { token, protocolVersion = DesignerProtocol.Version, sessionId = SessionId }, cancellationToken)
				.WaitAsync(HandshakeTimeout, cancellationToken).ConfigureAwait(false);
			Handshake = handshake;
			if (handshake.ProtocolVersion != DesignerProtocol.Version)
				throw new InvalidDataException("The iOS " + displayName + " host speaks protocol " + handshake.ProtocolVersion + "; this build expects " + DesignerProtocol.Version + ".");
			if (handshake.SessionId != SessionId)
				throw new InvalidDataException("The iOS " + displayName + " host did not echo the expected session id.");
		}

		/// <summary>Disposing the client also stops the app: killing the watcher script alone would leave it running.</summary>
		protected override void OnDisposed()
		{
			TerminateSimulatedApp();
			try { File.Delete(scriptPath); } catch (Exception) { }
		}

		/// <summary>Letters, digits and <c>. _ : -</c> only: safe to place unquoted in a shell script.</summary>
		public static void RequireSafeIdentifier(string value, string name)
		{
			if (string.IsNullOrWhiteSpace(value) || !System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9._:-]+$"))
				throw new ArgumentException("'" + value + "' is not a valid identifier here.", name);
		}

		/// <summary>Kills the app inside the simulator; the watcher script then exits by itself.</summary>
		public void TerminateSimulatedApp()
		{
			try {
				var startInfo = new ProcessStartInfo("/usr/bin/xcrun") { UseShellExecute = false, RedirectStandardError = true };
				startInfo.ArgumentList.Add("simctl");
				startInfo.ArgumentList.Add("terminate");
				startInfo.ArgumentList.Add(udid);
				startInfo.ArgumentList.Add(bundleId);
				using (var process = Process.Start(startInfo))
					process?.WaitForExit(5000);
			} catch (Exception) {
				// Best effort: the simulator may be down already.
			}
		}

		/// <summary>Boots the simulator (waiting until it is usable) and installs the app bundle.
		/// A cold boot can take a minute; cancel through the token.</summary>
		public static async Task PrepareAsync(string udid, string appBundlePath, CancellationToken cancellationToken)
		{
			if (!Directory.Exists(appBundlePath))
				throw new FileNotFoundException("The iOS design host bundle is not installed: " + appBundlePath, appBundlePath);
			await RunSimctlAsync(new[] { "bootstatus", udid, "-b" }, "bootstatus", cancellationToken).ConfigureAwait(false);
			await RunSimctlAsync(new[] { "install", udid, appBundlePath }, "install", cancellationToken).ConfigureAwait(false);
		}

		/// <summary>Reads the CFBundleIdentifier of a built app bundle's XML Info.plist, or returns
		/// <paramref name="fallback"/> when the plist is missing or has none.</summary>
		public static string ReadBundleId(string appBundlePath, string fallback) =>
			ReadPlistString(appBundlePath, "CFBundleIdentifier", fallback);

		/// <summary>Reads a string value from the bundle's XML Info.plist; <paramref name="fallback"/> when absent.</summary>
		public static string ReadPlistString(string appBundlePath, string key, string fallback)
		{
			var plist = Path.Combine(appBundlePath, "Info.plist");
			if (File.Exists(plist)) {
				// An XML plist is read with a plain substring search (dependency-free). A built app's plist
				// is often binary ("bplist00": Uno and the Apple SDK compile it), so convert that first.
				string xml = ReadPlistAsXml(plist);
				string marker = "<key>" + key + "</key>";
				int i = xml.IndexOf(marker, StringComparison.Ordinal);
				if (i >= 0) {
					int valueStart = xml.IndexOf("<string>", i, StringComparison.Ordinal);
					if (valueStart >= 0) {
						valueStart += "<string>".Length;
						int valueEnd = xml.IndexOf("</string>", valueStart, StringComparison.Ordinal);
						if (valueEnd > valueStart)
							return xml.Substring(valueStart, valueEnd - valueStart).Trim();
					}
				}
			}

			return fallback;
		}

		static string ReadPlistAsXml(string plistPath)
		{
			var bytes = File.ReadAllBytes(plistPath);
			bool binary = bytes.Length >= 6 && bytes[0] == (byte)'b' && bytes[1] == (byte)'p' && bytes[2] == (byte)'l' && bytes[3] == (byte)'i' && bytes[4] == (byte)'s' && bytes[5] == (byte)'t';
			if (!binary)
				return System.Text.Encoding.UTF8.GetString(bytes);
			try {
				var startInfo = new ProcessStartInfo("/usr/bin/plutil") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
				foreach (var argument in new[] { "-convert", "xml1", "-o", "-", plistPath })
					startInfo.ArgumentList.Add(argument);
				using (var process = Process.Start(startInfo)) {
					if (process == null)
						return string.Empty;
					var output = process.StandardOutput.ReadToEndAsync();
					process.StandardError.ReadToEndAsync();
					if (!process.WaitForExit(10000)) {
						try { process.Kill(true); } catch (Exception) { }
						return string.Empty;
					}
					return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : string.Empty;
				}
			} catch (Exception) {
				return string.Empty;
			}
		}

		static async Task RunSimctlAsync(string[] args, string operation, CancellationToken cancellationToken)
		{
			var startInfo = new ProcessStartInfo("/usr/bin/xcrun") {
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			startInfo.ArgumentList.Add("simctl");
			foreach (var arg in args)
				startInfo.ArgumentList.Add(arg);
			using (var process = Process.Start(startInfo) ?? throw new InvalidOperationException("xcrun simctl could not be started.")) {
				var stdout = process.StandardOutput.ReadToEndAsync();
				var stderr = process.StandardError.ReadToEndAsync();
				try {
					await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					try { process.Kill(true); } catch (Exception) { }
					throw;
				}
				string output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
				if (process.ExitCode != 0)
					throw new InvalidOperationException("simctl " + operation + " failed (exit " + process.ExitCode + ")." + Environment.NewLine + output);
			}
		}
	}
}
