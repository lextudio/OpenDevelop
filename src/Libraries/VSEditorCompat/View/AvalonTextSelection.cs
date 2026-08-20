// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextSelection over AvalonEdit's TextArea.Selection (vs-editor-api.md section 24). Starts with
// stream selection only, as the doc recommends - box/rectangle selection has its own virtual-
// space semantics that AvalonEdit's RectangleSelection and the VS box-selection model do not line
// up on closely enough to fake here; Mode reports/accepts Stream only for now.

using System;
using System.Collections.ObjectModel;
using System.Linq;

using AvalonEditing = ICSharpCode.AvalonEdit.Editing;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonTextSelection : ITextSelection
{
	readonly AvalonTextView view;
	readonly AvalonEditing.TextArea textArea;

	public AvalonTextSelection(AvalonTextView view, AvalonEditing.TextArea textArea)
	{
		this.view = view ?? throw new ArgumentNullException(nameof(view));
		this.textArea = textArea ?? throw new ArgumentNullException(nameof(textArea));
		textArea.SelectionChanged += (sender, e) => SelectionChanged?.Invoke(this, EventArgs.Empty);
	}

	public ITextView TextView => view;

	public bool IsEmpty => textArea.Selection.IsEmpty;

	public bool IsActive { get; set; } = true;

	public bool ActivationTracksFocus { get; set; }

	public TextSelectionMode Mode { get; set; } = TextSelectionMode.Stream;

	// Best-effort: AvalonEdit's Selection does not expose direction directly, so approximate it
	// from which end of the segment the caret currently sits at (the active end).
	public bool IsReversed => !IsEmpty && textArea.Caret.Offset == textArea.Selection.SurroundingSegment?.Offset;

	public NormalizedSnapshotSpanCollection SelectedSpans
	{
		get
		{
			var snapshot = view.TextBuffer.CurrentSnapshot;
			var segment = textArea.Selection.SurroundingSegment;
			if (segment == null)
				return NormalizedSnapshotSpanCollection.Empty;
			return new NormalizedSnapshotSpanCollection(new SnapshotSpan(snapshot, segment.Offset, segment.Length));
		}
	}

	public ReadOnlyCollection<VirtualSnapshotSpan> VirtualSelectedSpans
		=> new(SelectedSpans.Select(span => new VirtualSnapshotSpan(span)).ToList());

	public VirtualSnapshotSpan StreamSelectionSpan
	{
		get
		{
			var spans = SelectedSpans;
			if (spans.Count > 0)
				return new VirtualSnapshotSpan(spans[0]);
			var caretPoint = new VirtualSnapshotPoint(new SnapshotPoint(view.TextBuffer.CurrentSnapshot, textArea.Caret.Offset));
			return new VirtualSnapshotSpan(caretPoint, caretPoint);
		}
	}

	public VirtualSnapshotPoint AnchorPoint => IsReversed ? End : Start;

	public VirtualSnapshotPoint ActivePoint => IsReversed ? Start : End;

	public VirtualSnapshotPoint Start
	{
		get
		{
			var segment = textArea.Selection.SurroundingSegment;
			var offset = segment?.Offset ?? textArea.Caret.Offset;
			return new VirtualSnapshotPoint(new SnapshotPoint(view.TextBuffer.CurrentSnapshot, offset));
		}
	}

	public VirtualSnapshotPoint End
	{
		get
		{
			var segment = textArea.Selection.SurroundingSegment;
			var offset = segment?.EndOffset ?? textArea.Caret.Offset;
			return new VirtualSnapshotPoint(new SnapshotPoint(view.TextBuffer.CurrentSnapshot, offset));
		}
	}

	public event EventHandler SelectionChanged;

	public void Select(SnapshotSpan selectionSpan, bool isReversed)
	{
		var start = isReversed ? selectionSpan.End : selectionSpan.Start;
		var end = isReversed ? selectionSpan.Start : selectionSpan.End;
		textArea.Selection = AvalonEditing.Selection.Create(textArea, start.Position, end.Position);
		textArea.Caret.Offset = end.Position;
	}

	public void Select(VirtualSnapshotPoint anchorPoint, VirtualSnapshotPoint activePoint)
	{
		textArea.Selection = AvalonEditing.Selection.Create(textArea, anchorPoint.Position.Position, activePoint.Position.Position);
		textArea.Caret.Offset = activePoint.Position.Position;
	}

	public void Clear() => textArea.ClearSelection();

	public VirtualSnapshotSpan? GetSelectionOnTextViewLine(ITextViewLine line)
	{
		if (IsEmpty)
			return null;
		var overlap = line.ExtentIncludingLineBreak.Intersection(StreamSelectionSpan.SnapshotSpan);
		return overlap.HasValue ? new VirtualSnapshotSpan(overlap.Value) : (VirtualSnapshotSpan?)null;
	}
}
