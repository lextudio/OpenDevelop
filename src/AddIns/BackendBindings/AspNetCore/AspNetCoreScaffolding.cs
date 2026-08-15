using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.AspNetCore
{
	public static class AspNetCoreScaffolding
	{
		public const string InstallCommand = "dotnet tool install --global Microsoft.dotnet-scaffold";

		public static async Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
		{
			var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
			info.ArgumentList.Add("scaffold"); info.ArgumentList.Add("--help");
			using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the dotnet CLI.");
			try { await process.WaitForExitAsync(cancellationToken); return process.ExitCode == 0; }
			catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
		}

		public static ProcessStartInfo CreateInteractiveTerminalCommand(string projectDirectory)
		{
			var directory = Path.GetFullPath(projectDirectory);
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
				var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = directory };
				info.ArgumentList.Add("/k"); info.ArgumentList.Add("dotnet scaffold"); return info;
			}
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
				var shellCommand = "cd " + ShellQuote(directory) + " && dotnet scaffold";
				var script = "tell application \"Terminal\" to activate\ntell application \"Terminal\" to do script \"" + AppleScriptEscape(shellCommand) + "\"";
				var info = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
				info.ArgumentList.Add("-e"); info.ArgumentList.Add(script); return info;
			}
			var linux = new ProcessStartInfo("x-terminal-emulator") { UseShellExecute = false, WorkingDirectory = directory };
			linux.ArgumentList.Add("-e"); linux.ArgumentList.Add("dotnet"); linux.ArgumentList.Add("scaffold"); return linux;
		}

		static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
		static string AppleScriptEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
	}
}
