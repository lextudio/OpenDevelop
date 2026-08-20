// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// An ITextSnapshotLine must NOT wrap a live AvalonEdit DocumentLine: it belongs to an immutable
// snapshot, while DocumentLine belongs to the live document (vs-editor-api.md section 16). Line
// metadata is therefore derived from the snapshot's own line table.

using System;

using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>A line of an immutable snapshot, derived from that snapshot's line table.</summary>
public sealed class AvalonTextSnapshotLine : ITextSnapshotLine
{
	readonly AvalonTextSnapshot snapshot;
	readonly int lineNumber;
	readonly int start;
	readonly int length;
	readonly int lineBreakLength;

	internal AvalonTextSnapshotLine(AvalonTextSnapshot snapshot, int lineNumber,
		int start, int length, int lineBreakLength)
	{
		this.snapshot = snapshot;
		this.lineNumber = lineNumber;
		this.start = start;
		this.length = length;
		this.lineBreakLength = lineBreakLength;
	}

	public ITextSnapshot Snapshot => snapshot;

	public int LineNumber => lineNumber;

	/// <summary>The character offset where this line starts.</summary>
	internal int StartOffset => start;

	/// <summary>The character offset just past this line's text (excluding its line break).</summary>
	internal int EndOffset => start + length;

	/// <summary>The character offset just past this line including its line break.</summary>
	internal int EndOffsetIncludingLineBreak => start + length + lineBreakLength;

	public int Length => length;

	public int LengthIncludingLineBreak => length + lineBreakLength;

	public int LineBreakLength => lineBreakLength;

	public SnapshotPoint Start => new SnapshotPoint(snapshot, start);

	public SnapshotPoint End => new SnapshotPoint(snapshot, start + length);

	public SnapshotPoint EndIncludingLineBreak => new SnapshotPoint(snapshot, start + length + lineBreakLength);

	public SnapshotSpan Extent => new SnapshotSpan(snapshot, new Span(start, length));

	public SnapshotSpan ExtentIncludingLineBreak => new SnapshotSpan(snapshot, new Span(start, length + lineBreakLength));

	public string GetText() => snapshot.GetText(Start, Length);

	public string GetTextIncludingLineBreak() => snapshot.GetText(Start, Length + lineBreakLength);

	public string GetLineBreakText() => lineBreakLength == 0 ? string.Empty : snapshot.GetText(Start + Length, lineBreakLength);
}
