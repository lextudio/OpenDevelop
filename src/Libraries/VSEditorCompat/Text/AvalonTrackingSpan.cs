// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// A tracking span is modeled as two version-based tracking points whose insertion affinity is
// derived from the SpanTrackingMode, matching the VS edge semantics (vs-editor-api.md section
// 15):
//
//   EdgeInclusive  start = negative, end = positive (insertion at either edge stays inside)
//   EdgeExclusive  start = positive, end = negative (insertion at either edge stays outside)
//   EdgePositive   start = positive, end = positive (start edge excludes, end edge includes)
//   EdgeNegative   start = negative, end = negative (start edge includes, end edge excludes)

using System;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>A version-based tracking span over an AvalonEdit document.</summary>
public sealed class AvalonTrackingSpan : ITrackingSpan
{
	readonly AvalonTextVersion version;
	readonly Span span;
	readonly SpanTrackingMode trackingMode;
	readonly TrackingFidelityMode trackingFidelity;

	internal AvalonTrackingSpan(AvalonTextVersion version, Span span,
		SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
	{
		this.version = version ?? throw new ArgumentNullException(nameof(version));
		this.span = span;
		this.trackingMode = trackingMode;
		this.trackingFidelity = trackingFidelity;
	}

	public ITextBuffer TextBuffer => version.TextBuffer;

	public TrackingFidelityMode TrackingFidelity => trackingFidelity;

	public SpanTrackingMode TrackingMode => trackingMode;

	public SnapshotPoint GetStartPoint(ITextSnapshot snapshot)
		=> new SnapshotPoint(snapshot ?? throw new ArgumentNullException(nameof(snapshot)), GetSpan(snapshot).Start);

	public SnapshotPoint GetEndPoint(ITextSnapshot snapshot)
		=> new SnapshotPoint(snapshot ?? throw new ArgumentNullException(nameof(snapshot)), GetSpan(snapshot).End);

	public Span GetSpan(ITextVersion targetVersion)
	{
		var start = GetBoundaryPoint(span.Start, trackingMode, isStart: true).GetPosition(targetVersion);
		var end = GetBoundaryPoint(span.End, trackingMode, isStart: false).GetPosition(targetVersion);
		return new Span(start, Math.Max(0, end - start));
	}

	public SnapshotSpan GetSpan(ITextSnapshot snapshot)
		=> new SnapshotSpan(snapshot ?? throw new ArgumentNullException(nameof(snapshot)), GetSpan((ITextVersion)snapshot.Version));

	public string GetText(ITextSnapshot snapshot)
		=> GetSpan(snapshot).GetText();

	public string GetText(ITextVersion version)
	{
		if (version is not AvalonTextVersion avalonVersion)
			throw new ArgumentException("The version belongs to a different implementation.", nameof(version));
		// Text for a historical version is only recoverable while that version's snapshot is
		// still alive; the spike resolves the current version, which is what the tests use.
		var buffer = avalonVersion.TextBuffer as AvalonTextBuffer;
		if (buffer != null && ReferenceEquals(buffer.CurrentSnapshot.Version, avalonVersion))
			return buffer.CurrentSnapshot.GetText(GetSpan(version));
		throw new NotSupportedException("Resolving a tracking span's text against a historical version is not supported by the spike.");
	}

	AvalonTrackingPoint GetBoundaryPoint(int position, SpanTrackingMode trackingMode, bool isStart)
	{
		var (startMode, endMode) = ToPointTrackingModes(trackingMode);
		return new AvalonTrackingPoint(version, position,
			isStart ? startMode : endMode, trackingFidelity);
	}

	internal static (PointTrackingMode start, PointTrackingMode end) ToPointTrackingModes(SpanTrackingMode trackingMode)
		=> trackingMode switch {
			SpanTrackingMode.EdgeExclusive => (PointTrackingMode.Positive, PointTrackingMode.Negative),
			SpanTrackingMode.EdgeInclusive => (PointTrackingMode.Negative, PointTrackingMode.Positive),
			SpanTrackingMode.EdgePositive => (PointTrackingMode.Positive, PointTrackingMode.Positive),
			SpanTrackingMode.EdgeNegative => (PointTrackingMode.Negative, PointTrackingMode.Negative),
			_ => (PointTrackingMode.Negative, PointTrackingMode.Positive),
		};
}
