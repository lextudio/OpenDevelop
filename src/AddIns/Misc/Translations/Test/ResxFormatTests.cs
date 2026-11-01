using ICSharpCode.Translations;
using Xunit;

namespace Translations.Tests;

public sealed class ResxFormatTests
{
	const string Header = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n  <resheader name=\"resmimetype\">\n    <value>text/microsoft-resx</value>\n  </resheader>\n  <resheader name=\"version\">\n    <value>2.0</value>\n  </resheader>\n";

	static readonly string Neutral = Header + """
		  <data name="$this.Text" xml:space="preserve">
		    <value>Settings</value>
		  </data>
		  <data name="saveButton.Text" xml:space="preserve">
		    <value>Save</value>
		    <comment>Shown on the save button</comment>
		  </data>
		  <data name="openButton.Text" xml:space="preserve">
		    <value>Open "recent" &amp; more</value>
		  </data>
		  <data name="saveButton.Location" type="System.Drawing.Point, System.Drawing">
		    <value>13, 13</value>
		  </data>
		  <data name="saveButton.TabIndex" type="System.Int32, mscorlib">
		    <value>1</value>
		  </data>
		  <data name="&gt;&gt;saveButton.Type" xml:space="preserve">
		    <value>System.Windows.Forms.Button, System.Windows.Forms</value>
		  </data>
		  <data name="icon" type="System.Resources.ResXFileRef, System.Windows.Forms">
		    <value>icon.png;System.Drawing.Bitmap</value>
		  </data>
		</root>
		""".Replace("\n\t\t", "\n").TrimStart();

	static readonly string French = Header + """
		  <data name="saveButton.Text" xml:space="preserve">
		    <value>Enregistrer</value>
		    <comment>checked by Ana</comment>
		  </data>
		  <data name="saveButton.Location" type="System.Drawing.Point, System.Drawing">
		    <value>20, 13</value>
		  </data>
		  <data name="removed.Text" xml:space="preserve">
		    <value>Supprimé</value>
		  </data>
		</root>
		""".Replace("\n\t\t", "\n").TrimStart() + "\n";

	[Theory]
	[InlineData("Form1.fr.resx", "fr")]
	[InlineData("Windows/Form1.zh-Hans.resx", "zh-Hans")]
	[InlineData("Form1.pt-BR.resx", "pt-BR")]
	[InlineData("Form1.resx", null)]
	[InlineData("Form1.Designer.resx", null)]
	[InlineData("Form1.fr.cs", null)]
	[InlineData("Form1.xx_yy.resx", null)]
	public void CultureOf_RecognisesCultureResourceFilesOnly(string path, string? expected)
		=> Assert.Equal(expected, ResxFormat.CultureOf(path));

	[Fact]
	public void NeutralPathOf_DropsTheCulture()
		=> Assert.Equal(Path.Combine("Windows", "Form1.resx"), ResxFormat.NeutralPathOf(Path.Combine("Windows", "Form1.fr.resx")));

	[Fact]
	public void Strings_SkipTypedResourcesAndDesignerBookkeeping()
	{
		var strings = ResxFormat.Strings(Neutral);
		Assert.Equal(new[] { "$this.Text", "saveButton.Text", "openButton.Text" }, strings.Select(s => s.Name));
		Assert.Equal("Open \"recent\" & more", strings[2].Value);
		Assert.Equal("Shown on the save button", strings[1].Comment);
		Assert.Empty(ResxFormat.Strings("<not xml"));
	}

	static TranslatorModel Loaded()
	{
		var model = new TranslatorModel(new ResxFormat(Neutral));
		model.Load(French);
		return model;
	}

	[Fact]
	public void Model_ListsNeutralStringsAgainstTheCultureFile()
	{
		var model = Loaded();
		Assert.False(model.SupportsFuzzy);
		Assert.Equal("Name", model.ContextHeader);
		Assert.True(model.HasContexts);
		Assert.Equal(new[] { "$this.Text", "saveButton.Text", "openButton.Text" }, model.VisibleRows.Select(r => r.Context));
		Assert.Equal(new[] { "Settings", "Save", "Open \"recent\" & more" }, model.VisibleRows.Select(r => r.Source));
		Assert.Equal(new[] { TranslationState.Untranslated, TranslationState.Translated, TranslationState.Untranslated }, model.VisibleRows.Select(r => r.State));
		var save = model.Find("Save")!;
		Assert.Equal("Enregistrer", save.Translation);
		Assert.Equal("Shown on the save button", save.ExtractedComment);
		Assert.Equal("checked by Ana", save.TranslatorComment);
		Assert.Equal(1, model.ObsoleteCount); // removed.Text: in the culture file, gone from the neutral one
		model.Filter = TranslationFilter.Obsolete;
		Assert.Equal("Supprimé", model.VisibleRows.Single().Translation);
		Assert.Equal(1.0 / 3 * 100, model.PercentComplete, 3);
	}

	[Fact]
	public void Untouched_WritesTheCultureFileBackVerbatim()
	{
		var model = Loaded();
		Assert.Equal(French, model.ToText());
		model.Find("Save")!.IsFuzzy = true; // a resource file has no fuzzy state: not an edit
		Assert.False(model.IsDirty);
		Assert.Equal(French, model.ToText());
	}

	[Fact]
	public void Editing_UpdatesAddsAndRemovesDataKeepingEverythingElse()
	{
		var model = Loaded();
		model.Find("Settings")!.Translation = "Réglages";                  // new name: a <data> is added
		model.Find("Save")!.Translation = "Sauvegarder";                    // existing: updated in place
		model.Find("Save")!.TranslatorComment = "";                         // its comment goes
		model.Find("Open \"recent\" & more")!.Translation = "Ouvrir \"récents\" & plus";
		Assert.True(model.IsDirty);
		var text = model.ToText();

		var strings = ResxFormat.Strings(text).ToDictionary(s => s.Name, s => s);
		Assert.Equal("Réglages", strings["$this.Text"].Value);
		Assert.Equal("Sauvegarder", strings["saveButton.Text"].Value);
		Assert.Equal("", strings["saveButton.Text"].Comment);
		Assert.Equal("Ouvrir \"récents\" & plus", strings["openButton.Text"].Value);
		Assert.Equal("Supprimé", strings["removed.Text"].Value); // the obsolete one is never written away
		// The culture file's own non-string resource and headers are untouched.
		Assert.Contains("<value>20, 13</value>", text);
		Assert.Contains("text/microsoft-resx", text);
		Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>", text);
		Assert.DoesNotContain("checked by Ana", text);

		// Emptying a translation removes its <data>, so the entry is untranslated again.
		model.MarkSaved();
		model.Find("Settings")!.Translation = "";
		Assert.DoesNotContain("$this.Text", model.ToText());
		var reloaded = new TranslatorModel(new ResxFormat(Neutral));
		reloaded.Load(model.ToText());
		Assert.Equal(TranslationState.Untranslated, reloaded.Find("Settings")!.State);
		Assert.Equal("Sauvegarder", reloaded.Find("Save")!.Translation);
	}

	[Fact]
	public void AnEmptyOrBrokenCultureFileIsTranslatedFromScratch()
	{
		foreach (var original in new[] { "", "<not xml" }) {
			var model = new TranslatorModel(new ResxFormat(Neutral));
			model.Load(original);
			Assert.Equal(3, model.UntranslatedCount);
			model.Find("Save")!.Translation = "Enregistrer";
			var text = model.ToText();
			Assert.Equal("Enregistrer", ResxFormat.Strings(text).Single().Value);
			Assert.Contains("resmimetype", text); // the headers a resource file needs
		}
	}
}
