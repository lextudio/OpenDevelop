// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITrackingPoint over a projection buffer's own offsets, walking AvalonProjectionVersion's
// single-change-per-version chain with the same before/inside/after arithmetic AvalonEdit's
// OffsetChangeMapEntry.GetNewOffset uses for a plain text buffer (see AvalonTrackingPoint's
// comment for that reference implementation) - reimplemented here because AvalonTrackingPoint is
// tied to AvalonTextVersion/AvalonEdit's real ITextSourceVersion, not this diff-based one.

using System;

using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonProjectionTrackingPoint : ITrackingPoint
{
	readonly AvalonProjectionVersion version;
	readonly int position;
	readonly PointTrackingMode trackingMode;

	internal AvalonProjectionTrackingPoint(AvalonProjectionVersion version, int position, PointTrackingMode trackingMode)
	{
		this.version = version ?? throw new ArgumentNullException(nameof(version));
		this.position = position;
		this.trackingMode = trackingMode;
	}

	public ITextBuffer TextBuffer => version.TextBuffer;

	public PointTrackingMode TrackingMode => trackingMode;

	public TrackingFidelityMode TrackingFidelity => TrackingFidelityMode.Forward;

	public int GetPosition(ITextSnapshot snapshot) => GetPosition(snapshot?.Version);

	public int GetPosition(ITextVersion targetVersion)
	{
		if (targetVersion is not AvalonProjectionVersion target || !ReferenceEquals(target.TextBuffer, version.TextBuffer))
			throw new ArgumentException("The version belongs to a different projection buffer.", nameof(targetVersion));

		int offset = position;
		var current = version;
		// Walk forward or backward along the single-linked-list chain, applying each hop's one
		// recorded change - mirrors ITextSourceVersion.GetChangesTo + GetNewOffset, just against
		// AvalonProjectionVersion's own (diffed) change record instead of AvalonEdit's real one.
		if (target.VersionNumber >= current.VersionNumber) {
			while (current != target && current.Next is AvalonProjectionVersion next) {
				offset = Apply(offset, current.Changes, trackingMode);
				current = next;
			}
		} else {
			// Backward resolution isn't needed by any caller today (VS itself rarely resolves
			// into the past); report the un-moved offset clamped to the target's length rather
			// than silently producing a wrong answer.
			offset = Math.Min(offset, target.Length);
		}
		return Math.Min(Math.Max(offset, 0), target.Length);
	}

	public SnapshotPoint GetPoint(ITextSnapshot snapshot)
		=> new SnapshotPoint(snapshot ?? throw new ArgumentNullException(nameof(snapshot)), GetPosition(snapshot));

	public char GetCharacter(ITextSnapshot snapshot)
	{
		var point = GetPoint(snapshot);
		return point.Position < snapshot.Length ? snapshot[point.Position] : '\0';
	}

	/// <summary>Same case split as AvalonEdit's OffsetChangeMapEntry.GetNewOffset (see
	/// AvalonTrackingPoint's reference-implementation comment): unaffected before the change,
	/// shifted by the net delta after it, and disambiguated by tracking mode only when the
	/// offset falls inside (or, for a pure insertion, exactly at) the changed range.</summary>
	static int Apply(int offset, INormalizedTextChangeCollection changes, PointTrackingMode trackingMode)
	{
		foreach (var change in changes) {
			if (!(change.OldLength == 0 && offset == change.OldPosition)) {
				if (offset <= change.OldPosition)
					continue;
				if (offset >= change.OldEnd) {
					offset += change.Delta;
					continue;
				}
			}
			offset = trackingMode == PointTrackingMode.Positive ? change.NewEnd : change.NewPosition;
		}
		return offset;
	}
}
