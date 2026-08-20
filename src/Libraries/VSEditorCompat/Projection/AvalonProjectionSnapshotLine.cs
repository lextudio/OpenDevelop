// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextSnapshotLine over an AvalonProjectionSnapshot's materialized text - same shape as
// AvalonTextSnapshotLine, just backed by a plain string slice instead of an AvalonEdit ITextSource.

using System;

using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonProjectionSnapshotLine : ITextSnapshotLine
{
	readonly AvalonProjectionSnapshot snapshot;
	readonly int lineNumber;
	readonly int start;
	readonly int length;
	readonly int lineBreakLength;

	internal AvalonProjectionSnapshotLine(AvalonProjectionSnapshot snapshot, int lineNumber, int start, int length, int lineBreakLength)
	{
		this.snapshot = snapshot;
		this.lineNumber = lineNumber;
		this.start = start;
		this.length = length;
		this.lineBreakLength = lineBreakLength;
	}

	public ITextSnapshot Snapshot => snapshot;

	public int LineNumber => lineNumber;

	public SnapshotPoint Start => new(snapshot, start);

	public int Length => length;

	public int LengthIncludingLineBreak => length + lineBreakLength;

	public SnapshotPoint End => new(snapshot, start + length);

	public SnapshotPoint EndIncludingLineBreak => new(snapshot, start + length + lineBreakLength);

	public SnapshotSpan Extent => new(snapshot, start, length);

	public SnapshotSpan ExtentIncludingLineBreak => new(snapshot, start, length + lineBreakLength);

	public int LineBreakLength => lineBreakLength;

	public string GetLineBreakText() => lineBreakLength == 0 ? string.Empty : snapshot.GetText(start + length, lineBreakLength);

	public string GetText() => snapshot.GetText(start, length);

	public string GetTextIncludingLineBreak() => snapshot.GetText(start, length + lineBreakLength);
}
