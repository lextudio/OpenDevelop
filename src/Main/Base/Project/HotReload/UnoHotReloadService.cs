using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

namespace ICSharpCode.SharpDevelop.Project.HotReload;

/// <summary>
/// Starts Uno's supported DevServer for a locally launched desktop application.
/// The runtime consumes the endpoint from environment variables, so this does not
/// write a <c>.csproj.user</c> file or require a second build before launch.
/// </summary>
public static class UnoHotReloadService
{
	static readonly ConcurrentDictionary<string, UnoHotReloadSession> sessions = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Configures <paramref name="startInfo"/> for Uno hot reload when the project is an Uno
	/// desktop project running on macOS. Other project kinds are intentionally left untouched.
	/// </summary>
	public static bool TryConfigureLaunch(IProject project, ProcessStartInfo startInfo)
	{
		if (startInfo == null)
			return false;
		if (!CanConfigureLaunch(project, out var diagnostic)) {
			LoggingService.Warn($"Uno Hot Reload was not enabled: {diagnostic}");
			return false;
		}

		var solutionPath = Path.GetFullPath(project.ParentSolution.FileName);
		try {
			var projectFile = project.FileName.ToString();
			var session = sessions.GetOrAdd(solutionPath, _ => StartSession(solutionPath, projectFile));
			startInfo.Environment["UNO_DEV_SERVER_HOST"] = IPAddress.Loopback.ToString();
			startInfo.Environment["UNO_DEV_SERVER_PORT"] = session.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
			// Desktop .NET does not get this setting from Uno's mobile launch targets. It is
			// required for MetadataUpdater.ApplyUpdate to accept the DevServer's deltas.
			startInfo.Environment["DOTNET_MODIFIABLE_ASSEMBLIES"] = "debug";
			LoggingService.Info($"Uno Hot Reload: using DevServer on {IPAddress.Loopback}:{session.Port}.");
			return true;
		} catch (Exception ex) {
			// Hot reload is an optional launch enhancement. A missing DevServer must never prevent Run.
			LoggingService.Warn($"Uno Hot Reload was not enabled: {ex.Message}");
			return false;
		}
	}

	/// <summary>Gets the log file for the DevServer session associated with a solution.</summary>
	public static string GetDevServerLogFile(IProject project)
	{
		if (project?.ParentSolution?.FileName is not { } solutionFile)
			return null;
		return sessions.TryGetValue(Path.GetFullPath(solutionFile), out var session) ? session.LogFile : null;
	}

	/// <summary>Checks whether the current project can use the macOS Uno DevServer launch adapter.</summary>
	public static bool CanConfigureLaunch(IProject project, out string diagnostic)
	{
		if (project == null) {
			diagnostic = "No startup project is selected.";
			return false;
		}
		if (!OperatingSystem.IsMacOS()) {
			diagnostic = "Uno Hot Reload is currently implemented only on macOS.";
			return false;
		}
		if (XamlFrameworkDetector.DetectProjectFile(project.FileName).Runtime != XamlRuntimeKind.Uno) {
			diagnostic = "The selected project is not an Uno project.";
			return false;
		}
		if (project.ParentSolution?.FileName is not { } solutionFile || !File.Exists(solutionFile)) {
			diagnostic = "Save the solution before starting Uno Hot Reload.";
			return false;
		}
		if (FindHost(project.FileName.ToString()) == null) {
			diagnostic = "The matching Uno.WinUI.DevServer package was not found in the local NuGet cache.";
			return false;
		}
		diagnostic = null;
		return true;
	}

	static UnoHotReloadSession StartSession(string solutionFile, string projectFile)
	{
		var host = FindHost(projectFile);
		if (host == null)
			throw new InvalidOperationException("Uno.WinUI.DevServer was not found in the NuGet package cache.");

		var port = AllocatePort();
		var info = new ProcessStartInfo("dotnet") {
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = Path.GetDirectoryName(solutionFile)!,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		info.ArgumentList.Add(host);
		info.ArgumentList.Add("--httpPort");
		info.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
		info.ArgumentList.Add("--solution");
		info.ArgumentList.Add(solutionFile);
		info.ArgumentList.Add("--ppid");
		info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
		// Without this the DevServer takes the other branch of
		// ServerHotReloadProcessor.InitializeMetadataUpdater and logs
		// "Metadata updater **NOT** initialized", whose comment reads "We are relying on IDE,
		// we won't have any other hot-reload initialization steps" - i.e. it then expects the
		// IDE to carry hot reload over its own debugger channel, which OpenDevelop does not do.
		// Asking the DevServer to own the Roslyn metadata updates is what makes a saved edit
		// actually reach the running application.
		info.ArgumentList.Add("--metadata-updates");
		info.ArgumentList.Add("true");

		var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start Uno DevServer.");
		var logFile = Path.Combine(Path.GetTempPath(), "od-uno-devserver-" + process.Id + ".log");
		CaptureOutput(process, logFile);
		WaitForServerReady(port, process, logFile);
		process.EnableRaisingEvents = true;
		process.Exited += (_, _) => sessions.TryRemove(solutionFile, out _);
		return new UnoHotReloadSession(port, process, logFile);
	}

	static void WaitForServerReady(int port, Process process, string logFile)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
		while (DateTime.UtcNow < deadline) {
			if (process.HasExited)
				throw new InvalidOperationException($"Uno DevServer exited with code {process.ExitCode}. See '{logFile}'.");
			try {
				using var client = new TcpClient(AddressFamily.InterNetwork);
				var connect = client.ConnectAsync(IPAddress.Loopback, port);
				if (connect.Wait(TimeSpan.FromMilliseconds(250)) && client.Connected)
					return;
			} catch (Exception) {
				// The host has not opened its socket yet.
			}
			Thread.Sleep(100);
		}
		throw new TimeoutException($"Uno DevServer did not listen on 127.0.0.1:{port} within 45 seconds. See '{logFile}'.");
	}

	static void CaptureOutput(Process process, string logFile)
	{
		var sync = new object();
		void Append(DataReceivedEventArgs args)
		{
			if (args.Data == null) return;
			lock (sync) File.AppendAllText(logFile, args.Data + Environment.NewLine);
		}
		process.OutputDataReceived += (_, e) => Append(e);
		process.ErrorDataReceived += (_, e) => Append(e);
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
	}

	static string FindHost(string projectFile)
	{
		var packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
		if (string.IsNullOrEmpty(packageRoot))
			packageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
		var devServerRoot = Path.Combine(packageRoot, "uno.winui.devserver");
		if (!Directory.Exists(devServerRoot))
			return null;

		var hostTfm = Environment.Version.Major >= 10 ? "net10.0" : "net9.0";
		var packageVersion = FindDevServerVersion(projectFile);
		if (!string.IsNullOrEmpty(packageVersion)) {
			var matchingHost = Path.Combine(devServerRoot, packageVersion, "tools", "rc", "host", hostTfm, "Uno.UI.RemoteControl.Host.dll");
			if (File.Exists(matchingHost))
				return matchingHost;
		}

		return Directory.EnumerateDirectories(devServerRoot)
			.OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
			.Select(version => Path.Combine(version, "tools", "rc", "host", hostTfm, "Uno.UI.RemoteControl.Host.dll"))
			.FirstOrDefault(File.Exists);
	}

	static string FindDevServerVersion(string projectFile)
	{
		var assets = Path.Combine(Path.GetDirectoryName(projectFile) ?? string.Empty, "obj", "project.assets.json");
		if (!File.Exists(assets))
			return null;
		try {
			using var document = JsonDocument.Parse(File.ReadAllText(assets));
			if (!document.RootElement.TryGetProperty("libraries", out var libraries))
				return null;
			foreach (var library in libraries.EnumerateObject()) {
				var separator = library.Name.LastIndexOf('/');
				if (separator > 0
					&& (library.Name.StartsWith("uno.winui.devserver/", StringComparison.OrdinalIgnoreCase)
						|| library.Name.StartsWith("uno.ui.devserver/", StringComparison.OrdinalIgnoreCase)))
					return library.Name.Substring(separator + 1);
			}
		} catch (JsonException) {
			// The regular fallback below retains the normal Run behaviour when a restore is incomplete.
		}
		return null;
	}

	static int AllocatePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}

	sealed record UnoHotReloadSession(int Port, Process Process, string LogFile);
}
