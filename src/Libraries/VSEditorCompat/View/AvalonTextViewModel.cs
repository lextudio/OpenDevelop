// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// A one-buffer ITextViewModel (vs-editor-api.md section 21): edit/data/visual buffer are all the
// same AvalonTextBuffer, since this compatibility layer does not implement projection buffers
// (section 32). Position mapping is therefore always the identity.

using System;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonTextViewModel : ITextViewModel
{
	readonly AvalonTextBuffer buffer;

	public AvalonTextViewModel(AvalonTextBuffer buffer)
	{
		this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
		DataModel = new AvalonTextDataModel(buffer);
	}

	public ITextDataModel DataModel { get; }

	public PropertyCollection Properties { get; } = new();

	public void Dispose()
	{
		// Nothing to release: the underlying AvalonTextBuffer's lifetime is owned by
		// AvalonTextBufferRegistry, not by this view model (section 45).
	}

	public ITextBuffer DataBuffer => buffer;

	public ITextBuffer EditBuffer => buffer;

	public ITextBuffer VisualBuffer => buffer;

	public bool IsPointInVisualBuffer(SnapshotPoint editBufferPoint, PositionAffinity affinity) => true;

	public SnapshotPoint GetNearestPointInVisualBuffer(SnapshotPoint editBufferPoint) => editBufferPoint;

	public SnapshotPoint GetNearestPointInVisualSnapshot(SnapshotPoint editBufferPoint, ITextSnapshot targetVisualSnapshot, PointTrackingMode trackingMode)
		=> editBufferPoint.TranslateTo(targetVisualSnapshot, trackingMode);
}
