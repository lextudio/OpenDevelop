using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ICSharpCode.SharpDevelop.LanguageServices.Lsp
{
	public static class LspServiceManager
	{
		static readonly LspServerRegistry registry = LspServerRegistry.CreateDefault();
		static readonly Dictionary<string, LspLanguageService> services = new(StringComparer.OrdinalIgnoreCase);
		static readonly object syncRoot = new();

		/// <summary>
		/// Allows addins to register additional LSP server mappings at startup.
		/// Called by addin startup commands, not from the Base project.
		/// </summary>
		public static void RegisterExtension(string extension, LspServerLaunchSpec spec)
		{
			if (spec is null)
				throw new ArgumentNullException(nameof(spec));
			lock (syncRoot) {
				registry.Register(extension, spec);
			}
		}

		public static LspLanguageService GetService(string fileName)
		{
			var extension = Path.GetExtension(fileName);
			LspServerLaunchSpec spec;
			lock (syncRoot) {
				if (!registry.TryGetLaunchSpec(extension, out spec))
					return null;
			}

			var rootPath = FindWorkspaceRoot(fileName);
			var key = spec.LanguageId + "\0" + rootPath;
			lock (syncRoot) {
				if (!services.TryGetValue(key, out var service)) {
					var rootUri = new Uri(rootPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
						? rootPath
						: rootPath + Path.DirectorySeparatorChar).AbsoluteUri;
					service = new LspLanguageService(spec, rootUri);
					services[key] = service;
				}
				return service;
			}
		}

		static string FindWorkspaceRoot(string fileName)
		{
			var directory = new DirectoryInfo(Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory);
			while (directory != null) {
				if (Directory.EnumerateFiles(directory.FullName, "*.sln*").Any()
				    || Directory.EnumerateFiles(directory.FullName, "*.*proj").Any())
					return directory.FullName;
				directory = directory.Parent;
			}
			return Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory;
		}
	}
}
