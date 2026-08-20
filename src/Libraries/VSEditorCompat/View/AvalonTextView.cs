// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// ITextView over one AvalonEdit-hosted editor view (vs-editor-api.md section 21, Milestone 6).
// TextViewLines and friends are backed by AvalonTextViewLine/AvalonTextViewLineCollection, which
// need a real, measured/arranged TextView to produce meaningful coordinates - see
// EnsureLaidOut(). Space-reservation adornment stacks (QueueSpaceReservationStackRefresh) are
// still not implemented (section 64: adornments remain out of scope).
//
// One AvalonTextView per (AvalonTextBuffer, AvalonEdit TextArea) pair - see
// AvalonTextViewRegistry for the identity rule (mirrors AvalonTextBufferRegistry's reasoning for
// buffers, section 11).

using System;

using AvalonEditing = ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Utilities;

namespace LeXtudio.OpenDevelop.VSEditor;

public sealed class AvalonTextView : ITextView
{
	readonly AvalonEditing.TextArea textArea;
	bool isClosed;

	internal AvalonEditing.TextArea TextArea => textArea;

	public AvalonTextView(AvalonTextBuffer buffer, AvalonEditing.TextArea textArea, ITextViewRoleSet roles)
	{
		TextBuffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
		this.textArea = textArea ?? throw new ArgumentNullException(nameof(textArea));
		Roles = roles ?? new AvalonTextViewRoleSet(new[] { PredefinedTextViewRoles.Document, PredefinedTextViewRoles.Editable, PredefinedTextViewRoles.Interactive, PredefinedTextViewRoles.PrimaryDocument });

		BufferGraph = new FlatBufferGraph(buffer);
		TextViewModel = new AvalonTextViewModel(buffer);
		Properties = new PropertyCollection();
		Options = new AvalonEditorOptions();

		Caret = new AvalonTextCaret(this, textArea);
		Selection = new AvalonTextSelection(this, textArea);
		ViewScroller = new AvalonViewScroller(this, textArea.TextView);

		textArea.TextView.ScrollOffsetChanged += (sender, e) =>
		{
			ViewportLeftChanged?.Invoke(this, EventArgs.Empty);
			var state = new ViewState(this);
			LayoutChanged?.Invoke(this, new TextViewLayoutChangedEventArgs(
				state, state, Array.Empty<ITextViewLine>(), Array.Empty<ITextViewLine>()));
		};
		textArea.GotFocus += (sender, e) => GotAggregateFocus?.Invoke(this, EventArgs.Empty);
		textArea.LostFocus += (sender, e) => LostAggregateFocus?.Invoke(this, EventArgs.Empty);
	}

	internal TextDocument Document => ((AvalonTextBuffer)TextBuffer).Document;

	public ITextBuffer TextBuffer { get; }

	public ITextSnapshot TextSnapshot => TextBuffer.CurrentSnapshot;

	public ITextSnapshot VisualSnapshot => TextBuffer.CurrentSnapshot;

	public ITextViewModel TextViewModel { get; }

	public ITextDataModel TextDataModel => TextViewModel.DataModel;

	public IBufferGraph BufferGraph { get; }

	public ITextCaret Caret { get; }

	public ITextSelection Selection { get; }

	public IViewScroller ViewScroller { get; }

	public ITextViewRoleSet Roles { get; }

	public IEditorOptions Options { get; }

	public PropertyCollection Properties { get; }

	public ITrackingSpan ProvisionalTextHighlight { get; set; }

	public bool InLayout => false;

	public bool IsClosed => isClosed;

	public bool HasAggregateFocus => textArea.IsFocused || textArea.IsKeyboardFocusWithin;

	public bool IsMouseOverViewOrAdornments => textArea.IsMouseOver;

	public double MaxTextRightCoordinate
	{
		get
		{
			double max = 0;
			foreach (var line in TextViewLines)
				max = Math.Max(max, line.Right);
			return max;
		}
	}

	public double ViewportLeft
	{
		get => textArea.TextView.HorizontalOffset;
		set => ViewScroller.ScrollViewportHorizontallyByPixels(value - textArea.TextView.HorizontalOffset);
	}

	public double ViewportTop => textArea.TextView.VerticalOffset;

	public double ViewportRight => ViewportLeft + ViewportWidth;

	public double ViewportBottom => ViewportTop + ViewportHeight;

	public double ViewportWidth => textArea.TextView.RenderSize.Width;

	public double ViewportHeight => textArea.TextView.RenderSize.Height;

	public double LineHeight => textArea.TextView.DefaultLineHeight;

	public ITextViewLineCollection TextViewLines
	{
		get
		{
			EnsureLaidOut();
			return new AvalonTextViewLineCollection(this, textArea.TextView);
		}
	}

	/// <summary>
	/// AvalonEdit only produces real VisualLine/TextLine geometry after a WPF measure/arrange
	/// pass has actually run - EnsureVisualLines() forces that (it no-ops if a valid layout
	/// already exists). Callers that never attach this view's TextArea to a live visual tree get
	/// a WPF InvalidOperationException from EnsureVisualLines() itself, not a silently-wrong
	/// empty collection.
	/// </summary>
	void EnsureLaidOut() => textArea.TextView.EnsureVisualLines();

	public event EventHandler<TextViewLayoutChangedEventArgs> LayoutChanged;
	public event EventHandler ViewportLeftChanged;
	public event EventHandler ViewportHeightChanged;
	public event EventHandler ViewportWidthChanged;
	public event EventHandler<MouseHoverEventArgs> MouseHover;
	public event EventHandler Closed;
	public event EventHandler LostAggregateFocus;
	public event EventHandler GotAggregateFocus;

	public void Close()
	{
		if (isClosed)
			return;
		isClosed = true;
		Closed?.Invoke(this, EventArgs.Empty);
	}

	public void DisplayTextLineContainingBufferPosition(SnapshotPoint bufferPosition, double verticalDistance, ViewRelativePosition relativeTo)
		=> DisplayTextLineContainingBufferPosition(bufferPosition, verticalDistance, relativeTo, null, null);

	public void DisplayTextLineContainingBufferPosition(SnapshotPoint bufferPosition, double verticalDistance, ViewRelativePosition relativeTo, double? viewportWidthOverride, double? viewportHeightOverride)
	{
		var location = Document.GetLocation(bufferPosition.Position);
		var documentLine = Document.GetLineByNumber(location.Line);
		var visualTop = textArea.TextView.GetVisualTopByDocumentLine(location.Line);
		var height = viewportHeightOverride ?? ViewportHeight;
		var targetTop = relativeTo == ViewRelativePosition.Top
			? visualTop - verticalDistance
			: visualTop - height + verticalDistance;
		ViewScroller.EnsureSpanVisible(new SnapshotSpan(TextSnapshot, documentLine.Offset, documentLine.Length));
		textArea.TextView.MakeVisible(new System.Windows.Rect(0, targetTop, 1, height));
	}

	public SnapshotSpan GetTextElementSpan(SnapshotPoint point) => TextViewLines.GetTextElementSpan(point);

	public ITextViewLine GetTextViewLineContainingBufferPosition(SnapshotPoint bufferPosition)
		=> TextViewLines.GetTextViewLineContainingBufferPosition(bufferPosition);

	public void QueueSpaceReservationStackRefresh()
	{
		// No space-reservation-manager adornment stack exists yet (section 64: adornments are
		// High risk / deferred) - nothing to refresh.
	}
}
