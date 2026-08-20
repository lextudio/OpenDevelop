// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextViewLine over one AvalonEdit VisualLine + one of its (possibly several, if word-wrapped)
// WPF TextLines (vs-editor-api.md sections 22/64, upgraded from NotSupportedException once real
// layout coordinates became available to verify). AvalonEdit's soft-wrap model keeps ONE
// VisualLine per document line with multiple internal TextLines for the wrapped rows; VS's model
// instead treats every wrapped row as its own ITextViewLine - so this class wraps a
// (VisualLine, TextLine) pair, not the VisualLine alone, to get VS's per-row semantics.
//
// Folding/collapsing (a VisualLine spanning more than one DocumentLine via AvalonEdit's
// FoldingManager/CollapsedLineSection) needs no special-casing here: the offset math below never
// assumes FirstDocumentLine == LastDocumentLine - it always derives document offsets from
// FirstDocumentLine.Offset plus VisualLine.GetRelativeOffset(visualColumn), and
// VisualLine.CalculateOffsets already guarantees GetRelativeOffset(VisualLength) equals
// LastDocumentLine.EndOffset - FirstDocumentLine.Offset regardless of folding. Verified against
// a real fold in tests/OpenDevelop.IntegrationTests/VSEditorViewIntegrationTests.cs
// (Folding_Merges_The_Folded_DocumentLines_Into_One_ITextViewLine_With_The_Combined_Extent).
//
// Coordinates are reported relative to the *viewport*, not the document, matching VS's
// convention for ITextViewLine.Top/Left etc. (an adornment layer positions UI elements directly
// against these numbers) - VisualLine.VisualTop is document-relative, so the view's current
// vertical/horizontal scroll offset is subtracted here.

using System;
using System.Collections.ObjectModel;
using System.Linq;

using AvalonRendering = ICSharpCode.AvalonEdit.Rendering;
using TextLine = System.Windows.Media.TextFormatting.TextLine;
using VisualYPosition = ICSharpCode.AvalonEdit.Rendering.VisualYPosition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Formatting;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonTextViewLine : ITextViewLine
{
	readonly AvalonTextView view;
	readonly AvalonRendering.VisualLine visualLine;
	readonly TextLine textLine;
	readonly int startVisualColumn;
	readonly int endVisualColumn;
	readonly int documentStartOffset;
	readonly int documentEndOffset;

	internal AvalonTextViewLine(AvalonTextView view, AvalonRendering.VisualLine visualLine, TextLine textLine)
	{
		this.view = view ?? throw new ArgumentNullException(nameof(view));
		this.visualLine = visualLine ?? throw new ArgumentNullException(nameof(visualLine));
		this.textLine = textLine ?? throw new ArgumentNullException(nameof(textLine));

		startVisualColumn = visualLine.GetTextLineVisualStartColumn(textLine);
		endVisualColumn = startVisualColumn + textLine.Length;
		var lineOffset = visualLine.FirstDocumentLine.Offset;
		documentStartOffset = lineOffset + visualLine.GetRelativeOffset(startVisualColumn);
		documentEndOffset = lineOffset + visualLine.GetRelativeOffset(endVisualColumn);
	}

	internal bool IsFirstTextLine => visualLine.TextLines[0] == textLine;

	internal bool IsLastTextLine => visualLine.TextLines[visualLine.TextLines.Count - 1] == textLine;

	public ITextSnapshot Snapshot => view.TextSnapshot;

	public object IdentityTag => textLine;

	public bool IsFirstTextViewLineForSnapshotLine => IsFirstTextLine;

	public bool IsLastTextViewLineForSnapshotLine => IsLastTextLine;

	public double Baseline => textLine.Baseline;

	public SnapshotSpan Extent => new(Snapshot, documentStartOffset, documentEndOffset - documentStartOffset);

	public IMappingSpan ExtentAsMappingSpan => view.BufferGraph.CreateMappingSpan(Extent, SpanTrackingMode.EdgeExclusive);

	public SnapshotSpan ExtentIncludingLineBreak
	{
		get
		{
			if (!IsLastTextLine)
				return Extent;
			var delimiterLength = visualLine.LastDocumentLine.DelimiterLength;
			return new SnapshotSpan(Snapshot, documentStartOffset, documentEndOffset - documentStartOffset + delimiterLength);
		}
	}

	public IMappingSpan ExtentIncludingLineBreakAsMappingSpan => view.BufferGraph.CreateMappingSpan(ExtentIncludingLineBreak, SpanTrackingMode.EdgeExclusive);

	public SnapshotPoint Start => Extent.Start;

	public int Length => Extent.Length;

	public int LengthIncludingLineBreak => ExtentIncludingLineBreak.Length;

	public SnapshotPoint End => Extent.End;

	public SnapshotPoint EndIncludingLineBreak => ExtentIncludingLineBreak.End;

	public int LineBreakLength => LengthIncludingLineBreak - Length;

	double RowTop => visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.LineTop) - view.ViewportTop;

	public double Top => RowTop;

	public double Height => textLine.Height;

	public double Bottom => Top + Height;

	public double TextTop => visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.TextTop) - view.ViewportTop;

	public double TextBottom => visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.TextBottom) - view.ViewportTop;

	public double TextHeight => TextBottom - TextTop;

	public double Left => visualLine.GetTextLineVisualXPosition(textLine, startVisualColumn) - view.ViewportLeft;

	public double Right => Left + Width;

	public double Width => visualLine.GetTextLineVisualXPosition(textLine, endVisualColumn) - visualLine.GetTextLineVisualXPosition(textLine, startVisualColumn);

	public double TextLeft => Left;

	public double TextRight => Right;

	public double TextWidth => Width;

	public double EndOfLineWidth => IsLastTextLine && visualLine.LastDocumentLine.DelimiterLength > 0 ? view.LineHeight / 2 : 0;

	public double VirtualSpaceWidth => view.LineHeight / 2;

	public bool IsValid => !visualLine.IsDisposed;

	public LineTransform LineTransform => new(1.0);

	public LineTransform DefaultLineTransform => new(1.0);

	public VisibilityState VisibilityState
	{
		get
		{
			if (Bottom <= 0 || Top >= view.ViewportHeight)
				return VisibilityState.Hidden;
			if (Top >= 0 && Bottom <= view.ViewportHeight)
				return VisibilityState.FullyVisible;
			return VisibilityState.PartiallyVisible;
		}
	}

	public double DeltaY => 0;

	public TextViewLineChange Change => TextViewLineChange.NewOrReformatted;

	public bool ContainsBufferPosition(SnapshotPoint bufferPosition)
		=> bufferPosition.Position >= documentStartOffset &&
			(bufferPosition.Position < documentEndOffset || (IsLastTextLine && bufferPosition.Position <= ExtentIncludingLineBreak.End.Position));

	public bool IntersectsBufferSpan(SnapshotSpan bufferSpan)
		=> bufferSpan.OverlapsWith(Extent) || bufferSpan.Start == Extent.End || bufferSpan.End == Extent.Start;

	public SnapshotSpan GetTextElementSpan(SnapshotPoint bufferPosition)
	{
		if (!ContainsBufferPosition(bufferPosition))
			throw new ArgumentOutOfRangeException(nameof(bufferPosition));
		// No text-element grouping (surrogate pairs, combining marks, etc.) is implemented -
		// every element is exactly one UTF-16 code unit wide.
		return new SnapshotSpan(Snapshot, bufferPosition.Position, 1);
	}

	int VisualColumnOf(int documentOffset) => visualLine.GetVisualColumn(documentOffset - visualLine.FirstDocumentLine.Offset);

	public TextBounds GetCharacterBounds(SnapshotPoint bufferPosition) => GetCharacterBounds(new VirtualSnapshotPoint(bufferPosition));

	public TextBounds GetCharacterBounds(VirtualSnapshotPoint bufferPosition)
	{
		var offset = bufferPosition.Position.Position;
		var visualColumn = VisualColumnOf(offset) + bufferPosition.VirtualSpaces;
		var left = visualLine.GetTextLineVisualXPosition(textLine, visualColumn) - view.ViewportLeft;
		var nextLeft = visualLine.GetTextLineVisualXPosition(textLine, visualColumn + 1) - view.ViewportLeft;
		return new TextBounds(left, Top, Math.Max(0, nextLeft - left), Height, TextTop, TextHeight);
	}

	public TextBounds GetExtendedCharacterBounds(SnapshotPoint bufferPosition) => GetCharacterBounds(bufferPosition);

	public TextBounds GetExtendedCharacterBounds(VirtualSnapshotPoint bufferPosition) => GetCharacterBounds(bufferPosition);

	public Collection<TextBounds> GetNormalizedTextBounds(SnapshotSpan bufferSpan)
	{
		var overlap = bufferSpan.Intersection(Extent);
		if (overlap == null)
			return new Collection<TextBounds>();
		var startColumn = VisualColumnOf(overlap.Value.Start.Position);
		var endColumn = VisualColumnOf(overlap.Value.End.Position);
		var left = visualLine.GetTextLineVisualXPosition(textLine, startColumn) - view.ViewportLeft;
		var right = visualLine.GetTextLineVisualXPosition(textLine, endColumn) - view.ViewportLeft;
		return new Collection<TextBounds> { new(left, Top, Math.Max(0, right - left), Height, TextTop, TextHeight) };
	}

	public SnapshotPoint? GetBufferPositionFromXCoordinate(double xCoordinate) => GetBufferPositionFromXCoordinate(xCoordinate, textOnly: false);

	public SnapshotPoint? GetBufferPositionFromXCoordinate(double xCoordinate, bool textOnly)
	{
		if (textOnly && (xCoordinate < Left || xCoordinate > Right))
			return null;
		var hit = textLine.GetCharacterHitFromDistance(xCoordinate - Left + visualLine.GetTextLineVisualXPosition(textLine, startVisualColumn));
		var visualColumn = hit.FirstCharacterIndex + hit.TrailingLength;
		var offset = visualLine.FirstDocumentLine.Offset + visualLine.GetRelativeOffset(visualColumn);
		if (offset < documentStartOffset || offset > documentEndOffset)
			return null;
		return new SnapshotPoint(Snapshot, offset);
	}

	public VirtualSnapshotPoint GetVirtualBufferPositionFromXCoordinate(double xCoordinate)
	{
		var point = GetBufferPositionFromXCoordinate(xCoordinate, textOnly: false);
		if (point.HasValue)
			return new VirtualSnapshotPoint(point.Value);
		// Beyond the last character: report virtual space proportional to the overshoot.
		var overshoot = Math.Max(0, xCoordinate - Right);
		var virtualSpaces = (int)Math.Round(overshoot / Math.Max(1, VirtualSpaceWidth));
		return new VirtualSnapshotPoint(End, virtualSpaces);
	}

	public VirtualSnapshotPoint GetInsertionBufferPositionFromXCoordinate(double xCoordinate) => GetVirtualBufferPositionFromXCoordinate(xCoordinate);

	public TextBounds? GetAdornmentBounds(object identityTag) => null;

	public ReadOnlyCollection<object> GetAdornmentTags(object identityTag) => new(Array.Empty<object>());
}
