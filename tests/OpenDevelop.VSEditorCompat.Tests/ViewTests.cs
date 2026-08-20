// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextView/ITextCaret/ITextSelection tests (vs-editor-api.md sections 21-24). These exercise the
// TextArea-backed adapters without attaching to a live visual tree - caret/selection offset
// tracking does not need layout/rendering, only the document and the AvalonEdit editing objects.
// View-line geometry members remain out of scope here too (see AvalonTextCaret/AvalonTextView).

using System;

using AvalonEditing = ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class ViewTests
{
	static (AvalonTextBuffer buffer, AvalonEditing.TextArea textArea, AvalonTextView view) CreateView(string text)
	{
		var document = new TextDocument(text);
		var buffer = AvalonTextBufferRegistry.GetOrCreate(document, AvalonContentTypeRegistry.Text);
		var textArea = new AvalonEditing.TextArea { Document = document };
		var view = AvalonTextViewRegistry.GetOrCreate(buffer, textArea);
		return (buffer, textArea, view);
	}

	[Fact]
	public void TextView_Exposes_The_Same_Buffer_And_Current_Snapshot()
	{
		var (buffer, _, view) = CreateView("hello world");

		Assert.Same(buffer, view.TextBuffer);
		Assert.Equal("hello world", view.TextSnapshot.GetText());
	}

	[Fact]
	public void TextViewRegistry_Returns_The_Same_Instance_For_The_Same_TextArea()
	{
		var (_, textArea, view) = CreateView("x");

		var again = AvalonTextViewRegistry.GetOrCreate((AvalonTextBuffer)view.TextBuffer, textArea);

		Assert.Same(view, again);
	}

	[Fact]
	public void Caret_MoveTo_Updates_Position_And_Offset()
	{
		var (_, textArea, view) = CreateView("hello world");

		view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, 5));

		Assert.Equal(5, textArea.Caret.Offset);
		Assert.Equal(5, view.Caret.Position.BufferPosition.Position);
	}

	[Fact]
	public void Caret_PositionChanged_Fires_When_Caret_Moves()
	{
		var (_, textArea, view) = CreateView("hello world");
		int raised = 0;
		view.Caret.PositionChanged += (_, __) => raised++;

		textArea.Caret.Offset = 3;

		Assert.Equal(1, raised);
	}

	[Fact]
	public void Caret_MoveToNextCaretPosition_Advances_By_One()
	{
		var (_, textArea, view) = CreateView("hello");
		textArea.Caret.Offset = 2;

		view.Caret.MoveToNextCaretPosition();

		Assert.Equal(3, textArea.Caret.Offset);
	}

	[Fact]
	public void Caret_MoveToNextCaretPosition_Clamps_At_End()
	{
		var (_, textArea, view) = CreateView("hi");
		textArea.Caret.Offset = 2;

		view.Caret.MoveToNextCaretPosition();

		Assert.Equal(2, textArea.Caret.Offset);
	}

	[Fact]
	public void Selection_Select_Sets_Start_And_End()
	{
		var (_, textArea, view) = CreateView("hello world");

		view.Selection.Select(new SnapshotSpan(view.TextSnapshot, 0, 5), isReversed: false);

		Assert.False(view.Selection.IsEmpty);
		Assert.Equal(0, view.Selection.Start.Position.Position);
		Assert.Equal(5, view.Selection.End.Position.Position);
		Assert.Equal("hello", view.Selection.SelectedSpans[0].GetText());
	}

	[Fact]
	public void Selection_Clear_Makes_It_Empty()
	{
		var (_, textArea, view) = CreateView("hello world");
		view.Selection.Select(new SnapshotSpan(view.TextSnapshot, 0, 5), isReversed: false);

		view.Selection.Clear();

		Assert.True(view.Selection.IsEmpty);
	}

	[Fact]
	public void Selection_Changed_Fires_On_Select()
	{
		var (_, _, view) = CreateView("hello world");
		int raised = 0;
		view.Selection.SelectionChanged += (_, __) => raised++;

		view.Selection.Select(new SnapshotSpan(view.TextSnapshot, 0, 3), isReversed: false);

		Assert.Equal(1, raised);
	}

	[Fact]
	public void Roles_Default_To_The_Standard_Document_Roles()
	{
		var (_, _, view) = CreateView("x");

		Assert.True(view.Roles.Contains(PredefinedTextViewRoles.Document));
		Assert.True(view.Roles.Contains(PredefinedTextViewRoles.Editable));
		Assert.False(view.Roles.Contains(PredefinedTextViewRoles.Debuggable));
	}

	[Fact]
	public void TextView_Close_Raises_Closed_And_Sets_IsClosed()
	{
		var (_, _, view) = CreateView("x");
		bool raised = false;
		view.Closed += (_, __) => raised = true;

		view.Close();

		Assert.True(raised);
		Assert.True(view.IsClosed);
	}

	[Fact]
	public void TextView_Close_Is_Idempotent()
	{
		var (_, _, view) = CreateView("x");
		int raised = 0;
		view.Closed += (_, __) => raised++;

		view.Close();
		view.Close();

		Assert.Equal(1, raised);
	}

	[Fact]
	public void TextView_Options_Get_Set_RoundTrip()
	{
		var (_, _, view) = CreateView("x");
		var key = new EditorOptionKey<bool>("Test/RoundTrip");

		view.Options.SetOptionValue(key, true);

		Assert.True(view.Options.GetOptionValue(key));
	}

	[Fact]
	public void TextViewLines_Does_Not_Throw_Even_Without_A_Live_Window()
	{
		// Real coordinates (line ordering, flush rows, character bounds, ...) can only be
		// trusted once the TextArea has gone through a real WPF measure/arrange pass in an
		// attached window - see tests/OpenDevelop.IntegrationTests/VSEditorViewIntegrationTests.cs
		// (driven through VSEditorViewDevFlowActions against the real running app) for that.
		// This just confirms TextViewLines itself is safe to call in a headless unit test - no
		// NotSupportedException, no crash - even though geometry isn't meaningfully verifiable here.
		var (_, _, view) = CreateView("x");

		var lines = view.TextViewLines;

		Assert.NotNull(lines);
	}
}
