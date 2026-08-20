// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IProjectionSnapshot: an immutable concatenation of the projection buffer's current source
// spans, plus the projection <-> source offset mapping (vs-editor-api.md section 32). Unlike
// AvalonTextSnapshot, the concatenated text is materialized eagerly at snapshot-creation time -
// a projection is expected to be a handful of spans over already-loaded buffers (embedded
// languages, Razor-style generated regions), not itself a rope-scale document, so this is a
// deliberate simplicity-over-micro-optimization tradeoff, not an oversight.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonProjectionSnapshot : IProjectionSnapshot
{
	readonly AvalonProjectionBuffer buffer;
	readonly IReadOnlyList<ProjectionSourceSpan> segments;
	readonly string text;
	readonly int[] segmentStarts; // projection offset at which each segment's text begins
	AvalonProjectionVersion version;
	int[] lineStarts;

	internal AvalonProjectionSnapshot(AvalonProjectionBuffer buffer, IReadOnlyList<ProjectionSourceSpan> segments)
	{
		this.buffer = buffer;
		this.segments = segments;
		segmentStarts = new int[segments.Count];
		var builder = new StringBuilder();
		for (int i = 0; i < segments.Count; i++) {
			segmentStarts[i] = builder.Length;
			builder.Append(segments[i].CurrentSpan.GetText());
		}
		text = builder.ToString();
	}

	internal void SetVersion(AvalonProjectionVersion version) => this.version = version;

	internal IReadOnlyList<ProjectionSourceSpan> Segments => segments;

	public IProjectionBufferBase TextBuffer => buffer;

	public int SpanCount => segments.Count;

	public ReadOnlyCollection<ITextSnapshot> SourceSnapshots
		=> new(segments.Select(s => s.Buffer.CurrentSnapshot).Distinct().ToList());

	public ITextSnapshot GetMatchingSnapshot(ITextBuffer textBuffer)
		=> segments.FirstOrDefault(s => ReferenceEquals(s.Buffer, textBuffer))?.Buffer.CurrentSnapshot;

	public ReadOnlyCollection<SnapshotSpan> GetSourceSpans()
		=> new(segments.Select(s => s.CurrentSpan).ToList());

	public ReadOnlyCollection<SnapshotSpan> GetSourceSpans(int startSpanIndex, int count)
		=> new(segments.Skip(startSpanIndex).Take(count).Select(s => s.CurrentSpan).ToList());

	(int segmentIndex, int offsetInSegment) Locate(int projectionOffset, bool preferEarlierAtBoundary)
	{
		for (int i = 0; i < segments.Count; i++) {
			var start = segmentStarts[i];
			var segmentLength = segments[i].CurrentSpan.Length;
			var end = start + segmentLength;
			if (projectionOffset < start)
				break;
			if (projectionOffset < end || (projectionOffset == end && !preferEarlierAtBoundary))
				return (i, projectionOffset - start);
			if (projectionOffset == end)
				return (i, segmentLength);
		}
		if (segments.Count > 0)
			return (segments.Count - 1, segments[segments.Count - 1].CurrentSpan.Length);
		return (-1, 0);
	}

	public SnapshotPoint MapToSourceSnapshot(int position) => MapToSourceSnapshot(position, PositionAffinity.Successor);

	public SnapshotPoint MapToSourceSnapshot(int position, PositionAffinity affinity)
	{
		var (index, offsetInSegment) = Locate(position, preferEarlierAtBoundary: affinity == PositionAffinity.Predecessor);
		if (index < 0)
			throw new ArgumentOutOfRangeException(nameof(position));
		var segment = segments[index];
		return new SnapshotPoint(segment.Buffer.CurrentSnapshot, segment.CurrentSpan.Start.Position + offsetInSegment);
	}

	public ReadOnlyCollection<SnapshotPoint> MapToSourceSnapshots(int position)
	{
		var result = new List<SnapshotPoint>();
		var (index, offsetInSegment) = Locate(position, preferEarlierAtBoundary: false);
		if (index < 0)
			return new ReadOnlyCollection<SnapshotPoint>(result);
		result.Add(new SnapshotPoint(segments[index].Buffer.CurrentSnapshot, segments[index].CurrentSpan.Start.Position + offsetInSegment));
		// At an exact segment boundary, the point also legitimately resolves to the end of the
		// PREVIOUS segment - report both, matching VS's "there can be more than one" contract.
		if (offsetInSegment == 0 && index > 0) {
			var previous = segments[index - 1];
			result.Insert(0, new SnapshotPoint(previous.Buffer.CurrentSnapshot, previous.CurrentSpan.End.Position));
		}
		return new ReadOnlyCollection<SnapshotPoint>(result);
	}

	public ReadOnlyCollection<SnapshotSpan> MapToSourceSnapshots(Span span)
	{
		var result = new List<SnapshotSpan>();
		var (startIndex, startOffset) = Locate(span.Start, preferEarlierAtBoundary: false);
		var (endIndex, endOffset) = Locate(span.End, preferEarlierAtBoundary: true);
		if (startIndex < 0)
			return new ReadOnlyCollection<SnapshotSpan>(result);
		for (int i = startIndex; i <= endIndex; i++) {
			var segment = segments[i];
			var from = i == startIndex ? startOffset : 0;
			var to = i == endIndex ? endOffset : segment.CurrentSpan.Length;
			result.Add(new SnapshotSpan(segment.Buffer.CurrentSnapshot, segment.CurrentSpan.Start.Position + from, Math.Max(0, to - from)));
		}
		return new ReadOnlyCollection<SnapshotSpan>(result);
	}

	public SnapshotPoint? MapFromSourceSnapshot(SnapshotPoint point, PositionAffinity affinity)
	{
		for (int i = 0; i < segments.Count; i++) {
			var segment = segments[i];
			if (!ReferenceEquals(segment.Buffer.CurrentSnapshot, point.Snapshot))
				continue;
			var current = segment.CurrentSpan;
			if (point.Position >= current.Start.Position && point.Position <= current.End.Position)
				return new SnapshotPoint(this, segmentStarts[i] + (point.Position - current.Start.Position));
		}
		return null;
	}

	public ReadOnlyCollection<Span> MapFromSourceSnapshot(SnapshotSpan span)
	{
		var result = new List<Span>();
		for (int i = 0; i < segments.Count; i++) {
			var segment = segments[i];
			if (!ReferenceEquals(segment.Buffer.CurrentSnapshot, span.Snapshot))
				continue;
			var overlap = span.Intersection(segment.CurrentSpan);
			if (overlap.HasValue)
				result.Add(new Span(segmentStarts[i] + (overlap.Value.Start.Position - segment.CurrentSpan.Start.Position), overlap.Value.Length));
		}
		return new ReadOnlyCollection<Span>(result);
	}

	#region ITextSnapshot

	ITextBuffer ITextSnapshot.TextBuffer => buffer;

	public ITextVersion Version => version;

	public IContentType ContentType => buffer.ContentType;

	public int Length => text.Length;

	public char this[int position] => text[position];

	public int LineCount => EnsureLineStarts().Length;

	public IEnumerable<ITextSnapshotLine> Lines
	{
		get
		{
			for (int i = 0; i < LineCount; i++)
				yield return GetLineFromLineNumber(i);
		}
	}

	public ITextSnapshotLine GetLineFromLineNumber(int lineNumber)
	{
		var starts = EnsureLineStarts();
		if (lineNumber < 0 || lineNumber >= starts.Length)
			throw new ArgumentOutOfRangeException(nameof(lineNumber));
		var start = starts[lineNumber];
		int length, lineBreakLength;
		if (lineNumber == starts.Length - 1) {
			length = Length - start;
			lineBreakLength = 0;
		} else {
			length = starts[lineNumber + 1] - 1 - start;
			lineBreakLength = 1;
		}
		return new AvalonProjectionSnapshotLine(this, lineNumber, start, length, lineBreakLength);
	}

	public ITextSnapshotLine GetLineFromPosition(int position) => GetLineFromLineNumber(GetLineNumberFromPosition(position));

	public int GetLineNumberFromPosition(int position)
	{
		if (position < 0 || position > Length)
			throw new ArgumentOutOfRangeException(nameof(position));
		var starts = EnsureLineStarts();
		int low = 0, high = starts.Length - 1;
		while (low < high) {
			int mid = (low + high + 1) / 2;
			if (starts[mid] <= position)
				low = mid;
			else
				high = mid - 1;
		}
		return low;
	}

	public string GetText() => text;

	public string GetText(int startIndex, int length) => text.Substring(startIndex, length);

	public string GetText(Span span) => GetText(span.Start, span.Length);

	public char[] ToCharArray(int startIndex, int length) => text.Substring(startIndex, length).ToCharArray();

	public void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count)
		=> text.CopyTo(sourceIndex, destination, destinationIndex, count);

	public void Write(TextWriter writer) => writer.Write(text);

	public void Write(TextWriter writer, Span span) => writer.Write(GetText(span));

	public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode)
		=> version.CreateTrackingPoint(position, trackingMode);

	public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
		=> version.CreateTrackingPoint(position, trackingMode, trackingFidelity);

	public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode)
		=> version.CreateTrackingSpan(span, trackingMode);

	public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
		=> version.CreateTrackingSpan(span, trackingMode, trackingFidelity);

	public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode)
		=> version.CreateTrackingSpan(start, length, trackingMode);

	public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
		=> version.CreateTrackingSpan(start, length, trackingMode, trackingFidelity);

	int[] EnsureLineStarts()
	{
		if (lineStarts != null)
			return lineStarts;
		var starts = new List<int> { 0 };
		int from = 0;
		while (from < text.Length) {
			int newline = text.IndexOf('\n', from);
			if (newline < 0)
				break;
			starts.Add(newline + 1);
			from = newline + 1;
		}
		return lineStarts = starts.ToArray();
	}

	#endregion
}
