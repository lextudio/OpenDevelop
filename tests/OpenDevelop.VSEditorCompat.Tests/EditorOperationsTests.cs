// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IEditorOperations tests (vs-editor-api.md section 34). Headless: a bare TextArea (no live
// window) is sufficient since these operate on Document/Caret/Selection, not layout.

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using Microsoft.VisualStudio.Text.Operations;
using Xunit;

namespace LeXtudio.OpenDevelop.VSEditor.Tests;

public sealed class EditorOperationsTests
{
	static (TextArea textArea, AvalonEditorOperations ops) Create(string text)
	{
		var document = new TextDocument(text);
		var buffer = AvalonTextBufferRegistry.GetOrCreate(document, AvalonContentTypeRegistry.Text);
		var textArea = new TextArea { Document = document };
		var view = AvalonTextViewRegistry.GetOrCreate(buffer, textArea);
		var ops = (AvalonEditorOperations)new AvalonEditorOperationsFactoryService().GetEditorOperations(view);
		return (textArea, ops);
	}

	[Fact]
	public void MoveToNextCharacter_Advances_The_Caret()
	{
		var (textArea, ops) = Create("abc");
		textArea.Caret.Offset = 0;

		ops.MoveToNextCharacter(false);

		Assert.Equal(1, textArea.Caret.Offset);
	}

	[Fact]
	public void MoveToNextWord_Stops_At_The_Next_Word_Start()
	{
		var (textArea, ops) = Create("foo bar baz");
		textArea.Caret.Offset = 0;

		ops.MoveToNextWord(false);

		Assert.Equal(4, textArea.Caret.Offset); // start of "bar"
	}

	[Fact]
	public void MoveToEndOfLine_And_MoveToStartOfLine_Roundtrip()
	{
		var (textArea, ops) = Create("hello\nworld");
		textArea.Caret.Offset = 2;

		ops.MoveToEndOfLine(false);
		Assert.Equal(5, textArea.Caret.Offset);

		ops.MoveToStartOfLine(false);
		Assert.Equal(0, textArea.Caret.Offset);
	}

	[Fact]
	public void MoveLineDown_Preserves_Column()
	{
		var (textArea, ops) = Create("abcde\nfg\nhijkl");
		textArea.Caret.Offset = 3; // column 4 on line 1

		ops.MoveLineDown(false);

		Assert.Equal(8, textArea.Caret.Offset); // line 2 ("fg") only has 2 chars - clamped to end
	}

	[Fact]
	public void Backspace_Removes_The_Previous_Character()
	{
		var (textArea, ops) = Create("abc");
		textArea.Caret.Offset = 2;

		Assert.True(ops.Backspace());

		Assert.Equal("ac", textArea.Document.Text);
		Assert.Equal(1, textArea.Caret.Offset);
	}

	[Fact]
	public void Delete_Removes_The_Selection_When_Present()
	{
		var (textArea, ops) = Create("abcdef");
		textArea.Selection = Selection.Create(textArea, 1, 4);

		Assert.True(ops.Delete());

		Assert.Equal("aef", textArea.Document.Text);
	}

	[Fact]
	public void DeleteWordToRight_Removes_The_Next_Word()
	{
		var (textArea, ops) = Create("foo bar");
		textArea.Caret.Offset = 0;

		Assert.True(ops.DeleteWordToRight());

		Assert.Equal("bar", textArea.Document.Text);
	}

	[Fact]
	public void InsertNewLine_Uses_The_Line_Own_Delimiter()
	{
		var (textArea, ops) = Create("abc");
		textArea.Caret.Offset = 3;

		Assert.True(ops.InsertNewLine());

		Assert.Equal("abc\n", textArea.Document.Text);
	}

	[Fact]
	public void Indent_Inserts_At_The_Caret_When_Selection_Is_Empty()
	{
		var (textArea, ops) = Create("abc");
		textArea.Caret.Offset = 0;

		Assert.True(ops.Indent());

		Assert.StartsWith("\t", textArea.Document.Text);
	}

	[Fact]
	public void Indent_Prefixes_Every_Selected_Line()
	{
		var (textArea, ops) = Create("one\ntwo\nthree");
		textArea.Selection = Selection.Create(textArea, 0, textArea.Document.TextLength);

		Assert.True(ops.Indent());

		Assert.Equal("\tone\n\ttwo\n\tthree", textArea.Document.Text);
	}

	[Fact]
	public void Unindent_Removes_A_Leading_Tab()
	{
		var (textArea, ops) = Create("\tabc");
		textArea.Caret.Offset = 0;

		Assert.True(ops.Unindent());

		Assert.Equal("abc", textArea.Document.Text);
	}

	[Fact]
	public void MakeUppercase_Transforms_The_Selection()
	{
		var (textArea, ops) = Create("hello world");
		textArea.Selection = Selection.Create(textArea, 0, 5);

		Assert.True(ops.MakeUppercase());

		Assert.Equal("HELLO world", textArea.Document.Text);
	}

	[Fact]
	public void MakeUppercase_Falls_Back_To_The_Current_Word_When_No_Selection()
	{
		var (textArea, ops) = Create("hello world");
		textArea.Caret.Offset = 2; // inside "hello"

		Assert.True(ops.MakeUppercase());

		Assert.Equal("HELLO world", textArea.Document.Text);
	}

	[Fact]
	public void TransposeCharacter_Swaps_The_Two_Characters_Around_The_Caret()
	{
		var (textArea, ops) = Create("ab");
		textArea.Caret.Offset = 1;

		Assert.True(ops.TransposeCharacter());

		Assert.Equal("ba", textArea.Document.Text);
	}

	[Fact]
	public void SelectCurrentWord_Selects_The_Word_Under_The_Caret()
	{
		var (textArea, ops) = Create("foo bar baz");
		textArea.Caret.Offset = 5; // inside "bar"

		ops.SelectCurrentWord();

		Assert.Equal("bar", textArea.Selection.GetText());
	}

	[Fact]
	public void SelectAll_Selects_The_Whole_Document()
	{
		var (textArea, ops) = Create("hello world");

		ops.SelectAll();

		Assert.Equal("hello world", textArea.Selection.GetText());
	}

	[Fact]
	public void ReplaceAllMatches_Replaces_Every_Occurrence()
	{
		var (textArea, ops) = Create("cat cats catalog");

		var count = ops.ReplaceAllMatches("cat", "dog", matchCase: true, matchWholeWord: true, useRegularExpressions: false);

		Assert.Equal(1, count);
		Assert.Equal("dog cats catalog", textArea.Document.Text);
	}

	[Fact]
	public void DuplicateSelection_Duplicates_The_Current_Line_When_No_Selection()
	{
		var (textArea, ops) = Create("hello\nworld");
		textArea.Caret.Offset = 2;

		Assert.True(ops.DuplicateSelection());

		Assert.Equal("hello\nhello\nworld", textArea.Document.Text);
	}

	[Fact]
	public void MoveSelectedLinesDown_Swaps_With_The_Following_Line()
	{
		var (textArea, ops) = Create("one\ntwo\nthree");
		textArea.Caret.Offset = 1; // on "one"

		Assert.True(ops.MoveSelectedLinesDown());

		Assert.Equal("two\none\nthree", textArea.Document.Text);
	}

	[Fact]
	public void TrimTrailingWhiteSpace_Removes_Trailing_Spaces_From_Every_Line()
	{
		var (textArea, ops) = Create("hello   \nworld\t\t");

		Assert.True(ops.TrimTrailingWhiteSpace());

		Assert.Equal("hello\nworld", textArea.Document.Text);
	}
}
