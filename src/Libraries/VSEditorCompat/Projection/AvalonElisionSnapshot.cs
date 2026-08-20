// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IElisionSnapshot: thin wrapper delegating everything to the inner AvalonProjectionSnapshot
// (AvalonElisionBuffer's private "visible spans" projection), adding only the 3
// elision-specific members (TextBuffer as IElisionBuffer, SourceSnapshot, and
// MapFromSourceSnapshotToNearest - "the nearest visible point" when the given source point
// itself falls inside an elided range).

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonElisionSnapshot : IElisionSnapshot
{
	readonly AvalonElisionBuffer elisionBuffer;
	readonly AvalonProjectionSnapshot inner;

	internal AvalonElisionSnapshot(AvalonElisionBuffer elisionBuffer, AvalonProjectionSnapshot inner, ITextSnapshot sourceSnapshot)
	{
		this.elisionBuffer = elisionBuffer;
		this.inner = inner;
		SourceSnapshot = sourceSnapshot;
	}

	public IElisionBuffer TextBuffer => elisionBuffer;

	public ITextSnapshot SourceSnapshot { get; }

	public SnapshotPoint MapFromSourceSnapshotToNearest(SnapshotPoint point)
	{
		var mapped = inner.MapFromSourceSnapshot(point, PositionAffinity.Successor);
		if (mapped.HasValue)
			return mapped.Value;
		// Inside an elided range: snap to the next visible span's start (or, if none follows,
		// the end of the last visible span).
		SnapshotPoint? nearestBoundary = null;
		foreach (var span in inner.GetSourceSpans()) {
			if (span.Start.Position >= point.Position) {
				nearestBoundary = span.Start;
				break;
			}
			nearestBoundary = span.End;
		}
		if (nearestBoundary == null)
			return new SnapshotPoint(this, 0);
		return inner.MapFromSourceSnapshot(nearestBoundary.Value, PositionAffinity.Successor) ?? new SnapshotPoint(this, Length);
	}

	IProjectionBufferBase IProjectionSnapshot.TextBuffer => elisionBuffer;
	ITextBuffer ITextSnapshot.TextBuffer => elisionBuffer;

	public int SpanCount => inner.SpanCount;
	public ReadOnlyCollection<ITextSnapshot> SourceSnapshots => inner.SourceSnapshots;
	public ITextSnapshot GetMatchingSnapshot(ITextBuffer textBuffer) => inner.GetMatchingSnapshot(textBuffer);
	public ReadOnlyCollection<SnapshotSpan> GetSourceSpans() => inner.GetSourceSpans();
	public ReadOnlyCollection<SnapshotSpan> GetSourceSpans(int startSpanIndex, int count) => inner.GetSourceSpans(startSpanIndex, count);
	public SnapshotPoint MapToSourceSnapshot(int position) => inner.MapToSourceSnapshot(position);
	public SnapshotPoint MapToSourceSnapshot(int position, PositionAffinity affinity) => inner.MapToSourceSnapshot(position, affinity);
	public ReadOnlyCollection<SnapshotPoint> MapToSourceSnapshots(int position) => inner.MapToSourceSnapshots(position);
	public ReadOnlyCollection<SnapshotSpan> MapToSourceSnapshots(Span span) => inner.MapToSourceSnapshots(span);
	public SnapshotPoint? MapFromSourceSnapshot(SnapshotPoint point, PositionAffinity affinity) => inner.MapFromSourceSnapshot(point, affinity);
	public ReadOnlyCollection<Span> MapFromSourceSnapshot(SnapshotSpan span) => inner.MapFromSourceSnapshot(span);

	public ITextVersion Version => inner.Version;
	public IContentType ContentType => inner.ContentType;
	public int Length => inner.Length;
	public char this[int position] => inner[position];
	public int LineCount => inner.LineCount;
	public IEnumerable<ITextSnapshotLine> Lines => inner.Lines;
	public ITextSnapshotLine GetLineFromLineNumber(int lineNumber) => inner.GetLineFromLineNumber(lineNumber);
	public ITextSnapshotLine GetLineFromPosition(int position) => inner.GetLineFromPosition(position);
	public int GetLineNumberFromPosition(int position) => inner.GetLineNumberFromPosition(position);
	public string GetText() => inner.GetText();
	public string GetText(int startIndex, int length) => inner.GetText(startIndex, length);
	public string GetText(Span span) => inner.GetText(span);
	public char[] ToCharArray(int startIndex, int length) => inner.ToCharArray(startIndex, length);
	public void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count) => inner.CopyTo(sourceIndex, destination, destinationIndex, count);
	public void Write(TextWriter writer) => inner.Write(writer);
	public void Write(TextWriter writer, Span span) => inner.Write(writer, span);
	public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode) => inner.CreateTrackingPoint(position, trackingMode);
	public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode, TrackingFidelityMode trackingFidelity) => inner.CreateTrackingPoint(position, trackingMode, trackingFidelity);
	public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode) => inner.CreateTrackingSpan(span, trackingMode);
	public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity) => inner.CreateTrackingSpan(span, trackingMode, trackingFidelity);
	public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode) => inner.CreateTrackingSpan(start, length, trackingMode);
	public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity) => inner.CreateTrackingSpan(start, length, trackingMode, trackingFidelity);
}
