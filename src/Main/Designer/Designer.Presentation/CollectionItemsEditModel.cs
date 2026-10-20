using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>
/// Framework-neutral state of a collection editor dialog over a list of item texts. Each entry
/// remembers the index it had when the dialog opened (-1 when added in the dialog), so the result
/// tells the host which existing items moved, which were renamed, which are new and - by omission -
/// which were removed. The dialog is only a view of this model.
/// </summary>
public sealed class CollectionItemsEditModel
{
	public sealed class Entry
	{
		internal Entry(int sourceIndex, string text, string kind = "")
		{
			SourceIndex = sourceIndex;
			Text = text;
			Kind = kind;
		}

		public int SourceIndex { get; }
		public string Text { get; set; }
		public string Kind { get; }
		public override string ToString() => Text;
	}

	readonly IReadOnlyList<string> original;

	public CollectionItemsEditModel(IEnumerable<string> texts, IEnumerable<string>? kinds = null, bool supportsDataGridColumnKinds = false)
	{
		// Normalized once, so a null item text compares equal to its unedited "" entry.
		original = (texts ?? throw new ArgumentNullException(nameof(texts))).Select(text => text ?? "").ToList();
		var kindList = (kinds ?? Array.Empty<string>()).ToList(); SupportsDataGridColumnKinds = supportsDataGridColumnKinds;
		Entries = new ObservableCollection<Entry>(original.Select((text, index) => new Entry(index, text, index < kindList.Count ? kindList[index] : "")));
	}
	public bool SupportsDataGridColumnKinds { get; }

	public ObservableCollection<Entry> Entries { get; }

	/// <summary>Adds a new item after <paramref name="afterIndex"/> (at the end when out of range)
	/// and returns its index.</summary>
	public int Add(string text, int afterIndex = -1, string kind = "")
	{
		var index = afterIndex >= 0 && afterIndex < Entries.Count ? afterIndex + 1 : Entries.Count;
		Entries.Insert(index, new Entry(-1, text ?? "", kind));
		return index;
	}

	public bool Remove(int index)
	{
		if (index < 0 || index >= Entries.Count)
			return false;
		Entries.RemoveAt(index);
		return true;
	}

	public bool MoveUp(int index) => Move(index, index - 1);

	public bool MoveDown(int index) => Move(index, index + 1);

	bool Move(int from, int to)
	{
		if (from < 0 || from >= Entries.Count || to < 0 || to >= Entries.Count)
			return false;
		Entries.Move(from, to);
		return true;
	}

	public bool Rename(int index, string text)
	{
		if (index < 0 || index >= Entries.Count)
			return false;
		Entries[index].Text = text ?? "";
		return true;
	}

	/// <summary>False when the edited list is identical to the one the dialog opened with, so
	/// confirming an untouched dialog makes no document edit.</summary>
	public bool IsChanged
		=> Entries.Count != original.Count
			|| Entries.Select((entry, index) => entry.SourceIndex != index || !string.Equals(entry.Text, original[index], StringComparison.Ordinal)).Any(changed => changed);

	/// <summary>The edited list in the wire shape of a whole-collection mutation.</summary>
	public IReadOnlyList<DesignerCollectionItemEdit> ToEdits()
		=> Entries.Select(entry => new DesignerCollectionItemEdit { SourceIndex = entry.SourceIndex, Text = entry.Text, Kind = entry.Kind }).ToList();
}
