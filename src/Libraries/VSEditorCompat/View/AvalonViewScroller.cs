// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// IViewScroller over AvalonEdit's Rendering.TextView scroll offset/MakeVisible (vs-editor-api.md
// section 21). Pixel-precision line-height math uses TextView.DefaultLineHeight as a stand-in
// for exact visual-line heights (variable line heights need the ITextViewLine layer deferred
// elsewhere in this project).

using System;
using System.Windows;

using AvalonRendering = ICSharpCode.AvalonEdit.Rendering;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonViewScroller : IViewScroller
{
	readonly AvalonTextView view;
	readonly AvalonRendering.TextView textView;

	public AvalonViewScroller(AvalonTextView view, AvalonRendering.TextView textView)
	{
		this.view = view ?? throw new ArgumentNullException(nameof(view));
		this.textView = textView ?? throw new ArgumentNullException(nameof(textView));
	}

	// AvalonEdit's TextView only exposes scroll offset as a read-only property outside of its
	// (internal) IScrollInfo implementation - MakeVisible is the one public entry point that can
	// actually move the viewport, so every scroll request here is expressed as "make this
	// rectangle visible" rather than a direct offset assignment.
	public void ScrollViewportVerticallyByPixels(double distanceToScroll)
	{
		var offset = textView.ScrollOffset;
		var targetTop = offset.Y - distanceToScroll;
		textView.MakeVisible(new Rect(offset.X, targetTop, 1, textView.RenderSize.Height));
	}

	public void ScrollViewportVerticallyByLine(ScrollDirection direction)
		=> ScrollViewportVerticallyByLines(direction, 1);

	public void ScrollViewportVerticallyByLines(ScrollDirection direction, int count)
	{
		var delta = textView.DefaultLineHeight * count * (direction == ScrollDirection.Up ? 1 : -1);
		ScrollViewportVerticallyByPixels(delta);
	}

	public bool ScrollViewportVerticallyByPage(ScrollDirection direction)
	{
		var delta = textView.RenderSize.Height * (direction == ScrollDirection.Up ? 1 : -1);
		ScrollViewportVerticallyByPixels(delta);
		return true;
	}

	public void ScrollViewportHorizontallyByPixels(double distanceToScroll)
	{
		var offset = textView.ScrollOffset;
		var targetLeft = offset.X + distanceToScroll;
		textView.MakeVisible(new Rect(targetLeft, offset.Y, textView.RenderSize.Width, 1));
	}

	public void EnsureSpanVisible(SnapshotSpan span) => EnsureSpanVisible(span, EnsureSpanVisibleOptions.None);

	public void EnsureSpanVisible(SnapshotSpan span, EnsureSpanVisibleOptions options)
	{
		var document = view.Document;
		var startLocation = document.GetLocation(span.Start.Position);
		var endLocation = document.GetLocation(span.End.Position);
		var visualLine = textView.GetOrConstructVisualLine(document.GetLineByNumber(startLocation.Line));
		var top = textView.GetVisualTopByDocumentLine(startLocation.Line);
		var bottom = top + visualLine.Height;
		textView.MakeVisible(new Rect(0, top, textView.DefaultLineHeight, bottom - top));
	}

	public void EnsureSpanVisible(VirtualSnapshotSpan span, EnsureSpanVisibleOptions options)
		=> EnsureSpanVisible(span.SnapshotSpan, options);
}
