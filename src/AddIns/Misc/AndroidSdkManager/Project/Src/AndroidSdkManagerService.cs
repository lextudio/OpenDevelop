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
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using ICSharpCode.Core;

namespace ICSharpCode.AndroidSdkManager
{
	/// <summary>
	/// Wraps the Android command-line `sdkmanager` tool: locating it under an SDK root,
	/// listing installed/available packages, and installing/uninstalling by package id.
	/// </summary>
	public sealed class AndroidSdkManagerService
	{
		const string SdkPathPropertyKey = "AndroidSdkManager.SdkPath";

		public static string GetSavedSdkPath()
		{
			return PropertyService.Get(SdkPathPropertyKey, FindDefaultSdkPath() ?? string.Empty);
		}

		public static void SaveSdkPath(string path)
		{
			PropertyService.Set(SdkPathPropertyKey, path ?? string.Empty);
		}

		static string FindDefaultSdkPath()
		{
			var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME");
			if (!string.IsNullOrEmpty(androidHome) && Directory.Exists(androidHome))
				return androidHome;

			var androidSdkRoot = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
			if (!string.IsNullOrEmpty(androidSdkRoot) && Directory.Exists(androidSdkRoot))
				return androidSdkRoot;

			var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			var defaultWindowsPath = Path.Combine(localAppData, "Android", "Sdk");
			if (Directory.Exists(defaultWindowsPath))
				return defaultWindowsPath;

			return null;
		}

		/// <summary>
		/// Finds sdkmanager under &lt;sdkRoot&gt;/cmdline-tools/&lt;version&gt;/bin, preferring "latest",
		/// falling back to the legacy &lt;sdkRoot&gt;/tools/bin location.
		/// </summary>
		public static string FindSdkManagerExecutable(string sdkRoot)
		{
			if (string.IsNullOrEmpty(sdkRoot) || !Directory.Exists(sdkRoot))
				return null;

			var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "sdkmanager.bat" : "sdkmanager";

			var cmdlineToolsDir = Path.Combine(sdkRoot, "cmdline-tools");
			if (Directory.Exists(cmdlineToolsDir)) {
				var latest = Path.Combine(cmdlineToolsDir, "latest", "bin", exeName);
				if (File.Exists(latest))
					return latest;

				var candidate = Directory.EnumerateDirectories(cmdlineToolsDir)
					.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
					.Select(d => Path.Combine(d, "bin", exeName))
					.FirstOrDefault(File.Exists);
				if (candidate != null)
					return candidate;
			}

			var legacy = Path.Combine(sdkRoot, "tools", "bin", exeName);
			return File.Exists(legacy) ? legacy : null;
		}

		public async Task<IReadOnlyList<SdkPackage>> ListPackagesAsync(string sdkRoot)
		{
			var (output, _) = await RunAsync(sdkRoot, "--list --verbose", stdin: null).ConfigureAwait(false);
			return SdkManagerOutputParser.Parse(output);
		}

		public async Task<bool> InstallAsync(string sdkRoot, IEnumerable<string> packageIds)
		{
			var args = "--install " + string.Join(" ", packageIds.Select(Quote));
			// sdkmanager prompts "Accept? (y/N)" for licenses on stdin; auto-accept.
			var (_, exitCode) = await RunAsync(sdkRoot, args, stdin: RepeatYes()).ConfigureAwait(false);
			return exitCode == 0;
		}

		public async Task<bool> UninstallAsync(string sdkRoot, IEnumerable<string> packageIds)
		{
			var args = "--uninstall " + string.Join(" ", packageIds.Select(Quote));
			var (_, exitCode) = await RunAsync(sdkRoot, args, stdin: null).ConfigureAwait(false);
			return exitCode == 0;
		}

		static string Quote(string id)
		{
			return "\"" + id + "\"";
		}

		static string RepeatYes()
		{
			return string.Concat(Enumerable.Repeat("y\n", 50));
		}

		static async Task<(string Output, int ExitCode)> RunAsync(string sdkRoot, string arguments, string stdin)
		{
			var executable = FindSdkManagerExecutable(sdkRoot);
			if (executable == null)
				throw new InvalidOperationException("Could not find sdkmanager under \"" + sdkRoot + "\". Set the Android SDK location first.");

			var startInfo = new ProcessStartInfo {
				FileName = executable,
				Arguments = arguments,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = stdin != null,
				CreateNoWindow = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
			};
			startInfo.EnvironmentVariables["ANDROID_HOME"] = sdkRoot;

			using (var process = new Process { StartInfo = startInfo }) {
				var output = new StringBuilder();
				process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
				process.ErrorDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };

				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();

				if (stdin != null) {
					await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
					process.StandardInput.Close();
				}

				await process.WaitForExitAsync().ConfigureAwait(false);
				return (output.ToString(), process.ExitCode);
			}
		}
	}
}
