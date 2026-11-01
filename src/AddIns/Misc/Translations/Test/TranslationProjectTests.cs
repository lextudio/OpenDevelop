using ICSharpCode.Translations;
using Xunit;

namespace Translations.Tests;

public sealed class TranslationProjectTests : IDisposable
{
	readonly string directory = Path.Combine(Path.GetTempPath(), "translations-" + Guid.NewGuid().ToString("N"));

	public TranslationProjectTests() => Directory.CreateDirectory(directory);
	public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }

	/// <summary>A source for ".txt" files: each non-empty line is a string, "context|text" gives it a context.</summary>
	sealed class LineSource : ITranslatableStringSource
	{
		public string Name => "Lines";
		public bool Handles(string filePath) => filePath.EndsWith(".txt", StringComparison.Ordinal);
		public IReadOnlyList<PoEntry> Extract(string text, string referencePath)
		{
			var result = new List<PoEntry>();
			var number = 0;
			foreach (var line in text.Replace("\r", "").Split('\n')) {
				number++;
				if (line.Length == 0) continue;
				var bar = line.IndexOf('|');
				var entry = new PoEntry { Context = bar < 0 ? null : line.Substring(0, bar), MsgId = bar < 0 ? line : line.Substring(bar + 1) };
				entry.References.Add(referencePath + ":" + number);
				result.Add(entry);
			}
			return result;
		}
	}

	static readonly IReadOnlyList<ITranslatableStringSource> Sources = new ITranslatableStringSource[] { new LineSource() };

	[Fact]
	public void PoCatalog_RoundTripsQuotingMultilineContextFlagsAndObsoleteEntries()
	{
		var catalog = new PoCatalog { Header = new PoEntry { MsgStr = PoCatalog.DefaultHeader("Demo") + "Language: fr\n" } };
		var entry = new PoEntry { Context = "button", MsgId = "Say \"hi\"\n\tbye\\", MsgStr = "Dis \"salut\"\n\tau revoir\\" };
		entry.ExtractedComments.Add("A greeting");
		entry.References.Add("Main.ui:12");
		entry.Flags.Add("fuzzy");
		entry.TranslatorComments.Add("checked by Ana");
		catalog.Entries.Add(entry);
		catalog.Entries.Add(new PoEntry { MsgId = "Gone", MsgStr = "Parti", Obsolete = true });
		var text = catalog.Write();
		var back = PoCatalog.Parse(text);
		Assert.Equal(catalog.Header.MsgStr, back.Header.MsgStr);
		Assert.Equal(2, back.Entries.Count);
		var first = back.Entries[0];
		Assert.Equal("button", first.Context);
		Assert.Equal(entry.MsgId, first.MsgId);
		Assert.Equal(entry.MsgStr, first.MsgStr);
		Assert.Equal(new[] { "A greeting" }, first.ExtractedComments);
		Assert.Equal(new[] { "Main.ui:12" }, first.References);
		Assert.True(first.IsFuzzy);
		Assert.Equal(new[] { "checked by Ana" }, first.TranslatorComments);
		Assert.True(back.Entries[1].Obsolete);
		Assert.Equal("Parti", back.Entries[1].MsgStr);
		Assert.Equal(text, back.Write());
	}

	[Fact]
	public void PoCatalog_ParsesPluralEntries()
	{
		var catalog = PoCatalog.Parse("msgid \"\"\nmsgstr \"\"\n\"Content-Type: text/plain; charset=UTF-8\\n\"\n\nmsgid \"%d file\"\nmsgid_plural \"%d files\"\nmsgstr[0] \"%d fichier\"\nmsgstr[1] \"%d fichiers\"\n");
		var entry = Assert.Single(catalog.Entries);
		Assert.Equal("%d files", entry.MsgIdPlural);
		Assert.Equal(new[] { "%d fichier", "%d fichiers" }, entry.PluralStrings);
		Assert.True(entry.IsTranslated);
		Assert.Contains("msgstr[1] \"%d fichiers\"", catalog.Write());
	}

	[Fact]
	public void Merge_KeepsTranslationsAddsNewStringsAndObsoletesRemovedOnes()
	{
		var template = new PoCatalog();
		template.Entries.Add(new PoEntry { MsgId = "Open" });
		template.Entries.Add(new PoEntry { Context = "button", MsgId = "Save" });
		template.Entries.Add(new PoEntry { MsgId = "New" });
		var existing = new PoCatalog { Header = new PoEntry { MsgStr = "Language: fr\n" } };
		existing.Entries.Add(new PoEntry { MsgId = "Open", MsgStr = "Ouvrir", Flags = { "fuzzy" }, TranslatorComments = { "ok" } });
		existing.Entries.Add(new PoEntry { MsgId = "Save", MsgStr = "Enregistrer" }); // no context in the old file: a different string
		existing.Entries.Add(new PoEntry { MsgId = "Removed", MsgStr = "Supprimé" });
		existing.Entries.Add(new PoEntry { MsgId = "Untranslated gone" });
		var merged = PoCatalog.Merge(template, existing);
		Assert.Equal("Language: fr\n", merged.Header.MsgStr);
		// The context-less "Save" no longer exists, so its translation is kept as obsolete, like "Removed"'s; the untranslated one is dropped.
		Assert.Equal(new[] { "Open", "Save", "New", "Save", "Removed" }, merged.Entries.Select(e => e.MsgId));
		Assert.Equal("Ouvrir", merged.Entries[0].MsgStr);
		Assert.True(merged.Entries[0].IsFuzzy);
		Assert.Equal(new[] { "ok" }, merged.Entries[0].TranslatorComments);
		Assert.Equal("", merged.Entries[1].MsgStr); // context differs: not carried over
		Assert.False(merged.Entries[2].IsTranslated);
		Assert.True(merged.Entries[3].Obsolete);
		Assert.Equal("Enregistrer", merged.Entries[3].MsgStr);
		Assert.True(merged.Entries[4].Obsolete);
		Assert.Equal("Supprimé", merged.Entries[4].MsgStr);
		// A string that comes back revives its obsolete translation.
		var revived = PoCatalog.Merge(new PoCatalog { Entries = { new PoEntry { MsgId = "Removed" } } }, merged);
		Assert.Equal("Supprimé", Assert.Single(revived.Entries, e => !e.Obsolete).MsgStr);
	}

	[Fact]
	public void UpdateProject_WritesThePotMergesPoFilesAndDoesNotRewriteUnchangedOnes()
	{
		Directory.CreateDirectory(Path.Combine(directory, "Windows"));
		File.WriteAllText(Path.Combine(directory, "Windows", "Main.txt"), "Settings\nbutton|Save\nOpen\n");
		File.WriteAllText(Path.Combine(directory, "Windows", "Other.txt"), "Settings\n");
		File.WriteAllText(Path.Combine(directory, "Windows", "Ignored.cs"), "Not handled by any source\n");
		Directory.CreateDirectory(Path.Combine(directory, "po"));
		File.WriteAllText(Path.Combine(directory, "po", "fr.po"), "msgid \"\"\nmsgstr \"\"\n\"Language: fr\\n\"\n\nmsgid \"Settings\"\nmsgstr \"Réglages\"\n\nmsgid \"Old\"\nmsgstr \"Ancien\"\n");
		var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".po", StringComparison.Ordinal)).ToList();

		var result = TranslationProject.UpdateProject("Demo", directory, files, Sources);
		Assert.True(result.PotWritten);
		Assert.Equal(3, result.StringCount); // "Settings" is in two files but is one entry
		var pot = PoCatalog.Parse(File.ReadAllText(result.PotPath));
		Assert.Equal(new[] { "Settings", "Open", "Save" }.OrderBy(s => s), pot.Entries.Select(e => e.MsgId).OrderBy(s => s));
		var settings = pot.Entries.Single(e => e.MsgId == "Settings");
		Assert.Equal(new[] { "Windows/Main.txt:1", "Windows/Other.txt:1" }, settings.References);
		Assert.Equal("button", pot.Entries.Single(e => e.MsgId == "Save").Context);
		var fr = PoCatalog.Parse(File.ReadAllText(Path.Combine(directory, "po", "fr.po")));
		Assert.Equal("Réglages", fr.Entries.Single(e => e.MsgId == "Settings").MsgStr);
		Assert.True(fr.Entries.Single(e => e.MsgId == "Old").Obsolete);
		Assert.Single(result.UpdatedCatalogs);

		// Nothing changed: nothing is rewritten, so the creation date and the files stay put.
		var potText = File.ReadAllText(result.PotPath);
		var again = TranslationProject.UpdateProject("Demo", directory, files, Sources);
		Assert.False(again.PotWritten);
		Assert.Empty(again.UpdatedCatalogs);
		Assert.Equal(potText, File.ReadAllText(result.PotPath));
	}

	[Fact]
	public void UpdateProject_WithNoTranslatableStringsWritesNothing()
	{
		File.WriteAllText(Path.Combine(directory, "A.txt"), "");
		var result = TranslationProject.UpdateProject("Demo", directory, new[] { Path.Combine(directory, "A.txt") }, Sources);
		Assert.Equal(0, result.StringCount);
		Assert.False(Directory.Exists(Path.Combine(directory, "po")));
	}

	[Fact]
	public void AddLanguage_CopiesTheTemplateOnceAndRefusesOddCodes()
	{
		File.WriteAllText(Path.Combine(directory, "A.txt"), "One\nTwo\n");
		TranslationProject.UpdateProject("Demo", directory, new[] { Path.Combine(directory, "A.txt") }, Sources);
		var path = TranslationProject.AddLanguage("Demo", directory, "de");
		Assert.NotNull(path);
		var de = PoCatalog.Parse(File.ReadAllText(path!));
		Assert.Contains("Language: de", de.Header.MsgStr);
		Assert.Equal(2, de.Entries.Count);
		Assert.All(de.Entries, e => Assert.False(e.IsTranslated));
		Assert.Null(TranslationProject.AddLanguage("Demo", directory, "de")); // already there
		Assert.Null(TranslationProject.AddLanguage("Demo", directory, "../evil"));
		Assert.Null(TranslationProject.AddLanguage("Demo", directory, ""));
	}

	[Fact]
	public void SourceRegistry_RegistersEachNameOnce()
	{
		TranslatableStringSources.Register(new LineSource());
		TranslatableStringSources.Register(new LineSource());
		Assert.Single(TranslatableStringSources.All, s => s.Name == "Lines");
	}
}
