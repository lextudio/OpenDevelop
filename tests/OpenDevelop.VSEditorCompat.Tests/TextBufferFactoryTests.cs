// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextBufferFactoryService tests: factory-created buffers own their own AvalonEdit document,
// are fully editable through the VS API, and raise TextBufferCreated (vs-editor-api.md section
// 30).

using System.IO;
using System.Text;

using Microsoft.VisualStudio.Text;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class TextBufferFactoryTests
{
	readonly AvalonTextBufferFactoryService factory = new(AvalonContentTypeRegistry.Instance);

	[Fact]
	public void Standard_Content_Types_Are_Exposed()
	{
		Assert.Equal("text", factory.TextContentType.TypeName);
		Assert.Equal("plaintext", factory.PlaintextContentType.TypeName);
		Assert.Equal("inert", factory.InertContentType.TypeName);
	}

	[Fact]
	public void CreateTextBuffer_Without_ContentType_Uses_Text()
	{
		var buffer = factory.CreateTextBuffer();
		Assert.Same(factory.TextContentType, buffer.ContentType);
		Assert.Equal("", buffer.CurrentSnapshot.GetText());
	}

	[Fact]
	public void CreateTextBuffer_With_Text_And_ContentType()
	{
		var buffer = factory.CreateTextBuffer("class C {}", AvalonContentTypeRegistry.CSharp);
		Assert.Equal("class C {}", buffer.CurrentSnapshot.GetText());
		Assert.Same(AvalonContentTypeRegistry.CSharp, buffer.ContentType);
		// The factory buffer is fully editable through the VS API.
		buffer.Insert(6, "partial ");
		Assert.Equal("class partial C {}", buffer.CurrentSnapshot.GetText());
	}

	[Fact]
	public void CreateTextBuffer_From_Reader()
	{
		var buffer = factory.CreateTextBuffer(new StringReader("from reader"), AvalonContentTypeRegistry.Text);
		Assert.Equal("from reader", buffer.CurrentSnapshot.GetText());
	}

	[Fact]
	public void CreateTextBuffer_Raises_TextBufferCreated()
	{
		ITextBuffer? created = null;
		factory.TextBufferCreated += (_, e) => created = e.TextBuffer;
		var buffer = factory.CreateTextBuffer("x", AvalonContentTypeRegistry.Text);
		Assert.Same(buffer, created);
	}
}
