// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// An immutable VS snapshot over AvalonEdit's immutable ITextSource produced by
// TextDocument.CreateSnapshot() (thread-safe - vs-editor-api.md sections 9 and 17). Old
// snapshots stay valid because their underlying ITextSource is immutable, which is the property
// Roslyn-style background analysis relies on. Line metadata is computed lazily into a small
// snapshot-local start-offset table so no code path reaches back into the live TextDocument.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

/// <summary>An immutable snapshot of an AvalonEdit document, in VS editor terms.</summary>
public sealed class AvalonTextSnapshot : ITextSnapshot
{
	internal readonly ITextSource Source;
	readonly AvalonTextBuffer buffer;
	readonly AvalonTextVersion version;
	int[] lineStarts;

	internal AvalonTextSnapshot(AvalonTextBuffer buffer, ITextSource source, AvalonTextVersion version)
	{
		this.buffer = buffer;
		Source = source;
		this.version = version;
	}

	/// <summary>The snapshot text this immutable view wraps (AvalonEdit rope snapshot).</summary>
	internal string Text => Source.Text;

	public ITextBuffer TextBuffer => buffer;

	public ITextVersion Version => version;

	public IContentType ContentType => buffer.ContentType;

	public int Length => Source.TextLength;

	public char this[int position] => Source.GetCharAt(position);

	public int LineCount => EnsureLineStarts().Length;

	public IEnumerable<ITextSnapshotLine> Lines
	{
		get
		{
			var count = LineCount;
			for (int i = 0; i < count; i++)
				yield return GetLineFromLineNumber(i);
		}
	}

	public ITextSnapshotLine GetLineFromLineNumber(int lineNumber)
	{
		var starts = EnsureLineStarts();
		if (lineNumber < 0 || lineNumber >= starts.Length)
			throw new ArgumentOutOfRangeException(nameof(lineNumber));
		var start = starts[lineNumber];
		int length;
		int lineBreakLength;
		if (lineNumber == starts.Length - 1) {
			length = Length - start;
			lineBreakLength = 0;
		} else {
			length = starts[lineNumber + 1] - 1 - start;
			lineBreakLength = 1;
		}
		return new AvalonTextSnapshotLine(this, lineNumber, start, length, lineBreakLength);
	}

	public ITextSnapshotLine GetLineFromPosition(int position)
		=> GetLineFromLineNumber(GetLineNumberFromPosition(position));

	public int GetLineNumberFromPosition(int position)
	{
		if (position < 0 || position > Length)
			throw new ArgumentOutOfRangeException(nameof(position));
		var starts = EnsureLineStarts();
		int low = 0;
		int high = starts.Length - 1;
		while (low < high) {
			int mid = (low + high + 1) / 2;
			if (starts[mid] <= position)
				low = mid;
			else
				high = mid - 1;
		}
		return low;
	}

	public string GetText() => Source.Text;

	public string GetText(int startIndex, int length)
	{
		if (length == 0)
			return string.Empty;
		return Source.GetText(startIndex, length);
	}

	public string GetText(Span span) => GetText(span.Start, span.Length);

	public char[] ToCharArray(int startIndex, int length)
	{
		var result = new char[length];
		CopyTo(startIndex, result, 0, length);
		return result;
	}

	public void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count)
	{
		if (destination == null)
			throw new ArgumentNullException(nameof(destination));
		for (int i = 0; i < count; i++)
			destination[destinationIndex + i] = Source.GetCharAt(sourceIndex + i);
	}

	public void Write(TextWriter writer) => Source.WriteTextTo(writer ?? throw new ArgumentNullException(nameof(writer)));

	public void Write(TextWriter writer, Span span)
	{
		if (writer == null)
			throw new ArgumentNullException(nameof(writer));
		Source.WriteTextTo(writer, span.Start, span.Length);
	}

	public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode)
		=> version.CreateTrackingPoint(position, trackingMode);

	public ITrackingPoint CreateTrackingPoint(int position, PointTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
		=> version.CreateTrackingPoint(position, trackingMode, trackingFidelity);

	public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode)
		=> version.CreateTrackingSpan(span, trackingMode);

	public ITrackingSpan CreateTrackingSpan(Span span, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
		=> version.CreateTrackingSpan(span, trackingMode, trackingFidelity);

	public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode)
		=> version.CreateTrackingSpan(start, length, trackingMode);

	public ITrackingSpan CreateTrackingSpan(int start, int length, SpanTrackingMode trackingMode, TrackingFidelityMode trackingFidelity)
		=> version.CreateTrackingSpan(start, length, trackingMode, trackingFidelity);

	int[] EnsureLineStarts()
	{
		if (lineStarts != null)
			return lineStarts;
		var starts = new List<int> { 0 };
		int searchFrom = 0;
		while (searchFrom < Length) {
			int newline = Source.IndexOf('\n', searchFrom, Length - searchFrom);
			if (newline < 0)
				break;
			starts.Add(newline + 1);
			searchFrom = newline + 1;
		}
		return lineStarts = starts.ToArray();
	}
}
