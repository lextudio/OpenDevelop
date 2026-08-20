// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// vs-editor-api.md section 42's edit-transaction and undo test list.

using System;

using ICSharpCode.AvalonEdit.Document;
using LeXtudio.OpenDevelop.VSEditor;
using Microsoft.VisualStudio.Text;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class EditTests
{
	static AvalonTextBuffer CreateBuffer(string text) =>
		AvalonTextBufferRegistry.GetOrCreate(new TextDocument(text), AvalonContentTypeRegistry.Text);

	[Fact]
	public void MultipleInsertsInOneEditApplyAtTheirOriginalOffsets()
	{
		var buffer = CreateBuffer("abcdef");

		using (var edit = buffer.CreateEdit()) {
			edit.Insert(0, "1");
			edit.Insert(3, "2");
			edit.Insert(6, "3");
			edit.Apply();
		}

		Assert.Equal("1abc2def3", buffer.CurrentSnapshot.GetText());
	}

	[Fact]
	public void OverlappingReplacementsFailValidationWithoutMutatingTheDocument()
	{
		var buffer = CreateBuffer("abcdef");

		using (var edit = buffer.CreateEdit()) {
			Assert.True(edit.Replace(new Span(1, 3), "X"));
			// A span reaching past the snapshot's length is rejected outright (section 13).
			Assert.False(edit.Replace(new Span(4, 10), "Y"));
			Assert.True(edit.HasFailedChanges);
			edit.Cancel();
		}

		Assert.Equal("abcdef", buffer.CurrentSnapshot.GetText());
	}

	[Fact]
	public void CancelledEditNeverTouchesTheDocument()
	{
		var buffer = CreateBuffer("abcdef");

		using (var edit = buffer.CreateEdit()) {
			edit.Insert(0, "XYZ");
			edit.Cancel();
		}

		Assert.Equal("abcdef", buffer.CurrentSnapshot.GetText());
	}

	[Fact]
	public void ApplyingTwiceThrows()
	{
		var buffer = CreateBuffer("abcdef");
		var edit = buffer.CreateEdit();
		edit.Insert(0, "X");
		edit.Apply();

		Assert.Throws<InvalidOperationException>(() => edit.Apply());
	}

	[Fact]
	public void CancellingAfterApplyThrows()
	{
		var buffer = CreateBuffer("abcdef");
		var edit = buffer.CreateEdit();
		edit.Apply();

		Assert.Throws<InvalidOperationException>(() => edit.Cancel());
	}

	[Fact]
	public void DisposingWithoutApplyOrCancelThrows()
	{
		var buffer = CreateBuffer("abcdef");
		var edit = buffer.CreateEdit();
		edit.Insert(0, "X");

		Assert.Throws<InvalidOperationException>(() => edit.Dispose());
	}

	[Fact]
	public void OneEditApplyProducesOneUndoGroup()
	{
		var buffer = CreateBuffer("abcdef");

		using (var edit = buffer.CreateEdit()) {
			edit.Insert(0, "1");
			edit.Insert(3, "2");
			edit.Apply();
		}

		Assert.Equal("1abc2def", buffer.CurrentSnapshot.GetText());
		Assert.True(buffer.Document.UndoStack.CanUndo);

		buffer.Document.UndoStack.Undo();

		Assert.Equal("abcdef", buffer.CurrentSnapshot.GetText());
		Assert.False(buffer.Document.UndoStack.CanUndo);
	}
}
