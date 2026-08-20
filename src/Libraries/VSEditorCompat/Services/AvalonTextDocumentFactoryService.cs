// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// The official ITextDocumentFactoryService: creates file-backed documents from disk or wraps an
// existing buffer, and tracks the buffer -> document mapping (vs-editor-api.md sections 31 and
// 39, P1). The spike loads/saves via plain file I/O; OpenDevelop integration later delegates to
// its own file management (section 67).

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>Creates and tracks VS ITextDocument instances.</summary>
public sealed class AvalonTextDocumentFactoryService : ITextDocumentFactoryService
{
	readonly ConditionalWeakTable<ITextBuffer, ITextDocument> documents = new();

	public event EventHandler<TextDocumentEventArgs> TextDocumentCreated;
	public event EventHandler<TextDocumentEventArgs> TextDocumentDisposed;

	public ITextDocument CreateTextDocument(ITextBuffer textBuffer, string filePath)
	{
		if (textBuffer is not AvalonTextBuffer avalonBuffer)
			throw new ArgumentException("The buffer is not an OpenDevelop VSEditor buffer.", nameof(textBuffer));
		if (documents.TryGetValue(textBuffer, out var existing))
			return existing;
		var document = new AvalonTextDocument(avalonBuffer, filePath);
		documents.Add(textBuffer, document);
		TextDocumentCreated?.Invoke(this, new TextDocumentEventArgs(document));
		return document;
	}

	public ITextDocument CreateAndLoadTextDocument(string filePath, IContentType contentType)
	{
		if (filePath == null)
			throw new ArgumentNullException(nameof(filePath));
		if (contentType == null)
			throw new ArgumentNullException(nameof(contentType));
		var text = File.ReadAllText(filePath);
		var buffer = AvalonTextBufferRegistry.GetOrCreate(new TextDocument(text), contentType);
		return CreateTextDocument(buffer, filePath);
	}

	public ITextDocument CreateAndLoadTextDocument(string filePath, IContentType contentType, Encoding encoding, out bool characterSubstitutionsOccurred)
	{
		if (encoding == null)
			throw new ArgumentNullException(nameof(encoding));
		characterSubstitutionsOccurred = false;
		var buffer = AvalonTextBufferRegistry.GetOrCreate(new TextDocument(File.ReadAllText(filePath, encoding)), contentType);
		var document = CreateTextDocument(buffer, filePath) as AvalonTextDocument;
		document.Encoding = encoding;
		return document;
	}

	public ITextDocument CreateAndLoadTextDocument(string filePath, IContentType contentType, bool attemptUtf8Detection, out bool characterSubstitutionsOccurred)
	{
		characterSubstitutionsOccurred = false;
		var encoding = attemptUtf8Detection ? Encoding.UTF8 : Encoding.Default;
		var buffer = AvalonTextBufferRegistry.GetOrCreate(new TextDocument(File.ReadAllText(filePath, encoding)), contentType);
		var document = CreateTextDocument(buffer, filePath) as AvalonTextDocument;
		document.Encoding = encoding;
		return document;
	}

	public bool TryGetTextDocument(ITextBuffer textBuffer, out ITextDocument textDocument)
		=> documents.TryGetValue(textBuffer, out textDocument);
}
