// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextCaret over AvalonEdit's TextArea.Caret (vs-editor-api.md section 23). Caret-rectangle
// geometry and ContainingTextViewLine are backed by AvalonTextView.TextViewLines - see that
// class and AvalonTextViewLine for the underlying VisualLine/TextLine mapping.

using System;

using AvalonEditing = ICSharpCode.AvalonEdit.Editing;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonTextCaret : ITextCaret
{
	readonly AvalonTextView view;
	readonly AvalonEditing.TextArea textArea;

	public AvalonTextCaret(AvalonTextView view, AvalonEditing.TextArea textArea)
	{
		this.view = view ?? throw new ArgumentNullException(nameof(view));
		this.textArea = textArea ?? throw new ArgumentNullException(nameof(textArea));
		textArea.Caret.PositionChanged += (sender, e) => PositionChanged?.Invoke(this,
			new CaretPositionChangedEventArgs(view, Position, Position));
	}

	public CaretPosition Position
	{
		get
		{
			var snapshot = view.TextBuffer.CurrentSnapshot;
			int offset = Math.Min(Math.Max(textArea.Caret.Offset, 0), snapshot.Length);
			var point = new SnapshotPoint(snapshot, offset);
			var mappingPoint = view.BufferGraph.CreateMappingPoint(point, PointTrackingMode.Positive);
			return new CaretPosition(new VirtualSnapshotPoint(point), mappingPoint, PositionAffinity.Successor);
		}
	}

	public bool OverwriteMode => textArea.OverstrikeMode;

	public bool InVirtualSpace => textArea.Caret.IsInVirtualSpace;

	public bool IsHidden { get; set; }

	public ITextViewLine ContainingTextViewLine => view.GetTextViewLineContainingBufferPosition(Position.BufferPosition);

	public double Left => ContainingTextViewLine.GetCharacterBounds(Position.VirtualBufferPosition).Left;
	public double Width => ContainingTextViewLine.GetCharacterBounds(Position.VirtualBufferPosition).Width;
	public double Right => Left + Width;
	public double Top => ContainingTextViewLine.Top;
	public double Height => ContainingTextViewLine.Height;
	public double Bottom => Top + Height;

	public event EventHandler<CaretPositionChangedEventArgs> PositionChanged;

	public void EnsureVisible() => textArea.Caret.BringCaretToView();

	public CaretPosition MoveTo(SnapshotPoint bufferPosition) => MoveTo(bufferPosition, PositionAffinity.Successor, true);

	public CaretPosition MoveTo(SnapshotPoint bufferPosition, PositionAffinity caretAffinity) => MoveTo(bufferPosition, caretAffinity, true);

	public CaretPosition MoveTo(SnapshotPoint bufferPosition, PositionAffinity caretAffinity, bool captureHorizontalPosition)
	{
		textArea.Caret.Offset = bufferPosition.Position;
		return Position;
	}

	public CaretPosition MoveTo(VirtualSnapshotPoint bufferPosition) => MoveTo(bufferPosition.Position);

	public CaretPosition MoveTo(VirtualSnapshotPoint bufferPosition, PositionAffinity caretAffinity) => MoveTo(bufferPosition.Position, caretAffinity);

	public CaretPosition MoveTo(VirtualSnapshotPoint bufferPosition, PositionAffinity caretAffinity, bool captureHorizontalPosition)
		=> MoveTo(bufferPosition.Position, caretAffinity, captureHorizontalPosition);

	public CaretPosition MoveTo(ITextViewLine textLine) => MoveTo(textLine.Start);

	public CaretPosition MoveTo(ITextViewLine textLine, double xCoordinate)
		=> MoveTo(textLine.GetInsertionBufferPositionFromXCoordinate(xCoordinate));

	public CaretPosition MoveTo(ITextViewLine textLine, double xCoordinate, bool captureHorizontalPosition)
		=> MoveTo(textLine.GetInsertionBufferPositionFromXCoordinate(xCoordinate), PositionAffinity.Successor, captureHorizontalPosition);

	public CaretPosition MoveToPreferredCoordinates() => Position;

	public CaretPosition MoveToNextCaretPosition()
	{
		var snapshot = view.TextBuffer.CurrentSnapshot;
		var next = Math.Min(textArea.Caret.Offset + 1, snapshot.Length);
		return MoveTo(new SnapshotPoint(snapshot, next));
	}

	public CaretPosition MoveToPreviousCaretPosition()
	{
		var previous = Math.Max(textArea.Caret.Offset - 1, 0);
		return MoveTo(new SnapshotPoint(view.TextBuffer.CurrentSnapshot, previous));
	}
}
