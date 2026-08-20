// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// One AvalonTextBuffer plays both the "document" and "data" role - there is no secondary
// projection buffer (see FlatBufferGraph.cs / vs-editor-api.md section 32).

using System;

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonTextDataModel : ITextDataModel
{
	readonly AvalonTextBuffer buffer;

	public AvalonTextDataModel(AvalonTextBuffer buffer)
	{
		this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
		buffer.ContentTypeChanged += (sender, e) =>
			ContentTypeChanged?.Invoke(this, new TextDataModelContentTypeChangedEventArgs(e.BeforeContentType, e.AfterContentType));
	}

	public IContentType ContentType => buffer.ContentType;

	public ITextBuffer DocumentBuffer => buffer;

	public ITextBuffer DataBuffer => buffer;

	public event EventHandler<TextDataModelContentTypeChangedEventArgs> ContentTypeChanged;
}
