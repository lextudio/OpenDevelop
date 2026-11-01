namespace ICSharpCode.SharpDevelop.LanguageServices.Lsp
{
	/// <summary>Completion for files served by an external language server (TypeScript, Blueprint, ...): the
	/// punctuation that opens member/attribute/value lists in those languages, and any letter.</summary>
	public sealed class LspCodeCompletionBinding : LanguageServiceCodeCompletionBinding
	{
		protected override bool IsTriggerCharacter(char ch)
			=> ch == '<' || ch == '.' || ch == ':' || ch == '=' || char.IsLetter(ch) || ch == '_';
	}
}
