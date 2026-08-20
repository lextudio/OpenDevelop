// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextUndoTransaction: a named grouping of edits, backed by AvalonEdit's UndoStack.StartUndoGroup/
// EndUndoGroup (vs-editor-api.md section 18/P1). A transaction disposed without Complete()/Cancel()
// having been called is auto-canceled - matching VS's "not completed means rolled back" contract.

using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.Text.Operations;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonTextUndoTransaction : ITextUndoTransaction
{
	readonly AvalonTextUndoHistory history;
	readonly List<ITextUndoPrimitive> primitives = new();
	bool disposed;

	internal AvalonTextUndoTransaction(AvalonTextUndoHistory history, string description, ITextUndoTransaction parent)
	{
		this.history = history;
		Description = description;
		Parent = parent;
		State = UndoTransactionState.Open;
	}

	public string Description { get; set; }

	public UndoTransactionState State { get; internal set; }

	public ITextUndoHistory History => history;

	public IList<ITextUndoPrimitive> UndoPrimitives => primitives;

	public ITextUndoTransaction Parent { get; }

	public bool CanRedo => State == UndoTransactionState.Undone;

	public bool CanUndo => State == UndoTransactionState.Completed;

	public IMergeTextUndoTransactionPolicy MergePolicy { get; set; }

	public void AddUndo(ITextUndoPrimitive undo)
	{
		if (State != UndoTransactionState.Open)
			throw new InvalidOperationException("Cannot add to a transaction that is not open.");
		undo.Parent = this;
		primitives.Add(undo);
	}

	public void Complete()
	{
		if (State != UndoTransactionState.Open)
			throw new InvalidOperationException("The transaction is not open.");
		State = UndoTransactionState.Completed;
		history.OnTransactionCompleted(this);
	}

	public void Cancel()
	{
		if (State != UndoTransactionState.Open)
			throw new InvalidOperationException("The transaction is not open.");
		State = UndoTransactionState.Canceled;
		history.OnTransactionCanceled(this);
	}

	public void Do()
	{
		foreach (var primitive in primitives)
			primitive.Do();
		State = UndoTransactionState.Completed;
	}

	public void Undo()
	{
		for (int i = primitives.Count - 1; i >= 0; i--)
			primitives[i].Undo();
		State = UndoTransactionState.Undone;
	}

	public void Dispose()
	{
		if (disposed)
			return;
		disposed = true;
		if (State == UndoTransactionState.Open)
			Cancel();
	}
}
