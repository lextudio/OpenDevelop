// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextEdit for AvalonProjectionBuffer: each requested change is resolved to the ONE segment it
// falls within (AvalonProjectionBuffer.ResolveEditTarget - see that class's comment on the
// single-segment restriction) and delegated to that segment's real source buffer. Applying is
// therefore "apply N edits to (possibly several) source buffers", after which the projection
// simply recomputes from its now-changed sources (AvalonProjectionBuffer.RecomputeAfterEdit) -
// there is no separate "apply to the projection's own text" step.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonProjectionTextEdit : ITextEdit
{
	sealed record Change(int Start, int OldLength, string NewText);

	readonly AvalonProjectionBuffer buffer;
	readonly ITextSnapshot snapshot;
	readonly List<Change> changes = new();
	bool applied;
	bool canceled;
	bool hasFailedChanges;

	internal AvalonProjectionTextEdit(AvalonProjectionBuffer buffer)
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
		if (position < 0 || position > snapshot.Length) {
			hasFailedChanges = true;
			return false;
		}
		if (string.IsNullOrEmpty(text))
			return true;
		changes.Add(new Change(position, 0, text));
		return true;
	}

	public bool Insert(int position, char[] characterBuffer, int startIndex, int length)
		=> Insert(position, new string(characterBuffer ?? throw new ArgumentNullException(nameof(characterBuffer)), startIndex, length));

	public bool Delete(Span deleteSpan) => Replace(deleteSpan, string.Empty);

	public bool Delete(int startPosition, int charsToDelete) => Replace(new Span(startPosition, charsToDelete), string.Empty);

	public bool Replace(Span replaceSpan, string replaceWith)
	{
		if (replaceSpan.Start < 0 || replaceSpan.End > snapshot.Length) {
			hasFailedChanges = true;
			return false;
		}
		replaceWith ??= string.Empty;
		if (replaceSpan.Length == 0 && replaceWith.Length == 0)
			return true;
		changes.Add(new Change(replaceSpan.Start, replaceSpan.Length, replaceWith));
		return true;
	}

	public bool Replace(int startPosition, int charsToReplace, string replaceWith) => Replace(new Span(startPosition, charsToReplace), replaceWith);

	public ITextSnapshot Apply()
	{
		if (applied)
			throw new InvalidOperationException("The edit has already been applied.");
		if (canceled)
			throw new InvalidOperationException("The edit has been canceled.");
		applied = true;

		buffer.BeginEdit();
		buffer.RaiseChanging(snapshot);
		try {
			// Descending order so earlier offsets in the (shared, un-recomputed) starting
			// snapshot stay valid across the whole batch, same reasoning as AvalonTextEdit.
			foreach (var change in changes.OrderByDescending(c => c.Start)) {
				var (segment, offsetInSegment) = buffer.ResolveEditTarget(change.Start, change.OldLength);
				using var sourceEdit = segment.Buffer.CreateEdit();
				sourceEdit.Replace(new Span(segment.CurrentSpan.Start.Position + offsetInSegment, change.OldLength), change.NewText);
				sourceEdit.Apply();
			}
		} finally {
			buffer.EndEdit();
		}
		// Source buffers already fired their own Changed, which AvalonProjectionBuffer
		// subscribes to and recomputes from - but only if at least one segment actually saw a
		// real edit (an empty change list must still return a snapshot without a spurious
		// recompute/event pair).
		return changes.Count > 0 ? buffer.CurrentSnapshot : snapshot;
	}

	public void Cancel()
	{
		if (applied)
			throw new InvalidOperationException("The edit has already been applied.");
		canceled = true;
	}

	public void Dispose()
	{
		if (!applied && !canceled)
			throw new InvalidOperationException("The edit must be applied or canceled before it is disposed.");
	}
}
