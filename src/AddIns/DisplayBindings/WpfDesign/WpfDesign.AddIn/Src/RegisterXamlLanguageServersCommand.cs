using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// Registers the XAML language servers of the two WPF runtimes this addin designs:
	/// profile-aware WinUiXaml.LanguageServer for Microsoft WPF and LibreWPF. Both
	/// are WPF markup, but they are separate runtimes with separate assemblies, so each has its own
	/// server reading only its own. XamlBinding's ".xaml" resolver routes a file to the server
	/// registered for its runtime (see <see cref="XamlLanguageServers"/>).
	/// </summary>
	public sealed class RegisterXamlLanguageServersCommand : AbstractCommand, IDisposable
	{
		IDisposable microsoftWpf;
		IDisposable libreWpf;

		public override void Run()
		{
			microsoftWpf = Register(XamlLanguageServers.MicrosoftWpf,
				XamlLanguageServers.FindDeployedServer("MicrosoftWpfXamlLanguageServer", "WinUiXaml.LanguageServer.dll"),
				"WinUiXaml.LanguageServer.dll", "--framework", "wpf");
			libreWpf = Register(XamlLanguageServers.LibreWpf,
				XamlLanguageServers.FindDeployedServer("LibreWpfXamlLanguageServer", "WinUiXaml.LanguageServer.dll"),
				"WinUiXaml.LanguageServer.dll", "--framework", "librewpf");
			// The XAML source editor's Toolbox for WPF files (see XamlDialectRegistry).
			XamlDialectRegistry.RegisterBuiltInToolsContent(XamlDialectKeys.Wpf, () => WpfToolbox.Instance.ToolboxControl);
		}

		static IDisposable Register(string key, string server, string assembly, params string[] arguments)
		{
			if (server == null)
			{
				LoggingService.Warn($"WpfDesign: {assembly} is not deployed; {key} XAML has no language service.");
				return null;
			}
			return XamlLanguageServers.Register(key, server, arguments);
		}

		public void Dispose()
		{
			microsoftWpf?.Dispose();
			libreWpf?.Dispose();
		}
	}
}
