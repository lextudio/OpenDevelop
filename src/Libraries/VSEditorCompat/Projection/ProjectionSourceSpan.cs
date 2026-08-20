// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// One segment of a projection buffer: a tracked range in a real source ITextBuffer. A literal
// string given to InsertSpan(int, string) is not special-cased - AvalonProjectionBuffer wraps it
// in its own private AvalonTextBuffer first (exactly the "literal text" source in vs-editor-api.md
// section 32's diagram), so every segment uniformly has a real backing buffer to map to/from.

using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

sealed class ProjectionSourceSpan
{
	public ProjectionSourceSpan(ITextBuffer buffer, ITrackingSpan trackingSpan, bool isLiteral)
	{
		Buffer = buffer;
		TrackingSpan = trackingSpan;
		IsLiteral = isLiteral;
	}

	public ITextBuffer Buffer { get; }

	public ITrackingSpan TrackingSpan { get; }

	/// <summary>True for a segment created from a literal string rather than a caller-supplied
	/// ITrackingSpan over an existing buffer (ProjectionBufferOptions.WritableLiteralSpans governs
	/// whether edits into it are allowed - this compatibility layer always allows them).</summary>
	public bool IsLiteral { get; }

	public SnapshotSpan CurrentSpan => TrackingSpan.GetSpan(Buffer.CurrentSnapshot);
}
