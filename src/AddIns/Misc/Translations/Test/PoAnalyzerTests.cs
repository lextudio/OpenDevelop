using ICSharpCode.Translations;
using Xunit;

namespace Translations.Tests;

public sealed class PoAnalyzerTests
{
	const string Header = "msgid \"\"\nmsgstr \"\"\n\"Language: fr\\n\"\n\"Content-Type: text/plain; charset=UTF-8\\n\"\n\"Plural-Forms: nplurals=2; plural=(n > 1);\\n\"\n\n";

	static IReadOnlyList<PoProblem> Problems(string body, string header = Header) => PoAnalyzer.Analyze(header + body);
	static string[] Ids(string body, string header = Header) => Problems(body, header).Select(p => p.Id).ToArray();

	[Fact]
	public void Placeholders_NumberImplicitOnesInOrderAndIgnoreLiteralPercent()
	{
		Assert.Equal(new[] { (1, 's'), (2, 'd') }, PoAnalyzer.Placeholders("%s has %d files, 100%% done"));
		Assert.Equal(new[] { (2, 's'), (1, 'd') }, PoAnalyzer.Placeholders("%2$s ... %1$d"));
		Assert.Equal(new[] { (1, 'f') }, PoAnalyzer.Placeholders("%5.2f"));
		Assert.Empty(PoAnalyzer.Placeholders("no placeholders, 50% off"));
	}

	[Fact]
	public void MatchingPlaceholdersAndAReorderedTranslationAreFine()
	{
		Assert.Empty(Problems("msgid \"%s has %d files\"\nmsgstr \"%s a %d fichiers\"\n"));
		// A translation may reorder placeholders when it numbers them.
		Assert.Empty(Problems("msgid \"%s has %d files\"\nmsgstr \"%2$d fichiers pour %1$s\"\n"));
	}

	[Fact]
	public void MismatchedPlaceholdersAreAnErrorOnTheMsgstrLine()
	{
		var text = Header + "msgid \"%s has %d files\"\nmsgstr \"%s a des fichiers\"\n";
		var problem = Assert.Single(PoAnalyzer.Analyze(text));
		Assert.Equal("PO1001", problem.Id);
		Assert.Equal(PoSeverity.Error, problem.Severity);
		Assert.Equal(text.Replace("\r", "").Split('\n').ToList().FindIndex(l => l.StartsWith("msgstr \"%s a des")) + 1, problem.Line);
		Assert.Contains("do not match", problem.Message);
		Assert.Equal(new[] { "PO1001" }, Ids("msgid \"%s\"\nmsgstr \"%d\"\n")); // a different conversion
	}

	[Fact]
	public void ExtraPlaceholderInATranslationIsOnlyCheckedWhenFlaggedCFormat()
	{
		Assert.Empty(Problems("msgid \"Save\"\nmsgstr \"Enregistrer 50%\"\n"));
		Assert.Equal(new[] { "PO1001" }, Ids("#, c-format\nmsgid \"Save\"\nmsgstr \"Enregistrer %s\"\n"));
		Assert.Empty(Problems("#, no-c-format\nmsgid \"%s\"\nmsgstr \"rien\"\n"));
	}

	[Fact]
	public void PluralFormsMayDropAPlaceholderButNotAddOne()
	{
		Assert.Empty(Problems("msgid \"%d file\"\nmsgid_plural \"%d files\"\nmsgstr[0] \"un fichier\"\nmsgstr[1] \"%d fichiers\"\n"));
		Assert.Equal(new[] { "PO1001" }, Ids("msgid \"%d file\"\nmsgid_plural \"%d files\"\nmsgstr[0] \"%d fichier\"\nmsgstr[1] \"%s fichiers\"\n"));
	}

	[Fact]
	public void PluralCountMustMatchTheHeader()
	{
		var wrong = Problems("msgid \"%d file\"\nmsgid_plural \"%d files\"\nmsgstr[0] \"%d fichier\"\n");
		Assert.Equal(new[] { "PO1005" }, wrong.Select(p => p.Id));
		Assert.Equal(PoSeverity.Error, wrong[0].Severity);
		Assert.Equal(new[] { "PO1006" }, Ids("msgid \"%d file\"\nmsgid_plural \"%d files\"\nmsgstr[0] \"%d fichier\"\nmsgstr[1] \"%d fichiers\"\n",
			"msgid \"\"\nmsgstr \"\"\n\"Content-Type: text/plain; charset=UTF-8\\n\"\n\n"));
		Assert.Empty(Problems("msgid \"%d file\"\nmsgid_plural \"%d files\"\nmsgstr[0] \"\"\nmsgstr[1] \"\"\n")); // untranslated: nothing to check
	}

	[Fact]
	public void MarkupTagsAndLineBreaksMustAgree()
	{
		Assert.Empty(Problems("msgid \"<b>Bold</b> text\"\nmsgstr \"Texte <b>gras</b>\"\n"));
		Assert.Equal(new[] { "PO1002" }, Ids("msgid \"<b>Bold</b> text\"\nmsgstr \"Texte gras</b>\"\n"));
		Assert.Equal(new[] { "PO1003" }, Ids("msgid \"Line\\n\"\nmsgstr \"Ligne\"\n"));
		Assert.Equal(new[] { "PO1003" }, Ids("msgid \"\\nLine\"\nmsgstr \"Ligne\"\n"));
	}

	[Fact]
	public void FuzzyIsReportedOnItsFlagLineAndItsTextIsNotChecked()
	{
		var text = Header + "#, fuzzy\nmsgid \"%s\"\nmsgstr \"%d\"\n";
		var problem = Assert.Single(PoAnalyzer.Analyze(text));
		Assert.Equal("PO1004", problem.Id);
		Assert.Equal(PoSeverity.Info, problem.Severity);
		Assert.Equal(7, problem.Line); // the "#, fuzzy" line, after the 5-line header block and a blank line
	}

	[Fact]
	public void DuplicateMessagesAreAnErrorOnTheSecond()
	{
		var problems = Problems("msgid \"Open\"\nmsgstr \"Ouvrir\"\n\nmsgid \"Open\"\nmsgstr \"Ouvrir encore\"\n\nmsgctxt \"menu\"\nmsgid \"Open\"\nmsgstr \"Ouvrir\"\n");
		var duplicate = Assert.Single(problems);
		Assert.Equal("PO1007", duplicate.Id);
		Assert.Equal(10, duplicate.Line);
		Assert.Contains("already defined at line 7", duplicate.Message);
	}

	[Fact]
	public void TheHeaderIsChecked()
	{
		Assert.Equal(new[] { "PO1008" }, Ids("msgid \"Open\"\nmsgstr \"Ouvrir\"\n", ""));
		Assert.Equal(new[] { "PO1008" }, Ids("msgid \"Open\"\nmsgstr \"Ouvrir\"\n", "msgid \"\"\nmsgstr \"\"\n\"Language: fr\\n\"\n\n"));
		var latin = Problems("msgid \"Open\"\nmsgstr \"Ouvrir\"\n", "msgid \"\"\nmsgstr \"\"\n\"Content-Type: text/plain; charset=ISO-8859-1\\n\"\n\n");
		Assert.Contains("UTF-8", Assert.Single(latin).Message);
		Assert.Empty(PoAnalyzer.Analyze("")); // nothing to say about an empty file
	}

	[Fact]
	public void ObsoleteEntriesAreIgnored()
	{
		Assert.Empty(Problems("#~ msgid \"%s\"\n#~ msgstr \"%d\"\n"));
	}

	[Fact]
	public void Hover_DescribesTheMessageUnderTheLine()
	{
		var text = Header + "#. Shown on the save button\n#: Main.ui:12\nmsgctxt \"button\"\nmsgid \"Save %s\"\nmsgstr \"Enregistrer %s\"\n\nmsgid \"Open\"\nmsgstr \"\"\n";
		var save = PoAnalyzer.Hover(text, 10)!; // the msgid line
		Assert.StartsWith("Save %s", save);
		Assert.Contains("Context: button", save);
		Assert.Contains("Translation (Translated): Enregistrer %s", save);
		Assert.Contains("Note: Shown on the save button", save);
		Assert.Contains("Sources: Main.ui:12", save);
		Assert.Contains("Placeholders: %1$s", save);
		Assert.Contains("Translation (Untranslated): -", PoAnalyzer.Hover(text, 14)!);
		Assert.StartsWith("Header", PoAnalyzer.Hover(text, 2)!);
		Assert.Null(PoAnalyzer.Hover(text, 6)); // the blank line between the header and the first message
		Assert.Null(PoAnalyzer.Hover(text, 12)); // and the one between two messages
	}

	static (string Text, int Offset) Caret(string text)
	{
		var at = text.IndexOf('|');
		return (text.Remove(at, 1), at);
	}

	[Fact]
	public void Complete_OffersFlagsAfterHashComma()
	{
		var (text, offset) = Caret(Header + "#, fu|\nmsgid \"x\"\nmsgstr \"\"\n");
		var (items, start) = PoAnalyzer.Complete(text, offset)!.Value;
		Assert.Equal(new[] { "fuzzy" }, items.Select(i => i.Display));
		Assert.Equal(offset - 2, start);
		var (text2, offset2) = Caret(Header + "#, fuzzy, c|\nmsgid \"x\"\nmsgstr \"\"\n");
		Assert.Equal(new[] { "c-format" }, PoAnalyzer.Complete(text2, offset2)!.Value.Items.Select(i => i.Display));
	}

	[Fact]
	public void Complete_OffersTheKeywordsThatMayFollow()
	{
		string[] Offer(string body) { var (text, offset) = Caret(Header + body); return PoAnalyzer.Complete(text, offset)!.Value.Items.Select(i => i.Display).ToArray(); }
		Assert.Equal(new[] { "msgid", "msgctxt" }, Offer("|"));
		Assert.Equal(new[] { "msgid" }, Offer("msgctxt \"a\"\nm|"));
		Assert.Equal(new[] { "msgid_plural", "msgstr" }, Offer("msgid \"a\"\nm|"));
		Assert.Equal(new[] { "msgstr" }, Offer("msgid \"a\"\nmsgs|"));
		Assert.Equal(new[] { "msgstr[0]", "msgstr[1]" }, Offer("msgid \"a\"\nmsgid_plural \"b\"\n|"));
		Assert.Equal(new[] { "msgid", "msgctxt" }, Offer("msgid \"a\"\nmsgstr \"b\"\n\n|")); // a new entry after a blank line
		Assert.Null(PoAnalyzer.Complete(Header + "msgid \"a\"\nxyz", Header.Length + 14));
	}

	[Fact]
	public void Complete_OffersTheSourceTextInsideAnOpenMsgstr()
	{
		var (text, offset) = Caret(Header + "msgid \"Say \\\"hi\\\"\"\nmsgstr \"|");
		var (items, start) = PoAnalyzer.Complete(text, offset)!.Value;
		var item = Assert.Single(items);
		Assert.Equal("Say \\\"hi\\\"", item.Insertion);
		Assert.Equal(offset, start);
		var (plural, pluralOffset) = Caret(Header + "msgid \"%d file\"\nmsgid_plural \"%d files\"\nmsgstr[1] \"|");
		Assert.Equal("%d files", PoAnalyzer.Complete(plural, pluralOffset)!.Value.Items.Single().Insertion);
	}

	[Fact]
	public void ReferenceAt_FindsTheFileAndLineUnderTheCaret()
	{
		var text = "#: Windows/Main.ui:12 Other.ui:7\nmsgid \"x\"\nmsgstr \"\"\n";
		Assert.Equal(("Windows/Main.ui", 12), PoAnalyzer.ReferenceAt(text, 6)!.Value);
		Assert.Equal(("Other.ui", 7), PoAnalyzer.ReferenceAt(text, 26)!.Value);
		Assert.Null(PoAnalyzer.ReferenceAt(text, 2)); // the "#:" marker itself
		Assert.Null(PoAnalyzer.ReferenceAt(text, 36)); // not on a reference line
	}

	[Fact]
	public void Outline_ListsTheHeaderAndEachLiveMessage()
	{
		var outline = PoAnalyzer.Outline(Header + "msgctxt \"button\"\nmsgid \"Save\"\nmsgstr \"Enregistrer\"\n\n#, fuzzy\nmsgid \"Close\"\nmsgstr \"Ferme\"\n\nmsgid \"Open\"\nmsgstr \"\"\n\n#~ msgid \"Gone\"\n#~ msgstr \"Parti\"\n");
		Assert.Equal(new[] { "Header", "button: Save", "Close", "Open" }, outline.Select(o => o.Name));
		Assert.Equal(new[] { "header", "translated", "fuzzy", "untranslated" }, outline.Select(o => o.Kind));
		Assert.Equal(1, outline[0].Line);
		Assert.Equal(new[] { 8, 12, 15 }, outline.Skip(1).Select(o => o.Line));
	}
}
