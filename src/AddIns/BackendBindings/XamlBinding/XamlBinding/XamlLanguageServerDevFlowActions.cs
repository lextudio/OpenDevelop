// DevFlow action used by tests/OpenDevelop.IntegrationTests to check which XAML language server a
// file is routed to: each XAML runtime has its own, and a file must never reach another's.

using System.IO;
using System.Text.Json;

using ICSharpCode.SharpDevelop.LanguageServices.Xaml;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.XamlBinding
{
	[DevFlowUIThread]
	public static class XamlLanguageServerDevFlowActions
	{
		[DevFlowAction("od.xaml.language-server", Description = "Which XAML language server serves a .xaml file: its runtime key, launch command and arguments (null server = no language service)")]
		public static string GetXamlLanguageServer(string fileName)
		{
			var key = XamlLanguageServers.ResolveKey(fileName);
			var spec = XamlLanguageServers.GetLaunchSpec(fileName);
			var arguments = spec?.Arguments;
			return JsonSerializer.Serialize(new {
				success = true,
				key,
				server = arguments == null ? null : Path.GetFileName(arguments[1]),
				workspace = arguments == null ? null : arguments[3],
				arguments,
			});
		}
	}
}
