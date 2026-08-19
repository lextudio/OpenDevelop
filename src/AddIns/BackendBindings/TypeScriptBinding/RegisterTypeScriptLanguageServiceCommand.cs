// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// TypeScript/JavaScript LSP registration, mirroring the F# addin's pattern
// (RegisterFSharpLanguageServiceCommand). This addin owns ALL of its own LSP wiring: it resolves
// the TypeScript 7 (Go) server binary itself (TryFindTypeScriptGoBinary, using the shared
// NpmLanguageServerLocator) and registers its own LspServerLaunchSpecs directly via
// LspServiceManager.RegisterExtension - LspServerRegistry.CreateDefault (Base) does not know
// TypeScript exists at all. Then it binds the same extensions to LspServiceManager.GetService
// on LanguageServiceRegistry so a .ts/.tsx/.js/.jsx document is actually served by the language
// service instead of falling back to lexical-only highlighting.
//
// Lives in its own addin (TypeScriptBinding), not AvalonEdit.AddIn, so a user who doesn't do
// web development can disable/remove TypeScript support without touching the text editor
// itself - the same reasoning FSharpBinding/VBBinding/XamlBinding already follow for their own
// languages.

using System;
using System.IO;
using System.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Lsp;

namespace TypeScriptBinding
{
	public sealed class RegisterTypeScriptLanguageServiceCommand : AbstractCommand, IDisposable
	{
		IDisposable tsRegistration;
		IDisposable tsxRegistration;
		IDisposable jsRegistration;
		IDisposable jsxRegistration;

		public override void Run()
		{
			var tsGo = TryFindTypeScriptGoBinary();
			if (tsGo != null)
			{
				var typescript = new LspServerLaunchSpec("typescript", tsGo, null, "--lsp", "--stdio");
				LspServiceManager.RegisterExtension(".ts", typescript);
				LspServiceManager.RegisterExtension(".tsx", typescript);
				var javascript = new LspServerLaunchSpec("javascript", tsGo, null, "--lsp", "--stdio");
				LspServiceManager.RegisterExtension(".js", javascript);
				LspServiceManager.RegisterExtension(".jsx", javascript);
			}

			var registry = SD.GetRequiredService<LanguageServiceRegistry>();
			tsRegistration = registry.RegisterExtension(".ts", LspServiceManager.GetService);
			tsxRegistration = registry.RegisterExtension(".tsx", LspServiceManager.GetService);
			jsRegistration = registry.RegisterExtension(".js", LspServiceManager.GetService);
			jsxRegistration = registry.RegisterExtension(".jsx", LspServiceManager.GetService);
			LoggingService.Debug("Registered TS/JS extensions with LanguageServiceRegistry.");
		}

		public void Dispose()
		{
			tsRegistration?.Dispose();
			tsxRegistration?.Dispose();
			jsRegistration?.Dispose();
			jsxRegistration?.Dispose();
		}

		/// <summary>
		/// Locates the TypeScript 7 (Go) LSP executable installed by npm. Looks for the
		/// platform package under an npm global root (@typescript/native-preview-&lt;platform&gt;,
		/// or @typescript/typescript-&lt;platform&gt; for the GA package), preferring the preview
		/// binary. The executable is a native binary, not a node shim, so it can be started
		/// with Process.Start directly. Overridable via the OD_TSGO_BIN environment variable.
		/// Returns null when npm has never installed it - the caller then leaves the TS/JS
		/// extensions unregistered rather than falling back to the old Node-based
		/// typescript-language-server shim.
		/// </summary>
		static string TryFindTypeScriptGoBinary()
		{
			var envBin = Environment.GetEnvironmentVariable("OD_TSGO_BIN");
			if (!string.IsNullOrEmpty(envBin) && File.Exists(envBin))
				return envBin;

			var exeSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
			foreach (var globalRoot in NpmLanguageServerLocator.NpmGlobalRoots())
			{
				var tsDir = Path.Combine(globalRoot, "@typescript");
				if (!Directory.Exists(tsDir))
					continue;
				// The platform package lives under @typescript/ either flat
				// (@typescript/native-preview-darwin-arm64/...) or - for a global install -
				// nested inside the main package's node_modules
				// (@typescript/native-preview/node_modules/@typescript/native-preview-darwin-arm64/...).
				// Walk recursively for the native executable so both layouts resolve.
				foreach (var binaryName in new[] { "tsgo" + exeSuffix, "tsc" + exeSuffix })
				{
					try
					{
						// Skip the JS shim at <pkg>/bin/tsgo (#!/usr/bin/env node wrapper that
						// would need a Node runtime); the native Go executable lives at the
						// platform package's lib/tsgo (or lib/tsc for the GA package).
						var found = Directory.EnumerateFiles(tsDir, binaryName, SearchOption.AllDirectories)
							.FirstOrDefault(path => !IsNodeShim(path));
						if (found != null)
							return found;
					}
					catch (IOException)
					{
					}
					catch (UnauthorizedAccessException)
					{
					}
				}
			}
			return null;
		}

		/// <summary>
		/// True for the npm bin shims (e.g. &lt;pkg&gt;/bin/tsgo, a &lt;code&gt;#!/usr/bin/env node&lt;/code&gt;
		/// JS wrapper that would drag a Node runtime into the LSP launch). The real native
		/// TypeScript 7 executable lives in the platform package's lib directory.
		/// </summary>
		static bool IsNodeShim(string path)
		{
			return path.IndexOf("bin" + Path.DirectorySeparatorChar + "tsgo", StringComparison.OrdinalIgnoreCase) >= 0
				|| path.IndexOf("bin" + Path.DirectorySeparatorChar + "tsc", StringComparison.OrdinalIgnoreCase) >= 0;
		}
	}
}
