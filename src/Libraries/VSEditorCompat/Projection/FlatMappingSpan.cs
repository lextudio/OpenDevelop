// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IMappingSpan over FlatBufferGraph's single, non-projecting buffer (see FlatBufferGraph.cs).

using System;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Projection;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class FlatMappingSpan : IMappingSpan
{
	readonly FlatBufferGraph graph;
	readonly SnapshotSpan span;
	readonly SpanTrackingMode trackingMode;

	public FlatMappingSpan(FlatBufferGraph graph, SnapshotSpan span, SpanTrackingMode trackingMode)
	{
		this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
		this.span = span;
		this.trackingMode = trackingMode;
	}

	public ITextBuffer AnchorBuffer => span.Snapshot.TextBuffer;

	public IBufferGraph BufferGraph => graph;

	public IMappingPoint Start => new FlatMappingPoint(graph, span.Start, PointTrackingMode.Negative);

	public IMappingPoint End => new FlatMappingPoint(graph, span.End, PointTrackingMode.Positive);

	public NormalizedSnapshotSpanCollection GetSpans(ITextBuffer targetBuffer)
		=> graph.MapDownToBuffer(span, trackingMode, targetBuffer);

	public NormalizedSnapshotSpanCollection GetSpans(ITextSnapshot targetSnapshot)
		=> graph.MapDownToSnapshot(span, trackingMode, targetSnapshot);

	public NormalizedSnapshotSpanCollection GetSpans(Predicate<ITextBuffer> match)
		=> match(AnchorBuffer) ? GetSpans(AnchorBuffer) : NormalizedSnapshotSpanCollection.Empty;
}
