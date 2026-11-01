using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace ICSharpCode.Translations;

/// <summary>Where a catalogue entry stands, as the translator view's State column and filters show it.</summary>
public enum TranslationState { Untranslated, Fuzzy, Translated, Obsolete }

/// <summary>Which rows the translator view lists.</summary>
public enum TranslationFilter
{
	/// <summary>Every live entry (obsolete ones are listed only by <see cref="Obsolete"/>).</summary>
	All,
	/// <summary>Untranslated and fuzzy entries: what a translator still has to do.</summary>
	NeedsWork,
	Untranslated,
	Fuzzy,
	Translated,
	Obsolete
}

/// <summary>One row of the translator view over a <see cref="PoEntry"/>. Plural and obsolete entries are shown but not
/// edited here: their forms and their fate are decisions for the source view.</summary>
public sealed class TranslatorRow : INotifyPropertyChanged
{
	readonly TranslatorModel owner;
	internal PoEntry Entry { get; }

	internal TranslatorRow(TranslatorModel owner, PoEntry entry) { this.owner = owner; Entry = entry; }

	public event PropertyChangedEventHandler? PropertyChanged;
	void Notify(params string[] names) { foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }

	public string Context => Entry.Context ?? "";
	/// <summary>The source text; a plural entry shows both of its forms.</summary>
	public string Source => Entry.MsgIdPlural == null ? Entry.MsgId : Entry.MsgId + " | " + Entry.MsgIdPlural;
	public bool IsPlural => Entry.MsgIdPlural != null;
	public bool IsReadOnly => Entry.Obsolete || IsPlural;
	public string ExtractedComment => string.Join("\n", Entry.ExtractedComments);
	public string References => string.Join("  ", Entry.References);

	/// <summary>The translation. A plural entry shows its forms separated by " | " and cannot be set here. Entering text
	/// in a fuzzy entry confirms it (the fuzzy flag goes), as other PO editors do.</summary>
	public string Translation
	{
		get => IsPlural ? string.Join(" | ", Entry.PluralStrings) : Entry.MsgStr;
		set
		{
			if (IsReadOnly || value == null || value == Entry.MsgStr) return;
			Entry.MsgStr = value;
			if (value.Length > 0) Entry.Flags.Remove("fuzzy");
			Notify(nameof(Translation), nameof(State), nameof(IsFuzzy));
			owner.MarkChanged();
		}
	}

	public bool IsFuzzy
	{
		get => Entry.IsFuzzy;
		set
		{
			if (IsReadOnly || value == Entry.IsFuzzy) return;
			if (value) Entry.Flags.Add("fuzzy"); else Entry.Flags.RemoveAll(f => f == "fuzzy");
			Notify(nameof(IsFuzzy), nameof(State));
			owner.MarkChanged();
		}
	}

	/// <summary>The translator's own comment (the <c>#</c> lines), one line per line.</summary>
	public string TranslatorComment
	{
		get => string.Join("\n", Entry.TranslatorComments);
		set
		{
			if (Entry.Obsolete || value == null || value == TranslatorComment) return;
			Entry.TranslatorComments.Clear();
			if (value.Length > 0) Entry.TranslatorComments.AddRange(value.Replace("\r", "").Split('\n'));
			Notify(nameof(TranslatorComment));
			owner.MarkChanged();
		}
	}

	public TranslationState State => Entry.Obsolete ? TranslationState.Obsolete
		: !Entry.IsTranslated ? TranslationState.Untranslated
		: Entry.IsFuzzy ? TranslationState.Fuzzy : TranslationState.Translated;

	internal bool Matches(string search)
		=> Source.Contains(search, StringComparison.OrdinalIgnoreCase) || Translation.Contains(search, StringComparison.OrdinalIgnoreCase)
			|| ExtractedComment.Contains(search, StringComparison.OrdinalIgnoreCase) || TranslatorComment.Contains(search, StringComparison.OrdinalIgnoreCase)
			|| Context.Contains(search, StringComparison.OrdinalIgnoreCase) || References.Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The translator view's model over one <c>.po</c> text: its rows, filters and progress. Pure (no WPF), so the unit tests
/// drive it directly. Text that was not edited is written back exactly as it was loaded, so opening and saving a
/// hand-formatted file through the view changes nothing; once a row is edited the catalogue is rewritten in gettext's layout.
/// </summary>
public sealed class TranslatorModel
{
	string originalText = "";
	PoCatalog catalog = new PoCatalog();
	List<TranslatorRow> rows = new List<TranslatorRow>();

	/// <summary>Raised after any edit; the view content marks the file dirty from it.</summary>
	public event EventHandler? Changed;
	/// <summary>Raised after <see cref="Load"/> replaced the rows.</summary>
	public event EventHandler? Reloaded;

	public bool IsDirty { get; private set; }
	public TranslationFilter Filter { get; set; } = TranslationFilter.All;
	public string Search { get; set; } = "";

	public void Load(string text)
	{
		originalText = text ?? "";
		catalog = PoCatalog.Parse(originalText);
		rows = catalog.Entries.Select(entry => new TranslatorRow(this, entry)).ToList();
		IsDirty = false;
		Reloaded?.Invoke(this, EventArgs.Empty);
	}

	internal void MarkChanged() { IsDirty = true; Changed?.Invoke(this, EventArgs.Empty); }

	/// <summary>The text to save: the loaded text while nothing was edited, otherwise the rewritten catalogue.</summary>
	public string ToText() => IsDirty ? catalog.Write() : originalText;

	/// <summary>Records that <see cref="ToText"/>'s result was written, so it is the new baseline.</summary>
	public void MarkSaved() { originalText = ToText(); IsDirty = false; }

	public IReadOnlyList<TranslatorRow> AllRows => rows;

	public IReadOnlyList<TranslatorRow> VisibleRows
	{
		get
		{
			var visible = rows.Where(row => Filter switch {
				TranslationFilter.Obsolete => row.State == TranslationState.Obsolete,
				TranslationFilter.NeedsWork => row.State is TranslationState.Untranslated or TranslationState.Fuzzy,
				TranslationFilter.Untranslated => row.State == TranslationState.Untranslated,
				TranslationFilter.Fuzzy => row.State == TranslationState.Fuzzy,
				TranslationFilter.Translated => row.State == TranslationState.Translated,
				_ => row.State != TranslationState.Obsolete
			});
			if (!string.IsNullOrWhiteSpace(Search)) visible = visible.Where(row => row.Matches(Search.Trim()));
			return visible.ToList();
		}
	}

	int Count(TranslationState state) => rows.Count(r => r.State == state);
	public int Total => rows.Count(r => r.State != TranslationState.Obsolete);
	public int TranslatedCount => Count(TranslationState.Translated);
	public int FuzzyCount => Count(TranslationState.Fuzzy);
	public int UntranslatedCount => Count(TranslationState.Untranslated);
	public int ObsoleteCount => Count(TranslationState.Obsolete);
	/// <summary>Share of live entries that are translated and not fuzzy, 0-100.</summary>
	public double PercentComplete => Total == 0 ? 100 : 100.0 * TranslatedCount / Total;

	/// <summary>The header's <c>Language:</c> value ("fr"), or "".</summary>
	public string Language
	{
		get
		{
			foreach (var line in catalog.Header.MsgStr.Split('\n'))
				if (line.StartsWith("Language:", StringComparison.Ordinal)) return line.Substring(9).Trim();
			return "";
		}
	}

	/// <summary>The row of the live entry with this msgid (and context, when given); null when there is none.</summary>
	public TranslatorRow? Find(string msgId, string? context = null)
		=> rows.FirstOrDefault(r => r.State != TranslationState.Obsolete && r.Entry.MsgId == msgId && (context == null || (r.Entry.Context ?? "") == context));
}
