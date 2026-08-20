// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextUndoHistoryRegistry: one AvalonTextUndoHistory per context object, keyed the same way VS
// itself keys them - typically an ITextBuffer, but RegisterHistory/AttachHistory accept any
// object (vs-editor-api.md section 18/P1). A context that is (or wraps) an AvalonTextBuffer gets
// wired to that buffer's real AvalonEdit UndoStack; any other context gets a history with no
// backing store (see AvalonTextUndoHistory's class comment).

using System;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.Text.Operations;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonTextUndoHistoryRegistry : ITextUndoHistoryRegistry
{
	readonly ConditionalWeakTable<object, AvalonTextUndoHistory> histories = new();

	public ITextUndoHistory RegisterHistory(object context)
	{
		if (context == null)
			throw new ArgumentNullException(nameof(context));
		if (histories.TryGetValue(context, out var existing))
			return existing;
		var undoStack = (context as AvalonTextBuffer)?.Document.UndoStack;
		var created = new AvalonTextUndoHistory(undoStack);
		histories.Add(context, created);
		return created;
	}

	public ITextUndoHistory GetHistory(object context)
	{
		if (context != null && histories.TryGetValue(context, out var existing))
			return existing;
		throw new InvalidOperationException("No undo history has been registered for this context.");
	}

	public bool TryGetHistory(object context, out ITextUndoHistory history)
	{
		if (context != null && histories.TryGetValue(context, out var existing)) {
			history = existing;
			return true;
		}
		history = null;
		return false;
	}

	public void AttachHistory(object context, ITextUndoHistory history)
	{
		if (context == null)
			throw new ArgumentNullException(nameof(context));
		if (history is not AvalonTextUndoHistory avalonHistory)
			throw new ArgumentException("The history was not created by this compatibility layer.", nameof(history));
		histories.Add(context, avalonHistory);
	}

	public void RemoveHistory(ITextUndoHistory history)
	{
		// ConditionalWeakTable has no reverse (value -> key) lookup; nothing external holds a
		// strong reference to the context solely because of this registry, so an explicit
		// removal is not required for correctness - entries are collected once the context
		// itself becomes unreachable. If eager removal is ever needed, this would require
		// tracking the context alongside the history explicitly.
	}
}
