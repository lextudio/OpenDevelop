using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Lsp;

namespace FSharpBinding
{
	public sealed class RegisterFSharpLanguageServiceCommand : AbstractCommand, IDisposable
	{
		IDisposable fsRegistration;
		IDisposable fsiRegistration;

		public override void Run()
		{
			var registry = SD.GetRequiredService<LanguageServiceRegistry>();
			fsRegistration = registry.RegisterExtension(".fs", LspServiceManager.GetService);
			fsiRegistration = registry.RegisterExtension(".fsi", LspServiceManager.GetService);
		}

		public void Dispose()
		{
			fsiRegistration?.Dispose();
			fsRegistration?.Dispose();
		}
	}
}
