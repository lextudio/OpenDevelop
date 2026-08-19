// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// CSS/SCSS/LESS LSP registration, mirroring RegisterTypeScriptLanguageServiceCommand. This
// addin owns ALL of its own LSP wiring: it resolves vscode-css-language-server itself (via the
// shared NpmLanguageServerLocator) and registers its own LspServerLaunchSpecs directly via
// LspServiceManager.RegisterExtension - LspServerRegistry.CreateDefault (Base) does not know
// CSS exists at all. Then it binds the same extensions to LspServiceManager.GetService on
// LanguageServiceRegistry so a .css/.scss/.less document is actually served by the language
// service instead of falling back to lexical-only highlighting.
//
// Lives in its own addin (CssBinding), not AvalonEdit.AddIn, so a user who doesn't do web
// development can disable/remove CSS support without touching the text editor itself - the
// same reasoning FSharpBinding/VBBinding/XamlBinding/TypeScriptBinding already follow for their
// own languages.

using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Lsp;

namespace CssBinding
{
	public sealed class RegisterCssLanguageServiceCommand : AbstractCommand, IDisposable
	{
		IDisposable cssRegistration;
		IDisposable scssRegistration;
		IDisposable lessRegistration;

		public override void Run()
		{
			var cssLsp = NpmLanguageServerLocator.TryFindBinShim(
				"OD_CSS_LSP_BIN", "vscode-langservers-extracted", "vscode-css-language-server");
			if (cssLsp != null)
			{
				// One binary serves all three dialects, but the server picks its parser from
				// the LSP languageId sent in textDocument/didOpen, not the file extension - so
				// each extension gets its own LspServerLaunchSpec (own languageId, own
				// LspLanguageService/child process).
				LspServiceManager.RegisterExtension(".css", new LspServerLaunchSpec("css", cssLsp, null, "--stdio"));
				LspServiceManager.RegisterExtension(".scss", new LspServerLaunchSpec("scss", cssLsp, null, "--stdio"));
				LspServiceManager.RegisterExtension(".less", new LspServerLaunchSpec("less", cssLsp, null, "--stdio"));
			}

			var registry = SD.GetRequiredService<LanguageServiceRegistry>();
			cssRegistration = registry.RegisterExtension(".css", LspServiceManager.GetService);
			scssRegistration = registry.RegisterExtension(".scss", LspServiceManager.GetService);
			lessRegistration = registry.RegisterExtension(".less", LspServiceManager.GetService);
			LoggingService.Debug("Registered CSS/SCSS/LESS extensions with LanguageServiceRegistry.");
		}

		public void Dispose()
		{
			cssRegistration?.Dispose();
			scssRegistration?.Dispose();
			lessRegistration?.Dispose();
		}
	}
}
