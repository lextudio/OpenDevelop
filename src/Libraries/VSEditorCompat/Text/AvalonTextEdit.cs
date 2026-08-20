// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// A VS ITextEdit collects requested mutations, then applies them as ONE AvalonEdit update group
// so the document's undo stack sees a single undoable operation (vs-editor-api.md sections 13
// and 18). Changes are applied in descending source-offset order so earlier offsets stay valid.

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>A deferred edit transaction over an AvalonEdit document.</summary>
public sealed class AvalonTextEdit : ITextEdit
{
	readonly AvalonTextBuffer buffer;
	readonly ITextSnapshot snapshot;
	readonly List<Change> changes = new();
	bool applied;
	bool canceled;
	bool hasFailedChanges;

	sealed record Change(int Start, int OldLength, string NewText);

	internal AvalonTextEdit(AvalonTextBuffer buffer)
	{
		this.buffer = buffer;
		snapshot = buffer.CurrentSnapshot;
	}

	public ITextSnapshot Snapshot => snapshot;

	public bool Canceled => canceled;

	public bool HasEffectiveChanges => changes.Count > 0;

	public bool HasFailedChanges => hasFailedChanges;

	public bool Insert(int position, string text)
	{
		if (!ValidatePosition(position))
			return MarkFailed();
		if (string.IsNullOrEmpty(text))
			return true;
		changes.Add(new Change(position, 0, text));
		return true;
	}

	public bool Insert(int position, char[] characterBuffer, int startIndex, int length)
	{
		if (!ValidatePosition(position))
			return MarkFailed();
		if (characterBuffer == null)
			throw new ArgumentNullException(nameof(characterBuffer));
		if (length == 0)
			return true;
		return Insert(position, new string(characterBuffer, startIndex, length));
	}

	public bool Delete(Span deleteSpan) => Replace(deleteSpan, string.Empty);

	public bool Delete(int startPosition, int charsToDelete)
		=> Replace(new Span(startPosition, charsToDelete), string.Empty);

	public bool Replace(Span replaceSpan, string replaceWith)
	{
		if (replaceSpan.Start < 0 || replaceSpan.End > snapshot.Length)
			return MarkFailed();
		replaceWith ??= string.Empty;
		if (replaceSpan.Length == 0 && replaceWith.Length == 0)
			return true;
		changes.Add(new Change(replaceSpan.Start, replaceSpan.Length, replaceWith));
		return true;
	}

	public bool Replace(int startPosition, int charsToReplace, string replaceWith)
		=> Replace(new Span(startPosition, charsToReplace), replaceWith);

	public ITextSnapshot Apply()
	{
		if (applied)
			throw new InvalidOperationException("The edit has already been applied.");
		if (canceled)
			throw new InvalidOperationException("The edit has been canceled.");
		applied = true;

		buffer.BeginEdit();
		var document = buffer.Document;
		document.BeginUpdate();
		try {
			foreach (var change in changes.OrderByDescending(c => c.Start))
				document.Replace(change.Start, change.OldLength, change.NewText);
		} finally {
			document.EndUpdate();
			buffer.EndEdit();
		}
		return buffer.CurrentSnapshot;
	}

	public void Cancel()
	{
		if (applied)
			throw new InvalidOperationException("The edit has already been applied.");
		canceled = true;
	}

	/// <summary>VS semantics: an edit must be applied or canceled before it is disposed.</summary>
	public void Dispose()
	{
		if (!applied && !canceled)
			throw new InvalidOperationException("The edit must be applied or canceled before it is disposed.");
	}

	bool ValidatePosition(int position) => position >= 0 && position <= snapshot.Length;

	bool MarkFailed()
	{
		hasFailedChanges = true;
		return false;
	}
}
