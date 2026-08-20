// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextVersion for a projection buffer's OWN offsets (not to be confused with the source
// buffers' own versions, which AvalonTextVersion already covers). A projection snapshot's text
// is fully recomputed on every structural or source-buffer change (see AvalonProjectionBuffer),
// so - unlike AvalonTextBuffer, which gets an exact change list for free from AvalonEdit's
// ITextSourceVersion - the change between two projection versions here is reconstructed by
// diffing the old/new concatenated text (common prefix/suffix), producing exactly one
// ITextChange. That is not always the minimal change VS would report for a multi-part edit, but
// it is a correct (if occasionally coarser) description of the same net text change, which is
// all ITrackingPoint/ITrackingSpan.MoveOffsetTo-style resolution needs.

using System;

using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonProjectionVersion : ITextVersion
{
	readonly AvalonProjectionBuffer buffer;
	readonly int versionNumber;
	readonly int length;
	readonly INormalizedTextChangeCollection changes;
	AvalonProjectionVersion next;

	internal AvalonProjectionVersion(AvalonProjectionBuffer buffer, int versionNumber, int length, INormalizedTextChangeCollection changes)
	{
		this.buffer = buffer;
		this.versionNumber = versionNumber;
		this.length = length;
		this.changes = changes ?? AvalonTextChangeCollection.Empty;
	}

	internal void SetNext(AvalonProjectionVersion version) => next = version;

	public int VersionNumber => versionNumber;

	public int ReiteratedVersionNumber => versionNumber;

	public ITextVersion Next => next;

	public ITextBuffer TextBuffer => buffer;

	public int Length => length;

	public INormalizedTextChangeCollection Changes => changes;

	public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode)
		=> new AvalonProjectionTrackingPoint(this, position, trackingMode);

	public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
		=> new AvalonProjectionTrackingPoint(this, position, trackingMode);

	public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode)
		=> new AvalonProjectionTrackingSpan(this, span, trackingMode);

	public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
		=> new AvalonProjectionTrackingSpan(this, span, trackingMode);

	public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode)
		=> new AvalonProjectionTrackingSpan(this, new Span(start, length), trackingMode);

	public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
		=> new AvalonProjectionTrackingSpan(this, new Span(start, length), trackingMode);

	public ITrackingSpan CreateCustomTrackingSpan(Span span, TrackingFidelityMode trackingFidelity, object customState, CustomTrackToVersion behavior)
		=> throw new NotSupportedException("Custom tracking spans are not supported by the projection compatibility layer.");

	/// <summary>Builds the single-change diff between two projection texts (common prefix/suffix).</summary>
	internal static INormalizedTextChangeCollection Diff(string before, string after)
	{
		if (string.Equals(before, after, StringComparison.Ordinal))
			return AvalonTextChangeCollection.Empty;

		int prefix = 0;
		int maxPrefix = Math.Min(before.Length, after.Length);
		while (prefix < maxPrefix && before[prefix] == after[prefix])
			prefix++;

		int suffix = 0;
		int maxSuffix = Math.Min(before.Length, after.Length) - prefix;
		while (suffix < maxSuffix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
			suffix++;

		var oldText = before.Substring(prefix, before.Length - prefix - suffix);
		var newText = after.Substring(prefix, after.Length - prefix - suffix);
		var change = new AvalonTextChange(prefix, oldText, prefix, newText, CountLines(newText) - CountLines(oldText));
		return new AvalonTextChangeCollection(new[] { (ITextChange)change });
	}

	static int CountLines(string text)
	{
		int count = 0;
		foreach (var c in text)
			if (c == '\n')
				count++;
		return count;
	}
}
