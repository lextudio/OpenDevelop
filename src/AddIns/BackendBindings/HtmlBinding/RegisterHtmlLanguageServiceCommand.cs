// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// HTML LSP registration, mirroring RegisterCssLanguageServiceCommand. This addin owns ALL of
// its own LSP wiring: it resolves vscode-html-language-server itself (via the shared
// NpmLanguageServerLocator, the same vscode-langservers-extracted package CSS uses) and
// registers its own LspServerLaunchSpec directly via LspServiceManager.RegisterExtension -
// LspServerRegistry.CreateDefault (Base) does not know HTML exists at all. Then it binds the
// same extensions to LspServiceManager.GetService on LanguageServiceRegistry so a .html/.htm
// document is actually served by the language service instead of falling back to lexical-only
// highlighting.
//
// Lives in its own addin (HtmlBinding), not AvalonEdit.AddIn, so a user who doesn't do web
// development can disable/remove HTML support without touching the text editor itself - the
// same reasoning FSharpBinding/VBBinding/XamlBinding/TypeScriptBinding/CssBinding already follow
// for their own languages.

using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Lsp;

namespace HtmlBinding
{
	public sealed class RegisterHtmlLanguageServiceCommand : AbstractCommand, IDisposable
	{
		IDisposable htmRegistration;
		IDisposable htmlRegistration;

		public override void Run()
		{
			var htmlLsp = NpmLanguageServerLocator.TryFindBinShim(
				"OD_HTML_LSP_BIN", "vscode-langservers-extracted", "vscode-html-language-server");
			if (htmlLsp != null)
			{
				var html = new LspServerLaunchSpec("html", htmlLsp, null, "--stdio");
				LspServiceManager.RegisterExtension(".html", html);
				LspServiceManager.RegisterExtension(".htm", html);
			}

			var registry = SD.GetRequiredService<LanguageServiceRegistry>();
			htmRegistration = registry.RegisterExtension(".htm", LspServiceManager.GetService);
			htmlRegistration = registry.RegisterExtension(".html", LspServiceManager.GetService);
			LoggingService.Debug("Registered HTML extensions with LanguageServiceRegistry.");
		}

		public void Dispose()
		{
			htmRegistration?.Dispose();
			htmlRegistration?.Dispose();
		}
	}
}
