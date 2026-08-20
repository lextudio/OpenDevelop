// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITrackingSpan over a projection buffer's own offsets - same edge-mode-to-point-tracking-mode
// mapping as AvalonTrackingSpan, built on AvalonProjectionTrackingPoint instead.

using System;

using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonProjectionTrackingSpan : ITrackingSpan
{
	readonly AvalonProjectionVersion version;
	readonly Span span;
	readonly SpanTrackingMode trackingMode;

	internal AvalonProjectionTrackingSpan(AvalonProjectionVersion version, Span span, SpanTrackingMode trackingMode)
	{
		this.version = version ?? throw new ArgumentNullException(nameof(version));
		this.span = span;
		this.trackingMode = trackingMode;
	}

	public ITextBuffer TextBuffer => version.TextBuffer;

	public TrackingFidelityMode TrackingFidelity => TrackingFidelityMode.Forward;

	public SpanTrackingMode TrackingMode => trackingMode;

	public SnapshotPoint GetStartPoint(ITextSnapshot snapshot)
		=> new SnapshotPoint(snapshot ?? throw new ArgumentNullException(nameof(snapshot)), GetSpan(snapshot).Start);

	public SnapshotPoint GetEndPoint(ITextSnapshot snapshot)
		=> new SnapshotPoint(snapshot ?? throw new ArgumentNullException(nameof(snapshot)), GetSpan(snapshot).End);

	public Span GetSpan(ITextVersion targetVersion)
	{
		var (startMode, endMode) = AvalonTrackingSpan.ToPointTrackingModes(trackingMode);
		int start = new AvalonProjectionTrackingPoint(version, span.Start, startMode).GetPosition(targetVersion);
		int end = new AvalonProjectionTrackingPoint(version, span.End, endMode).GetPosition(targetVersion);
		return new Span(start, Math.Max(0, end - start));
	}

	public SnapshotSpan GetSpan(ITextSnapshot snapshot)
		=> new SnapshotSpan(snapshot ?? throw new ArgumentNullException(nameof(snapshot)), GetSpan((ITextVersion)snapshot.Version));

	public string GetText(ITextSnapshot snapshot) => GetSpan(snapshot).GetText();

	public string GetText(ITextVersion version)
	{
		if (version is not AvalonProjectionVersion projectionVersion || !ReferenceEquals(projectionVersion.TextBuffer, this.version.TextBuffer))
			throw new ArgumentException("The version belongs to a different projection buffer.", nameof(version));
		var buffer = (AvalonProjectionBuffer)projectionVersion.TextBuffer;
		if (!ReferenceEquals(buffer.CurrentSnapshot.Version, projectionVersion))
			throw new NotSupportedException("Resolving a tracking span's text against a historical projection version is not supported.");
		return buffer.CurrentSnapshot.GetText(GetSpan(version));
	}
}
