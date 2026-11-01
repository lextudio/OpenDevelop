using ICSharpCode.Core;
using ICSharpCode.Translations;

namespace ICSharpCode.GtkDesigner;

/// <summary>Registers the GtkBuilder source with the Translations addin, so Update Translations picks up .ui files.</summary>
public sealed class RegisterGtkTranslationSourceCommand : AbstractCommand
{
	public override void Run() => TranslatableStringSources.Register(new GtkTranslatableSource());
}
