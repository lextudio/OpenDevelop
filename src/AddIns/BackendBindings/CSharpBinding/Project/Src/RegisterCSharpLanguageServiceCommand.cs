using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Roslyn;

namespace CSharpBinding
{
	public sealed class RegisterCSharpLanguageServiceCommand : AbstractCommand, IDisposable
	{
		CSharpVBLanguageService service;
		IDisposable registration;

		public override void Run()
		{
			service = new CSharpVBLanguageService();
			registration = SD.GetRequiredService<LanguageServiceRegistry>().RegisterExtension(".cs", service);
		}

		public void Dispose()
		{
			registration?.Dispose();
			service?.Dispose();
		}
	}
}
