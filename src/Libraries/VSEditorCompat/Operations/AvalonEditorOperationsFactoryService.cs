// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IEditorOperationsFactoryService: one AvalonEditorOperations per ITextView (vs-editor-api.md
// section 34).

using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonEditorOperationsFactoryService : IEditorOperationsFactoryService
{
	static readonly ConditionalWeakTable<ITextView, IEditorOperations> operationsByView = new();

	public IEditorOperations GetEditorOperations(ITextView textView)
	{
		if (operationsByView.TryGetValue(textView, out var existing))
			return existing;
		if (textView is not AvalonTextView avalonView)
			throw new System.ArgumentException("The view was not created by this compatibility layer.", nameof(textView));
		var created = new AvalonEditorOperations(avalonView, avalonView.TextArea);
		return operationsByView.GetValue(textView, _ => created);
	}
}
