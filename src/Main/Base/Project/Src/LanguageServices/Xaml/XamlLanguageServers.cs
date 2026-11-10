using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ICSharpCode.SharpDevelop.LanguageServices.Lsp;

namespace ICSharpCode.SharpDevelop.LanguageServices.Xaml;

/// <summary>
/// Which language server serves which XAML runtime.
/// <para>
/// Every XAML runtime has its own server executable, reading only its own assemblies: Microsoft WPF
/// (WinUiXaml.LanguageServer --framework wpf) and LibreWPF (librewpf-xaml-ls) are separate even though both are WPF markup, and
/// so are Microsoft WinUI (winui-xaml-ls) and Uno
/// (uno-xaml-ls), even though both are WinUI markup; an out-of-tree dialect (MAUI:
/// maui-xaml-ls) has its own too. The addin that owns a runtime registers its server here under that
/// runtime's key (<see cref="ResolveKey"/>). A file is routed to its runtime's server and no other,
/// and a runtime with no registered server gets no language service (lexical highlighting only)
/// rather than a server that would analyse it against another runtime's controls.
/// </para>
/// See doc/technotes/xaml-language-servers.md.
/// </summary>
public static class XamlLanguageServers
{
	static readonly Dictionary<string, ServerRegistration> servers = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Server keys of the built-in runtimes.</summary>
	public const string MicrosoftWpf = "MicrosoftWpf";
	public const string LibreWpf = "LibreWpf";
	public const string MicrosoftWinUI = "MicrosoftWinUI";
	public const string Uno = "Uno";

	/// <summary>
	/// The server key of <paramref name="xamlFileName"/>: an out-of-tree dialect's own key when a
	/// registered dialect claims the file; otherwise the runtime of the owning project. Null when the
	/// file belongs to no known runtime.
	/// </summary>
	public static string? ResolveKey(string xamlFileName)
	{
		var dialect = XamlDialectRegistry.ResolveDialect(xamlFileName);
		if (dialect == null)
			return null;
		if (!string.Equals(dialect, XamlDialectKeys.Wpf, StringComparison.OrdinalIgnoreCase)
		    && !string.Equals(dialect, XamlDialectKeys.WinUI, StringComparison.OrdinalIgnoreCase)
		    && !string.Equals(dialect, XamlDialectKeys.Uno, StringComparison.OrdinalIgnoreCase))
			return dialect;

		var context = XamlFrameworkDetector.Detect(xamlFileName);
		return context.Runtime switch {
			XamlRuntimeKind.MicrosoftWpf => MicrosoftWpf,
			XamlRuntimeKind.LibreWpf => LibreWpf,
			XamlRuntimeKind.MicrosoftWinUI => MicrosoftWinUI,
			XamlRuntimeKind.Uno => Uno,
			_ => null
		};
	}

	/// <summary>Registers <paramref name="serverAssembly"/> (a managed dll started with
	/// <c>dotnet exec</c>) as the language server for <paramref name="key"/> (see
	/// <see cref="ResolveKey"/>). Registering a key again replaces the earlier server; disposing the
	/// result removes it.</summary>
	public static IDisposable Register(string key, string serverAssembly, params string[] serverArguments)
	{
		if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A server key is required.", nameof(key));
		if (string.IsNullOrWhiteSpace(serverAssembly)) throw new ArgumentException("A server assembly is required.", nameof(serverAssembly));
		var registration = new ServerRegistration(serverAssembly, serverArguments ?? Array.Empty<string>());
		lock (servers)
			servers[key] = registration;
		return new Registration(key, registration);
	}

	/// <summary>The server registered for <paramref name="key"/>, or null.</summary>
	public static string? GetServerAssembly(string key)
	{
		if (string.IsNullOrEmpty(key)) return null;
		lock (servers)
			return servers.TryGetValue(key, out var server) ? server.ServerAssembly : null;
	}

	/// <summary>
	/// How to start the server for <paramref name="xamlFileName"/>: its runtime's own server, with
	/// <c>--workspace</c> naming the directory of the project that owns the file (the server
	/// prewarms its full compilation from the first project there). Null when no server serves the
	/// file's runtime, or the file has none.
	/// </summary>
	public static LspServerLaunchSpec? GetLaunchSpec(string xamlFileName)
	{
		var key = ResolveKey(xamlFileName);
		ServerRegistration? registration;
		lock (servers)
			registration = key != null && servers.TryGetValue(key, out var registered) ? registered : null;
		if (registration == null)
			return null;
		return new LspServerLaunchSpec("xaml", "dotnet", Path.GetDirectoryName(registration.ServerAssembly),
			new[] { "exec", registration.ServerAssembly, "--workspace", FindProjectDirectory(xamlFileName) }
				.Concat(registration.ServerArguments).ToArray());
	}

	/// <summary>The directory of the project that owns the file, else the file's own directory.
	/// Never OpenDevelop's source root - which is what every document used to get, so the server
	/// compiled one of OpenDevelop's own projects and never saw the user's.</summary>
	public static string FindProjectDirectory(string fileName)
	{
		var project = XamlFrameworkDetector.Detect(fileName).ProjectFileName;
		if (!string.IsNullOrEmpty(project))
			return Path.GetDirectoryName(project)!;
		return Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory;
	}

	/// <summary>
	/// Finds a deployed server: <c>AddIns/LanguageServices/&lt;folder&gt;/&lt;assembly&gt;</c> next to
	/// the running IDE (a published build), else under the source tree's AddIns (a dev build, where
	/// each server's DeployToAddIns target copies every build it makes). Null when neither exists.
	/// </summary>
	public static string? FindDeployedServer(string folder, string assemblyFileName)
	{
		foreach (var root in CandidateRoots())
		{
			var path = Path.Combine(root, "AddIns", "LanguageServices", folder, assemblyFileName);
			if (File.Exists(path))
				return path;
		}
		return null;
	}

	static IEnumerable<string> CandidateRoots()
	{
		yield return AppContext.BaseDirectory;
		foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
		{
			for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
			{
				if (Directory.Exists(Path.Combine(directory.FullName, "AddIns", "LanguageServices"))
				    && Directory.Exists(Path.Combine(directory.FullName, "src", "Main", "Base")))
				{
					yield return directory.FullName;
					break;
				}
			}
		}
	}

	sealed class Registration : IDisposable
	{
		readonly string key;
		readonly ServerRegistration registration;

		public Registration(string key, ServerRegistration registration)
		{
			this.key = key;
			this.registration = registration;
		}

		public void Dispose()
		{
			lock (servers)
			{
				// Only remove what this registration put there, not a later replacement.
				if (servers.TryGetValue(key, out var current) && current == registration)
					servers.Remove(key);
			}
		}
	}

	sealed record ServerRegistration(string ServerAssembly, string[] ServerArguments);
}
