using System;
using System.IO;
using System.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Lsp;

namespace ICSharpCode.XamlBinding
{
	/// <summary>
	/// XAML LSP registration, mirroring TypeScriptBinding/CssBinding/HtmlBinding's pattern
	/// (RegisterTypeScriptLanguageServiceCommand et al.). This addin owns ALL of its own LSP
	/// wiring: it resolves the wpf-xaml-ls.dll server itself and registers its own
	/// LspServerLaunchSpec directly via LspServiceManager.RegisterExtension -
	/// LspServerRegistry.CreateDefault (Base) does not know wpf-xaml-ls exists at all. Then it
	/// binds ".xaml" to LspServiceManager.GetService on LanguageServiceRegistry so a .xaml
	/// document is actually served by the language service instead of falling back to
	/// lexical-only highlighting.
	/// </summary>
	public sealed class RegisterXamlLanguageServiceCommand : AbstractCommand, IDisposable
	{
		IDisposable registration;

		public override void Run()
		{
			var wpfServerDll = TryFindWpfLanguageServerDll();
			if (wpfServerDll != null)
			{
				var xaml = new LspServerLaunchSpec(
					"xaml",
					"dotnet",
					Path.GetDirectoryName(wpfServerDll),
					"exec",
					wpfServerDll,
					"--workspace",
					FindOpenDevelopRoot());
				LspServiceManager.RegisterExtension(".xaml", xaml);
			}

			registration = SD.GetRequiredService<LanguageServiceRegistry>()
				.RegisterExtension(".xaml", LspServiceManager.GetService);
		}

		public void Dispose() => registration?.Dispose();

		/// <summary>
		/// Finds wpf-xaml-ls.dll, preferring the deployed copy under AddIns/LanguageServices -
		/// which a published OpenDevelop.app bundle carries alongside the rest of AddIns (see
		/// XamlLanguageServer.Wpf.csproj's DeployToAddIns target and build-application-bundle.sh's
		/// keep_addin_folders) - and falling back to XamlLanguageServer.Wpf's own bin output for
		/// dev-mode runs from the source tree (preferring Release over Debug and the most
		/// recently written match within a configuration, since multiple TFM/RID subfolders are
		/// possible depending on how it was last built). Returns null if neither location has
		/// it, in which case ".xaml" is left unregistered (LspServiceManager.GetService already
		/// handles a missing registration by falling back to lexical-only highlighting) rather
		/// than falling back to "dotnet run" - see the launch spec's comment in
		/// RegisterTypeScriptLanguageServiceCommand-style addins for why "dotnet exec" against a
		/// prebuilt dll is required instead ("dotnet run" writes NuGet/MSBuild progress to
		/// stdout, corrupting the stdio-framed LSP protocol that shares the same stream).
		/// </summary>
		static string TryFindWpfLanguageServerDll()
		{
			var deployedDll = Path.Combine(AppContext.BaseDirectory, "AddIns", "LanguageServices", "XamlLanguageServer.Wpf", "wpf-xaml-ls.dll");
			if (File.Exists(deployedDll))
				return deployedDll;

			var binRoot = Path.Combine(FindOpenDevelopRoot(), "externals", "vscode-wpf", "src", "XamlLanguageServer.Wpf", "bin");
			if (!Directory.Exists(binRoot))
				return null;

			return new[] { "Release", "Debug" }
				.Select(configuration => Path.Combine(binRoot, configuration))
				.Where(Directory.Exists)
				.SelectMany(configurationDirectory => Directory.GetFiles(configurationDirectory, "wpf-xaml-ls.dll", SearchOption.AllDirectories))
				.OrderByDescending(File.GetLastWriteTimeUtc)
				.FirstOrDefault();
		}

		static string FindOpenDevelopRoot()
		{
			var candidates = new[]
			{
				AppContext.BaseDirectory,
				Environment.CurrentDirectory
			};

			foreach (var candidate in candidates)
			{
				var root = FindOpenDevelopRoot(candidate);
				if (root != null)
					return root;
			}

			return Environment.CurrentDirectory;
		}

		static string FindOpenDevelopRoot(string startDirectory)
		{
			if (string.IsNullOrEmpty(startDirectory))
				return null;

			var directory = new DirectoryInfo(startDirectory);
			while (directory != null)
			{
				if (Directory.Exists(Path.Combine(directory.FullName, "externals", "vscode-wpf")) &&
					Directory.Exists(Path.Combine(directory.FullName, "src", "Main", "Base")))
					return directory.FullName;

				directory = directory.Parent;
			}

			return null;
		}
	}
}
