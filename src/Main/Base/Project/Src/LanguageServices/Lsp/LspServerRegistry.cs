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
            // TypeScript, CSS/SCSS/LESS, HTML, and XAML deliberately do NOT register here - each
            // has its own addin (TypeScriptBinding, CssBinding, HtmlBinding, XamlBinding) that
            // resolves its own server binary (via the shared NpmLanguageServerLocator, or - for
            // XAML - by locating the bundled wpf-xaml-ls.dll) and registers itself directly with
            // LspServiceManager.RegisterExtension at addin startup (see XamlBinding's
            // RegisterXamlLanguageServiceCommand). This registry (and the rest of Base) has no
            // business knowing that TypeScript needs a Go binary, that CSS/HTML need a Node one,
            // or that XAML needs a WPF-hosted Roslyn server - that's exactly the kind of
            // per-language knowledge the "IDE semantic service layer" is supposed to stay
            // ignorant of (see doc/technotes/language-services.md's layering rules), and it means
            // disabling e.g. XamlBinding actually stops Base from even trying to resolve
            // wpf-xaml-ls, not just from registering the extension mapping.
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

        static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                throw new ArgumentException("An extension is required.", nameof(extension));

            return extension[0] == '.' ? extension : "." + extension;
        }
    }
}
