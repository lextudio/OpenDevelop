// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// VS tracking points map to AvalonEdit's ITextSourceVersion.MoveOffsetTo, NOT to a live
// TextAnchor: a VS tracking point must be resolvable against arbitrary old/new snapshots from
// background threads, while TextAnchor is live-document/UI-thread-affine (vs-editor-api.md
// section 14). The fidelity mode is carried for API parity; the spike resolves every fidelity
// the same way, which is exact for the single-forward-edit cases the tests exercise.

using System;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>A version-based tracking point over an AvalonEdit document.</summary>
public sealed class AvalonTrackingPoint : ITrackingPoint
{
	readonly AvalonTextVersion version;
	readonly int position;
	readonly PointTrackingMode trackingMode;
	readonly TrackingFidelityMode trackingFidelity;

	internal AvalonTrackingPoint(AvalonTextVersion version, int position,
		PointTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
	{
		this.version = version ?? throw new ArgumentNullException(nameof(version));
		this.position = position;
		this.trackingMode = trackingMode;
		this.trackingFidelity = trackingFidelity;
	}

	public ITextBuffer TextBuffer => version.TextBuffer;

	public PointTrackingMode TrackingMode => trackingMode;

	public TrackingFidelityMode TrackingFidelity => trackingFidelity;

	public int GetPosition(ITextSnapshot snapshot)
		=> MoveTo(snapshot?.Version, nameof(snapshot));

	public int GetPosition(ITextVersion targetVersion)
		=> MoveTo(targetVersion, nameof(targetVersion));

	public SnapshotPoint GetPoint(ITextSnapshot snapshot)
		=> new SnapshotPoint(snapshot ?? throw new ArgumentNullException(nameof(snapshot)), GetPosition(snapshot));

	public char GetCharacter(ITextSnapshot snapshot)
	{
		var point = GetPoint(snapshot);
		return point.Position < snapshot.Length ? snapshot[point.Position] : '\0';
	}

	int MoveTo(ITextVersion target, string paramName)
	{
		if (target is not AvalonTextVersion targetVersion)
			throw new ArgumentException("The version belongs to a different implementation.", paramName);
		if (!ReferenceEquals(targetVersion.TextBuffer, version.TextBuffer))
			throw new ArgumentException("The version belongs to a different text buffer.", paramName);
		var source = version.SourceVersion;
		var destination = targetVersion.SourceVersion;
		if (source == null)
			return Math.Min(position, target.Length);
		if (destination == null)
			return position;
		return source.MoveOffsetTo(destination, position, ToAnchorMovement(trackingMode));
	}

	internal static AnchorMovementType ToAnchorMovement(PointTrackingMode trackingMode)
		=> trackingMode == PointTrackingMode.Positive
			? AnchorMovementType.AfterInsertion
			: AnchorMovementType.BeforeInsertion;
}
