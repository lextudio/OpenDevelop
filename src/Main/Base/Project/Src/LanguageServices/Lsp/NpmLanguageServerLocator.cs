// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Shared npm-installed-LSP-binary resolution helpers, used by each language addin's own
// Register*LanguageServiceCommand (TypeScriptBinding, CssBinding, HtmlBinding, ...) to find its
// own server binary and register it directly via LspServiceManager.RegisterExtension -
// LspServerRegistry.CreateDefault (in this same project) deliberately does NOT know about any
// npm-installed language server: that per-language binary-resolution knowledge belongs in each
// language's own addin, not in the shared Base IDE-semantic-service layer (see
// doc/technotes/language-services.md's layering rules) - a user who disables/removes e.g.
// CssBinding should mean Base never even tries to resolve vscode-css-language-server.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ICSharpCode.SharpDevelop.LanguageServices.Lsp
{
	public static class NpmLanguageServerLocator
	{
		/// <summary>
		/// Candidate npm global roots (where package bin links and platform packages live):
		/// Homebrew and /usr/local installs, the user's .npm-global prefix, every nvm node
		/// version directory, and the prefix implied by `node` found on PATH. Distinct roots
		/// are returned in that order.
		/// </summary>
		public static IEnumerable<string> NpmGlobalRoots()
		{
			var roots = new List<string>
			{
				"/opt/homebrew/lib/node_modules",
				"/usr/local/lib/node_modules",
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npm-global", "lib", "node_modules")
			};
			try
			{
				var nvmVersions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nvm", "versions", "node");
				if (Directory.Exists(nvmVersions))
					foreach (var versionDirectory in Directory.GetDirectories(nvmVersions))
						roots.Add(Path.Combine(versionDirectory, "lib", "node_modules"));
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			try
			{
				var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
				foreach (var directory in pathVariable.Split(Path.PathSeparator))
				{
					if (string.IsNullOrEmpty(directory) || !File.Exists(Path.Combine(directory, "node")))
						continue;
					var prefix = Directory.GetParent(directory)?.FullName;
					if (prefix != null)
						roots.Add(Path.Combine(prefix, "lib", "node_modules"));
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			return roots.Distinct(StringComparer.Ordinal);
		}

		/// <summary>
		/// Locates a plain npm bin shim (a <c>#!/usr/bin/env node</c> script, or its Windows
		/// <c>.cmd</c> proxy) that Process.Start can run directly - e.g.
		/// vscode-css-language-server, vscode-html-language-server. Checks the given environment
		/// variable override first, then PATH (the common case: npm's global bin directory is
		/// already on PATH after a global install), then walks <see cref="NpmGlobalRoots"/>
		/// directly for a global install whose bin symlink was never added to PATH. Returns null
		/// when never installed.
		/// </summary>
		public static string TryFindBinShim(string envVarName, string npmPackageName, string binaryBaseName)
		{
			var envBin = Environment.GetEnvironmentVariable(envVarName);
			if (!string.IsNullOrEmpty(envBin) && File.Exists(envBin))
				return envBin;

			var binaryName = binaryBaseName + (OperatingSystem.IsWindows() ? ".cmd" : string.Empty);
			var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
			foreach (var directory in pathVariable.Split(Path.PathSeparator))
			{
				if (string.IsNullOrEmpty(directory))
					continue;
				var candidate = Path.Combine(directory, binaryName);
				if (File.Exists(candidate))
					return candidate;
			}
			foreach (var globalRoot in NpmGlobalRoots())
			{
				var candidate = Path.Combine(globalRoot, npmPackageName, "bin", binaryName);
				if (File.Exists(candidate))
					return candidate;
			}
			return null;
		}
	}
}
