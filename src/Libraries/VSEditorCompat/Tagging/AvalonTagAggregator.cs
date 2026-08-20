// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITagAggregator<T> over a fixed list of ITagger<T> instances for one buffer (vs-editor-api.md
// section 25). No cross-buffer projection mapping is needed (see FlatBufferGraph.cs) - the
// aggregator just fans a query out to every tagger, using their raw offsets directly since
// source and target buffer are always the same.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Text.Tagging;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonTagAggregator<T> : ITagAggregator<T> where T : ITag
{
	readonly ITextBuffer buffer;
	readonly List<ITagger<T>> taggers;

	public AvalonTagAggregator(ITextBuffer buffer, IEnumerable<ITagger<T>> taggers)
	{
		this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
		this.taggers = taggers?.ToList() ?? throw new ArgumentNullException(nameof(taggers));
		foreach (var tagger in this.taggers)
			tagger.TagsChanged += OnTaggerTagsChanged;
	}

	public IBufferGraph BufferGraph => new FlatBufferGraph(buffer);

	public event EventHandler<TagsChangedEventArgs> TagsChanged;
	public event EventHandler<BatchedTagsChangedEventArgs> BatchedTagsChanged;

	void OnTaggerTagsChanged(object sender, SnapshotSpanEventArgs e)
	{
		var mappingSpan = BufferGraph.CreateMappingSpan(e.Span, SpanTrackingMode.EdgeInclusive);
		TagsChanged?.Invoke(this, new TagsChangedEventArgs(mappingSpan));
		BatchedTagsChanged?.Invoke(this, new BatchedTagsChangedEventArgs(new[] { mappingSpan }));
	}

	public IEnumerable<IMappingTagSpan<T>> GetTags(SnapshotSpan span)
		=> GetTags(new NormalizedSnapshotSpanCollection(span));

	public IEnumerable<IMappingTagSpan<T>> GetTags(IMappingSpan span)
		=> GetTags(span.GetSpans(buffer));

	public IEnumerable<IMappingTagSpan<T>> GetTags(NormalizedSnapshotSpanCollection spans)
	{
		foreach (var tagger in taggers) {
			foreach (var tagSpan in tagger.GetTags(spans)) {
				var mappingSpan = BufferGraph.CreateMappingSpan(tagSpan.Span, SpanTrackingMode.EdgeInclusive);
				yield return new AvalonMappingTagSpan<T>(mappingSpan, tagSpan.Tag);
			}
		}
	}

	public void Dispose()
	{
		foreach (var tagger in taggers)
			tagger.TagsChanged -= OnTaggerTagsChanged;
	}
}

sealed class AvalonMappingTagSpan<T> : IMappingTagSpan<T> where T : ITag
{
	public AvalonMappingTagSpan(IMappingSpan span, T tag)
	{
		Span = span;
		Tag = tag;
	}

	public IMappingSpan Span { get; }

	public T Tag { get; }
}
