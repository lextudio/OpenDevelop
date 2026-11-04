// A designer host that runs inside an Android emulator. Shared by every designer that previews on
// an emulator (MAUI, Uno, ...): it installs the APK, starts the app with the loopback port and token
// it must call back on, bridges the loopback with `adb reverse`, and keeps the DDP connection and
// the app's lifetime in one place.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using StreamJsonRpc;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>
	/// The child cannot be spawned directly: it lives in an emulator, whose 127.0.0.1 is its own, so
	/// the connection goes through <c>adb reverse tcp:N tcp:N</c> (removed again when the app ends).
	/// <see cref="InstallAsync"/> installs the APK; <see cref="StartConnectionAsync"/> starts the app
	/// through a watcher script whose lifetime stands for the app's, and completes the authenticated
	/// handshake. The token, not the process id, is the identity check.
	/// </summary>
	public sealed class AndroidEmulatorHostProcess : DesignerHostProcessClient
	{
		readonly string adb;
		readonly string serial;
		readonly string apkPath;
		readonly string packageName;
		readonly string displayName;
		readonly string scriptPath;
		int bridgedPort;
		Process logcat;
		CancellationTokenSource logcatCancellation;

		public AndroidEmulatorHostProcess(string adb, string serial, string apkPath, string packageName, string displayName = "designer")
			// A cold, small emulator renders a first frame in tens of seconds (measured: over 30 s on a 1.5 GB AVD); the default
			// request timeout would terminate a healthy but slow host.
			: base(TimeSpan.FromMinutes(3))
		{
			// The watcher is a /bin/sh script: macOS and Linux only.
			if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Android emulator previews need a Unix shell (macOS or Linux).");
			if (string.IsNullOrWhiteSpace(adb)) throw new ArgumentException("adb is required.", nameof(adb));
			// Serial and package go into the watcher script, so only characters that cannot change its meaning.
			IosSimulatorHostProcess.RequireSafeIdentifier(serial, nameof(serial));
			IosSimulatorHostProcess.RequireSafeIdentifier(packageName, nameof(packageName));
			this.adb = adb;
			this.serial = serial;
			this.apkPath = apkPath;
			this.packageName = packageName;
			this.displayName = displayName;
			scriptPath = Path.Combine(Path.GetTempPath(), "designer-android-host-" + Guid.NewGuid().ToString("N") + ".sh");
			HostExited += (_, _) => StopLogcat();
		}

		/// <summary>Starts the app and connects; mirrors the device's .NET log (<c>DOTNET:I</c>) into
		/// <see cref="DesignerHostProcessClient.OutputLineReceived"/> as diagnostics.</summary>
		public async Task StartConnectionAsync(CancellationToken cancellationToken)
		{
			await StartAsync(cancellationToken).ConfigureAwait(false);
			StartLogcat();
		}

		// Only feeds the base class's existence check; the app is started by the script below.
		protected override string GetChildDllPath() => apkPath;

		protected override string DotnetHostPath => "/bin/sh";

		protected override TimeSpan HandshakeTimeout => TimeSpan.FromSeconds(180);

		// A cold emulator needs minutes to start a Mono app (measured: over 30 s on a 1.5 GB AVD).
		protected override TimeSpan ConnectTimeout => TimeSpan.FromSeconds(180);

		protected override string BuildCommandLine(string childDll, int port, string token)
		{
			bridgedPort = port;
			File.WriteAllText(scriptPath, LaunchScript(adb, serial, packageName, displayName, port, token));
			// The base class hands this to /bin/sh as one argument string: quote the path (TMPDIR may contain spaces).
			return "\"" + scriptPath + "\"";
		}

		/// <summary>The watcher script: bridge the port, restart the app with this attempt's port and
		/// token, wait for its process, then stay alive while it runs and finally remove the bridge.</summary>
		public static string LaunchScript(string adb, string serial, string packageName, string displayName, int port, string token)
		{
			return string.Join("\n", new[] {
				"set -e",
				"ADB=" + Quote(adb),
				"S=" + Quote(serial),
				"$ADB -s $S reverse tcp:" + port + " tcp:" + port + " >/dev/null",
				// Whatever way the script ends (no pid, the app exits, an error), take the bridge down again.
				"trap '$ADB -s $S reverse --remove tcp:" + port + " >/dev/null 2>&1 || true' EXIT",
				"$ADB -s $S shell am force-stop " + packageName + " >/dev/null 2>&1 || true",
				"ACT=$($ADB -s $S shell cmd package resolve-activity --brief " + packageName + " | tr -d '\\r' | tail -1)",
				"$ADB -s $S shell am start -n \"$ACT\" --es port " + port + " --es token " + token + " >/dev/null",
				"PID=\"\"",
				"for i in 1 2 3 4 5 6 7 8 9 10; do",
				"  PID=$($ADB -s $S shell pidof " + packageName + " | tr -d '\\r')",
				"  if [ -n \"$PID\" ]; then break; fi",
				"  sleep 1",
				"done",
				"if [ -z \"$PID\" ]; then echo \"" + System.Text.RegularExpressions.Regex.Replace(displayName ?? string.Empty, "[^A-Za-z0-9._:-]", string.Empty) + " Android host: not running\" >&2; exit 1; fi",
				"$ADB -s $S shell \"while [ -d /proc/$PID ]; do sleep 2; done\"",
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
				throw new InvalidDataException("The Android " + displayName + " host speaks protocol " + handshake.ProtocolVersion + "; this build expects " + DesignerProtocol.Version + ".");
			if (handshake.SessionId != SessionId)
				throw new InvalidDataException("The Android " + displayName + " host did not echo the expected session id.");
		}

		/// <summary>Disposing the client stops the app and the logcat mirror: the watcher script's end alone
		/// would leave the app running and logcat reading for the IDE's lifetime.</summary>
		protected override void OnDisposed()
		{
			StopLogcat();
			TerminateApp();
			RemoveBridge();
			try { File.Delete(scriptPath); } catch (Exception) { }
		}

		/// <summary>Force-stops the app (the watcher script then exits by itself).</summary>
		public void TerminateApp()
		{
			try {
				var startInfo = new ProcessStartInfo(adb) { RedirectStandardError = true, UseShellExecute = false };
				startInfo.ArgumentList.Add("-s");
				startInfo.ArgumentList.Add(serial);
				startInfo.ArgumentList.Add("shell");
				startInfo.ArgumentList.Add("am");
				startInfo.ArgumentList.Add("force-stop");
				startInfo.ArgumentList.Add(packageName);
				using (var process = Process.Start(startInfo))
					process?.WaitForExit(15000);
			} catch (Exception) {
				// Best effort.
			}
		}

		/// <summary>Takes the <c>adb reverse</c> mapping down. The script's EXIT trap does not run when the
		/// script is killed by a signal, so disposing removes it explicitly too.</summary>
		void RemoveBridge()
		{
			if (bridgedPort == 0)
				return;
			try {
				var startInfo = new ProcessStartInfo(adb) { RedirectStandardError = true, UseShellExecute = false };
				foreach (var argument in new[] { "-s", serial, "reverse", "--remove", "tcp:" + bridgedPort })
					startInfo.ArgumentList.Add(argument);
				using (var process = Process.Start(startInfo))
					process?.WaitForExit(15000);
			} catch (Exception) {
				// Best effort.
			}
		}

		void StartLogcat()
		{
			try {
				logcatCancellation = new CancellationTokenSource();
				var startInfo = new ProcessStartInfo(adb) {
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
				};
				foreach (var argument in new[] { "-s", serial, "logcat", "-s", "DOTNET:I" })
					startInfo.ArgumentList.Add(argument);
				logcat = Process.Start(startInfo);
				if (logcat == null)
					return;
				_ = PumpAsync(logcat.StandardOutput, logcatCancellation.Token);
				_ = PumpAsync(logcat.StandardError, logcatCancellation.Token);
			} catch (Exception) {
				// Diagnostics only: the child still works without its log.
			}
		}

		async Task PumpAsync(StreamReader reader, CancellationToken cancellationToken)
		{
			try {
				string line;
				while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
					RaiseOutputLine("[android] " + line);
			} catch (Exception) {
				// The stream ends when logcat is killed.
			}
		}

		void StopLogcat()
		{
			try { logcatCancellation?.Cancel(); } catch (Exception) { }
			var process = System.Threading.Interlocked.Exchange(ref logcat, null);
			if (process == null)
				return;
			try { if (!process.HasExited) process.Kill(true); } catch (Exception) { }
			try { process.Dispose(); } catch (Exception) { }
		}

		// ---- Installing and finding the device -------------------------------------------------

		/// <summary>Installs (replacing) the APK on <paramref name="serial"/>.</summary>
		public static Task InstallAsync(string adb, string serial, string apkPath, CancellationToken cancellationToken)
		{
			if (!File.Exists(apkPath))
				throw new FileNotFoundException("The Android design host APK is not installed: " + apkPath, apkPath);
			return RunAdbAsync(adb, serial, new[] { "install", "-r", apkPath }, cancellationToken);
		}

		/// <summary>Picks the emulator to install into: <paramref name="overrideSerial"/> when given,
		/// else the named AVD when it is running, else the first connected emulator, else any device.</summary>
		public static string ResolveSerial(string adb, string avdName, string overrideSerial = null)
		{
			if (!string.IsNullOrWhiteSpace(overrideSerial))
				return overrideSerial;

			var devices = ListDevices(adb);
			if (devices.Count == 0)
				throw new InvalidOperationException("No Android emulator is connected. Start one with `emulator -avd <name>`.");

			if (!string.IsNullOrWhiteSpace(avdName)) {
				// The serial is normally emulator-<port>; ask the device for its AVD name.
				foreach (var serial in devices) {
					var name = RunAdb(adb, serial, new[] { "emu", "avd", "name" });
					var first = name?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && l != "OK");
					if (string.Equals(first, avdName, StringComparison.OrdinalIgnoreCase))
						return serial;
				}
			}

			return devices.FirstOrDefault(d => d.StartsWith("emulator-", StringComparison.Ordinal)) ?? devices[0];
		}

		/// <summary>The serial of the running emulator started from AVD <paramref name="avdName"/>, or null when that AVD is
		/// not running (unlike <see cref="ResolveSerial"/>, never falls back to another device).</summary>
		public static string FindSerialForAvd(string adb, string avdName)
		{
			foreach (var serial in ListDevices(adb)) {
				var name = RunAdb(adb, serial, new[] { "emu", "avd", "name" });
				var first = name?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && l != "OK");
				if (string.Equals(first, avdName, StringComparison.OrdinalIgnoreCase))
					return serial;
			}

			return null;
		}

		/// <summary>True once the device reports <c>sys.boot_completed=1</c> (adb sees it a while before it is usable).</summary>
		public static bool IsBootCompleted(string adb, string serial) =>
			RunAdb(adb, serial, new[] { "shell", "getprop", "sys.boot_completed" })?.Trim() == "1";

		/// <summary>Online device serials from <c>adb devices</c> output.</summary>
		public static IReadOnlyList<string> ParseDevices(string adbDevicesOutput) =>
			(adbDevicesOutput ?? string.Empty)
				.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(line => line.Trim())
				.Where(line => line.EndsWith("\tdevice", StringComparison.Ordinal))
				.Select(line => line.Substring(0, line.IndexOf('\t')))
				.ToArray();

		static IReadOnlyList<string> ListDevices(string adb)
		{
			try {
				var startInfo = new ProcessStartInfo(adb, "devices") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
				using (var process = Process.Start(startInfo)) {
					if (process == null)
						return new string[0];
					return ParseDevices(ReadWithTimeout(process));
				}
			} catch (Exception) {
				return new string[0];
			}
		}

		/// <summary>The adb executable: the Android SDK's platform-tools, else PATH. Null when absent.</summary>
		public static string LocateAdb()
		{
			string name = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
			foreach (var root in SdkRoots()) {
				var candidate = Path.Combine(root, "platform-tools", name);
				if (File.Exists(candidate))
					return candidate;
			}

			foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)) {
				if (directory.Length == 0)
					continue;
				var candidate = Path.Combine(directory, name);
				if (File.Exists(candidate))
					return candidate;
			}

			return null;
		}

		static IEnumerable<string> SdkRoots()
		{
			foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" }) {
				var root = Environment.GetEnvironmentVariable(variable);
				if (!string.IsNullOrWhiteSpace(root))
					yield return root;
			}

			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (OperatingSystem.IsMacOS())
				yield return Path.Combine(home, "Library", "Android", "sdk");
			else if (OperatingSystem.IsWindows())
				yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
			else
				yield return Path.Combine(home, "Android", "Sdk");
		}

		static async Task RunAdbAsync(string adb, string serial, string[] arguments, CancellationToken cancellationToken)
		{
			var startInfo = new ProcessStartInfo(adb) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
			startInfo.ArgumentList.Add("-s");
			startInfo.ArgumentList.Add(serial);
			foreach (var argument in arguments)
				startInfo.ArgumentList.Add(argument);
			using (var process = Process.Start(startInfo) ?? throw new InvalidOperationException("adb could not be started.")) {
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
					throw new InvalidOperationException("adb " + arguments[0] + " failed (exit " + process.ExitCode + ")." + Environment.NewLine + output);
			}
		}

		static string RunAdb(string adb, string serial, string[] arguments)
		{
			try {
				var startInfo = new ProcessStartInfo(adb) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
				startInfo.ArgumentList.Add("-s");
				startInfo.ArgumentList.Add(serial);
				foreach (var argument in arguments)
					startInfo.ArgumentList.Add(argument);
				using (var process = Process.Start(startInfo)) {
					if (process == null)
						return null;
					string output = ReadWithTimeout(process);
					return output != null && process.HasExited && process.ExitCode == 0 ? output : null;
				}
			} catch (Exception) {
				return null;
			}
		}

		/// <summary>Reads a short adb command's stdout under a 15 s limit: both pipes drain concurrently and a wedged adb is
		/// killed, so the boot-wait loop that polls it can never hang. Null on timeout.</summary>
		static string ReadWithTimeout(Process process)
		{
			var output = process.StandardOutput.ReadToEndAsync();
			process.StandardError.ReadToEndAsync().ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
			if (!process.WaitForExit(15000)) {
				try { process.Kill(true); } catch (Exception) { }
				return null;
			}

			return output.Wait(5000) ? output.Result : null;
		}

		static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
	}
}
