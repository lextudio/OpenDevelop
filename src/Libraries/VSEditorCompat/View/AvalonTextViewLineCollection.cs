// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextViewLineCollection over the AvalonEdit TextView's currently laid-out VisualLines
// (vs-editor-api.md sections 22/64). Requires EnsureVisualLines() to have run - callers go
// through AvalonTextView.TextViewLines, which does that first.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using AvalonRendering = ICSharpCode.AvalonEdit.Rendering;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonTextViewLineCollection : ReadOnlyCollection<ITextViewLine>, ITextViewLineCollection
{
	readonly AvalonTextView view;

	internal AvalonTextViewLineCollection(AvalonTextView view, AvalonRendering.TextView textView)
		: base(BuildLines(view, textView))
	{
		this.view = view;
	}

	static IList<ITextViewLine> BuildLines(AvalonTextView view, AvalonRendering.TextView textView)
	{
		var lines = new List<ITextViewLine>();
		foreach (var visualLine in textView.VisualLines) {
			foreach (var textLine in visualLine.TextLines)
				lines.Add(new AvalonTextViewLine(view, visualLine, textLine));
		}
		return lines;
	}

	public bool IsValid => Count > 0;

	public SnapshotSpan FormattedSpan => Count == 0
		? new SnapshotSpan(view.TextSnapshot, 0, 0)
		: new SnapshotSpan(this[0].Start, this[Count - 1].EndIncludingLineBreak);

	public ITextViewLine FirstVisibleLine => this.FirstOrDefault(line => line.VisibilityState != VisibilityState.Hidden) ?? this.FirstOrDefault();

	public ITextViewLine LastVisibleLine => this.LastOrDefault(line => line.VisibilityState != VisibilityState.Hidden) ?? this.LastOrDefault();

	public bool ContainsBufferPosition(SnapshotPoint bufferPosition) => this.Any(line => line.ContainsBufferPosition(bufferPosition));

	public bool IntersectsBufferSpan(SnapshotSpan bufferSpan) => this.Any(line => line.IntersectsBufferSpan(bufferSpan));

	public ITextViewLine GetTextViewLineContainingBufferPosition(SnapshotPoint bufferPosition)
		=> this.FirstOrDefault(line => line.ContainsBufferPosition(bufferPosition));

	public ITextViewLine GetTextViewLineContainingYCoordinate(double y)
		=> this.FirstOrDefault(line => y >= line.Top && y < line.Bottom);

	public Collection<ITextViewLine> GetTextViewLinesIntersectingSpan(SnapshotSpan bufferSpan)
		=> new(this.Where(line => line.IntersectsBufferSpan(bufferSpan)).ToList());

	public SnapshotSpan GetTextElementSpan(SnapshotPoint bufferPosition)
		=> GetTextViewLineContainingBufferPosition(bufferPosition)?.GetTextElementSpan(bufferPosition)
			?? throw new ArgumentOutOfRangeException(nameof(bufferPosition));

	public TextBounds GetCharacterBounds(SnapshotPoint bufferPosition)
		=> GetTextViewLineContainingBufferPosition(bufferPosition)?.GetCharacterBounds(bufferPosition)
			?? throw new ArgumentOutOfRangeException(nameof(bufferPosition));

	public Collection<TextBounds> GetNormalizedTextBounds(SnapshotSpan bufferSpan)
	{
		var result = new Collection<TextBounds>();
		foreach (var line in this) {
			foreach (var bounds in line.GetNormalizedTextBounds(bufferSpan))
				result.Add(bounds);
		}
		return result;
	}

	public int GetIndexOfTextLine(ITextViewLine textLine) => IndexOf(textLine);
}
