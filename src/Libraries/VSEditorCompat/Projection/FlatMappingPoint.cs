// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IMappingPoint over FlatBufferGraph's single, non-projecting buffer (see FlatBufferGraph.cs).

using System;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Projection;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class FlatMappingPoint : IMappingPoint
{
	readonly FlatBufferGraph graph;
	readonly SnapshotPoint point;
	readonly PointTrackingMode trackingMode;

	public FlatMappingPoint(FlatBufferGraph graph, SnapshotPoint point, PointTrackingMode trackingMode)
	{
		this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
		this.point = point;
		this.trackingMode = trackingMode;
	}

	public ITextBuffer AnchorBuffer => point.Snapshot.TextBuffer;

	public IBufferGraph BufferGraph => graph;

	public SnapshotPoint? GetPoint(ITextBuffer targetBuffer, PositionAffinity affinity)
		=> graph.MapDownToBuffer(point, trackingMode, targetBuffer, affinity);

	public SnapshotPoint? GetPoint(ITextSnapshot targetSnapshot, PositionAffinity affinity)
		=> graph.MapDownToSnapshot(point, trackingMode, targetSnapshot, affinity);

	public SnapshotPoint? GetPoint(Predicate<ITextBuffer> match, PositionAffinity affinity)
		=> match(AnchorBuffer) ? GetPoint(AnchorBuffer, affinity) : (SnapshotPoint?)null;

	public SnapshotPoint? GetInsertionPoint(Predicate<ITextBuffer> match)
		=> GetPoint(match, PositionAffinity.Successor);
}
