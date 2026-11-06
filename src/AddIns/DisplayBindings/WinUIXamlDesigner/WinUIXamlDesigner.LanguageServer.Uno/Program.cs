using ICSharpCode.WinUIXamlDesigner.LanguageServer.Uno;
using XamlToCSharpGenerator.LanguageServer.Hosting;
using XamlToCSharpGenerator.LanguageService.Framework.Uno;

// Retired source entry point. The project now deploys Microsoft's profile-aware
// WinUiXaml.LanguageServer with --framework uno instead of the AXSG-based host below.
Environment.ExitCode = await XamlLanguageServerHost.RunAsync(
    args,
    UnoLanguageFrameworkProvider.Instance.Framework,
    UnoTier1ReferenceSet.Instance);
