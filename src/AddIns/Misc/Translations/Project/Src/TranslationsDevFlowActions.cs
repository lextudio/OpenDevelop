using System.Linq;
using System.Text.Json;
using ICSharpCode.Core;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.Translations;

public sealed class RegisterDevFlowActionsCommand : DevFlowActionRegistrationCommand { }

[DevFlowUIThread]
public static class TranslationsDevFlowActions
{
	[DevFlowAction("od.translations.update", Description = "Regenerate a project's po/<project>.pot from its translatable sources and merge every po/*.po; the project is the one whose file is given, else the current project")]
	public static string Update(string projectFile = "")
	{
		var result = TranslationsService.Update(TranslationsService.FindProject(projectFile));
		return JsonSerializer.Serialize(new {
			success = result != null,
			potPath = result?.PotPath, stringCount = result?.StringCount ?? 0, potWritten = result?.PotWritten ?? false,
			updatedCatalogs = result?.UpdatedCatalogs ?? System.Array.Empty<string>()
		});
	}

	static TranslatorViewContent? Translator() => TranslatorViewContent.Activate();

	[DevFlowAction("od.translator.status", Description = "Inspect the active translator view: language, progress, filter and the listed rows")]
	public static string TranslatorStatus()
	{
		var view = Translator();
		if (view == null) return JsonSerializer.Serialize(new { active = false });
		var model = view.Model;
		return JsonSerializer.Serialize(new {
			active = true, language = model.Language, dirty = model.IsDirty, filter = model.Filter.ToString(), search = model.Search,
			total = model.Total, translated = model.TranslatedCount, fuzzy = model.FuzzyCount, untranslated = model.UntranslatedCount,
			obsolete = model.ObsoleteCount, percent = model.PercentComplete, listedRows = view.View.ListedRowCount, columns = view.View.ColumnHeaders,
			rows = model.VisibleRows.Select(r => new { source = r.Source, context = r.Context, translation = r.Translation, state = r.State.ToString(), fuzzy = r.IsFuzzy, comment = r.TranslatorComment, readOnly = r.IsReadOnly })
		});
	}

	[DevFlowAction("od.translator.set-translation", Description = "Set the translation of the entry with this msgid (and context, when given) in the active translator view, as typing into its cell does")]
	public static string SetTranslation(string msgId, string translation, string context = "")
	{
		var view = Translator();
		var row = view?.Model.Find(msgId, context.Length == 0 ? null : context);
		if (row == null) return JsonSerializer.Serialize(new { success = false });
		row.Translation = translation;
		return JsonSerializer.Serialize(new { success = !row.IsReadOnly, state = row.State.ToString(), dirty = view!.Model.IsDirty });
	}

	[DevFlowAction("od.translator.set-fuzzy", Description = "Mark or unmark the entry with this msgid as fuzzy in the active translator view")]
	public static string SetFuzzy(string msgId, bool fuzzy, string context = "")
	{
		var view = Translator();
		var row = view?.Model.Find(msgId, context.Length == 0 ? null : context);
		if (row == null) return JsonSerializer.Serialize(new { success = false });
		row.IsFuzzy = fuzzy;
		return JsonSerializer.Serialize(new { success = !row.IsReadOnly, state = row.State.ToString() });
	}

	[DevFlowAction("od.translator.filter", Description = "Show a filter (All, NeedsWork, Untranslated, Fuzzy, Translated, Obsolete) and a search text in the active translator view")]
	public static string Filter(string filter, string search = "")
	{
		var view = Translator();
		if (view == null || !System.Enum.TryParse<TranslationFilter>(filter, true, out var shown)) return JsonSerializer.Serialize(new { success = false });
		view.View.Show(shown, search);
		return JsonSerializer.Serialize(new { success = true, rows = view.Model.VisibleRows.Count });
	}
}
