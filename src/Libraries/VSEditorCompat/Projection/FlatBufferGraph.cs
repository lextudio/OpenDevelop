// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// A non-projecting IBufferGraph over exactly one ITextBuffer (vs-editor-api.md section 32
// explicitly defers real projection/elision buffers to a later, dedicated workstream). Every
// "mapping" here is either an identity (same buffer) or a version-to-version offset move via
// ITextVersion.MoveOffsetTo - there is no second source buffer to project across. This still
// satisfies the CaretPosition/ITagAggregator/IMappingSpan contracts that assume a buffer graph
// exists, without pretending to support Razor-style embedded-language projection.

using System;
using System.Collections.ObjectModel;

using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>A buffer graph containing exactly one text buffer - no projection.</summary>
public sealed class FlatBufferGraph : Microsoft.VisualStudio.Text.Projection.IBufferGraph
{
	readonly ITextBuffer buffer;

	public FlatBufferGraph(ITextBuffer buffer)
	{
		this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
	}

	public ITextBuffer TopBuffer => buffer;

	public event EventHandler<Microsoft.VisualStudio.Text.Projection.GraphBuffersChangedEventArgs> GraphBuffersChanged { add { } remove { } }
	public event EventHandler<Microsoft.VisualStudio.Text.Projection.GraphBufferContentTypeChangedEventArgs> GraphBufferContentTypeChanged { add { } remove { } }

	public Collection<ITextBuffer> GetTextBuffers(Predicate<ITextBuffer> match)
	{
		var result = new Collection<ITextBuffer>();
		if (match == null || match(buffer))
			result.Add(buffer);
		return result;
	}

	public IMappingPoint CreateMappingPoint(SnapshotPoint point, PointTrackingMode trackingMode)
		=> new FlatMappingPoint(this, point, trackingMode);

	public IMappingSpan CreateMappingSpan(SnapshotSpan span, SpanTrackingMode trackingMode)
		=> new FlatMappingSpan(this, span, trackingMode);

	SnapshotPoint? Retarget(SnapshotPoint point, PointTrackingMode trackingMode, ITextBuffer targetBuffer)
	{
		if (!ReferenceEquals(point.Snapshot.TextBuffer, buffer) || !ReferenceEquals(targetBuffer, buffer))
			return null;
		return point.TranslateTo(buffer.CurrentSnapshot, trackingMode);
	}

	public SnapshotPoint? MapDownToBuffer(SnapshotPoint point, PointTrackingMode trackingMode, ITextBuffer targetBuffer, PositionAffinity affinity)
		=> Retarget(point, trackingMode, targetBuffer);

	public SnapshotPoint? MapDownToSnapshot(SnapshotPoint point, PointTrackingMode trackingMode, ITextSnapshot targetSnapshot, PositionAffinity affinity)
		=> ReferenceEquals(point.Snapshot.TextBuffer, buffer) && ReferenceEquals(targetSnapshot.TextBuffer, buffer)
			? point.TranslateTo(targetSnapshot, trackingMode)
			: (SnapshotPoint?)null;

	public SnapshotPoint? MapDownToFirstMatch(SnapshotPoint point, PointTrackingMode trackingMode, Predicate<ITextSnapshot> match, PositionAffinity affinity)
		=> match(buffer.CurrentSnapshot) ? point.TranslateTo(buffer.CurrentSnapshot, trackingMode) : (SnapshotPoint?)null;

	public SnapshotPoint? MapDownToInsertionPoint(SnapshotPoint point, PointTrackingMode trackingMode, Predicate<ITextSnapshot> match)
		=> MapDownToFirstMatch(point, trackingMode, match, PositionAffinity.Successor);

	public NormalizedSnapshotSpanCollection MapDownToBuffer(SnapshotSpan span, SpanTrackingMode trackingMode, ITextBuffer targetBuffer)
		=> ReferenceEquals(span.Snapshot.TextBuffer, buffer) && ReferenceEquals(targetBuffer, buffer)
			? new NormalizedSnapshotSpanCollection(span.TranslateTo(buffer.CurrentSnapshot, trackingMode))
			: NormalizedSnapshotSpanCollection.Empty;

	public NormalizedSnapshotSpanCollection MapDownToSnapshot(SnapshotSpan span, SpanTrackingMode trackingMode, ITextSnapshot targetSnapshot)
		=> ReferenceEquals(span.Snapshot.TextBuffer, buffer) && ReferenceEquals(targetSnapshot.TextBuffer, buffer)
			? new NormalizedSnapshotSpanCollection(span.TranslateTo(targetSnapshot, trackingMode))
			: NormalizedSnapshotSpanCollection.Empty;

	public NormalizedSnapshotSpanCollection MapDownToFirstMatch(SnapshotSpan span, SpanTrackingMode trackingMode, Predicate<ITextSnapshot> match)
		=> match(buffer.CurrentSnapshot) ? new NormalizedSnapshotSpanCollection(span.TranslateTo(buffer.CurrentSnapshot, trackingMode)) : NormalizedSnapshotSpanCollection.Empty;

	public SnapshotPoint? MapUpToBuffer(SnapshotPoint point, PointTrackingMode trackingMode, PositionAffinity affinity, ITextBuffer targetBuffer)
		=> Retarget(point, trackingMode, targetBuffer);

	public SnapshotPoint? MapUpToSnapshot(SnapshotPoint point, PointTrackingMode trackingMode, PositionAffinity affinity, ITextSnapshot targetSnapshot)
		=> MapDownToSnapshot(point, trackingMode, targetSnapshot, affinity);

	public SnapshotPoint? MapUpToFirstMatch(SnapshotPoint point, PointTrackingMode trackingMode, Predicate<ITextSnapshot> match, PositionAffinity affinity)
		=> MapDownToFirstMatch(point, trackingMode, match, affinity);

	public NormalizedSnapshotSpanCollection MapUpToBuffer(SnapshotSpan span, SpanTrackingMode trackingMode, ITextBuffer targetBuffer)
		=> MapDownToBuffer(span, trackingMode, targetBuffer);

	public NormalizedSnapshotSpanCollection MapUpToSnapshot(SnapshotSpan span, SpanTrackingMode trackingMode, ITextSnapshot targetSnapshot)
		=> MapDownToSnapshot(span, trackingMode, targetSnapshot);

	public NormalizedSnapshotSpanCollection MapUpToFirstMatch(SnapshotSpan span, SpanTrackingMode trackingMode, Predicate<ITextSnapshot> match)
		=> MapDownToFirstMatch(span, trackingMode, match);
}
