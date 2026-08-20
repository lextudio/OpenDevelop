// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextUndoHistory over an AvalonEdit UndoStack (vs-editor-api.md section 18/P1). AvalonEdit's
// own UndoStack does not expose a list of past groups with descriptions, so this keeps a
// parallel bookkeeping list (completed/redo transactions) alongside it - each Undo(n)/Redo(n)
// call here drives the SAME number of real AvalonEdit Undo()/Redo() calls, so the two stacks
// never drift apart as long as all edits to the underlying document go through a transaction
// created by this history (or through AvalonTextBuffer directly, which participates in the same
// real UndoStack but isn't reflected in this class's own bookkeeping list - see CanUndo/CanRedo,
// which always defer to the real stack rather than the bookkeeping list's Count).
//
// A history created without a backing UndoStack (RegisterHistory on a non-buffer context) still
// tracks transactions structurally (Complete/Cancel, nesting) but Undo/Redo are no-ops beyond
// popping the bookkeeping list - there is no real edit history to replay.

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonTextUndoHistory : ITextUndoHistory
{
	readonly UndoStack undoStack;
	readonly List<AvalonTextUndoTransaction> undoTransactions = new();
	readonly List<AvalonTextUndoTransaction> redoTransactions = new();
	int openDepth;
	AvalonTextUndoTransaction current;

	internal AvalonTextUndoHistory(UndoStack undoStack)
	{
		this.undoStack = undoStack;
	}

	public PropertyCollection Properties { get; } = new();

	public IEnumerable<ITextUndoTransaction> UndoStack => ((IEnumerable<AvalonTextUndoTransaction>)undoTransactions).Reverse();

	public IEnumerable<ITextUndoTransaction> RedoStack => ((IEnumerable<AvalonTextUndoTransaction>)redoTransactions).Reverse();

	public ITextUndoTransaction LastUndoTransaction => undoTransactions.LastOrDefault();

	public ITextUndoTransaction LastRedoTransaction => redoTransactions.LastOrDefault();

	public bool CanUndo => undoStack?.CanUndo ?? undoTransactions.Count > 0;

	public bool CanRedo => undoStack?.CanRedo ?? redoTransactions.Count > 0;

	public string UndoDescription => LastUndoTransaction?.Description;

	public string RedoDescription => LastRedoTransaction?.Description;

	public ITextUndoTransaction CurrentTransaction => current;

	public TextUndoHistoryState State { get; private set; } = TextUndoHistoryState.Idle;

	public event EventHandler<TextUndoRedoEventArgs> UndoRedoHappened;
	public event EventHandler<TextUndoTransactionCompletedEventArgs> UndoTransactionCompleted;

	public ITextUndoTransaction CreateTransaction(string description)
	{
		var transaction = new AvalonTextUndoTransaction(this, description, current);
		if (openDepth == 0)
			undoStack?.StartUndoGroup(transaction);
		openDepth++;
		current = transaction;
		return transaction;
	}

	internal void OnTransactionCompleted(AvalonTextUndoTransaction transaction)
	{
		openDepth--;
		current = transaction.Parent as AvalonTextUndoTransaction;
		if (openDepth > 0)
			return;
		undoStack?.EndUndoGroup();
		undoTransactions.Add(transaction);
		redoTransactions.Clear();
		UndoTransactionCompleted?.Invoke(this, new TextUndoTransactionCompletedEventArgs(transaction, TextUndoTransactionCompletionResult.TransactionAdded));
	}

	internal void OnTransactionCanceled(AvalonTextUndoTransaction transaction)
	{
		openDepth--;
		current = transaction.Parent as AvalonTextUndoTransaction;
		if (openDepth > 0)
			return;
		if (undoStack != null) {
			undoStack.EndUndoGroup();
			if (undoStack.CanUndo)
				undoStack.Undo();
		}
		// No TextUndoTransactionCompletionResult value represents "canceled" (only
		// TransactionAdded/TransactionMerged exist) - VS only raises UndoTransactionCompleted for
		// transactions that actually landed on the stack, so a canceled transaction raises nothing.
	}

	public void Undo(int count)
	{
		State = TextUndoHistoryState.Undoing;
		try {
			for (int i = 0; i < count && CanUndo; i++) {
				undoStack?.Undo();
				var transaction = undoTransactions.Count > 0 ? Pop(undoTransactions) : null;
				if (transaction != null) {
					transaction.State = UndoTransactionState.Undone;
					redoTransactions.Add(transaction);
				}
				UndoRedoHappened?.Invoke(this, new TextUndoRedoEventArgs(TextUndoHistoryState.Undoing, transaction));
			}
		} finally {
			State = TextUndoHistoryState.Idle;
		}
	}

	public void Redo(int count)
	{
		State = TextUndoHistoryState.Redoing;
		try {
			for (int i = 0; i < count && CanRedo; i++) {
				undoStack?.Redo();
				var transaction = redoTransactions.Count > 0 ? Pop(redoTransactions) : null;
				if (transaction != null) {
					transaction.State = UndoTransactionState.Completed;
					undoTransactions.Add(transaction);
				}
				UndoRedoHappened?.Invoke(this, new TextUndoRedoEventArgs(TextUndoHistoryState.Redoing, transaction));
			}
		} finally {
			State = TextUndoHistoryState.Idle;
		}
	}

	static T Pop<T>(List<T> list)
	{
		var item = list[^1];
		list.RemoveAt(list.Count - 1);
		return item;
	}
}
