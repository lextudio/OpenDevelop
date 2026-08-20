// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// The official ITextBufferFactoryService over AvalonEdit: creates standalone text buffers
// (no UI, no view) that own their own TextDocument - the scratch/test buffers language services
// and compatibility tests need (vs-editor-api.md section 30).

using System;
using System.IO;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>Creates VS ITextBuffer instances backed by fresh AvalonEdit documents.</summary>
public sealed class AvalonTextBufferFactoryService : ITextBufferFactoryService
{
	readonly IContentTypeRegistryService contentTypeRegistry;

	public AvalonTextBufferFactoryService(IContentTypeRegistryService contentTypeRegistry)
	{
		this.contentTypeRegistry = contentTypeRegistry ?? throw new ArgumentNullException(nameof(contentTypeRegistry));
	}

	public IContentType TextContentType => contentTypeRegistry.GetContentType("text");

	public IContentType PlaintextContentType => contentTypeRegistry.GetContentType("plaintext");

	public IContentType InertContentType => contentTypeRegistry.GetContentType("inert");

	public event EventHandler<TextBufferCreatedEventArgs> TextBufferCreated;

	public ITextBuffer CreateTextBuffer()
		=> CreateTextBuffer(TextContentType ?? throw new InvalidOperationException("The 'text' content type is not registered."));

	public ITextBuffer CreateTextBuffer(IContentType contentType)
		=> CreateTextBuffer(string.Empty, contentType);

	public ITextBuffer CreateTextBuffer(string text, IContentType contentType)
	{
		if (contentType == null)
			throw new ArgumentNullException(nameof(contentType));
		var buffer = AvalonTextBufferRegistry.GetOrCreate(new TextDocument(text ?? string.Empty), contentType);
		TextBufferCreated?.Invoke(this, new TextBufferCreatedEventArgs(buffer));
		return buffer;
	}

	public ITextBuffer CreateTextBuffer(TextReader reader, IContentType contentType)
	{
		if (reader == null)
			throw new ArgumentNullException(nameof(reader));
		return CreateTextBuffer(reader.ReadToEnd(), contentType);
	}
}
