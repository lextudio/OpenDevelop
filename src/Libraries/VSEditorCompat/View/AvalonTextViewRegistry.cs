// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Exactly one AvalonTextView per AvalonEdit TextArea, mirroring AvalonTextBufferRegistry's
// identity rule for buffers (vs-editor-api.md section 11) - split views over the same document
// get their own AvalonTextView (one TextArea each) but must all resolve to the same
// AvalonTextBuffer (section 52).

using System.Runtime.CompilerServices;

using AvalonEditing = ICSharpCode.AvalonEdit.Editing;
using Microsoft.VisualStudio.Text.Editor;

namespace LeXtudio.OpenDevelop.VSEditor;

public static class AvalonTextViewRegistry
{
	static readonly ConditionalWeakTable<AvalonEditing.TextArea, AvalonTextView> views = new();

	public static AvalonTextView GetOrCreate(AvalonTextBuffer buffer, AvalonEditing.TextArea textArea, ITextViewRoleSet roles = null)
	{
		if (views.TryGetValue(textArea, out var existing))
			return existing;
		var created = new AvalonTextView(buffer, textArea, roles);
		return views.GetValue(textArea, _ => created);
	}

	public static AvalonTextView GetOrNull(AvalonEditing.TextArea textArea)
		=> views.TryGetValue(textArea, out var existing) ? existing : null;
}
