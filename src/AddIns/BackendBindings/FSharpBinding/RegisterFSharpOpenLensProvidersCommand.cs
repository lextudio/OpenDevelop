using System;
using System.Collections.Generic;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;

namespace FSharpBinding
{
	// Mirrors CSharpBinding.RegisterCSharpOpenLensProvidersCommand: registers the shared,
	// ILanguageService-backed anchor + lens providers for F# source files. Anchors come from
	// ILanguageService.GetDocumentOutlineAsync (the LSP textDocument/documentSymbol response of
	// the fsautocomplete server) and lenses resolve through FindReferencesAsync
	// (textDocument/references) - the same LSP capabilities Roslyn provides for C#/VB, so .fs
	// files get the same "N references" / "M implementations" lenses above declarations.
	public sealed class RegisterFSharpOpenLensProvidersCommand : AbstractCommand, IDisposable
	{
		readonly List<IDisposable> registrations = new List<IDisposable>();

		public override void Run()
		{
			var registry = SD.GetRequiredService<OpenLensProviderRegistry>();
			registrations.Add(registry.RegisterAnchorProvider(new LanguageOpenLensAnchorProvider("FSharp", ".fs")));
			registrations.Add(registry.RegisterAnchorProvider(new LanguageOpenLensAnchorProvider("FSharpSignature", ".fsi")));
			registrations.Add(registry.RegisterProvider(new LanguageOpenLensProvider("FSharp", ".fs")));
			registrations.Add(registry.RegisterProvider(new LanguageOpenLensProvider("FSharpSignature", ".fsi")));
		}

		public void Dispose()
		{
			foreach (var registration in registrations)
				registration.Dispose();
			registrations.Clear();
		}
	}
}
