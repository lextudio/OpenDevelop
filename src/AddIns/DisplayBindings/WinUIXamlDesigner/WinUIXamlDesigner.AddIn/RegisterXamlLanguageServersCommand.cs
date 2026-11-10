using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

namespace ICSharpCode.WinUIXamlDesigner
{
	/// <summary>
	/// Registers this addin's XAML language servers, one per runtime it designs: Microsoft's
	/// profile-aware WinUiXaml.LanguageServer for Microsoft WinUI and Uno. Both are
	/// WinUI markup, but each runtime implements Microsoft.UI.Xaml with its own assemblies, so each
	/// has its own server reading only those. XamlBinding's ".xaml" resolver routes a file to the
	/// server registered for its runtime (see <see cref="XamlLanguageServers"/>).
	/// </summary>
	public sealed class RegisterXamlLanguageServersCommand : AbstractCommand, IDisposable
	{
		IDisposable winui;
		IDisposable uno;

		public override void Run()
		{
			winui = Register(XamlLanguageServers.MicrosoftWinUI, "WinUIXamlLanguageServer", "WinUiXaml.LanguageServer.dll", "--framework", "winui");
			uno = Register(XamlLanguageServers.Uno, "UnoXamlLanguageServer", "WinUiXaml.LanguageServer.dll", "--framework", "uno");
			// The XAML source editor's Toolbox for WinUI/Uno files (see XamlDialectRegistry).
			XamlDialectRegistry.RegisterBuiltInToolsContent(XamlDialectKeys.WinUI, () => WinUIXamlToolbox.Instance.ToolboxControl);
			XamlDialectRegistry.RegisterBuiltInToolsContent(XamlDialectKeys.Uno, () => WinUIXamlToolbox.Instance.ToolboxControl);
		}

		static IDisposable Register(string key, string folder, string assembly, params string[] arguments)
		{
			var server = XamlLanguageServers.FindDeployedServer(folder, assembly);
			if (server == null)
			{
				LoggingService.Warn($"WinUIXamlDesigner: {assembly} is not deployed (AddIns/LanguageServices/{folder}); {key} XAML has no language service.");
				return null;
			}
			return XamlLanguageServers.Register(key, server, arguments);
		}

		public void Dispose()
		{
			winui?.Dispose();
			uno?.Dispose();
		}
	}
}
