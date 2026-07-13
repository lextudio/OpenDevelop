using System;
using System.Collections.Generic;
using System.IO;

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
            var xamlServerProject = Path.Combine(vscodeWpfRoot, "src", "XamlLanguageServer.Wpf", "XamlLanguageServer.Wpf.csproj");
            var xaml = new LspServerLaunchSpec(
                "xaml",
                "dotnet",
                vscodeWpfRoot,
                "run",
                "--project",
                xamlServerProject,
                "--",
                "--workspace",
                repositoryRoot);
            registry.Register(".xaml", xaml);
            var fsAutoComplete = new LspServerLaunchSpec(
                "fsharp",
                "dotnet",
                repositoryRoot,
                "fsautocomplete",
                "--background-service-enabled",
                "--workspace",
                repositoryRoot);
            registry.Register(".fs", fsAutoComplete);
            registry.Register(".fsi", fsAutoComplete);
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
