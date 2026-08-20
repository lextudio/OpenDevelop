// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Conservative read-only region support for the spike (vs-editor-api.md section 47): the
// regions are recorded but the buffer's IsReadOnly/GetReadOnlyExtents always answer "nothing
// is read-only" unless OpenDevelop later marks regions explicitly.

using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>A read-only-region transaction; the spike records regions but never enforces them.</summary>
public sealed class AvalonReadOnlyRegionEdit : IReadOnlyRegionEdit
{
	readonly AvalonTextBuffer buffer;
	readonly ITextSnapshot snapshot;
	readonly List<IReadOnlyRegion> regions = new();
	bool applied;
	bool canceled;

	internal AvalonReadOnlyRegionEdit(AvalonTextBuffer buffer)
	{
		this.buffer = buffer;
		snapshot = buffer.CurrentSnapshot;
	}

	public ITextSnapshot Snapshot => snapshot;

	public bool Canceled => canceled;

	public IReadOnlyRegion CreateReadOnlyRegion(Span span)
		=> CreateReadOnlyRegion(span, SpanTrackingMode.EdgeInclusive, EdgeInsertionMode.Allow);

	public IReadOnlyRegion CreateReadOnlyRegion(Span span, SpanTrackingMode trackingMode, EdgeInsertionMode edgeInsertionMode)
	{
		var region = new AvalonReadOnlyRegion(snapshot.CreateTrackingSpan(span, trackingMode), edgeInsertionMode, null);
		regions.Add(region);
		return region;
	}

	public IReadOnlyRegion CreateDynamicReadOnlyRegion(Span span, SpanTrackingMode trackingMode, EdgeInsertionMode edgeInsertionMode, DynamicReadOnlyRegionQuery callback)
	{
		var region = new AvalonReadOnlyRegion(snapshot.CreateTrackingSpan(span, trackingMode), edgeInsertionMode, callback);
		regions.Add(region);
		return region;
	}

	public void RemoveReadOnlyRegion(IReadOnlyRegion readOnlyRegion) => regions.Remove(readOnlyRegion);

	public ITextSnapshot Apply()
	{
		if (applied)
			throw new InvalidOperationException("The read-only-region edit has already been applied.");
		if (canceled)
			throw new InvalidOperationException("The read-only-region edit has been canceled.");
		applied = true;
		return buffer.CurrentSnapshot;
	}

	public void Cancel()
	{
		if (applied)
			throw new InvalidOperationException("The read-only-region edit has already been applied.");
		canceled = true;
	}

	public void Dispose()
	{
		if (!applied && !canceled)
			throw new InvalidOperationException("The read-only-region edit must be applied or canceled before it is disposed.");
	}

	sealed class AvalonReadOnlyRegion : IReadOnlyRegion
	{
		public AvalonReadOnlyRegion(ITrackingSpan span, EdgeInsertionMode edgeInsertionMode, DynamicReadOnlyRegionQuery queryCallback)
		{
			Span = span;
			EdgeInsertionMode = edgeInsertionMode;
			QueryCallback = queryCallback;
		}

		public EdgeInsertionMode EdgeInsertionMode { get; }

		public DynamicReadOnlyRegionQuery QueryCallback { get; }

		public ITrackingSpan Span { get; }
	}
}
