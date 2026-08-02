using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Lsp;

namespace ICSharpCode.XamlBinding
{
	public sealed class RegisterXamlLanguageServiceCommand : AbstractCommand, IDisposable
	{
		IDisposable registration;

		public override void Run()
		{
			registration = SD.GetRequiredService<LanguageServiceRegistry>()
				.RegisterExtension(".xaml", LspServiceManager.GetService);
		}

		public void Dispose() => registration?.Dispose();
	}
}
