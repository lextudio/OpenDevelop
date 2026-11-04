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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Designer.Devices;

namespace ICSharpCode.AndroidDeviceManager
{
	/// <summary>
	/// Offers the AVDs this add-in already manages to every designer through the shared
	/// <see cref="DeviceService"/>. It only adapts <see cref="AvdManagerService"/>; there is no second
	/// avdmanager implementation.
	/// </summary>
	public sealed class AndroidDeviceProvider : IDeviceProvider, IDeviceManagement, IExternalDeviceCreator
	{
		readonly AvdManagerService service = new AvdManagerService();
		readonly Func<string> sdkRoot;
		readonly Func<string, bool> adbInstalled;
		readonly Func<string, string, CancellationToken, Task<string>> runAdb;
		readonly Func<string, Task<IReadOnlyList<AvdInfo>>> listAvds;
		readonly Func<string, string, Task<bool>> deleteAvd;

		public AndroidDeviceProvider()
		{
			sdkRoot = ResolveSdkRoot;
			adbInstalled = sdk => File.Exists(AdbPath(sdk));
			runAdb = RunAdbProcessAsync;
			listAvds = sdk => service.ListAvdsAsync(sdk);
			deleteAvd = (sdk, name) => service.DeleteAvdAsync(sdk, name);
		}

		/// <summary>For tests: the SDK root, whether adb is installed, how adb runs (null = failed),
		/// and how AVDs are listed and deleted.</summary>
		public AndroidDeviceProvider(
			Func<string> sdkRoot,
			Func<string, bool> adbInstalled,
			Func<string, string, CancellationToken, Task<string>> runAdb,
			Func<string, Task<IReadOnlyList<AvdInfo>>> listAvds,
			Func<string, string, Task<bool>> deleteAvd)
		{
			this.sdkRoot = sdkRoot;
			this.adbInstalled = adbInstalled;
			this.runAdb = runAdb;
			this.listAvds = listAvds;
			this.deleteAvd = deleteAvd;
		}

		public string DisplayName => "Android Emulator";

		public DevicePlatform Platform => DevicePlatform.AndroidEmulator;

		public string Unavailable {
			get {
				var sdk = sdkRoot();
				if (sdk == null)
					return "Android SDK not found (set it in the Android SDK Manager, or set ANDROID_HOME).";
				if (AvdManagerService.FindEmulatorExecutable(sdk) == null)
					return "The Android emulator package is not installed in the SDK.";
				return null;
			}
		}

		public async Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken = default)
		{
			var sdk = sdkRoot();
			if (sdk == null)
				return new Device[0];
			try {
				var avds = await listAvds(sdk).ConfigureAwait(false);
				var running = await RunningAvdNamesAsync(sdk, cancellationToken).ConfigureAwait(false);
				// When adb cannot say (missing, failed), the state is Unknown rather than a guess.
				bool known = running != null && adbInstalled(sdk);
				return avds.Select(avd => {
					var device = ToDevice(avd, known && running.Contains(avd.Name));
					return known ? device : device with { State = DeviceState.Unknown };
				}).ToArray();
			} catch (Exception ex) when (!(ex is OperationCanceledException)) {
				return new Device[0];
			}
		}

		/// <summary>The Android SDK root: the one the SDK Manager saved, else the environment, else
		/// the platform's default install location.</summary>
		public static string ResolveSdkRoot()
		{
			var candidates = new List<string> {
				AvdManagerService.GetSavedSdkPath(),
				Environment.GetEnvironmentVariable("ANDROID_HOME"),
				Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
			};
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (OperatingSystem.IsMacOS())
				candidates.Add(Path.Combine(home, "Library", "Android", "sdk"));
			else if (OperatingSystem.IsWindows())
				candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"));
			else
				candidates.Add(Path.Combine(home, "Android", "Sdk"));
			return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && Directory.Exists(c));
		}

		/// <summary>Maps one parsed AVD to a shared device. The runtime version is the API level: read
		/// from the target text when it names one, else from the AVD's own <c>config.ini</c>
		/// (<c>image.sysdir.1=system-images/android-35/...</c>), which is where avdmanager's output
		/// usually does not say it. A tablet is recognised by its hardware profile or name.</summary>
		public static Device ToDevice(AvdInfo avd) => ToDevice(avd, false);

		/// <summary><paramref name="running"/> is whether adb lists an emulator started from this AVD.</summary>
		public static Device ToDevice(AvdInfo avd, bool running)
		{
			string api = ApiLevelFromText(avd.Target) ?? ApiLevelFromText(avd.BasedOn) ?? ApiLevelFromConfig(avd.ConfigIniPath) ?? string.Empty;
			bool tablet = ((avd.Device ?? string.Empty) + " " + avd.Name).IndexOf("tablet", StringComparison.OrdinalIgnoreCase) >= 0;
			return new Device(DevicePlatform.AndroidEmulator, "android:" + avd.Name, avd.Name,
				tablet ? DeviceFamily.Tablet : DeviceFamily.Phone, "Android API", api,
				running ? DeviceState.Booted : DeviceState.Shutdown);
		}

		public string Label => "New Android emulator...";

		/// <summary>Android's device creation is the Android Device Manager window.</summary>
		public void Open() => new ShowAndroidDeviceManagerCommand().Execute(null);

		static readonly TimeSpan AdbTimeout = TimeSpan.FromSeconds(8);

		static string AdbPath(string sdk) =>
			Path.Combine(sdk, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb");

		/// <summary>Runs adb with a time limit; null when adb is missing, failed or timed out.</summary>
		static async Task<string> RunAdbProcessAsync(string sdk, string arguments, CancellationToken cancellationToken)
		{
			var adb = AdbPath(sdk);
			if (!File.Exists(adb))
				return null;
			var startInfo = new ProcessStartInfo(adb, arguments) {
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			using (var process = Process.Start(startInfo)) {
				if (process == null)
					return null;
				using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
					limit.CancelAfter(AdbTimeout);
					var output = process.StandardOutput.ReadToEndAsync();
					process.StandardError.ReadToEndAsync().ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
					try {
						await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
						return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
					} catch (OperationCanceledException) {
						try { process.Kill(true); } catch (Exception) { }
						if (cancellationToken.IsCancellationRequested)
							throw;
						return null;
					}
				}
			}
		}

		/// <summary>emulator-5554 style serials of the running emulators, from <c>adb devices</c>.</summary>
		public static IReadOnlyList<string> ParseEmulatorSerials(string adbDevicesOutput)
		{
			var serials = new List<string>();
			foreach (var line in (adbDevicesOutput ?? string.Empty).Split('\n')) {
				var parts = line.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length >= 2 && parts[0].StartsWith("emulator-", StringComparison.Ordinal) && parts[1] == "device")
					serials.Add(parts[0]);
			}
			return serials;
		}

		/// <summary>True when adb lists an emulator that is not (yet) online - booting, offline or
		/// unauthorized. Its AVD cannot be identified, so callers must treat "who runs" as unknown.</summary>
		public static bool HasUnidentifiableEmulator(string adbDevicesOutput)
		{
			foreach (var line in (adbDevicesOutput ?? string.Empty).Split('\n')) {
				var parts = line.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length >= 2 && parts[0].StartsWith("emulator-", StringComparison.Ordinal) && parts[1] != "device")
					return true;
			}
			return false;
		}

		/// <summary>The AVD name an emulator reports for <c>adb emu avd name</c> (first line, before OK).</summary>
		public static string ParseAvdName(string emuAvdNameOutput)
		{
			foreach (var line in (emuAvdNameOutput ?? string.Empty).Split('\n')) {
				var text = line.Trim();
				if (text.StartsWith("KO", StringComparison.Ordinal))
					return null; // the emulator console refused the command: no name
				if (text.Length > 0 && text != "OK")
					return text;
			}
			return null;
		}

		/// <summary>AVD name to serial for every running emulator, or null when adb is installed
		/// but could not answer (callers must not read that as "nothing is running").</summary>
		async Task<Dictionary<string, string>> RunningSerialsByAvdAsync(string sdk, CancellationToken cancellationToken)
		{
			var map = new Dictionary<string, string>(StringComparer.Ordinal);
			var devices = await runAdb(sdk, "devices", cancellationToken).ConfigureAwait(false);
			if (devices == null)
				return adbInstalled(sdk) ? null : map;
			if (HasUnidentifiableEmulator(devices))
				return null; // a booting/offline emulator may be this very AVD
			foreach (var serial in ParseEmulatorSerials(devices)) {
				var name = ParseAvdName(await runAdb(sdk, "-s " + serial + " emu avd name", cancellationToken).ConfigureAwait(false));
				if (name == null)
					return null; // a running emulator we cannot identify: unknown, not absent
				map[name] = serial;
			}
			return map;
		}

		/// <summary>Names of the running AVDs, or null when that could not be determined.</summary>
		async Task<HashSet<string>> RunningAvdNamesAsync(string sdk, CancellationToken cancellationToken)
		{
			try {
				var map = await RunningSerialsByAvdAsync(sdk, cancellationToken).ConfigureAwait(false);
				return map == null ? null : new HashSet<string>(map.Keys, StringComparer.Ordinal);
			} catch (Exception ex) when (!(ex is OperationCanceledException)) {
				return null;
			}
		}

		static string AvdName(Device device) => device.Id.StartsWith("android:", StringComparison.Ordinal) ? device.Id.Substring("android:".Length) : device.Id;

		public Task<DeviceActionResult> BootAsync(Device device, CancellationToken cancellationToken = default)
		{
			var sdk = sdkRoot();
			if (sdk == null)
				return Task.FromResult(DeviceActionResult.Failure("Android SDK not found."));
			try {
				service.StartAvd(sdk, AvdName(device));
				return Task.FromResult(DeviceActionResult.Success);
			} catch (Exception ex) when (!(ex is OperationCanceledException)) {
				return Task.FromResult(DeviceActionResult.Failure(ex.Message));
			}
		}

		public async Task<DeviceActionResult> ShutdownAsync(Device device, CancellationToken cancellationToken = default)
		{
			var sdk = sdkRoot();
			if (sdk == null)
				return DeviceActionResult.Failure("Android SDK not found.");
			try {
				if (!adbInstalled(sdk))
					return DeviceActionResult.Failure("adb is not installed in the Android SDK (platform-tools).");
				var running = await RunningSerialsByAvdAsync(sdk, cancellationToken).ConfigureAwait(false);
				if (running == null)
					return DeviceActionResult.Failure("adb could not list the running emulators.");
				if (!running.TryGetValue(AvdName(device), out var serial))
					return DeviceActionResult.Success; // not running: already true
				var answer = await runAdb(sdk, "-s " + serial + " emu kill", cancellationToken).ConfigureAwait(false);
				return answer == null ? DeviceActionResult.Failure("adb could not stop " + serial + ".") : DeviceActionResult.Success;
			} catch (Exception ex) when (!(ex is OperationCanceledException)) {
				return DeviceActionResult.Failure(ex.Message);
			}
		}

		public async Task<DeviceActionResult> DeleteAsync(Device device, CancellationToken cancellationToken = default)
		{
			var sdk = sdkRoot();
			if (sdk == null)
				return DeviceActionResult.Failure("Android SDK not found.");
			var running = await RunningAvdNamesAsync(sdk, cancellationToken).ConfigureAwait(false);
			if (running == null)
				return DeviceActionResult.Failure("adb could not say whether the emulator is running; stop it first.");
			if (running.Contains(AvdName(device)))
				return DeviceActionResult.Failure("Stop the emulator before deleting it.");
			try {
				return await deleteAvd(sdk, AvdName(device)).ConfigureAwait(false)
					? DeviceActionResult.Success
					: DeviceActionResult.Failure("avdmanager could not delete " + AvdName(device) + ".");
			} catch (Exception ex) when (!(ex is OperationCanceledException)) {
				return DeviceActionResult.Failure(ex.Message);
			}
		}

		static string ApiLevelFromText(string text)
		{
			var match = Regex.Match(text ?? string.Empty, @"API(?: level)?\s+(\d+)", RegexOptions.IgnoreCase);
			return match.Success ? match.Groups[1].Value : null;
		}

		static string ApiLevelFromConfig(string configIniPath)
		{
			try {
				if (!File.Exists(configIniPath))
					return null;
				foreach (var line in File.ReadLines(configIniPath)) {
					var match = Regex.Match(line, @"^\s*image\.sysdir\.1\s*=.*?android-(\d+)", RegexOptions.IgnoreCase);
					if (match.Success)
						return match.Groups[1].Value;
				}
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
			return null;
		}
	}

	/// <summary>Autostart: registers the Android provider so designers see AVDs without the
	/// Device Manager window ever being opened.</summary>
	public sealed class RegisterDeviceProviderCommand : AbstractCommand
	{
		public override void Run()
		{
			DeviceService.Default.Register(new AndroidDeviceProvider());
		}
	}
}
