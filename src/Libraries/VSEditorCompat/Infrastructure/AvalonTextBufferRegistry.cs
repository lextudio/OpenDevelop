// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// There must be exactly ONE AvalonTextBuffer per AvalonEdit TextDocument, or buffer/snapshot
// identity comparisons break (vs-editor-api.md section 11). A weak table also ensures the
// compatibility layer never keeps a closed document alive.

using System.Runtime.CompilerServices;

using ICSharpCode.AvalonEdit.Document;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>Maps each AvalonEdit TextDocument to its single VS ITextBuffer adapter.</summary>
public static class AvalonTextBufferRegistry
{
	static readonly ConditionalWeakTable<TextDocument, AvalonTextBuffer> buffers = new();

	/// <summary>Gets the existing adapter for a document, or creates one with the given content type.</summary>
	public static AvalonTextBuffer GetOrCreate(TextDocument document, Microsoft.VisualStudio.Utilities.IContentType contentType)
	{
		if (buffers.TryGetValue(document, out var existing))
			return existing;
		var created = new AvalonTextBuffer(document, contentType);
		return buffers.GetValue(document, _ => created);
	}

	/// <summary>Gets the existing adapter for a document, or null when none was created yet.</summary>
	public static AvalonTextBuffer GetOrNull(TextDocument document)
		=> buffers.TryGetValue(document, out var existing) ? existing : null;
}
