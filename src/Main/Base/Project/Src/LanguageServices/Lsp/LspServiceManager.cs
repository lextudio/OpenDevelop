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

		public static LspLanguageService GetService(string fileName)
		{
			var extension = Path.GetExtension(fileName);
			if (!registry.TryGetLaunchSpec(extension, out var spec))
				return null;

			var rootPath = FindWorkspaceRoot(fileName);
			var key = spec.LanguageId + "\0" + rootPath;
			if (!services.TryGetValue(key, out var service)) {
				var rootUri = new Uri(rootPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
					? rootPath
					: rootPath + Path.DirectorySeparatorChar).AbsoluteUri;
				service = new LspLanguageService(spec, rootUri);
				services[key] = service;
			}
			return service;
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
