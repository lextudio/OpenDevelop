// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IEditorOperations(2/3) over AvalonEdit's TextArea/Document/Caret/Selection (vs-editor-api.md
// section 34). System.Windows.Input.EditingCommands is not part of this project's WPF reference
// surface (same class of gap as AvalonTextView's earlier IScrollInfo issue - see its comment),
// so word/line/document navigation and deletion are implemented directly against
// ICSharpCode.AvalonEdit.Document.TextUtilities.GetNextCaretPosition (the same public helper
// AvalonEdit's own CaretNavigationCommandHandler/EditingCommandHandler use internally) rather
// than executing routed commands. System.Windows.Input.ApplicationCommands (Copy/Cut/Paste/
// SelectAll) IS available and is used directly against textArea.

using System;
using System.Linq;
using System.Windows.Input;

using AvalonEditing = ICSharpCode.AvalonEdit.Editing;
using LogicalDirection = System.Windows.Documents.LogicalDirection;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Operations;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonEditorOperations : IEditorOperations, IEditorOperations2, IEditorOperations3
{
	readonly AvalonTextView view;
	readonly AvalonEditing.TextArea textArea;

	internal AvalonEditorOperations(AvalonTextView view, AvalonEditing.TextArea textArea)
	{
		this.view = view ?? throw new ArgumentNullException(nameof(view));
		this.textArea = textArea ?? throw new ArgumentNullException(nameof(textArea));
	}

	public ITextView TextView => view;

	public IEditorOptions Options => view.Options;

	public ITrackingSpan ProvisionalCompositionSpan { get; private set; }

	public string SelectedText => textArea.Selection.GetText();

	public bool CanCut => !textArea.Selection.IsEmpty;

	public bool CanCopy => !textArea.Selection.IsEmpty;

	public bool CanPaste => System.Windows.Clipboard.ContainsText();

	public bool CanDelete => !textArea.Selection.IsEmpty || textArea.Caret.Offset < textArea.Document.TextLength;

	#region Navigation

	static void Execute(RoutedCommand command, AvalonEditing.TextArea textArea) => command.Execute(null, textArea);

	int NextCaretPosition(LogicalDirection direction, CaretPositioningMode mode)
		=> TextUtilities.GetNextCaretPosition(textArea.Document, textArea.Caret.Offset, direction, mode);

	public void MoveToNextCharacter(bool extendSelection) => MoveOrSelectTo(NextCaretPosition(LogicalDirection.Forward, CaretPositioningMode.Normal), extendSelection);
	public void MoveToPreviousCharacter(bool extendSelection) => MoveOrSelectTo(NextCaretPosition(LogicalDirection.Backward, CaretPositioningMode.Normal), extendSelection);
	public void MoveToNextWord(bool extendSelection) => MoveOrSelectTo(NextCaretPosition(LogicalDirection.Forward, CaretPositioningMode.WordStart), extendSelection);
	public void MoveToPreviousWord(bool extendSelection) => MoveOrSelectTo(NextCaretPosition(LogicalDirection.Backward, CaretPositioningMode.WordStart), extendSelection);

	public void MoveLineUp(bool extendSelection) => MoveByLines(-1, extendSelection);
	public void MoveLineDown(bool extendSelection) => MoveByLines(1, extendSelection);

	void MoveByLines(int delta, bool extendSelection)
	{
		var location = textArea.Document.GetLocation(textArea.Caret.Offset);
		var targetLineNumber = Math.Min(Math.Max(location.Line + delta, 1), textArea.Document.LineCount);
		var targetLine = textArea.Document.GetLineByNumber(targetLineNumber);
		var targetOffset = targetLine.Offset + Math.Min(location.Column - 1, targetLine.Length);
		MoveOrSelectTo(targetOffset, extendSelection);
	}

	int LinesPerPage() => Math.Max(1, (int)(view.ViewportHeight / Math.Max(1.0, view.LineHeight)));

	public void PageUp(bool extendSelection) => MoveByLines(-LinesPerPage(), extendSelection);
	public void PageDown(bool extendSelection) => MoveByLines(LinesPerPage(), extendSelection);

	public void MoveToEndOfLine(bool extendSelection) => MoveOrSelectTo(textArea.Document.GetLineByOffset(textArea.Caret.Offset).EndOffset, extendSelection);
	public void MoveToStartOfLine(bool extendSelection) => MoveOrSelectTo(textArea.Document.GetLineByOffset(textArea.Caret.Offset).Offset, extendSelection);
	public void MoveToStartOfDocument(bool extendSelection) => MoveOrSelectTo(0, extendSelection);
	public void MoveToEndOfDocument(bool extendSelection) => MoveOrSelectTo(textArea.Document.TextLength, extendSelection);

	/// <summary>"Smart home": AvalonEdit has no separate command for jumping to the first
	/// non-whitespace character - move there directly, falling back to the true line start.</summary>
	public void MoveToHome(bool extendSelection)
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		var text = textArea.Document.GetText(line);
		int firstNonWhitespace = 0;
		while (firstNonWhitespace < text.Length && char.IsWhiteSpace(text[firstNonWhitespace]))
			firstNonWhitespace++;
		var target = line.Offset + (textArea.Caret.Offset == line.Offset + firstNonWhitespace ? 0 : firstNonWhitespace);
		MoveOrSelectTo(target, extendSelection);
	}

	public void GotoLine(int lineNumber)
	{
		var line = textArea.Document.GetLineByNumber(Math.Min(Math.Max(lineNumber + 1, 1), textArea.Document.LineCount));
		textArea.Caret.Offset = line.Offset;
		textArea.Caret.BringCaretToView();
	}

	public void MoveCurrentLineToTop() => view.ViewScroller.EnsureSpanVisible(CaretLineSpan(), EnsureSpanVisibleOptions.MinimumScroll);
	public void MoveCurrentLineToBottom() => view.ViewScroller.EnsureSpanVisible(CaretLineSpan(), EnsureSpanVisibleOptions.MinimumScroll);
	public void MoveToTopOfView(bool extendSelection) => MoveOrSelectTo(view.TextViewLines.FirstVisibleLine.Start.Position, extendSelection);
	public void MoveToBottomOfView(bool extendSelection) => MoveOrSelectTo(view.TextViewLines.LastVisibleLine.Start.Position, extendSelection);

	public void MoveToStartOfLineAfterWhiteSpace(bool extendSelection) => MoveToHome(extendSelection);

	public void MoveToStartOfNextLineAfterWhiteSpace(bool extendSelection)
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		if (line.NextLine == null)
			return;
		MoveOrSelectTo(FirstNonWhitespaceOffset(line.NextLine), extendSelection);
	}

	public void MoveToStartOfPreviousLineAfterWhiteSpace(bool extendSelection)
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		if (line.PreviousLine == null)
			return;
		MoveOrSelectTo(FirstNonWhitespaceOffset(line.PreviousLine), extendSelection);
	}

	public void MoveToLastNonWhiteSpaceCharacter(bool extendSelection)
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		var text = textArea.Document.GetText(line);
		int lastNonWhitespace = text.Length;
		while (lastNonWhitespace > 0 && char.IsWhiteSpace(text[lastNonWhitespace - 1]))
			lastNonWhitespace--;
		MoveOrSelectTo(line.Offset + lastNonWhitespace, extendSelection);
	}

	public void SwapCaretAndAnchor()
	{
		if (textArea.Selection.IsEmpty)
			return;
		var anchorOffset = textArea.Document.GetOffset(textArea.Selection.StartPosition.Location);
		var activeOffset = textArea.Document.GetOffset(textArea.Selection.EndPosition.Location);
		bool wasCaretAtStart = textArea.Caret.Offset == anchorOffset;
		var (newAnchor, newActive) = wasCaretAtStart ? (activeOffset, anchorOffset) : (anchorOffset, activeOffset);
		textArea.Selection = AvalonEditing.Selection.Create(textArea, newAnchor, newActive);
		textArea.Caret.Offset = newActive;
	}

	int FirstNonWhitespaceOffset(DocumentLine line)
	{
		var text = textArea.Document.GetText(line);
		int i = 0;
		while (i < text.Length && char.IsWhiteSpace(text[i]))
			i++;
		return line.Offset + i;
	}

	void MoveOrSelectTo(int offset, bool extendSelection)
	{
		if (extendSelection)
			textArea.Selection = AvalonEditing.Selection.Create(textArea, textArea.Caret.Offset, offset);
		textArea.Caret.Offset = offset;
	}

	SnapshotSpan CaretLineSpan()
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		return new SnapshotSpan(view.TextSnapshot, line.Offset, line.Length);
	}

	#endregion

	#region Selection

	public void SelectCurrentWord()
	{
		var offset = textArea.Caret.Offset;
		var start = TextUtilities.GetNextCaretPosition(textArea.Document, offset + 1, LogicalDirection.Backward, CaretPositioningMode.WordStart);
		var end = TextUtilities.GetNextCaretPosition(textArea.Document, offset, LogicalDirection.Forward, CaretPositioningMode.WordBorder);
		if (start >= 0 && end >= 0 && end > start)
			textArea.Selection = AvalonEditing.Selection.Create(textArea, start, end);
	}

	public void SelectEnclosing() { /* no syntax-aware enclosing-node model exists in this compatibility layer */ }
	public void SelectFirstChild() { }
	public void SelectNextSibling(bool extendSelection) { }
	public void SelectPreviousSibling(bool extendSelection) { }

	public void SelectLine(ITextViewLine viewLine, bool extendSelection)
	{
		var span = viewLine.ExtentIncludingLineBreak;
		if (extendSelection && !textArea.Selection.IsEmpty)
			textArea.Selection = AvalonEditing.Selection.Create(textArea, textArea.Caret.Offset, span.End.Position);
		else
			textArea.Selection = AvalonEditing.Selection.Create(textArea, span.Start.Position, span.End.Position);
		textArea.Caret.Offset = span.End.Position;
	}

	public void SelectAll() => Execute(ApplicationCommands.SelectAll, textArea);

	public void ExtendSelection(int newEnd) => textArea.Selection = AvalonEditing.Selection.Create(textArea, textArea.Selection.SurroundingSegment?.Offset ?? textArea.Caret.Offset, newEnd);

	public void MoveCaret(ITextViewLine textLine, double horizontalOffset, bool extendSelection)
		=> MoveOrSelectTo(textLine.GetInsertionBufferPositionFromXCoordinate(horizontalOffset).Position.Position, extendSelection);

	public void ResetSelection() => textArea.ClearSelection();

	#endregion

	#region Editing

	public bool Backspace()
	{
		if (!textArea.Selection.IsEmpty) {
			textArea.Selection.ReplaceSelectionWithText(string.Empty);
			return true;
		}
		var offset = textArea.Caret.Offset;
		if (offset == 0)
			return false;
		var previous = TextUtilities.GetNextCaretPosition(textArea.Document, offset, LogicalDirection.Backward, CaretPositioningMode.Normal);
		textArea.Document.Replace(previous, offset - previous, string.Empty);
		textArea.Caret.Offset = previous;
		return true;
	}

	public bool DeleteWordToRight()
	{
		var offset = textArea.Caret.Offset;
		var end = TextUtilities.GetNextCaretPosition(textArea.Document, offset, LogicalDirection.Forward, CaretPositioningMode.WordStart);
		if (end < 0 || end <= offset)
			return false;
		textArea.Document.Replace(offset, end - offset, string.Empty);
		return true;
	}

	public bool DeleteWordToLeft()
	{
		var offset = textArea.Caret.Offset;
		var start = TextUtilities.GetNextCaretPosition(textArea.Document, offset, LogicalDirection.Backward, CaretPositioningMode.WordStart);
		if (start < 0 || start >= offset)
			return false;
		textArea.Document.Replace(start, offset - start, string.Empty);
		textArea.Caret.Offset = start;
		return true;
	}

	public bool DeleteToEndOfLine()
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		textArea.Document.Replace(textArea.Caret.Offset, line.EndOffset - textArea.Caret.Offset, string.Empty);
		return true;
	}

	public bool DeleteToBeginningOfLine()
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		var length = textArea.Caret.Offset - line.Offset;
		textArea.Document.Replace(line.Offset, length, string.Empty);
		textArea.Caret.Offset = line.Offset;
		return true;
	}

	public bool DeleteBlankLines()
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		if (line.Length != 0)
			return false;
		var first = line;
		while (first.PreviousLine != null && first.PreviousLine.Length == 0)
			first = first.PreviousLine;
		var last = line;
		while (last.NextLine != null && last.NextLine.Length == 0)
			last = last.NextLine;
		textArea.Document.Replace(first.Offset, last.EndOffset - first.Offset, string.Empty);
		return true;
	}

	public bool DeleteHorizontalWhiteSpace()
	{
		var text = textArea.Document.Text;
		var offset = textArea.Caret.Offset;
		int start = offset, end = offset;
		while (start > 0 && (text[start - 1] == ' ' || text[start - 1] == '\t'))
			start--;
		while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
			end++;
		textArea.Document.Replace(start, end - start, string.Empty);
		return true;
	}

	/// <summary>The delimiter actually used by the line the caret is on, falling back to "\n"
	/// for a document with a single, still-delimiter-less line.</summary>
	string LineTerminator(DocumentLine line)
		=> line.DelimiterLength > 0 ? textArea.Document.GetText(line.Offset + line.Length, line.DelimiterLength) : "\n";

	public bool InsertNewLine()
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		return InsertText(LineTerminator(line));
	}

	public bool OpenLineAbove()
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		textArea.Document.Insert(line.Offset, LineTerminator(line));
		textArea.Caret.Offset = line.Offset;
		return true;
	}

	public bool OpenLineBelow()
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		var terminator = LineTerminator(line);
		textArea.Document.Insert(line.EndOffset, terminator);
		textArea.Caret.Offset = line.EndOffset + terminator.Length;
		return true;
	}

	public bool Indent() => IndentSelectedLines(indent: true);
	public bool Unindent() => IndentSelectedLines(indent: false);
	public bool IncreaseLineIndent() => Indent();
	public bool DecreaseLineIndent() => Unindent();

	bool IndentSelectedLines(bool indent)
	{
		var indentString = view.Options.GetOptionValue<bool>("Tabs/ConvertTabsToSpaces")
			? new string(' ', view.Options.GetOptionValue<int>("Tabs/IndentSize") is int size && size > 0 ? size : 4)
			: "\t";

		if (textArea.Selection.IsEmpty && indent) {
			textArea.Document.Insert(textArea.Caret.Offset, indentString);
			return true;
		}

		var segment = textArea.Selection.IsEmpty
			? (ICSharpCode.AvalonEdit.Document.ISegment)textArea.Document.GetLineByOffset(textArea.Caret.Offset)
			: textArea.Selection.SurroundingSegment;
		var firstLine = textArea.Document.GetLineByOffset(segment.Offset);
		var lastLine = textArea.Document.GetLineByOffset(Math.Max(segment.Offset, segment.EndOffset - 1));

		textArea.Document.BeginUpdate();
		try {
			for (var line = firstLine; ; line = line.NextLine) {
				if (indent) {
					textArea.Document.Insert(line.Offset, indentString);
				} else {
					var text = textArea.Document.GetText(line);
					int removeCount = 0;
					while (removeCount < text.Length && removeCount < indentString.Length && text[removeCount] == indentString[0])
						removeCount++;
					if (removeCount == 0 && text.Length > 0 && text[0] == '\t')
						removeCount = 1;
					if (removeCount > 0)
						textArea.Document.Remove(line.Offset, removeCount);
				}
				if (line == lastLine)
					break;
			}
		} finally {
			textArea.Document.EndUpdate();
		}
		return true;
	}

	public bool InsertText(string text)
	{
		if (!textArea.Selection.IsEmpty)
			textArea.Selection.ReplaceSelectionWithText(text);
		else
			textArea.Document.Insert(textArea.Caret.Offset, text);
		return true;
	}

	public bool InsertTextAsBox(string text, out VirtualSnapshotPoint boxStart, out VirtualSnapshotPoint boxEnd)
		=> throw new NotSupportedException("Box (rectangular) text insertion is not implemented - see AvalonTextSelection's stream-selection-only note.");

	public void SelectAndMoveCaret(VirtualSnapshotPoint anchorPoint, VirtualSnapshotPoint activePoint)
		=> SelectAndMoveCaret(anchorPoint, activePoint, TextSelectionMode.Stream, null);

	public void SelectAndMoveCaret(VirtualSnapshotPoint anchorPoint, VirtualSnapshotPoint activePoint, TextSelectionMode selectionMode)
		=> SelectAndMoveCaret(anchorPoint, activePoint, selectionMode, null);

	public void SelectAndMoveCaret(VirtualSnapshotPoint anchorPoint, VirtualSnapshotPoint activePoint, TextSelectionMode selectionMode, EnsureSpanVisibleOptions? scrollOptions)
	{
		view.Selection.Select(anchorPoint, activePoint);
		view.Caret.MoveTo(activePoint);
		if (scrollOptions.HasValue)
			view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(activePoint.Position, 0), scrollOptions.Value);
	}

	public bool InsertProvisionalText(string text)
	{
		// Best-effort IME preview: insert normally and remember the span so a later real
		// InsertText/composition-end can replace it; no live "ghost text" rendering exists here.
		var start = textArea.Caret.Offset;
		textArea.Document.Insert(start, text);
		ProvisionalCompositionSpan = view.TextSnapshot.CreateTrackingSpan(new Span(start, text.Length), SpanTrackingMode.EdgeExclusive);
		return true;
	}

	public bool Delete()
	{
		if (!textArea.Selection.IsEmpty) {
			textArea.Selection.ReplaceSelectionWithText(string.Empty);
			return true;
		}
		var offset = textArea.Caret.Offset;
		if (offset >= textArea.Document.TextLength)
			return false;
		var next = TextUtilities.GetNextCaretPosition(textArea.Document, offset, LogicalDirection.Forward, CaretPositioningMode.Normal);
		textArea.Document.Replace(offset, next - offset, string.Empty);
		return true;
	}

	public bool DeleteFullLine()
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		textArea.Document.Replace(line.Offset, line.TotalLength, string.Empty);
		return true;
	}

	public bool ReplaceSelection(string text)
	{
		textArea.Selection.ReplaceSelectionWithText(text);
		return true;
	}

	public bool TransposeCharacter()
	{
		var offset = textArea.Caret.Offset;
		if (offset == 0 || offset >= textArea.Document.TextLength)
			return false;
		var a = textArea.Document.GetCharAt(offset - 1);
		var b = textArea.Document.GetCharAt(offset);
		textArea.Document.Replace(offset - 1, 2, new string(new[] { b, a }));
		textArea.Caret.Offset = offset + 1;
		return true;
	}

	public bool TransposeWord() => false; // word transposition is not implemented

	public bool TransposeLine()
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		if (line.NextLine == null)
			return false;
		var next = line.NextLine;
		var thisText = textArea.Document.GetText(line);
		var nextText = textArea.Document.GetText(next);
		textArea.Document.BeginUpdate();
		try {
			textArea.Document.Replace(next.Offset, next.Length, thisText);
			textArea.Document.Replace(line.Offset, line.Length, nextText);
		} finally {
			textArea.Document.EndUpdate();
		}
		return true;
	}

	public bool MakeLowercase() => ApplyToSelectionOrWord(s => s.ToLowerInvariant());
	public bool MakeUppercase() => ApplyToSelectionOrWord(s => s.ToUpperInvariant());
	public bool Capitalize() => ApplyToSelectionOrWord(s => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant());

	public bool ToggleCase() => ApplyToSelectionOrWord(s => new string(s.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray()));

	bool ApplyToSelectionOrWord(Func<string, string> transform)
	{
		if (!textArea.Selection.IsEmpty) {
			textArea.Selection.ReplaceSelectionWithText(transform(textArea.Selection.GetText()));
		} else {
			SelectCurrentWord();
			if (!textArea.Selection.IsEmpty)
				textArea.Selection.ReplaceSelectionWithText(transform(textArea.Selection.GetText()));
		}
		return true;
	}

	public bool ReplaceText(Span replaceSpan, string replaceWith)
	{
		textArea.Document.Replace(replaceSpan.Start, replaceSpan.Length, replaceWith);
		return true;
	}

	public int ReplaceAllMatches(string searchText, string replaceText, bool matchCase, bool matchWholeWord, bool useRegularExpressions)
	{
		var options = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		var text = textArea.Document.Text;
		int count = 0;
		int searchFrom = 0;
		var builder = new System.Text.StringBuilder();
		int lastCopied = 0;
		while (true) {
			int index = text.IndexOf(searchText, searchFrom, options);
			if (index < 0)
				break;
			if (matchWholeWord && !IsWholeWordMatch(text, index, searchText.Length)) {
				searchFrom = index + 1;
				continue;
			}
			builder.Append(text, lastCopied, index - lastCopied);
			builder.Append(replaceText);
			lastCopied = index + searchText.Length;
			searchFrom = lastCopied;
			count++;
		}
		if (count > 0) {
			builder.Append(text, lastCopied, text.Length - lastCopied);
			textArea.Document.Text = builder.ToString();
		}
		return count;
	}

	static bool IsWholeWordMatch(string text, int index, int length)
	{
		bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
		if (index > 0 && IsWordChar(text[index - 1]))
			return false;
		if (index + length < text.Length && IsWordChar(text[index + length]))
			return false;
		return true;
	}

	public bool InsertFile(string filePath)
	{
		var content = System.IO.File.ReadAllText(filePath);
		return InsertText(content);
	}

	public bool Tabify() => ConvertLeadingWhitespace(spacesToTabs: true);
	public bool Untabify() => ConvertLeadingWhitespace(spacesToTabs: false);
	public bool ConvertSpacesToTabs() => Tabify();
	public bool ConvertTabsToSpaces() => Untabify();

	bool ConvertLeadingWhitespace(bool spacesToTabs)
	{
		int tabSize = view.Options.GetOptionValue<int>("Tabs/Size") is int size && size > 0 ? size : 4;
		textArea.Document.BeginUpdate();
		try {
			foreach (var line in textArea.Document.Lines.ToList()) {
				var text = textArea.Document.GetText(line);
				int i = 0;
				while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
					i++;
				var leading = text.Substring(0, i);
				var visualWidth = 0;
				foreach (var c in leading)
					visualWidth += c == '\t' ? tabSize - (visualWidth % tabSize) : 1;
				var replacement = spacesToTabs
					? new string('\t', visualWidth / tabSize) + new string(' ', visualWidth % tabSize)
					: new string(' ', visualWidth);
				if (replacement != leading)
					textArea.Document.Replace(line.Offset, leading.Length, replacement);
			}
		} finally {
			textArea.Document.EndUpdate();
		}
		return true;
	}

	public bool NormalizeLineEndings(string replacement)
	{
		var normalized = System.Text.RegularExpressions.Regex.Replace(textArea.Document.Text, "\r\n|\r|\n", replacement);
		if (normalized != textArea.Document.Text)
			textArea.Document.Text = normalized;
		return true;
	}

	public bool InsertFinalNewLine()
	{
		if (textArea.Document.TextLength > 0 && textArea.Document.GetCharAt(textArea.Document.TextLength - 1) != '\n')
			textArea.Document.Insert(textArea.Document.TextLength, "\n");
		return true;
	}

	public bool TrimTrailingWhiteSpace()
	{
		textArea.Document.BeginUpdate();
		try {
			foreach (var line in textArea.Document.Lines.ToList()) {
				var text = textArea.Document.GetText(line);
				int end = text.Length;
				while (end > 0 && (text[end - 1] == ' ' || text[end - 1] == '\t'))
					end--;
				if (end < text.Length)
					textArea.Document.Replace(line.Offset + end, text.Length - end, string.Empty);
			}
		} finally {
			textArea.Document.EndUpdate();
		}
		return true;
	}

	public bool DuplicateSelection()
	{
		if (textArea.Selection.IsEmpty) {
			var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
			var terminator = LineTerminator(line);
			textArea.Document.Insert(line.EndOffset, terminator + textArea.Document.GetText(line));
		} else {
			var segment = textArea.Selection.SurroundingSegment;
			textArea.Document.Insert(segment.EndOffset, textArea.Document.GetText(segment.Offset, segment.Length));
		}
		return true;
	}

	public bool MoveSelectedLinesUp() => MoveSelectedLines(up: true);
	public bool MoveSelectedLinesDown() => MoveSelectedLines(up: false);

	/// <summary>Swaps the two adjacent chunks [block][neighbor] (moving down) or
	/// [neighbor][block] (moving up) as whole text, each including its own trailing line
	/// delimiter (if any) - so delimiters travel with their line instead of needing separate
	/// bookkeeping for "whose delimiter is this now".</summary>
	bool MoveSelectedLines(bool up)
	{
		var segment = textArea.Selection.IsEmpty
			? (ICSharpCode.AvalonEdit.Document.ISegment)textArea.Document.GetLineByOffset(textArea.Caret.Offset)
			: textArea.Selection.SurroundingSegment;
		var firstLine = textArea.Document.GetLineByOffset(segment.Offset);
		var lastLine = textArea.Document.GetLineByOffset(Math.Max(segment.Offset, segment.EndOffset - 1));
		var neighbor = up ? firstLine.PreviousLine : lastLine.NextLine;
		if (neighbor == null)
			return false;

		var rangeStart = up ? neighbor.Offset : firstLine.Offset;
		var rangeEnd = up ? lastLine.EndOffset + lastLine.DelimiterLength : neighbor.EndOffset + neighbor.DelimiterLength;
		var blockChunk = textArea.Document.GetText(firstLine.Offset, lastLine.EndOffset + lastLine.DelimiterLength - firstLine.Offset);
		var neighborChunk = textArea.Document.GetText(neighbor.Offset, neighbor.EndOffset + neighbor.DelimiterLength - neighbor.Offset);

		textArea.Document.Replace(rangeStart, rangeEnd - rangeStart, up ? blockChunk + neighborChunk : neighborChunk + blockChunk);
		return true;
	}

	#endregion

	#region Clipboard

	public bool CopySelection() { Execute(ApplicationCommands.Copy, textArea); return true; }
	public bool CutSelection() { Execute(ApplicationCommands.Cut, textArea); return true; }
	public bool Paste() { Execute(ApplicationCommands.Paste, textArea); return true; }

	public bool CutFullLine()
	{
		var line = textArea.Document.GetLineByOffset(textArea.Caret.Offset);
		var text = textArea.Document.GetText(line.Offset, line.TotalLength);
		System.Windows.Clipboard.SetText(text);
		textArea.Document.Replace(line.Offset, line.TotalLength, string.Empty);
		return true;
	}

	#endregion

	#region Scrolling / zoom

	public void ScrollUpAndMoveCaretIfNecessary() => view.ViewScroller.ScrollViewportVerticallyByLine(ScrollDirection.Up);
	public void ScrollDownAndMoveCaretIfNecessary() => view.ViewScroller.ScrollViewportVerticallyByLine(ScrollDirection.Down);
	public void ScrollPageUp() => view.ViewScroller.ScrollViewportVerticallyByPixels(view.ViewportHeight);
	public void ScrollPageDown() => view.ViewScroller.ScrollViewportVerticallyByPixels(-view.ViewportHeight);
	public void ScrollColumnLeft() => view.ViewScroller.ScrollViewportHorizontallyByPixels(-view.LineHeight);
	public void ScrollColumnRight() => view.ViewScroller.ScrollViewportHorizontallyByPixels(view.LineHeight);
	public void ScrollLineBottom() => view.ViewScroller.EnsureSpanVisible(CaretLineSpan(), EnsureSpanVisibleOptions.None);
	public void ScrollLineTop() => view.ViewScroller.EnsureSpanVisible(CaretLineSpan(), EnsureSpanVisibleOptions.None);
	public void ScrollLineCenter() => view.ViewScroller.EnsureSpanVisible(CaretLineSpan(), EnsureSpanVisibleOptions.AlwaysCenter);

	public void AddBeforeTextBufferChangePrimitive() { }
	public void AddAfterTextBufferChangePrimitive() { }

	double zoomLevel = 100.0;
	public void ZoomIn() => ZoomTo(zoomLevel + 10);
	public void ZoomOut() => ZoomTo(zoomLevel - 10);
	public void ZoomTo(double zoomLevel)
	{
		this.zoomLevel = Math.Max(20, Math.Min(400, zoomLevel));
		textArea.TextView.LayoutTransform = new System.Windows.Media.ScaleTransform(this.zoomLevel / 100.0, this.zoomLevel / 100.0);
	}

	#endregion

	public string GetWhitespaceForVirtualSpace(VirtualSnapshotPoint point)
	{
		if (!Options.GetOptionValue<bool>("Tabs/ConvertTabsToSpaces") == false)
			return new string(' ', point.VirtualSpaces);
		return new string(' ', point.VirtualSpaces);
	}
}
