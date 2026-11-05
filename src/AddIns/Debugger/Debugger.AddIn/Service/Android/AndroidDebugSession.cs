using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Debugger.AddIn.Android
{
	/// <summary>Owns the device files, adb forwarding and debug-app setting for one launch.</summary>
	public sealed class AndroidDebugSession : IAsyncDisposable
	{
		readonly string adb, serial, package, id, temporaryDirectory, privateDirectory;
		bool markedDebugApp;
		bool ownsApp;
		bool wroteConfiguration;
		int disposed;
		public string Token { get; }
		public int HostPort { get; private set; }
		public int DevicePort { get; }

		AndroidDebugSession(string adb, string serial, string package)
		{
			if (!Regex.IsMatch(serial ?? "", @"^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid adb serial.", nameof(serial));
			if (!Regex.IsMatch(package ?? "", @"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid Android package.", nameof(package));
			this.adb = adb;
			this.serial = serial;
			this.package = package;
			id = Guid.NewGuid().ToString("N");
			temporaryDirectory = "/data/local/tmp/opendevelop-" + id;
			privateDirectory = "files/sharpdbg/" + id;
			Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
			DevicePort = RandomNumberGenerator.GetInt32(20000, 60000);
		}

		/// <summary>Installs and starts the app after placing its matching modules in its private directory.</summary>
		public static async Task<AndroidDebugSession> StartAsync(string adb, string serial, string package, string apk, string modules, CancellationToken cancellationToken = default)
		{
			if (!File.Exists(apk)) throw new FileNotFoundException("The Android APK was not found.", apk);
			if (!Directory.Exists(modules)) throw new DirectoryNotFoundException("The Android module directory was not found: " + modules);
			var session = new AndroidDebugSession(adb, serial, package);
			try {
				await session.StartCoreAsync(apk, modules, cancellationToken).ConfigureAwait(false);
				return session;
			} catch {
				await session.DisposeAsync().ConfigureAwait(false);
				throw;
			}
		}

		async Task StartCoreAsync(string apk, string modules, CancellationToken cancellationToken)
		{
			string user = (await RunAsync(new[] { "shell", "am", "get-current-user" }, cancellationToken).ConfigureAwait(false)).Trim();
			if (user != "0") throw new InvalidOperationException("Android debugging currently requires the emulator's primary user.");
			await EnsureDebugAppAvailableAsync(cancellationToken).ConfigureAwait(false);
			await RunAsync(new[] { "install", "-r", apk }, cancellationToken).ConfigureAwait(false);
			await RunAsync(new[] { "shell", "run-as", package, "mkdir", "-p", privateDirectory + "/modules" }, cancellationToken).ConfigureAwait(false);
			await RunAsync(new[] { "push", modules + Path.DirectorySeparatorChar + ".", temporaryDirectory }, cancellationToken).ConfigureAwait(false);
			await RunAsync(new[] { "shell", "run-as", package, "cp", "-R", temporaryDirectory + "/.", privateDirectory + "/modules" }, cancellationToken).ConfigureAwait(false);
			await RunAsync(new[] { "shell", "rm", "-rf", temporaryDirectory }, cancellationToken).ConfigureAwait(false);
			string targetModules = "/data/user/0/" + package + "/" + privateDirectory + "/modules";
			string configuration = DevicePort + "\n" + Token + "\n" + targetModules + "\n";
			wroteConfiguration = true;
			await RunAsync(new[] { "shell", "run-as " + package + " sh -c 'cat > files/sharpdbg/session'" }, cancellationToken, configuration).ConfigureAwait(false);
			string forwarded = await RunAsync(new[] { "forward", "tcp:0", "tcp:" + DevicePort }, cancellationToken).ConfigureAwait(false);
			if (!int.TryParse(forwarded.Trim(), out int hostPort) || hostPort is < 1 or > 65535) throw new InvalidOperationException("adb did not return the forwarded port: " + forwarded);
			HostPort = hostPort;
			await EnsureDebugAppAvailableAsync(cancellationToken).ConfigureAwait(false);
			markedDebugApp = true;
			await RunAsync(new[] { "shell", "am", "set-debug-app", package }, cancellationToken).ConfigureAwait(false);
			ownsApp = true;
			await RunAsync(new[] { "shell", "am", "force-stop", package }, cancellationToken).ConfigureAwait(false);
			string activityOutput = await RunAsync(new[] { "shell", "cmd", "package", "resolve-activity", "--brief", package }, cancellationToken).ConfigureAwait(false);
			string activity = activityOutput.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Contains('/'));
			if (activity == null || !activity.StartsWith(package + "/", StringComparison.Ordinal) || !Regex.IsMatch(activity, @"^[A-Za-z0-9_.$/]+$"))
				throw new InvalidOperationException("Android did not return a launchable activity for " + package + ".");
			await RunAsync(new[] { "shell", "am", "start", "-n", activity }, cancellationToken).ConfigureAwait(false);
		}

		async Task EnsureDebugAppAvailableAsync(CancellationToken cancellationToken)
		{
			string state = await RunAsync(new[] { "shell", "dumpsys", "activity", "processes" }, cancellationToken).ConfigureAwait(false);
			var debugApp = Regex.Match(state, @"\bmDebugApp=([^/\s]+)");
			// ActivityManager prints mDebugApp only once a debug app has been set, so a fresh boot has no such
			// line. Treat its absence as "unset" only when the dump is a complete one.
			if (!debugApp.Success && !state.Contains("mProcessesReady=true", StringComparison.Ordinal))
				throw new InvalidOperationException("Could not inspect the Android debug-app setting.");
			if (debugApp.Success && debugApp.Groups[1].Value != "null") throw new InvalidOperationException("Another Android debug-app setting is active; clear it before starting this session.");
		}

		async Task<string> RunAsync(string[] arguments, CancellationToken cancellationToken, string input = null)
		{
			var info = new ProcessStartInfo(adb) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null, CreateNoWindow = true };
			info.ArgumentList.Add("-s");
			info.ArgumentList.Add(serial);
			foreach (var argument in arguments) info.ArgumentList.Add(argument);
			using (var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start adb.")) {
				var stdout = process.StandardOutput.ReadToEndAsync();
				var stderr = process.StandardError.ReadToEndAsync();
				if (input != null) {
					await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
					process.StandardInput.Close();
				}
				try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
				catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw; }
				string output = await stdout.ConfigureAwait(false);
				string error = await stderr.ConfigureAwait(false);
				if (process.ExitCode != 0) throw new InvalidOperationException("adb " + arguments[0] + " failed: " + error.Trim());
				return output;
			}
		}

		public async ValueTask DisposeAsync()
		{
			if (Interlocked.Exchange(ref disposed, 1) != 0) return;
			using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10))) {
				async Task TryRun(params string[] args) { try { await RunAsync(args, timeout.Token).ConfigureAwait(false); } catch { } }
				if (ownsApp) await TryRun("shell", "am", "force-stop", package).ConfigureAwait(false);
				if (HostPort != 0) await TryRun("forward", "--remove", "tcp:" + HostPort).ConfigureAwait(false);
				await TryRun("shell", "run-as", package, "rm", "-rf", privateDirectory).ConfigureAwait(false);
				if (wroteConfiguration) await TryRun("shell", "run-as", package, "rm", "-f", "files/sharpdbg/session").ConfigureAwait(false);
				await TryRun("shell", "rm", "-rf", temporaryDirectory).ConfigureAwait(false);
				if (markedDebugApp) {
					try {
						string state = await RunAsync(new[] { "shell", "dumpsys", "activity", "processes" }, timeout.Token).ConfigureAwait(false);
						if (Regex.IsMatch(state, @"\bmDebugApp=" + Regex.Escape(package) + @"(?:/|\s)")) await TryRun("shell", "am", "clear-debug-app").ConfigureAwait(false);
					} catch { }
				}
			}
		}
	}
}
