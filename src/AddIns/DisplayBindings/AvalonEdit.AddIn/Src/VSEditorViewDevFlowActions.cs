// DevFlow action used by tests/OpenDevelop.IntegrationTests to verify the VS editor
// compatibility layer's ITextViewLine/ITextViewLineCollection geometry (vs-editor-api.md
// sections 22/64) against the app's real, already-running Dispatcher. A bare in-process WPF
// Window + UpdateLayout() inside a plain unit test process hangs on this repo's LibreWPF/macOS
// stack (no native message loop pumping it) - [DevFlowUIThread] runs this on the actual UI
// thread of a live app instance instead, where AvalonEdit's VisualLine/TextLine layout already
// works because a real window is shown and laid out.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using LeXtudio.DevFlow.Agent.Core;
using LeXtudio.OpenDevelop.VSEditor;
using Microsoft.Maui.DevFlow.Agent.Core;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace ICSharpCode.AvalonEdit.AddIn
{
	[DevFlowUIThread]
	public static class VSEditorViewDevFlowActions
	{
		static readonly Dictionary<TextArea, FoldingManager> foldingManagers = new();

		static FoldingManager GetOrInstallFoldingManager(TextArea textArea)
		{
			if (foldingManagers.TryGetValue(textArea, out var manager))
				return manager;
			// A language binding (e.g. CSharpBinding) may already have installed one - reuse it
			// rather than calling FoldingManager.Install again, which throws
			// "service already exists" for a second registration on the same TextArea.
			manager = textArea.GetService(typeof(FoldingManager)) as FoldingManager ?? FoldingManager.Install(textArea);
			foldingManagers[textArea] = manager;
			return manager;
		}

		[DevFlowAction("od.vseditor.line-geometry", Description = "Inspect ITextViewLine geometry for the active text editor view (VS editor compatibility layer)")]
		public static string GetLineGeometry()
		{
			var editor = SD.Workbench.ActiveViewContent?.GetService(typeof(ITextEditor)) as ITextEditor;
			if (editor == null)
				return JsonSerializer.Serialize(new { active = false });

			var view = editor.GetService(typeof(ITextView)) as ITextView;
			if (view == null)
				return JsonSerializer.Serialize(new { active = true, viewAvailable = false });

			try {
				var lines = view.TextViewLines
					.Select(line => new {
						text = line.Extent.GetText(),
						top = line.Top,
						bottom = line.Bottom,
						height = line.Height,
						isFirst = line.IsFirstTextViewLineForSnapshotLine,
						isLast = line.IsLastTextViewLineForSnapshotLine,
						lengthIncludingLineBreak = line.LengthIncludingLineBreak,
					})
					.ToArray();

				var caretLine = view.Caret.ContainingTextViewLine;

				return JsonSerializer.Serialize(new {
					active = true,
					viewAvailable = true,
					viewportHeight = view.ViewportHeight,
					lineCount = lines.Length,
					lines,
					caret = new {
						offset = view.Caret.Position.BufferPosition.Position,
						lineText = caretLine.Extent.GetText(),
						top = view.Caret.Top,
						left = view.Caret.Left,
					},
				});
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { active = true, viewAvailable = true, error = ex.Message });
			}
		}

		/// <summary>
		/// Reports the actual bookmark objects consumed by <see cref="IconBarMargin"/> for the
		/// active editor. This deliberately observes the rendered gutter's manager rather than a
		/// parser or an outline response, so remote-host integration tests catch a regression that
		/// leaves declaration icons absent from the visual editor.
		/// </summary>
		public static string GetIconBarBookmarks()
		{
			var editor = SD.Workbench.ActiveViewContent?.GetService(typeof(ITextEditor)) as ITextEditor;
			var adapter = editor as AvalonEditTextEditorAdapter;
			var codeEditor = adapter?.TextEditor?.TextArea?.TextView.Services.GetService(typeof(CodeEditor)) as CodeEditor;
			if (codeEditor == null)
				return JsonSerializer.Serialize(new { active = false });

			var bookmarks = codeEditor.IconBarManager.Bookmarks
				.Select(bookmark => new {
					line = bookmark.LineNumber,
					type = bookmark.GetType().Name,
					tooltip = bookmark.DisplaysTooltip ? bookmark.CreateTooltipContent()?.ToString() : null,
				})
				.ToArray();
			return JsonSerializer.Serialize(new { active = true, count = bookmarks.Length, bookmarks });
		}

		/// <summary>
		/// The on-screen centre (PointToScreen, the convention ClickPointerAsync uses) of the active
		/// editor's Icon Bar on a document line, scrolling the line into view first. Lets a test aim a
		/// real click at the gutter: the generic UI tree only knows the whole margin, not its lines.
		/// </summary>
		public static string GetIconBarLinePoint(int line)
		{
			var editor = SD.Workbench.ActiveViewContent?.GetService(typeof(ITextEditor)) as ITextEditor;
			var adapter = editor as AvalonEditTextEditorAdapter;
			var textArea = adapter?.TextEditor?.TextArea;
			var margin = textArea?.LeftMargins.OfType<IconBarMargin>().FirstOrDefault();
			if (margin == null)
				return JsonSerializer.Serialize(new { success = false, reason = "no active editor with an Icon Bar" });
			if (line < 1 || line > textArea.Document.LineCount)
				return JsonSerializer.Serialize(new { success = false, reason = "line out of range: " + line });

			adapter.TextEditor.ScrollToLine(line);
			var textView = textArea.TextView;
			textView.EnsureVisualLines();
			var visualLine = textView.GetVisualLine(line);
			if (visualLine == null || !margin.IsVisible || margin.ActualWidth <= 0)
				return JsonSerializer.Serialize(new { success = false, reason = "line or Icon Bar not laid out" });

			// The Icon Bar shares the TextView's vertical coordinates (it maps the pointer to a line
			// the same way), so the line's visual top minus the scroll offset is its y in the margin.
			double y = visualLine.VisualTop - textView.ScrollOffset.Y + visualLine.Height / 2;
			var screen = margin.PointToScreen(new System.Windows.Point(margin.ActualWidth / 2, y));
			return JsonSerializer.Serialize(new { success = true, line, x = screen.X, y = screen.Y });
		}

		[DevFlowAction("od.vseditor.fold-and-geometry", Description = "Fold a document span (via AvalonEdit's FoldingManager) then report ITextViewLine geometry, to verify a folded VisualLine (FirstDocumentLine != LastDocumentLine) still maps to the correct combined Extent")]
		public static string FoldAndGetLineGeometry(int foldStart, int foldEnd)
		{
			var editor = SD.Workbench.ActiveViewContent?.GetService(typeof(ITextEditor)) as ITextEditor;
			var adapter = editor as AvalonEditTextEditorAdapter;
			var view = editor?.GetService(typeof(ITextView)) as ITextView;
			if (adapter == null || view == null)
				return JsonSerializer.Serialize(new { active = false });

			try {
				var textArea = adapter.TextEditor.TextArea;
				var foldingManager = GetOrInstallFoldingManager(textArea);
				var folding = foldingManager.CreateFolding(foldStart, foldEnd);
				folding.IsFolded = true;

				var lines = view.TextViewLines
					.Select(line => new {
						text = line.Extent.GetText(),
						start = line.Start.Position,
						end = line.End.Position,
						top = line.Top,
						bottom = line.Bottom,
						height = line.Height,
						isFirst = line.IsFirstTextViewLineForSnapshotLine,
						isLast = line.IsLastTextViewLineForSnapshotLine,
					})
					.ToArray();

				// AvalonEdit's own VisualLine model (FirstDocumentLine/LastDocumentLine per visual
				// row) - reported separately from the ITextViewLine rows above because a folded
				// VisualLine can still render as more than one physical TextLine row. Confirmed
				// (via FoldingManager.GetNextFoldedFoldingStart/GetFoldingsContaining) that
				// FoldingElementGenerator does fire at the correct offset and constructs the "..."
				// marker element correctly per VisualLine.PerformVisualElementConstruction's own
				// algorithm - the extra TextLine split happens somewhere inside WPF's
				// TextFormatter/FormattedTextElement embedded-object line-breaking, not in
				// AvalonEdit's own fold bookkeeping. Root cause not yet isolated further; treat as
				// a known open question rather than a specific line to fix. ITextViewLine
				// correctness (combined Extent across whichever physical rows result) does not
				// depend on it being resolved.
				var visualLines = textArea.TextView.VisualLines
					.Select(vl => new { firstDocumentLine = vl.FirstDocumentLine.LineNumber, lastDocumentLine = vl.LastDocumentLine.LineNumber, textLineCount = vl.TextLines.Count })
					.ToArray();

				return JsonSerializer.Serialize(new { active = true, lineCount = lines.Length, lines, visualLines });
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { active = true, error = ex.Message });
			}
		}

		[DevFlowAction("od.vseditor.select", Description = "Select a document span through the VS editor compatibility layer's ITextSelection, for verifying GetSelectionOnTextViewLine")]
		public static string Select(int start, int length)
		{
			var editor = SD.Workbench.ActiveViewContent?.GetService(typeof(ITextEditor)) as ITextEditor;
			var view = editor?.GetService(typeof(ITextView)) as ITextView;
			if (view == null)
				return JsonSerializer.Serialize(new { active = false });

			view.Selection.Select(new SnapshotSpan(view.TextSnapshot, start, length), isReversed: false);

			var perLine = view.TextViewLines
				.Select(line => new {
					text = line.Extent.GetText(),
					selection = view.Selection.GetSelectionOnTextViewLine(line)?.GetText(),
				})
				.ToArray();

			return JsonSerializer.Serialize(new { active = true, lines = perLine });
		}

		static TextArea ActiveTextArea()
		{
			var editor = SD.Workbench.ActiveViewContent?.GetService(typeof(ITextEditor)) as ITextEditor;
			return (editor as AvalonEditTextEditorAdapter)?.TextEditor?.TextArea;
		}

		/// <summary>
		/// Types <paramref name="text"/> into the active editor one character at a time through the same routed events a
		/// keyboard produces (PreviewTextInput, then TextInput on the TextArea), so code-completion bindings and an open
		/// completion window see it as the user's typing. A caret offset of -1 leaves the caret where it is.
		/// </summary>
		public static string TypeText(string text, int caretOffset = -1)
		{
			var textArea = ActiveTextArea();
			if (textArea == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active AvalonEdit text editor." });
			if (caretOffset >= 0)
				textArea.Caret.Offset = Math.Min(caretOffset, textArea.Document.TextLength);
			var trace = new List<object>();
			foreach (char c in text ?? string.Empty) {
				var composition = new TextComposition(InputManager.Current, textArea, c.ToString());
				var preview = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = UIElement.PreviewTextInputEvent };
				textArea.RaiseEvent(preview);
				if (preview.Handled)
					continue;
				var input = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = UIElement.TextInputEvent };
				textArea.RaiseEvent(input);
				var window = (textArea.TextView.Services.GetService(typeof(CodeEditor)) as CodeEditor)?.PrimaryTextEditor?.ActiveCompletionWindow;
				trace.Add(new {
					ch = c.ToString(), caret = textArea.Caret.Offset, windowOpen = window != null,
					start = window?.StartOffset, end = window?.EndOffset, count = window?.CompletionList.ListBox.Items.Count
				});
			}
			// Typing at the keyboard scrolls the caret into view; the synthetic events above do not necessarily.
			textArea.Caret.BringCaretToView();
			return JsonSerializer.Serialize(new { success = true, caretOffset = textArea.Caret.Offset, lineText = CaretLineText(textArea), trace });
		}

		/// <summary>
		/// Where the window is versus where the caret's line is, all in device-independent units. PointToScreen returns
		/// physical pixels for a native window and device-independent coordinates for a portable one, so its results are
		/// divided by the unit measured here (two points 1000 DIP apart on the root visual) before they are compared with
		/// Window.Left/Top.
		/// </summary>
		static object DescribePlacement(TextArea textArea, ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindowBase window)
		{
			var textView = textArea.TextView;
			var source = PresentationSource.FromVisual(textView);
			if (source?.RootVisual == null)
				return null; // not shown yet: PointToScreen would throw
			// A wide baseline: a native window reports whole pixels, so a short one would put a fractional scale off by a percent.
			const double baseline = 1000;
			var root = source.RootVisual;
			double unit = (root.PointToScreen(new Point(baseline, 0)).X - root.PointToScreen(new Point(0, 0)).X) / baseline;
			if (!(unit > 0) || double.IsInfinity(unit))
				unit = 1;
			Point ToDip(Point screen) => new Point(screen.X / unit, screen.Y / unit);

			var scroll = textView.ScrollOffset;
			var top = textView.GetVisualPosition(textArea.Caret.Position, ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineTop) - scroll;
			var bottom = textView.GetVisualPosition(textArea.Caret.Position, ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineBottom) - scroll;
			var origin = ToDip(textView.PointToScreen(new Point(0, 0)));
			var lineTopScreen = ToDip(textView.PointToScreen(top));
			var lineBottomScreen = ToDip(textView.PointToScreen(bottom));
			var owner = Window.GetWindow(textArea);
			// The popup stays anchored at the start of the word being typed, not at the caret.
			var startLocation = textArea.Document.GetLocation(Math.Min(window.StartOffset, textArea.Document.TextLength));
			var startVisual = textView.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(startLocation), ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineBottom) - scroll;
			var startScreen = ToDip(textView.PointToScreen(startVisual));
			var workArea = SystemParameters.WorkArea;
			return new {
				screenUnitsPerDip = unit,
				wordStartScreenX = startScreen.X, wordStartScreenY = startScreen.Y,
				gapRightOfWordStart = window.Left - startScreen.X,
				workArea = new { x = workArea.X, y = workArea.Y, width = workArea.Width, height = workArea.Height },
				windowRight = window.Left + window.ActualWidth,
				ownerWidth = owner?.ActualWidth, ownerHeight = owner?.ActualHeight,
				windowLeft = window.Left, windowTop = window.Top, windowWidth = window.ActualWidth, windowHeight = window.ActualHeight,
				windowBottom = window.Top + window.ActualHeight,
				caretLineTopScreenY = lineTopScreen.Y, caretLineBottomScreenY = lineBottomScreen.Y, caretScreenX = lineBottomScreen.X,
				textViewOriginScreen = new { x = origin.X, y = origin.Y },
				textViewScroll = new { x = scroll.X, y = scroll.Y },
				ownerTop = owner?.Top, ownerLeft = owner?.Left,
				dpiScale = new { x = VisualTreeHelper.GetDpi(textView).DpiScaleX, y = VisualTreeHelper.GetDpi(textView).DpiScaleY },
				// Negative: the window sits above the caret line; the gap to the line bottom is what the user sees.
				gapBelowCaretLine = window.Top - lineBottomScreen.Y,
				gapAboveCaretLine = lineTopScreen.Y - (window.Top + window.ActualHeight)
			};
		}

		static string CaretLineText(TextArea textArea)
		{
			var line = textArea.Document.GetLineByOffset(Math.Min(textArea.Caret.Offset, textArea.Document.TextLength));
			return textArea.Document.GetText(line.Offset, line.Length);
		}

		/// <summary>The open completion window's filtered list, selection and word range, or open=false.</summary>
		public static string GetCompletionStatus()
		{
			var textArea = ActiveTextArea();
			if (textArea == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active AvalonEdit text editor." });
			var codeEditor = textArea.TextView.Services.GetService(typeof(CodeEditor)) as CodeEditor;
			var window = (codeEditor?.PrimaryTextEditor)?.ActiveCompletionWindow;
			int caret = textArea.Caret.Offset;
			if (window == null)
				return JsonSerializer.Serialize(new { success = true, open = false, caretOffset = caret });
			var list = window.CompletionList;
			string typed = null;
			if (window.StartOffset >= 0 && window.EndOffset >= window.StartOffset && window.EndOffset <= textArea.Document.TextLength)
				typed = textArea.Document.GetText(window.StartOffset, window.EndOffset - window.StartOffset);
			var items = list.ListBox.Items.Cast<object>().Select(i => (i as ICSharpCode.AvalonEdit.CodeCompletion.ICompletionData)?.Text).ToArray();
			return JsonSerializer.Serialize(new {
				success = true, open = true, caretOffset = caret, lineText = CaretLineText(textArea),
				startOffset = window.StartOffset, endOffset = window.EndOffset, typed,
				placement = DescribePlacement(textArea, window),
				totalCount = list.CompletionData.Count, count = items.Length,
				selected = (list.SelectedItem as ICSharpCode.AvalonEdit.CodeCompletion.ICompletionData)?.Text,
				selectedIndex = list.ListBox.SelectedIndex,
				items = items.Take(30).ToArray()
			});
		}
	}
}
