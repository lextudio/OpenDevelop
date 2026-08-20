// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextUndoHistory/ITextUndoHistoryRegistry tests (vs-editor-api.md section 18/P1).

using System.Linq;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text.Operations;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class UndoHistoryTests
{
	static (AvalonTextBuffer buffer, ITextUndoHistory history) Create(string text)
	{
		var buffer = AvalonTextBufferRegistry.GetOrCreate(new TextDocument(text), AvalonContentTypeRegistry.Text);
		var registry = new AvalonTextUndoHistoryRegistry();
		var history = registry.RegisterHistory(buffer);
		return (buffer, history);
	}

	[Fact]
	public void Completed_Transaction_Participates_In_The_Real_Undo_Stack()
	{
		var (buffer, history) = Create("abc");

		using (var transaction = history.CreateTransaction("Insert X")) {
			buffer.Insert(1, "X");
			transaction.Complete();
		}

		Assert.Equal("aXbc", buffer.CurrentSnapshot.GetText());
		Assert.True(history.CanUndo);
		Assert.Equal("Insert X", history.UndoDescription);

		history.Undo(1);

		Assert.Equal("abc", buffer.CurrentSnapshot.GetText());
		Assert.False(history.CanUndo);
		Assert.True(history.CanRedo);
	}

	[Fact]
	public void Redo_Restores_The_Undone_Edit()
	{
		var (buffer, history) = Create("abc");
		using (var transaction = history.CreateTransaction("Insert X")) {
			buffer.Insert(1, "X");
			transaction.Complete();
		}
		history.Undo(1);

		history.Redo(1);

		Assert.Equal("aXbc", buffer.CurrentSnapshot.GetText());
		Assert.True(history.CanUndo);
		Assert.False(history.CanRedo);
	}

	[Fact]
	public void Canceled_Transaction_Rolls_Back_Its_Edit_And_Is_Not_Undoable()
	{
		var (buffer, history) = Create("abc");

		using (var transaction = history.CreateTransaction("Insert X")) {
			buffer.Insert(1, "X");
			transaction.Cancel();
		}

		Assert.Equal("abc", buffer.CurrentSnapshot.GetText());
		Assert.False(history.CanUndo);
	}

	[Fact]
	public void Disposing_Without_Complete_Or_Cancel_Auto_Cancels()
	{
		var (buffer, history) = Create("abc");

		using (history.CreateTransaction("Insert X")) {
			buffer.Insert(1, "X");
			// no Complete()/Cancel() - Dispose() must roll it back
		}

		Assert.Equal("abc", buffer.CurrentSnapshot.GetText());
		Assert.False(history.CanUndo);
	}

	[Fact]
	public void New_Edit_After_Undo_Clears_The_Redo_Stack()
	{
		var (buffer, history) = Create("abc");
		using (var t1 = history.CreateTransaction("Insert X")) { buffer.Insert(1, "X"); t1.Complete(); }
		history.Undo(1);
		Assert.True(history.CanRedo);

		using (var t2 = history.CreateTransaction("Insert Y")) { buffer.Insert(0, "Y"); t2.Complete(); }

		Assert.False(history.CanRedo);
	}

	[Fact]
	public void RegisterHistory_Returns_The_Same_Instance_For_The_Same_Context()
	{
		var buffer = AvalonTextBufferRegistry.GetOrCreate(new TextDocument("x"), AvalonContentTypeRegistry.Text);
		var registry = new AvalonTextUndoHistoryRegistry();

		var first = registry.RegisterHistory(buffer);
		var second = registry.RegisterHistory(buffer);

		Assert.Same(first, second);
	}

	[Fact]
	public void TryGetHistory_Returns_False_For_An_Unregistered_Context()
	{
		var registry = new AvalonTextUndoHistoryRegistry();
		var unregisteredContext = new object();

		Assert.False(registry.TryGetHistory(unregisteredContext, out var history));
		Assert.Null(history);
	}

	[Fact]
	public void UndoStack_Lists_Completed_Transactions_Most_Recent_First()
	{
		var (buffer, history) = Create("abc");
		using (var t1 = history.CreateTransaction("First")) { buffer.Insert(0, "1"); t1.Complete(); }
		using (var t2 = history.CreateTransaction("Second")) { buffer.Insert(0, "2"); t2.Complete(); }

		var stack = history.UndoStack.ToList();
		Assert.Equal(new[] { "Second", "First" }, stack.Select(t => t.Description));
	}
}
