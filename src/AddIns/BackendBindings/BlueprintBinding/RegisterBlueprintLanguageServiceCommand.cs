using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Lsp;

namespace BlueprintBinding
{
	/// <summary>
	/// Registers the ".blp" language service: the Blueprint compiler is its own language server (<c>blueprint-compiler lsp</c>), so the
	/// IDE only has to launch it through the shared LSP client - the same wiring the TypeScript addin uses. Completion, hover,
	/// diagnostics, formatting, document symbols and go to definition then come from the real compiler. With no compiler installed
	/// the extension stays unregistered and .blp opens as highlighted text, rather than pretending to be served.
	/// </summary>
	public sealed class RegisterBlueprintLanguageServiceCommand : AbstractCommand, IDisposable
	{
		IDisposable? registration;

		public override void Run()
		{
			var compiler = BlueprintCompilerLocator.Find();
			if (compiler == null) {
				LoggingService.Info("Blueprint: blueprint-compiler was not found (set " + BlueprintCompilerLocator.EnvironmentVariable + " or put it on the PATH); .blp files are highlighted only.");
				return;
			}
			LspServiceManager.RegisterExtension(".blp", new LspServerLaunchSpec("blueprint", compiler, null, "lsp"));
			registration = SD.GetRequiredService<LanguageServiceRegistry>().RegisterExtension(".blp", LspServiceManager.GetService);
			LoggingService.Debug("Blueprint: registered .blp with the language service registry (" + compiler + ").");
		}

		public void Dispose()
		{
			registration?.Dispose();
			registration = null;
		}
	}
}
