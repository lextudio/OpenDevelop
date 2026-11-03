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
	public sealed class AndroidDeviceProvider : IDeviceProvider
	{
		readonly AvdManagerService service = new AvdManagerService();

		public string DisplayName => "Android Emulator";

		public DevicePlatform Platform => DevicePlatform.AndroidEmulator;

		public string Unavailable {
			get {
				var sdk = ResolveSdkRoot();
				if (sdk == null)
					return "Android SDK not found (set it in the Android SDK Manager, or set ANDROID_HOME).";
				if (AvdManagerService.FindEmulatorExecutable(sdk) == null)
					return "The Android emulator package is not installed in the SDK.";
				return null;
			}
		}

		public async Task<IReadOnlyList<Device>> ListAsync(CancellationToken cancellationToken = default)
		{
			var sdk = ResolveSdkRoot();
			if (sdk == null)
				return new Device[0];
			try {
				var avds = await service.ListAvdsAsync(sdk).ConfigureAwait(false);
				return avds.Select(ToDevice).ToArray();
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
		public static Device ToDevice(AvdInfo avd)
		{
			string api = ApiLevelFromText(avd.Target) ?? ApiLevelFromText(avd.BasedOn) ?? ApiLevelFromConfig(avd.ConfigIniPath) ?? string.Empty;
			bool tablet = ((avd.Device ?? string.Empty) + " " + avd.Name).IndexOf("tablet", StringComparison.OrdinalIgnoreCase) >= 0;
			return new Device(DevicePlatform.AndroidEmulator, "android:" + avd.Name, avd.Name,
				tablet ? DeviceFamily.Tablet : DeviceFamily.Phone, "Android API", api);
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
