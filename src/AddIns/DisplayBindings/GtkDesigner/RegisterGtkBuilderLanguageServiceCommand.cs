using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace ICSharpCode.GtkDesigner
{
	/// <summary>
	/// Registers <see cref="GtkUiLanguageService"/> as the ".ui" language service. Like the XAML
	/// addin's registration this is a single binding per extension: completion, hover, outline and
	/// the navigation commands all resolve the service through
	/// <see cref="LanguageServiceRegistry"/>, so nothing else has to know about GtkBuilder.
	/// </summary>
	public sealed class RegisterGtkBuilderLanguageServiceCommand : AbstractCommand, IDisposable
	{
		IDisposable registration;

		public override void Run()
		{
			registration = SD.GetRequiredService<LanguageServiceRegistry>()
				.RegisterExtension(".ui", new GtkUiLanguageService());
		}

		public void Dispose()
		{
			registration?.Dispose();
			registration = null;
		}
	}

	/// <summary>
	/// Opens completion for GtkBuilder files. The XML triggers are different from C#'s: '&lt;' starts an element
	/// name, '=' and quotes start an attribute value, and a space inside a tag continues with the next
	/// attribute. The service decides what the position means and returns nothing elsewhere, so a space in a
	/// label does not open a window.
	/// </summary>
	public sealed class GtkUiCodeCompletionBinding : LanguageServiceCodeCompletionBinding
	{
		protected override bool IsTriggerCharacter(char ch)
			=> ch == '<' || ch == '"' || ch == '\'' || ch == '=' || ch == ' ' || ch == ':' || ch == '.' || ch == '-' || ch == '_'
				|| char.IsLetterOrDigit(ch);
	}
}
