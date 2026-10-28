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
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Workbench;
using ICSharpCode.SharpDevelop.Gui;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;

namespace ICSharpCode.PackageManagement.Scripting
{
	/// <summary>
	/// AddInTree shim that opens the Package Manager Console as a real pwsh session in the
	/// integrated Terminal. The old AvalonEdit/embedded-runspace console is deliberately not used:
	/// Terminal owns the PTY, rendering and lifecycle while this add-in supplies package commands.
	/// </summary>
	public class PackageManagementConsolePad : AbstractPadContent
	{
		static readonly object launchSync = new object();
		static readonly Dictionary<string, System.Threading.Tasks.Task> pendingLaunches = new Dictionary<string, System.Threading.Tasks.Task>();
		readonly TextBlock message = new TextBlock { Text = "Opening Package Manager Console in Terminal..." };

		public PackageManagementConsolePad()
		{
			// This AddInTree pad is also reachable directly from View > Pads. It must start
			// the terminal on first realization; later project changes come through the
			// workbench path and reuse or create the keyed session there.
			OpenTerminalAsync().FireAndForget();
		}

		async System.Threading.Tasks.Task OpenTerminalAsync()
		{
			try {
				await OpenConsoleAsync();
			} catch (Exception ex) {
				message.Text = "Package Manager Console could not start pwsh: " + ex.Message;
			}
		}

		public static System.Threading.Tasks.Task OpenConsoleAsync()
		{
			var project = (SD.ProjectService?.CurrentProject ?? SD.ProjectService?.CurrentSolution?.Projects.FirstOrDefault())?.FileName?.ToString();
			var sessionKey = "package-management:" + (string.IsNullOrWhiteSpace(project) ? "<no-project>" : Path.GetFullPath(project));
			lock (launchSync) {
				if (pendingLaunches.TryGetValue(sessionKey, out var pending) && !pending.IsCompleted)
					return pending;
				return pendingLaunches[sessionKey] = OpenConsoleCoreAsync(project, sessionKey);
			}
		}

		static async System.Threading.Tasks.Task OpenConsoleCoreAsync(string project, string sessionKey)
		{
			var terminal = SD.Services.GetService(typeof(ITerminalHost)) as ITerminalHost;
			if (terminal == null)
				throw new InvalidOperationException("The integrated Terminal add-in is unavailable.");
			var script = Path.Combine(Path.GetDirectoryName(typeof(PackageManagementConsolePad).Assembly.Location), "Scripts", "OpenDevelop.PackageManagement.ps1");
			if (!File.Exists(script))
				throw new FileNotFoundException("Package Manager Console startup script is missing.", script);
			var environment = new Dictionary<string, string>();
			if (!string.IsNullOrWhiteSpace(project))
				environment["OPENDEVELOP_PACKAGE_PROJECT"] = project;
			await terminal.OpenTerminalAsync(new TerminalLaunchRequest {
					Executable = FindPwsh(),
					// -File runs in a child script scope, which would discard the package functions
					// before the interactive prompt appears. Dot-source them in the pwsh global
					// command scope instead.
					Arguments = new[] { "-NoLogo", "-NoExit", "-Command", ". '" + script.Replace("'", "''") + "'" },
					WorkingDirectory = string.IsNullOrWhiteSpace(project) ? SD.ProjectService?.CurrentSolution?.Directory?.ToString() : Path.GetDirectoryName(project),
					// The terminal host reuses a live named session. Include the selected project in
					// that identity so switching projects never reactivates a session with stale cwd
					// or OPENDEVELOP_PACKAGE_PROJECT.
					Title = string.IsNullOrWhiteSpace(project) ? "Package Manager Console" : "Package Manager Console — " + Path.GetFileNameWithoutExtension(project),
					SessionKey = sessionKey,
					Environment = environment,
			});
		}

		static string FindPwsh()
		{
			var candidates = OperatingSystem.IsWindows()
				? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"), "pwsh.exe" }
				: new[] { "/opt/homebrew/bin/pwsh", "/usr/local/bin/pwsh", "/usr/bin/pwsh", "pwsh" };
			return candidates.FirstOrDefault(File.Exists) ?? candidates.Last();
		}

		public override object Control => message;
	}
}
