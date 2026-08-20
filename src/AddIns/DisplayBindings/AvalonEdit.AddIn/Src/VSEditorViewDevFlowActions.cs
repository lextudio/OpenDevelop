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
	}
}
