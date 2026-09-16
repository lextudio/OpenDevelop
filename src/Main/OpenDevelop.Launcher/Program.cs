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
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenDevelop.Launcher
{
	/// <summary>
	/// The native apphost the user double-clicks (deployed as OpenDevelop.exe / OpenDevelopARM64.exe
	/// - see the .csproj comment for why this project's own AssemblyName is neither of those).
	/// Its only job is to hand off to "dotnet exec OpenDevelop.dll", the exact invocation
	/// dist.ps1's own smoke test already exercises, using a dotnet.exe that is GUARANTEED to match
	/// this process's own architecture.
	///
	/// That guarantee needs no path guessing (no probing "Program Files\dotnet\x64\" vs the native
	/// root, the ambiguity DotNetSdkService.DiscoverSdks() has to resolve once the IDE itself is
	/// running): this launcher is ALREADY running as a specific architecture by the time Main()
	/// executes - hostfxr resolved that when the OS started this apphost, using the machine's own
	/// registered per-architecture .NET installs, before a single line of this file ran.
	/// RuntimeEnvironment.GetRuntimeDirectory() names the exact install hostfxr just used, and its
	/// dotnet.exe is therefore certain to match. Verified empirically: an apphost built for
	/// RuntimeIdentifier=win-x64, run on an ARM64 machine with both an ARM64 (native) and an x64
	/// (side-by-side, e.g. "Program Files\dotnet\x64\") .NET install present, resolves to the
	/// side-by-side x64 root - not the machine's native one - with zero extra code.
	/// </summary>
	static class Program
	{
		[STAThread]
		static int Main(string[] args)
		{
			string baseDirectory = AppContext.BaseDirectory;
			string appPath = Path.Combine(baseDirectory, "OpenDevelop.dll");
			if (!File.Exists(appPath)) {
				ShowError("OpenDevelop.dll was not found next to this launcher:" + Environment.NewLine + appPath +
					Environment.NewLine + Environment.NewLine +
					"This launcher must stay in the same folder as the rest of the OpenDevelop distribution.");
				return 1;
			}

			string? dotnetExecutable = FindMatchingArchitectureDotNetHost();
			if (dotnetExecutable == null) {
				ShowError("Could not locate a " + RuntimeInformation.ProcessArchitecture +
					" .NET runtime to launch OpenDevelop with, even though one hosted this very launcher." +
					Environment.NewLine + Environment.NewLine +
					"Please repair or reinstall the .NET 10 SDK for this architecture.");
				return 1;
			}

			var startInfo = new ProcessStartInfo(dotnetExecutable) {
				UseShellExecute = false,
				WorkingDirectory = baseDirectory,
				// dotnet.exe is a console-subsystem executable. This launcher is WinExe (no
				// console of its own to inherit), so without this Windows allocates a brand new,
				// visible console window for the child the moment it starts - exactly the flash
				// this exists to avoid for a GUI app launched by double-click.
				CreateNoWindow = true
			};
			startInfo.ArgumentList.Add("exec");
			startInfo.ArgumentList.Add(appPath);
			foreach (string arg in args)
				startInfo.ArgumentList.Add(arg);

			Process process;
			try {
				process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
			} catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) {
				ShowError("Could not start OpenDevelop:" + Environment.NewLine + ex.Message +
					Environment.NewLine + Environment.NewLine + "dotnet host: " + dotnetExecutable);
				return 1;
			}

			// Block for the app's whole lifetime rather than exiting immediately: this keeps
			// Explorer/the taskbar showing one process for the one thing the user double-clicked,
			// matching how the previous single self-hosting apphost behaved, and lets the launcher
			// forward the real exit code instead of always reporting success.
			process.WaitForExit();
			return process.ExitCode;
		}

		static string? FindMatchingArchitectureDotNetHost()
		{
			try {
				string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				// ".../shared/Microsoft.NETCore.App/<version>" -> three levels up is DOTNET_ROOT
				// for this exact install - the one that just hosted this launcher process.
				string? dotnetRoot = Directory.GetParent(runtimeDirectory)?.Parent?.Parent?.FullName;
				if (dotnetRoot != null) {
					string candidate = Path.Combine(dotnetRoot, "dotnet.exe");
					if (File.Exists(candidate))
						return candidate;
				}
			} catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) {
				// Fall through to the PATH search below.
			}

			// Last resort only - reached solely if the derivation above ever fails on some
			// install layout this launcher hasn't seen. May pick a differently-architected dotnet
			// if a machine's PATH points at one; the derivation above is what actually guarantees
			// correctness and is expected to succeed on every normal install.
			string? pathVariable = Environment.GetEnvironmentVariable("PATH");
			if (pathVariable != null) {
				foreach (string directory in pathVariable.Split(Path.PathSeparator)) {
					if (string.IsNullOrEmpty(directory))
						continue;
					string candidate = Path.Combine(directory, "dotnet.exe");
					if (File.Exists(candidate))
						return candidate;
				}
			}
			return null;
		}

		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

		const uint MB_ICONERROR = 0x10;

		static void ShowError(string message)
		{
			MessageBoxW(IntPtr.Zero, message, "OpenDevelop", MB_ICONERROR);
		}
	}
}
