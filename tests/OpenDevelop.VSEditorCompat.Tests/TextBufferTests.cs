// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextBuffer-level tests for the VS editor compatibility layer: CurrentSnapshot mirrors the
// AvalonEdit document, edits land through ITextEdit, and the VS event sequence fires in order.

using System;
using System.Collections.Generic;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class TextBufferTests
{
	static AvalonTextBuffer CreateBuffer(string text)
		=> AvalonTextBufferRegistry.GetOrCreate(new TextDocument(text), AvalonContentTypeRegistry.Text);

	[Fact]
	public void CurrentSnapshot_Mirrors_TextDocument()
	{
		var buffer = CreateBuffer("class C {}");
		Assert.Equal("class C {}", buffer.CurrentSnapshot.GetText());
		Assert.Equal(10, buffer.CurrentSnapshot.Length);
		Assert.Same(buffer, buffer.CurrentSnapshot.TextBuffer);
	}

	[Fact]
	public void Edit_Apply_Changes_CurrentSnapshot()
	{
		var buffer = CreateBuffer("class C {}");
		var snapshot0 = buffer.CurrentSnapshot;

		using (var edit = buffer.CreateEdit()) {
			edit.Insert(6, "partial ");
			var snapshot1 = edit.Apply();
			Assert.Equal("class partial C {}", snapshot1.GetText());
		}

		// The VS proof-of-concept shape (vs-editor-api.md section 41).
		Assert.Equal("class partial C {}", buffer.CurrentSnapshot.GetText());
		Assert.NotSame(snapshot0, buffer.CurrentSnapshot);
		Assert.Equal(0, snapshot0.Version.VersionNumber);
		Assert.Equal(1, buffer.CurrentSnapshot.Version.VersionNumber);
	}

	[Fact]
	public void OldSnapshot_Remains_Immutable_After_Edits()
	{
		var buffer = CreateBuffer("abc");
		var snapshot0 = buffer.CurrentSnapshot;

		buffer.Replace(new Span(0, 3), "xyz");
		buffer.Replace(new Span(0, 3), "123");

		Assert.Equal("123", buffer.CurrentSnapshot.GetText());
		Assert.Equal("abc", snapshot0.GetText());
		Assert.Equal(3, snapshot0.Length);
	}

	[Fact]
	public void Delete_And_Replace_Convenience_Methods()
	{
		var buffer = CreateBuffer("hello world");
		var afterDelete = buffer.Delete(new Span(5, 1));
		Assert.Equal("helloworld", afterDelete.GetText());
		var afterReplace = buffer.Replace(new Span(0, 5), "bye");
		Assert.Equal("byeworld", afterReplace.GetText());
	}

	[Fact]
	public void Events_Fire_In_VS_Order()
	{
		var buffer = CreateBuffer("abc");
		var order = new List<string>();
		buffer.Changing += (_, e) => order.Add($"Changing:{e.Before.GetText()}");
		buffer.ChangedHighPriority += (_, e) => order.Add($"ChangedHighPriority:{e.After.GetText()}");
		buffer.Changed += (_, e) => order.Add($"Changed:{e.After.GetText()}");
		buffer.ChangedLowPriority += (_, e) => order.Add($"ChangedLowPriority:{e.After.GetText()}");
		buffer.PostChanged += (_, e) => order.Add("PostChanged");

		buffer.Insert(1, "X");

		Assert.Equal(new[] {
			"Changing:abc",
			"ChangedHighPriority:aXbc",
			"Changed:aXbc",
			"ChangedLowPriority:aXbc",
			"PostChanged",
		}, order);
	}

	[Fact]
	public void ChangeContentType_Fires_ContentTypeChanged()
	{
		var buffer = CreateBuffer("abc");
		IContentType? before = null;
		IContentType? after = null;
		buffer.ContentTypeChanged += (_, e) => {
			before = e.BeforeContentType;
			after = e.AfterContentType;
		};

		buffer.ChangeContentType(AvalonContentTypeRegistry.Code, editTag: null);

		Assert.Same(AvalonContentTypeRegistry.Text, before);
		Assert.Same(AvalonContentTypeRegistry.Code, after);
		Assert.Same(AvalonContentTypeRegistry.Code, buffer.ContentType);
		Assert.Same(AvalonContentTypeRegistry.Code, buffer.CurrentSnapshot.ContentType);
	}

	[Fact]
	public void DoubleApply_Throws()
	{
		var buffer = CreateBuffer("abc");
		using var edit = buffer.CreateEdit();
		edit.Insert(0, "x");
		edit.Apply();
		Assert.Throws<InvalidOperationException>(() => edit.Apply());
	}

	[Fact]
	public void CanceledEdit_Cannot_Be_Applied()
	{
		var buffer = CreateBuffer("abc");
		using var edit = buffer.CreateEdit();
		edit.Insert(0, "x");
		edit.Cancel();
		Assert.True(edit.Canceled);
		Assert.Throws<InvalidOperationException>(() => edit.Apply());
	}

	[Fact]
	public void OutOfBounds_Edit_Reports_Failed_Change()
	{
		var buffer = CreateBuffer("abc");
		using var edit = buffer.CreateEdit();
		Assert.False(edit.Replace(new Span(2, 5), "x"));
		Assert.True(edit.HasFailedChanges);
		edit.Cancel();
	}

	[Fact]
	public void Undo_Participates_In_TextDocument_Undo()
	{
		var buffer = CreateBuffer("abc");
		buffer.Insert(1, "X");
		buffer.Document.UndoStack.Undo();
		Assert.Equal("abc", buffer.CurrentSnapshot.GetText());
		buffer.Document.UndoStack.Redo();
		Assert.Equal("aXbc", buffer.CurrentSnapshot.GetText());
	}
}
