// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IElisionBuffer: a projection over exactly ONE source buffer whose segments are "the parts NOT
// currently elided" (vs-editor-api.md section 32). Implemented by composition - an
// AvalonElisionBuffer owns a private AvalonProjectionBuffer whose span list is always exactly
// "source buffer's full extent minus the elided ranges", recomputed on ElideSpans/ExpandSpans/
// ModifySpans. FillInMappingMode (ElisionBufferOptions) - which side of a boundary new text
// typed exactly at an elision edge joins - is not implemented; new text always lands in the
// visible segment adjacent to where it was typed, which is the common case (most editors expand
// on edit near a fold, then re-elide) but not the full VS contract.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonElisionBuffer : IElisionBuffer
{
	readonly AvalonProjectionBuffer inner;
	// Tracked (not raw-offset) so an edit to the source buffer moves the elided boundaries with
	// it, the same way AvalonProjectionBuffer's own segments track their source spans - a plain
	// NormalizedSpanCollection of integer offsets would go stale the moment the source buffer's
	// text shifted underneath it.
	List<ITrackingSpan> elidedTrackingSpans = new();

	public AvalonElisionBuffer(ITextBuffer sourceBuffer, IProjectionEditResolver editResolver, NormalizedSpanCollection spansToElide, ElisionBufferOptions options, IContentType contentType)
	{
		SourceBuffer = sourceBuffer ?? throw new ArgumentNullException(nameof(sourceBuffer));
		Options = options;
		elidedTrackingSpans = ToTrackingSpans(spansToElide ?? NormalizedSpanCollection.Empty);
		inner = new AvalonProjectionBuffer(editResolver, BuildVisibleSpans(), ProjectionBufferOptions.None, contentType ?? sourceBuffer.ContentType);
		sourceBuffer.Changed += (_, __) => RebuildFromElidedSpans();
	}

	List<ITrackingSpan> ToTrackingSpans(NormalizedSpanCollection spans)
		=> spans.Select(span => SourceBuffer.CurrentSnapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeExclusive)).ToList();

	NormalizedSpanCollection CurrentElidedSpans()
		=> new(elidedTrackingSpans.Select(t => t.GetSpan(SourceBuffer.CurrentSnapshot).Span).ToList());

	public ITextBuffer SourceBuffer { get; }

	public ElisionBufferOptions Options { get; }

	public IElisionSnapshot CurrentSnapshot => new AvalonElisionSnapshot(this, (AvalonProjectionSnapshot)inner.CurrentSnapshot, SourceBuffer.CurrentSnapshot);

	IProjectionSnapshot IProjectionBufferBase.CurrentSnapshot => CurrentSnapshot;

	ITextSnapshot ITextBuffer.CurrentSnapshot => CurrentSnapshot;

	public IList<ITextBuffer> SourceBuffers => new List<ITextBuffer> { SourceBuffer };

	public IContentType ContentType => inner.ContentType;

	public bool EditInProgress => inner.EditInProgress;

	public PropertyCollection Properties => inner.Properties;

	public event EventHandler<TextContentChangingEventArgs> Changing { add => inner.Changing += value; remove => inner.Changing -= value; }
	public event EventHandler<TextContentChangedEventArgs> ChangedHighPriority { add => inner.ChangedHighPriority += value; remove => inner.ChangedHighPriority -= value; }
	public event EventHandler<TextContentChangedEventArgs> Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
	public event EventHandler<TextContentChangedEventArgs> ChangedLowPriority { add => inner.ChangedLowPriority += value; remove => inner.ChangedLowPriority -= value; }
	public event EventHandler PostChanged { add => inner.PostChanged += value; remove => inner.PostChanged -= value; }
	public event EventHandler<ContentTypeChangedEventArgs> ContentTypeChanged { add => inner.ContentTypeChanged += value; remove => inner.ContentTypeChanged -= value; }
	public event EventHandler<SnapshotSpanEventArgs> ReadOnlyRegionsChanged { add => inner.ReadOnlyRegionsChanged += value; remove => inner.ReadOnlyRegionsChanged -= value; }

	public event EventHandler<ElisionSourceSpansChangedEventArgs> SourceSpansChanged;

	IList<object> BuildVisibleSpans()
	{
		var full = new Span(0, SourceBuffer.CurrentSnapshot.Length);
		var visible = NormalizedSpanCollection.Difference(new NormalizedSpanCollection(full), CurrentElidedSpans());
		return visible.Select(span => (object)SourceBuffer.CurrentSnapshot.CreateTrackingSpan(span, SpanTrackingMode.EdgeExclusive)).ToList();
	}

	void RebuildFromElidedSpans()
	{
		var current = inner.CurrentSnapshot.SpanCount;
		if (current > 0)
			inner.DeleteSpans(0, current);
		var visible = BuildVisibleSpans();
		if (visible.Count > 0)
			inner.InsertSpans(0, visible);
	}

	public IProjectionSnapshot ElideSpans(NormalizedSpanCollection spansToElide) => ModifySpans(spansToElide, NormalizedSpanCollection.Empty);

	public IProjectionSnapshot ExpandSpans(NormalizedSpanCollection spansToExpand) => ModifySpans(NormalizedSpanCollection.Empty, spansToExpand);

	public IProjectionSnapshot ModifySpans(NormalizedSpanCollection spansToElide, NormalizedSpanCollection spansToExpand)
	{
		var before = CurrentSnapshot;
		var merged = NormalizedSpanCollection.Difference(NormalizedSpanCollection.Union(CurrentElidedSpans(), spansToElide), spansToExpand);
		elidedTrackingSpans = ToTrackingSpans(merged);
		RebuildFromElidedSpans();
		var after = CurrentSnapshot;
		SourceSpansChanged?.Invoke(this, new ElisionSourceSpansChangedEventArgs(before, after, spansToElide, spansToExpand, null));
		return after;
	}

	public ITextEdit CreateEdit() => inner.CreateEdit();

	public ITextEdit CreateEdit(EditOptions options, int? reiteratedVersionNumber, object editTag) => inner.CreateEdit(options, reiteratedVersionNumber, editTag);

	ITextSnapshot ITextBuffer.Insert(int position, string text) => ((ITextBuffer)inner).Insert(position, text);
	ITextSnapshot ITextBuffer.Delete(Span deleteSpan) => ((ITextBuffer)inner).Delete(deleteSpan);
	ITextSnapshot ITextBuffer.Replace(Span replaceSpan, string replaceWith) => ((ITextBuffer)inner).Replace(replaceSpan, replaceWith);
	IProjectionSnapshot IProjectionBufferBase.Insert(int position, string text) => (IProjectionSnapshot)((ITextBuffer)this).Insert(position, text);
	IProjectionSnapshot IProjectionBufferBase.Delete(Span deleteSpan) => (IProjectionSnapshot)((ITextBuffer)this).Delete(deleteSpan);
	IProjectionSnapshot IProjectionBufferBase.Replace(Span replaceSpan, string replaceWith) => (IProjectionSnapshot)((ITextBuffer)this).Replace(replaceSpan, replaceWith);

	public IReadOnlyRegionEdit CreateReadOnlyRegionEdit() => inner.CreateReadOnlyRegionEdit();
	public bool IsReadOnly(int position) => inner.IsReadOnly(position);
	public bool IsReadOnly(int position, bool isEdit) => inner.IsReadOnly(position, isEdit);
	public bool IsReadOnly(Span span) => inner.IsReadOnly(span);
	public bool IsReadOnly(Span span, bool isEdit) => inner.IsReadOnly(span, isEdit);
	public NormalizedSpanCollection GetReadOnlyExtents(Span span) => inner.GetReadOnlyExtents(span);
	public void ChangeContentType(IContentType newContentType, object editTag) => inner.ChangeContentType(newContentType, editTag);
	public bool CheckEditAccess() => inner.CheckEditAccess();
	public void TakeThreadOwnership() => inner.TakeThreadOwnership();
}
