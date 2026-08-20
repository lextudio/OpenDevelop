// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// One VS ITextVersion over one AvalonEdit ITextSourceVersion. AvalonEdit's version object is
// the strongest reason this adapter is feasible (vs-editor-api.md section 10): it can compare
// ages, enumerate the changes between two versions, and move an offset across versions - which
// is exactly the VS tracking machinery.

using System;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>A VS editor version backed by an AvalonEdit document version.</summary>
public sealed class AvalonTextVersion : ITextVersion
{
	readonly AvalonTextBuffer buffer;
	readonly AvalonTextVersion previous;
	readonly int versionNumber;
	readonly int length;
	readonly INormalizedTextChangeCollection changes;
	AvalonTextVersion next;

	internal AvalonTextVersion(AvalonTextBuffer buffer, AvalonTextVersion previous,
		int versionNumber, int length, INormalizedTextChangeCollection changes)
	{
		this.buffer = buffer;
		this.previous = previous;
		this.versionNumber = versionNumber;
		this.length = length;
		this.changes = changes ?? AvalonTextChangeCollection.Empty;
	}

	/// <summary>The AvalonEdit version this wraps; null for the initial version of an empty buffer.</summary>
	internal ITextSourceVersion SourceVersion { get; set; }

	/// <summary>Links this version as the successor of <paramref name="version"/> (which is this version's predecessor).</summary>
	internal void SetNext(AvalonTextVersion version) => next = version;

	internal AvalonTextVersion Previous => previous;

	public int VersionNumber => versionNumber;

	/// <summary>The spike does not reiterate edits, so this always equals <see cref="VersionNumber"/>.</summary>
	public int ReiteratedVersionNumber => versionNumber;

	public ITextVersion Next => next;

	public ITextBuffer TextBuffer => buffer;

	public int Length => length;

	public INormalizedTextChangeCollection Changes => changes;

	public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode)
		=> new AvalonTrackingPoint(this, position, trackingMode, TrackingFidelityMode.Forward);

	public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
		=> new AvalonTrackingPoint(this, position, trackingMode, trackingFidelity);

	public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode)
		=> new AvalonTrackingSpan(this, span, trackingMode, TrackingFidelityMode.Forward);

	public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
		=> new AvalonTrackingSpan(this, span, trackingMode, trackingFidelity);

	public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode)
		=> new AvalonTrackingSpan(this, new Span(start, length), trackingMode, TrackingFidelityMode.Forward);

	public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
		=> new AvalonTrackingSpan(this, new Span(start, length), trackingMode, trackingFidelity);

	public ITrackingSpan CreateCustomTrackingSpan(Span span, TrackingFidelityMode trackingFidelity, object customState, CustomTrackToVersion behavior)
		=> throw new NotSupportedException("Custom tracking spans are not supported by the spike.");
}
