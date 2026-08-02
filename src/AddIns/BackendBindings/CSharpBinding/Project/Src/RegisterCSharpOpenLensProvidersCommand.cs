using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;

namespace CSharpBinding
{
	public sealed class RegisterCSharpOpenLensProvidersCommand : AbstractCommand, IDisposable
	{
		IDisposable anchorRegistration;
		IDisposable providerRegistration;

		public override void Run()
		{
			var registry = SD.GetRequiredService<OpenLensProviderRegistry>();
			anchorRegistration = registry.RegisterAnchorProvider(new LanguageOpenLensAnchorProvider("CSharp", ".cs"));
			providerRegistration = registry.RegisterProvider(new LanguageOpenLensProvider("CSharp", ".cs"));
		}

		public void Dispose()
		{
			anchorRegistration?.Dispose();
			providerRegistration?.Dispose();
		}
	}
}
