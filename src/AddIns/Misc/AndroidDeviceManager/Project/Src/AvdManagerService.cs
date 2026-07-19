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
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using ICSharpCode.Core;

namespace ICSharpCode.AndroidDeviceManager
{
	/// <summary>
	/// Wraps the Android command-line `avdmanager` and `emulator` tools: locating them under an
	/// SDK root, listing AVDs/device definitions/system images, and creating/deleting/launching AVDs.
	/// </summary>
	public sealed class AvdManagerService
	{
		const string SdkPathPropertyKey = "AndroidSdkManager.SdkPath";

		public static string GetSavedSdkPath()
		{
			return PropertyService.Get(SdkPathPropertyKey, string.Empty);
		}

		static string FindCommandLineToolExecutable(string sdkRoot, string toolName)
		{
			if (string.IsNullOrEmpty(sdkRoot) || !Directory.Exists(sdkRoot))
				return null;

			var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? toolName + ".bat" : toolName;
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

		public static string FindEmulatorExecutable(string sdkRoot)
		{
			if (string.IsNullOrEmpty(sdkRoot) || !Directory.Exists(sdkRoot))
				return null;
			var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "emulator.exe" : "emulator";
			var path = Path.Combine(sdkRoot, "emulator", exeName);
			return File.Exists(path) ? path : null;
		}

		public async Task<IReadOnlyList<AvdInfo>> ListAvdsAsync(string sdkRoot)
		{
			var (output, _) = await RunAsync(sdkRoot, "avdmanager", "list avd").ConfigureAwait(false);
			return ParseAvds(output);
		}

		public async Task<IReadOnlyList<DeviceDefinition>> ListDeviceDefinitionsAsync(string sdkRoot)
		{
			var (output, _) = await RunAsync(sdkRoot, "avdmanager", "list device").ConfigureAwait(false);
			return ParseDeviceDefinitions(output);
		}

		public async Task<IReadOnlyList<SystemImageInfo>> ListInstalledSystemImagesAsync(string sdkRoot)
		{
			var (output, _) = await RunAsync(sdkRoot, "sdkmanager", "--list").ConfigureAwait(false);
			return output.Replace("\r\n", "\n").Split('\n')
				.Select(line => line.Trim())
				.Where(line => line.StartsWith("system-images;", StringComparison.OrdinalIgnoreCase))
				.Select(line => line.Split('|')[0].Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.Select(ParseSystemImageId)
				.Where(image => image != null)
				.ToList();
		}

		public async Task<bool> CreateAvdAsync(string sdkRoot, string name, string systemImagePackageId, string deviceId, bool force)
		{
			var args = "create avd -n \"" + name + "\" -k \"" + systemImagePackageId + "\"";
			if (!string.IsNullOrEmpty(deviceId))
				args += " -d \"" + deviceId + "\"";
			if (force)
				args += " --force";
			// avdmanager prompts "Do you wish to create a custom hardware profile? [no]" on stdin.
			var (_, exitCode) = await RunAsync(sdkRoot, "avdmanager", args, stdin: "no\n").ConfigureAwait(false);
			return exitCode == 0;
		}

		public async Task<bool> DeleteAvdAsync(string sdkRoot, string name)
		{
			var (_, exitCode) = await RunAsync(sdkRoot, "avdmanager", "delete avd -n \"" + name + "\"").ConfigureAwait(false);
			return exitCode == 0;
		}

		public void StartAvd(string sdkRoot, string name)
		{
			var emulator = FindEmulatorExecutable(sdkRoot);
			if (emulator == null)
				throw new InvalidOperationException("Could not find the emulator executable under \"" + sdkRoot + "\".");

			var startInfo = new ProcessStartInfo {
				FileName = emulator,
				Arguments = "-avd \"" + name + "\"",
				UseShellExecute = false,
			};
			Process.Start(startInfo);
		}

		static SystemImageInfo ParseSystemImageId(string packageId)
		{
			// e.g. "system-images;android-30;google_apis;x86_64"
			var parts = packageId.Split(';');
			if (parts.Length < 4)
				return null;
			return new SystemImageInfo {
				PackageId = packageId,
				ApiLevel = parts[1].Replace("android-", ""),
				Tag = parts[2],
				Abi = parts[3],
				DisplayName = parts[1] + " / " + parts[2] + " / " + parts[3],
			};
		}

		static List<AvdInfo> ParseAvds(string output)
		{
			var avds = new List<AvdInfo>();
			foreach (var block in output.Replace("\r\n", "\n").Split(new[] { "---------" }, StringSplitOptions.None)) {
				if (!block.Contains("Name:"))
					continue;
				var avd = new AvdInfo {
					Name = MatchField(block, "Name"),
					Device = MatchField(block, "Device"),
					Path = MatchField(block, "Path"),
					Target = MatchField(block, "Target"),
					BasedOn = MatchField(block, "Based on"),
					Skin = MatchField(block, "Skin"),
					Sdcard = MatchField(block, "Sdcard"),
				};
				if (!string.IsNullOrEmpty(avd.Name))
					avds.Add(avd);
			}
			return avds;
		}

		static List<DeviceDefinition> ParseDeviceDefinitions(string output)
		{
			var devices = new List<DeviceDefinition>();
			foreach (var block in output.Replace("\r\n", "\n").Split(new[] { "---------" }, StringSplitOptions.None)) {
				var idMatch = Regex.Match(block, "id:\\s*\\d+\\s*or\\s*\"(.+?)\"");
				if (!idMatch.Success)
					continue;
				var name = MatchField(block, "Name");
				devices.Add(new DeviceDefinition {
					Id = idMatch.Groups[1].Value,
					DisplayName = string.IsNullOrEmpty(name) ? idMatch.Groups[1].Value : name,
				});
			}
			return devices;
		}

		static string MatchField(string block, string fieldName)
		{
			var match = Regex.Match(block, @"^\s*" + Regex.Escape(fieldName) + @"\s*:\s*(.+)$", RegexOptions.Multiline);
			return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
		}

		static async Task<(string Output, int ExitCode)> RunAsync(string sdkRoot, string toolName, string arguments, string stdin = null)
		{
			var executable = FindCommandLineToolExecutable(sdkRoot, toolName);
			if (executable == null)
				throw new InvalidOperationException("Could not find " + toolName + " under \"" + sdkRoot + "\". Set the Android SDK location first.");

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
