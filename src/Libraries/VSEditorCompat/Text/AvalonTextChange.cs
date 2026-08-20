// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// One VS-style text change, mapped from an AvalonEdit TextChangeEventArgs. The AvalonEdit
// change carries the offset and the removed/inserted text; everything the VS ITextChange
// exposes (old/new spans, positions, delta, line-count delta) is derived from those.

using System;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>A single text change between two snapshots, in VS editor terms.</summary>
public sealed class AvalonTextChange : ITextChange
{
	readonly string oldText;
	readonly string newText;
	readonly int lineCountDelta;

	public AvalonTextChange(int oldPosition, string oldText, int newPosition, string newText, int lineCountDelta)
	{
		OldPosition = oldPosition;
		this.oldText = oldText ?? string.Empty;
		NewPosition = newPosition;
		this.newText = newText ?? string.Empty;
		this.lineCountDelta = lineCountDelta;
	}

	/// <summary>Maps one AvalonEdit change into a VS change for the same text.</summary>
	public static AvalonTextChange FromTextChangeEventArgs(TextChangeEventArgs e)
	{
		var oldText = e.RemovedText.Text;
		var newText = e.InsertedText.Text;
		return new AvalonTextChange(e.Offset, oldText, e.Offset, newText,
			CountLines(newText) - CountLines(oldText));
	}

	static int CountLines(string text)
	{
		int count = 0;
		foreach (char c in text)
			if (c == '\n')
				count++;
		return count;
	}

	public int OldPosition { get; }

	public int NewPosition { get; }

	public int OldLength => oldText.Length;

	public int NewLength => newText.Length;

	public int OldEnd => OldPosition + OldLength;

	public int NewEnd => NewPosition + NewLength;

	public Span OldSpan => new Span(OldPosition, OldLength);

	public Span NewSpan => new Span(NewPosition, NewLength);

	public string OldText => oldText;

	public string NewText => newText;

	public int Delta => NewLength - OldLength;

	public int LineCountDelta => lineCountDelta;

	public override string ToString()
		=> $"[{OldSpan}] '{oldText}' -> '{newText}'";
}
