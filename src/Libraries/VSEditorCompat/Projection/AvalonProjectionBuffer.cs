// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IProjectionBuffer: a buffer whose text is the concatenation of tracked spans over other
// buffers (vs-editor-api.md section 32's "generated display document" over several source
// buffers). A literal string handed to InsertSpan/InsertSpans is wrapped in its own private,
// inert-content-typed AvalonTextBuffer first - the diagram's "literal text" source becomes a
// real (if hidden) buffer, so every segment has one uniform shape to map to/from
// (ProjectionSourceSpan).
//
// Editing restriction: Insert/Delete/Replace (both the ITextBuffer and IProjectionBufferBase
// overloads - they are the SAME operation, exposed twice with different return types, hence the
// explicit interface implementations below) only support an edit that falls entirely within ONE
// segment. A real edit resolver (IProjectionEditResolver.FillInInsertionSizes/
// FillInReplacementSizes) is what VS uses to disambiguate an edit straddling a segment boundary
// or landing exactly on one; this compatibility layer does not implement that resolver protocol,
// so such an edit throws NotSupportedException instead of guessing. Editing purely at the
// projection's own span LIST (which segments are included, and in what order) via
// InsertSpan/InsertSpans/DeleteSpans/ReplaceSpans is fully supported and unrestricted.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonProjectionBuffer : IProjectionBuffer
{
	readonly List<ProjectionSourceSpan> segments = new();
	readonly HashSet<ITextBuffer> subscribedBuffers = new();
	readonly IProjectionEditResolver editResolver;
	readonly ProjectionBufferOptions options;
	IContentType contentType;
	int versionNumber;
	AvalonProjectionSnapshot currentSnapshot;
	bool editInProgress;

	public AvalonProjectionBuffer(IProjectionEditResolver editResolver, IList<object> sourceSpans, ProjectionBufferOptions options, IContentType contentType)
	{
		this.editResolver = editResolver;
		this.options = options;
		this.contentType = contentType ?? AvalonContentTypeRegistry.Text;
		foreach (var spec in sourceSpans ?? Array.Empty<object>())
			segments.Add(ToSegment(spec));
		foreach (var segment in segments)
			Subscribe(segment.Buffer);
		Recompute(fireEvents: false, insertedSpanPosition: -1, insertedSpans: null, deletedSpans: null, addedBuffers: null, removedBuffers: null);
	}

	static ProjectionSourceSpan ToSegment(object spec)
	{
		switch (spec) {
		case ITrackingSpan trackingSpan:
			return new ProjectionSourceSpan(trackingSpan.TextBuffer, trackingSpan, isLiteral: false);
		case string literal:
			var literalBuffer = new AvalonTextBufferFactoryService(AvalonContentTypeRegistry.Instance).CreateTextBuffer(literal, AvalonContentTypeRegistry.Inert);
			var span = literalBuffer.CurrentSnapshot.CreateTrackingSpan(new Span(0, literal.Length), SpanTrackingMode.EdgeExclusive);
			return new ProjectionSourceSpan(literalBuffer, span, isLiteral: true);
		default:
			throw new ArgumentException($"Unsupported projection span type: {spec?.GetType()}", nameof(spec));
		}
	}

	void Subscribe(ITextBuffer sourceBuffer)
	{
		if (subscribedBuffers.Add(sourceBuffer))
			sourceBuffer.Changed += OnSourceBufferChanged;
	}

	void OnSourceBufferChanged(object sender, TextContentChangedEventArgs e)
		=> Recompute(fireEvents: true, insertedSpanPosition: -1, insertedSpans: null, deletedSpans: null, addedBuffers: null, removedBuffers: null);

	void Recompute(bool fireEvents, int insertedSpanPosition, IList<ITrackingSpan> insertedSpans, IList<ITrackingSpan> deletedSpans,
		IList<ITextBuffer> addedBuffers, IList<ITextBuffer> removedBuffers)
	{
		var before = currentSnapshot;
		var beforeText = before?.GetText() ?? string.Empty;

		var snapshot = new AvalonProjectionSnapshot(this, segments.ToArray());
		var afterVersion = new AvalonProjectionVersion(this, ++versionNumber, snapshot.GetText().Length,
			AvalonProjectionVersion.Diff(beforeText, snapshot.GetText()));
		snapshot.SetVersion(afterVersion);
		((AvalonProjectionVersion)before?.Version)?.SetNext(afterVersion);
		currentSnapshot = snapshot;

		if (!fireEvents || before == null)
			return;

		var contentChanged = new TextContentChangedEventArgs(before, snapshot, EditOptions.None, editTag: null);
		ChangedHighPriority?.Invoke(this, contentChanged);
		Changed?.Invoke(this, contentChanged);
		ChangedLowPriority?.Invoke(this, contentChanged);
		PostChanged?.Invoke(this, EventArgs.Empty);

		if (insertedSpans != null || deletedSpans != null) {
			SourceSpansChanged?.Invoke(this, new ProjectionSourceSpansChangedEventArgs(
				before, snapshot, deletedSpans ?? Array.Empty<ITrackingSpan>(), insertedSpans ?? Array.Empty<ITrackingSpan>(),
				insertedSpanPosition, EditOptions.None, editTag: null));
		}
		if (addedBuffers != null || removedBuffers != null) {
			SourceBuffersChanged?.Invoke(this, new ProjectionSourceBuffersChangedEventArgs(
				before, snapshot, deletedSpans ?? Array.Empty<ITrackingSpan>(), insertedSpans ?? Array.Empty<ITrackingSpan>(),
				insertedSpanPosition, addedBuffers ?? Array.Empty<ITextBuffer>(), removedBuffers ?? Array.Empty<ITextBuffer>(),
				EditOptions.None, editTag: null));
		}
	}

	#region IProjectionBufferBase / IProjectionBuffer

	public IProjectionSnapshot CurrentSnapshot => currentSnapshot;

	public IList<ITextBuffer> SourceBuffers => segments.Select(s => s.Buffer).Distinct().ToList();

	public event EventHandler<ProjectionSourceSpansChangedEventArgs> SourceSpansChanged;
	public event EventHandler<ProjectionSourceBuffersChangedEventArgs> SourceBuffersChanged;

	public IProjectionSnapshot InsertSpan(int spanPosition, ITrackingSpan span) => InsertSpans(spanPosition, new object[] { span });

	public IProjectionSnapshot InsertSpan(int spanPosition, string text) => InsertSpans(spanPosition, new object[] { text });

	public IProjectionSnapshot InsertSpans(int spanPosition, IList<object> spansToInsert)
	{
		var newSegments = spansToInsert.Select(ToSegment).ToList();
		segments.InsertRange(spanPosition, newSegments);
		var addedBuffers = newSegments.Select(s => s.Buffer).Where(b => !subscribedBuffers.Contains(b)).Distinct().ToList();
		foreach (var s in newSegments)
			Subscribe(s.Buffer);
		Recompute(fireEvents: true, spanPosition, newSegments.Select(s => s.TrackingSpan).ToList(), null,
			addedBuffers.Count > 0 ? addedBuffers : null, null);
		return currentSnapshot;
	}

	public IProjectionSnapshot DeleteSpans(int startSpanIndex, int spanCount)
	{
		var removed = segments.GetRange(startSpanIndex, spanCount);
		segments.RemoveRange(startSpanIndex, spanCount);
		var stillUsed = segments.Select(s => s.Buffer).ToHashSet();
		var removedBuffers = removed.Select(s => s.Buffer).Distinct().Where(b => !stillUsed.Contains(b)).ToList();
		Recompute(fireEvents: true, startSpanIndex, null, removed.Select(s => s.TrackingSpan).ToList(),
			null, removedBuffers.Count > 0 ? removedBuffers : null);
		return currentSnapshot;
	}

	public IProjectionSnapshot ReplaceSpans(int startSpanIndex, int spanCount, IList<object> spansToInsert, EditOptions options, object editTag)
	{
		var removed = segments.GetRange(startSpanIndex, spanCount);
		var newSegments = spansToInsert.Select(ToSegment).ToList();
		segments.RemoveRange(startSpanIndex, spanCount);
		segments.InsertRange(startSpanIndex, newSegments);
		foreach (var s in newSegments)
			Subscribe(s.Buffer);
		var stillUsed = segments.Select(s => s.Buffer).ToHashSet();
		var removedBuffers = removed.Select(s => s.Buffer).Distinct().Where(b => !stillUsed.Contains(b)).ToList();
		var addedBuffers = newSegments.Select(s => s.Buffer).Distinct().Where(b => !removed.Any(r => r.Buffer == b)).ToList();
		Recompute(fireEvents: true, startSpanIndex, newSegments.Select(s => s.TrackingSpan).ToList(), removed.Select(s => s.TrackingSpan).ToList(),
			addedBuffers.Count > 0 ? addedBuffers : null, removedBuffers.Count > 0 ? removedBuffers : null);
		return currentSnapshot;
	}

	#endregion

	#region ITextBuffer

	public PropertyCollection Properties { get; } = new();

	ITextSnapshot ITextBuffer.CurrentSnapshot => currentSnapshot;

	public IContentType ContentType => contentType;

	public bool EditInProgress => editInProgress;

	public event EventHandler<TextContentChangingEventArgs> Changing;
	public event EventHandler<TextContentChangedEventArgs> ChangedHighPriority;
	public event EventHandler<TextContentChangedEventArgs> Changed;
	public event EventHandler<TextContentChangedEventArgs> ChangedLowPriority;
	public event EventHandler PostChanged;
	public event EventHandler<ContentTypeChangedEventArgs> ContentTypeChanged;
	public event EventHandler<SnapshotSpanEventArgs> ReadOnlyRegionsChanged;

	public ITextEdit CreateEdit() => new AvalonProjectionTextEdit(this);

	public ITextEdit CreateEdit(EditOptions options, int? reiteratedVersionNumber, object editTag) => new AvalonProjectionTextEdit(this);

	ITextSnapshot ITextBuffer.Insert(int position, string text)
	{
		using var edit = CreateEdit();
		edit.Insert(position, text);
		return edit.Apply();
	}

	ITextSnapshot ITextBuffer.Delete(Span deleteSpan)
	{
		using var edit = CreateEdit();
		edit.Delete(deleteSpan);
		return edit.Apply();
	}

	ITextSnapshot ITextBuffer.Replace(Span replaceSpan, string replaceWith)
	{
		using var edit = CreateEdit();
		edit.Replace(replaceSpan, replaceWith);
		return edit.Apply();
	}

	IProjectionSnapshot IProjectionBufferBase.Insert(int position, string text) => (IProjectionSnapshot)((ITextBuffer)this).Insert(position, text);

	IProjectionSnapshot IProjectionBufferBase.Delete(Span deleteSpan) => (IProjectionSnapshot)((ITextBuffer)this).Delete(deleteSpan);

	IProjectionSnapshot IProjectionBufferBase.Replace(Span replaceSpan, string replaceWith) => (IProjectionSnapshot)((ITextBuffer)this).Replace(replaceSpan, replaceWith);

	public IReadOnlyRegionEdit CreateReadOnlyRegionEdit() => throw new NotSupportedException("Read-only regions are not implemented for projection buffers.");

	public bool IsReadOnly(int position) => false;
	public bool IsReadOnly(int position, bool isEdit) => false;
	public bool IsReadOnly(Span span) => false;
	public bool IsReadOnly(Span span, bool isEdit) => false;
	public NormalizedSpanCollection GetReadOnlyExtents(Span span) => NormalizedSpanCollection.Empty;

	public void ChangeContentType(IContentType newContentType, object editTag)
	{
		if (newContentType == null)
			throw new ArgumentNullException(nameof(newContentType));
		var before = contentType;
		contentType = newContentType;
		ContentTypeChanged?.Invoke(this, new ContentTypeChangedEventArgs(currentSnapshot, currentSnapshot, before, newContentType, editTag));
	}

	public bool CheckEditAccess() => true;

	public void TakeThreadOwnership()
	{
	}

	#endregion

	/// <summary>Resolves the single segment an edit at <paramref name="position"/>..<paramref
	/// name="position"/>+<paramref name="length"/> falls entirely within, or throws (see class
	/// comment) if it straddles a boundary.</summary>
	internal (ProjectionSourceSpan segment, int offsetInSegment) ResolveEditTarget(int position, int length)
	{
		var snapshot = currentSnapshot;
		for (int i = 0; i < snapshot.Segments.Count; i++) {
			var segment = snapshot.Segments[i];
			var segmentStart = TextOffsetOfSegment(snapshot, i);
			var segmentLength = segment.CurrentSpan.Length;
			if (position >= segmentStart && position + length <= segmentStart + segmentLength)
				return (segment, position - segmentStart);
		}
		throw new NotSupportedException(
			"Editing a projection buffer across a segment boundary needs an IProjectionEditResolver, which this compatibility layer does not implement - edit the source buffer(s) directly, or InsertSpan/ReplaceSpans instead.");
	}

	static int TextOffsetOfSegment(AvalonProjectionSnapshot snapshot, int index)
	{
		int offset = 0;
		for (int i = 0; i < index; i++)
			offset += snapshot.Segments[i].CurrentSpan.Length;
		return offset;
	}

	internal void BeginEdit() => editInProgress = true;

	internal void EndEdit() => editInProgress = false;

	internal void RaiseChanging(ITextSnapshot before) => Changing?.Invoke(this, new TextContentChangingEventArgs(before, editTag: null, _ => { }));

	internal void RecomputeAfterEdit() => Recompute(fireEvents: true, -1, null, null, null, null);
}
