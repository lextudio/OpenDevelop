using ICSharpCode.Translations;
using Xunit;

namespace Translations.Tests;

public sealed class TranslatorModelTests
{
	const string Po = """
		# Translation of Demo
		msgid ""
		msgstr ""
		"Language: fr\n"
		"Content-Type: text/plain; charset=UTF-8\n"

		#. Shown on the save button
		#: Main.ui:12
		msgctxt "button"
		msgid "Save"
		msgstr "Enregistrer"

		#: Main.ui:20
		msgid "Open"
		msgstr ""

		# checked by Ana
		#: Main.ui:30
		#, fuzzy
		msgid "Close"
		msgstr "Ferme"

		msgid "%d file"
		msgid_plural "%d files"
		msgstr[0] "%d fichier"
		msgstr[1] "%d fichiers"

		#~ msgid "Gone"
		#~ msgstr "Parti"
		""";

	static TranslatorModel Loaded()
	{
		var model = new TranslatorModel();
		model.Load(Po);
		return model;
	}

	[Fact]
	public void Load_ShowsRowsStatesLanguageAndProgress()
	{
		var model = Loaded();
		Assert.Equal("fr", model.Language);
		Assert.Equal(new[] { "Save", "Open", "Close", "%d file | %d files" }, model.VisibleRows.Select(r => r.Source));
		Assert.Equal(new[] { TranslationState.Translated, TranslationState.Untranslated, TranslationState.Fuzzy, TranslationState.Translated }, model.VisibleRows.Select(r => r.State));
		Assert.Equal(4, model.Total);
		Assert.Equal(2, model.TranslatedCount);
		Assert.Equal(1, model.FuzzyCount);
		Assert.Equal(1, model.UntranslatedCount);
		Assert.Equal(1, model.ObsoleteCount);
		Assert.Equal(50, model.PercentComplete);
		var save = model.Find("Save", "button")!;
		Assert.Equal("button", save.Context);
		Assert.Equal("Shown on the save button", save.ExtractedComment);
		Assert.Equal("Main.ui:12", save.References);
		Assert.Equal("checked by Ana", model.Find("Close")!.TranslatorComment);
		Assert.Equal("%d fichier | %d fichiers", model.Find("%d file")!.Translation);
	}

	[Fact]
	public void Filters_ListWhatTheTranslatorStillHasToDo()
	{
		var model = Loaded();
		model.Filter = TranslationFilter.NeedsWork;
		Assert.Equal(new[] { "Open", "Close" }, model.VisibleRows.Select(r => r.Source));
		model.Filter = TranslationFilter.Untranslated;
		Assert.Equal(new[] { "Open" }, model.VisibleRows.Select(r => r.Source));
		model.Filter = TranslationFilter.Fuzzy;
		Assert.Equal(new[] { "Close" }, model.VisibleRows.Select(r => r.Source));
		model.Filter = TranslationFilter.Obsolete;
		Assert.Equal(new[] { "Gone" }, model.VisibleRows.Select(r => r.Source));
		model.Filter = TranslationFilter.All;
		model.Search = "ferme"; // a translation, any case
		Assert.Equal(new[] { "Close" }, model.VisibleRows.Select(r => r.Source));
		model.Search = "Main.ui:12"; // a reference
		Assert.Equal(new[] { "Save" }, model.VisibleRows.Select(r => r.Source));
		model.Search = "button"; // a context
		Assert.Equal(new[] { "Save" }, model.VisibleRows.Select(r => r.Source));
	}

	[Fact]
	public void Editing_ConfirmsFuzzyMarksDirtyAndRewritesTheCatalogue()
	{
		var model = Loaded();
		var changes = 0;
		model.Changed += (_, _) => changes++;
		Assert.False(model.IsDirty);
		Assert.Equal(Po, model.ToText()); // untouched text comes back verbatim

		model.Find("Open")!.Translation = "Ouvrir";
		Assert.Equal(TranslationState.Translated, model.Find("Open")!.State);
		Assert.True(model.IsDirty);
		model.Find("Close")!.Translation = "Fermer"; // typing into a fuzzy entry confirms it
		Assert.False(model.Find("Close")!.IsFuzzy);
		Assert.Equal(TranslationState.Translated, model.Find("Close")!.State);
		model.Find("Save", "button")!.IsFuzzy = true;
		model.Find("Save", "button")!.TranslatorComment = "to review\nsecond line";
		Assert.Equal(4, changes);

		var back = PoCatalog.Parse(model.ToText());
		Assert.Equal("Ouvrir", back.Entries.Single(e => e.MsgId == "Open").MsgStr);
		Assert.Equal("Fermer", back.Entries.Single(e => e.MsgId == "Close").MsgStr);
		Assert.False(back.Entries.Single(e => e.MsgId == "Close").IsFuzzy);
		Assert.True(back.Entries.Single(e => e.MsgId == "Save").IsFuzzy);
		Assert.Equal(new[] { "to review", "second line" }, back.Entries.Single(e => e.MsgId == "Save").TranslatorComments);
		Assert.Equal("Parti", back.Entries.Single(e => e.MsgId == "Gone").MsgStr);
		Assert.True(back.Entries.Single(e => e.MsgId == "Gone").Obsolete);
		Assert.Equal("%d fichiers", back.Entries.Single(e => e.MsgId == "%d file").PluralStrings[1]);

		model.MarkSaved();
		Assert.False(model.IsDirty);
		Assert.Equal(back.Write(), model.ToText());
	}

	[Fact]
	public void PluralAndObsoleteRowsAreShownButNotEdited()
	{
		var model = Loaded();
		var plural = model.Find("%d file")!;
		Assert.True(plural.IsReadOnly);
		plural.Translation = "x";
		plural.IsFuzzy = true;
		Assert.Equal("%d fichier | %d fichiers", plural.Translation);
		model.Filter = TranslationFilter.Obsolete;
		var gone = model.VisibleRows.Single();
		gone.Translation = "x";
		gone.TranslatorComment = "x";
		Assert.Equal("Parti", gone.Translation);
		Assert.False(model.IsDirty);
		// Setting the value a row already has is not an edit either.
		model.Filter = TranslationFilter.All;
		model.Find("Open")!.Translation = "";
		Assert.False(model.IsDirty);
	}

	[Fact]
	public void Load_ReplacesTheRowsAndClearsDirty()
	{
		var model = Loaded();
		var reloaded = 0;
		model.Reloaded += (_, _) => reloaded++;
		model.Find("Open")!.Translation = "Ouvrir";
		model.Load("msgid \"\"\nmsgstr \"\"\n\nmsgid \"New\"\nmsgstr \"\"\n");
		Assert.Equal(1, reloaded);
		Assert.False(model.IsDirty);
		Assert.Equal(new[] { "New" }, model.VisibleRows.Select(r => r.Source));
		Assert.Equal("", model.Language);
		Assert.Equal(0, model.PercentComplete);
		Assert.Equal(100, new TranslatorModel().PercentComplete); // nothing to translate is complete
	}
}
