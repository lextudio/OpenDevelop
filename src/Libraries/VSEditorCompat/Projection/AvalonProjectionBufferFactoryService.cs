// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IProjectionBufferFactoryService: creates AvalonProjectionBuffer/AvalonElisionBuffer instances
// (vs-editor-api.md section 32).

using System.Collections.Generic;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonProjectionBufferFactoryService : IProjectionBufferFactoryService
{
	readonly AvalonContentTypeRegistryService contentTypes;

	public AvalonProjectionBufferFactoryService(AvalonContentTypeRegistryService contentTypes)
	{
		this.contentTypes = contentTypes;
		ProjectionContentType = contentTypes.AddContentType("projection", new[] { "text" });
	}

	public IContentType ProjectionContentType { get; }

	public event System.EventHandler<TextBufferCreatedEventArgs> ProjectionBufferCreated;

	public IProjectionBuffer CreateProjectionBuffer(IProjectionEditResolver editResolver, IList<object> sourceSpans, ProjectionBufferOptions options)
		=> CreateProjectionBuffer(editResolver, sourceSpans, options, ProjectionContentType);

	public IProjectionBuffer CreateProjectionBuffer(IProjectionEditResolver editResolver, IList<object> sourceSpans, ProjectionBufferOptions options, IContentType contentType)
	{
		var buffer = new AvalonProjectionBuffer(editResolver, sourceSpans, options, contentType);
		ProjectionBufferCreated?.Invoke(this, new TextBufferCreatedEventArgs(buffer));
		return buffer;
	}

	public IElisionBuffer CreateElisionBuffer(IProjectionEditResolver editResolver, NormalizedSnapshotSpanCollection exposedSpans, ElisionBufferOptions options)
		=> CreateElisionBuffer(editResolver, exposedSpans, options, null);

	public IElisionBuffer CreateElisionBuffer(IProjectionEditResolver editResolver, NormalizedSnapshotSpanCollection exposedSpans, ElisionBufferOptions options, IContentType contentType)
	{
		if (exposedSpans.Count == 0)
			throw new System.ArgumentException("At least one exposed span is required to determine the source buffer.", nameof(exposedSpans));
		var sourceBuffer = exposedSpans[0].Snapshot.TextBuffer;
		var fullExtent = new Span(0, sourceBuffer.CurrentSnapshot.Length);
		var exposed = new NormalizedSpanCollection(System.Linq.Enumerable.Select(exposedSpans, s => s.Span));
		var elided = NormalizedSpanCollection.Difference(new NormalizedSpanCollection(fullExtent), exposed);
		var buffer = new AvalonElisionBuffer(sourceBuffer, editResolver, elided, options, contentType);
		ProjectionBufferCreated?.Invoke(this, new TextBufferCreatedEventArgs(buffer));
		return buffer;
	}
}
