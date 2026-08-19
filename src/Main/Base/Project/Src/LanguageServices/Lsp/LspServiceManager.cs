using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.Core;

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
				if (!registry.TryGetLaunchSpec(extension, out spec)) {
					LoggingService.Debug($"LspServiceManager: no launch spec for extension '{extension}' ({fileName})");
					return null;
				}
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
			var requestedDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory;
			var directory = new DirectoryInfo(requestedDirectory);
			while (directory != null) {
				if (ContainsWorkspaceFile(directory.FullName))
					return directory.FullName;
				directory = directory.Parent;
			}
			return requestedDirectory;
		}

		static bool ContainsWorkspaceFile(string directory)
		{
			// Editor hover and other delayed UI work may run after a test/project has deleted its
			// temporary workspace. Directory.Exists alone cannot close the check/enumerate race,
			// so enumeration itself must tolerate the directory (or a mounted ancestor) vanishing.
			try {
				return Directory.EnumerateFiles(directory, "*.sln*").Any()
				       || Directory.EnumerateFiles(directory, "*.*proj").Any();
			} catch (DirectoryNotFoundException) {
				return false;
			} catch (IOException) {
				return false;
			} catch (UnauthorizedAccessException) {
				return false;
			}
		}
	}
}
