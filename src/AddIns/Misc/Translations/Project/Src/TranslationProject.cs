using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ICSharpCode.Translations;

/// <summary>
/// The gettext workflow for a project: the strings its files' registered sources extract become
/// <c>po/&lt;project&gt;.pot</c>, and each <c>po/*.po</c> is merged against it so translators keep their work
/// when the UI changes. Pure file work, no IDE services, so the unit tests drive it against a temporary directory.
/// </summary>
public static class TranslationProject
{
	/// <summary>The .pot template for <paramref name="files"/>, each extracted by the first source that handles it (paths relative to
	/// <paramref name="projectDirectory"/> are written as references): one entry per distinct (context, msgid) in first-seen order, references and
	/// comments merged across files.</summary>
	public static PoCatalog BuildTemplate(string projectName, string projectDirectory, IEnumerable<string> files, IReadOnlyList<ITranslatableStringSource> sources)
	{
		var catalog = new PoCatalog { Header = new PoEntry { MsgStr = PoCatalog.DefaultHeader(projectName) } };
		var byKey = new Dictionary<string, PoEntry>(StringComparer.Ordinal);
		foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal)) {
			string text;
			try { text = File.ReadAllText(file); } catch (IOException) { continue; }
			var source = sources.FirstOrDefault(s => s.Handles(file));
			if (source == null) continue;
			foreach (var entry in source.Extract(text, Path.GetRelativePath(projectDirectory, file).Replace('\\', '/'))) {
				if (byKey.TryGetValue(entry.Key, out var existing)) {
					foreach (var reference in entry.References.Where(r => !existing.References.Contains(r))) existing.References.Add(reference);
					foreach (var comment in entry.ExtractedComments.Where(c => !existing.ExtractedComments.Contains(c))) existing.ExtractedComments.Add(comment);
				} else { byKey[entry.Key] = entry; catalog.Entries.Add(entry); }
			}
		}
		return catalog;
	}

	/// <summary>What <see cref="UpdateProject"/> did.</summary>
	public sealed record UpdateResult(string PotPath, int StringCount, bool PotWritten, IReadOnlyList<string> UpdatedCatalogs);

	/// <summary>Writes <c>po/&lt;projectName&gt;.pot</c> from the project's .ui files and merges every <c>po/*.po</c>
	/// against it. A file whose content would not change is not rewritten (no churn in source control); the
	/// .pot's creation date is kept from the existing file for the same reason.</summary>
	public static UpdateResult UpdateProject(string projectName, string projectDirectory, IEnumerable<string> files, IReadOnlyList<ITranslatableStringSource> sources)
	{
		var poDirectory = Path.Combine(projectDirectory, "po");
		var potPath = Path.Combine(poDirectory, projectName + ".pot");
		var template = BuildTemplate(projectName, projectDirectory, files, sources);
		var potWritten = false;
		var updated = new List<string>();
		if (template.Entries.Count == 0 && !File.Exists(potPath))
			return new UpdateResult(potPath, 0, false, updated);

		Directory.CreateDirectory(poDirectory);
		var previousPot = File.Exists(potPath) ? PoCatalog.Parse(File.ReadAllText(potPath)) : null;
		if (previousPot != null) template.Header = previousPot.Header;
		else template.Header.MsgStr += "POT-Creation-Date: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "+0000" + "\n";
		var potText = template.Write();
		if (previousPot == null || previousPot.Write() != potText) { File.WriteAllText(potPath, potText); potWritten = true; }

		foreach (var poPath in Directory.EnumerateFiles(poDirectory, "*.po").OrderBy(f => f, StringComparer.Ordinal)) {
			var existing = PoCatalog.Parse(File.ReadAllText(poPath));
			var mergedText = PoCatalog.Merge(template, existing).Write();
			if (mergedText == existing.Write()) continue;
			File.WriteAllText(poPath, mergedText);
			updated.Add(poPath);
		}
		return new UpdateResult(potPath, template.Entries.Count, potWritten, updated);
	}

	/// <summary>Creates <c>po/&lt;language&gt;.po</c> from the template (every string untranslated) when it does not exist.
	/// Returns its path, or null when the language code is not a plain locale code or the file is already there.</summary>
	public static string? AddLanguage(string projectName, string projectDirectory, string language)
	{
		if (language.Length == 0 || !language.All(c => char.IsLetterOrDigit(c) || c is '_' or '@' or '.' or '-')) return null;
		var poPath = Path.Combine(projectDirectory, "po", language + ".po");
		var potPath = Path.Combine(projectDirectory, "po", projectName + ".pot");
		if (File.Exists(poPath) || !File.Exists(potPath)) return null;
		var template = PoCatalog.Parse(File.ReadAllText(potPath));
		var catalog = PoCatalog.Merge(template, null);
		catalog.Header = new PoEntry { MsgStr = PoCatalog.DefaultHeader(projectName) + "Language: " + language + "\n" };
		File.WriteAllText(poPath, catalog.Write());
		return poPath;
	}
}
