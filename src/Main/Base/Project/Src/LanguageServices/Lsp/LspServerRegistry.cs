using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ICSharpCode.SharpDevelop.LanguageServices.Lsp
{
    public sealed class LspServerLaunchSpec
    {
        public LspServerLaunchSpec(string languageId, string command, params string[] arguments)
            : this(languageId, command, null, arguments)
        {
        }

        public LspServerLaunchSpec(string languageId, string command, string workingDirectory, params string[] arguments)
        {
            LanguageId = languageId ?? throw new ArgumentNullException(nameof(languageId));
            Command = command ?? throw new ArgumentNullException(nameof(command));
            WorkingDirectory = workingDirectory;
            Arguments = arguments ?? Array.Empty<string>();
        }

        public string LanguageId { get; }

        public string Command { get; }

        public string WorkingDirectory { get; }

        public IReadOnlyList<string> Arguments { get; }

        /// <summary>
        /// Optional <c>initializationOptions</c> payload for the LSP <c>initialize</c> request
        /// (serialized as JSON). Server-specific: e.g. fsautocomplete's
        /// <c>AutomaticWorkspaceInit</c> (see FsAutoComplete docs/communication-protocol.md) makes
        /// it auto-discover and load the workspace's projects without the client driving the
        /// <c>fsharp/workspacePeek</c>/<c>fsharp/workspaceLoad</c> dance - required before
        /// documentSymbol/references/codeLens can answer for any open F# file.
        /// </summary>
        public object InitializationOptions { get; init; }
    }

    public sealed class LspServerRegistry
    {
        readonly Dictionary<string, LspServerLaunchSpec> _specsByExtension =
            new(StringComparer.OrdinalIgnoreCase);

        public void Register(string extension, LspServerLaunchSpec spec)
        {
            if (spec is null)
                throw new ArgumentNullException(nameof(spec));

            _specsByExtension[NormalizeExtension(extension)] = spec;
        }

        public bool TryGetLaunchSpec(string extension, out LspServerLaunchSpec spec)
        {
            return _specsByExtension.TryGetValue(NormalizeExtension(extension), out spec!);
        }

        public static LspServerRegistry CreateDefault()
        {
            var registry = new LspServerRegistry();
            var repositoryRoot = FindOpenDevelopRoot();
            var vscodeWpfRoot = Path.Combine(repositoryRoot, "externals", "vscode-wpf");
            // "dotnet exec <built dll>", not "dotnet run --project <csproj>": a plain "dotnet run"
            // triggers an implicit restore/build whenever anything is out of date, and MSBuild/
            // NuGet write that progress to stdout - the same stream this process's stdio-framed
            // LSP protocol lives on, corrupting every frame after it. Confirmed directly while
            // building UnoDevelop's equivalent Uno host: a plain "dotnet run" wrote thousands of
            // bytes of NuGet warnings to stdout before the process ever spoke LSP; "dotnet exec"
            // against a prebuilt dll produced none. This does mean the project must have been
            // built at least once - if it hasn't, TryFindWpfLanguageServerDll returns null and
            // ".xaml" is left unregistered (LspServiceManager.GetService already handles a missing
            // registration by falling back to lexical-only highlighting) rather than falling back
            // to "dotnet run" and risking a corrupted pipe.
            var wpfServerDll = TryFindWpfLanguageServerDll(vscodeWpfRoot);
            if (wpfServerDll != null)
            {
                var xaml = new LspServerLaunchSpec(
                    "xaml",
                    "dotnet",
                    vscodeWpfRoot,
                    "exec",
                    wpfServerDll,
                    "--workspace",
                    repositoryRoot);
                registry.Register(".xaml", xaml);
            }
            var fsAutoComplete = new LspServerLaunchSpec(
                "fsharp",
                "dotnet",
                repositoryRoot,
                "tool",
                "run",
                "fsautocomplete",
                "--") {
                // fsautocomplete does not load any project unless told to (documentSymbol etc.
                // then fail with "Couldn't find <file> in LoadedProjects"). AutomaticWorkspaceInit
                // makes it discover the workspace itself - the documented option for clients like
                // OpenDevelop that have no custom workspace-selection UI.
                InitializationOptions = new { AutomaticWorkspaceInit = true }
            };
            registry.Register(".fs", fsAutoComplete);
            registry.Register(".fsi", fsAutoComplete);
            var pylsp = new LspServerLaunchSpec("python", "pylsp");
            registry.Register(".py", pylsp);
            var typescript = new LspServerLaunchSpec("typescript", "typescript-language-server", null, "--stdio");
            registry.Register(".ts", typescript);
            registry.Register(".tsx", typescript);
            registry.Register(".js", typescript);
            registry.Register(".jsx", typescript);
            return registry;
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

        /// <summary>
        /// Finds the built wpf-xaml-ls.dll under XamlLanguageServer.Wpf's bin output, preferring
        /// Release over Debug and the most recently written match within a configuration (multiple
        /// TFM/RID subfolders are possible depending on how it was last built). Returns null if it
        /// has never been built - see the call site's comment for why that means leaving ".xaml"
        /// unregistered rather than falling back to "dotnet run".
        /// </summary>
        static string TryFindWpfLanguageServerDll(string vscodeWpfRoot)
        {
            var binRoot = Path.Combine(vscodeWpfRoot, "src", "XamlLanguageServer.Wpf", "bin");
            if (!Directory.Exists(binRoot))
                return null;

            return new[] { "Release", "Debug" }
                .Select(configuration => Path.Combine(binRoot, configuration))
                .Where(Directory.Exists)
                .SelectMany(configurationDirectory => Directory.GetFiles(configurationDirectory, "wpf-xaml-ls.dll", SearchOption.AllDirectories))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                throw new ArgumentException("An extension is required.", nameof(extension));

            return extension[0] == '.' ? extension : "." + extension;
        }
    }
}
