using System;
using System.Runtime.CompilerServices;
using System.Threading;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace ICSharpCode.Translations
{
	/// <summary>Registers <see cref="PoLanguageService"/> as the ".po" language service: completion, hover, diagnostics, outline
	/// and go to definition all resolve it through <see cref="LanguageServiceRegistry"/>, so nothing else knows about gettext.</summary>
	public sealed class RegisterPoLanguageServiceCommand : AbstractCommand, IDisposable
	{
		IDisposable registration;

		public override void Run()
		{
			registration = SD.GetRequiredService<LanguageServiceRegistry>().RegisterExtension(".po", new PoLanguageService());
		}

		public void Dispose()
		{
			registration?.Dispose();
			registration = null;
		}
	}

	/// <summary>Opens completion for .po files on the keys that mean something there ('#,' flags, a quote inside msgstr,
	/// keyword letters). The service decides what the position means and returns nothing elsewhere.</summary>
	public sealed class PoCodeCompletionBinding : LanguageServiceCodeCompletionBinding
	{
		// ',' and a space reach the flags after "#,"; a quote reaches the source text inside msgstr "; letters reach the keywords.
		protected override bool IsTriggerCharacter(char ch)
			=> ch == ',' || ch == ' ' || ch == '"' || ch == '-' || ch == '[' || char.IsLetterOrDigit(ch);
	}
}
