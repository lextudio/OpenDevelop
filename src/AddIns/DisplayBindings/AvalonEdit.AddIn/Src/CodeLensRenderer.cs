using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor.Search;
using ICSharpCode.SharpDevelop.LanguageServices;
using SemanticLanguageService = ICSharpCode.SharpDevelop.LanguageServices.ILanguageService;
using TextLocation = ICSharpCode.AvalonEdit.Document.TextLocation;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// CodeLens-style "N references | M implementations" annotation embedded at the top of each
	/// declaration line (doc/technotes/codelens.md), reserving real vertical space above the code
	/// rather than overlapping the previous line - matching VS's own CodeLens behavior. Data comes
	/// entirely through the shared ILanguageService contract (GetDocumentOutlineAsync for
	/// declarations, FindReferencesAsync/GetDerivedSymbolsAsync for counts) - backend-neutral, and a
	/// disabled language's Binding stops producing declarations (and therefore annotations) the same
	/// way completion/diagnostics already do, unlike the plan's original RoslynWorkspaceHelper-based
	/// design.
	///
	/// Rendering uses a zero-document-length <see cref="InlineObjectElement"/> at the start of each
	/// declaration line, following the same technique <see cref="InlineUIElementGenerator"/> uses
	/// elsewhere in this codebase. The embedded element's own <c>Width</c> is forced to 0 (so it
	/// consumes no horizontal space and doesn't indent the real code text) with
	/// <c>ClipToBounds="False"</c> so its content still paints despite the 0-width arrange rect -
	/// WPF doesn't clip by default, and AvalonEdit measures inline objects with infinite available
	/// size (<see cref="TextView"/>'s inline-object measure pass) then arranges them into exactly
	/// their own <c>DesiredSize</c>, so an explicit <c>Width="0"</c> is honored as-is.
	/// <see cref="InlineObjectRun.Format"/> bottom-aligns an embedded element to the text baseline
	/// by default (no <c>TextBlock.BaselineOffset</c> set), so giving the element a height of
	/// <c>TextView.DefaultLineHeight + annotationHeight</c> reserves exactly
	/// <c>annotationHeight</c> of new space above the line, while its lower <c>DefaultLineHeight</c>
	/// portion sits where the line would already be.
	/// </summary>
	sealed class CodeLensRenderer : VisualLineElementGenerator, IDisposable
	{
		readonly TextView textView;
		readonly TextDocument document;
		readonly string fileName;
		readonly SemanticLanguageService languageService;
		CancellationTokenSource refreshCancellation = new();
		IReadOnlyList<CodeLensItem> items = Array.Empty<CodeLensItem>();

		CodeLensRenderer(TextDocument document, TextView textView, string fileName, SemanticLanguageService languageService)
		{
			this.document = document;
			this.textView = textView;
			this.fileName = fileName;
			this.languageService = languageService;
			document.Changed += DocumentChanged;
			textView.ElementGenerators.Add(this);
			ScheduleRefresh();
		}

		public static CodeLensRenderer Create(TextDocument document, TextView textView, string fileName)
		{
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(fileName, out var languageService))
				return null;
			return new CodeLensRenderer(document, textView, fileName, languageService);
		}

		void DocumentChanged(object sender, DocumentChangeEventArgs e) => ScheduleRefresh();

		void ScheduleRefresh()
		{
			refreshCancellation.Cancel();
			refreshCancellation.Dispose();
			refreshCancellation = new CancellationTokenSource();
			var cancellationToken = refreshCancellation.Token;
			_ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => {
				try {
					await Task.Delay(500, cancellationToken);
					var computed = await ComputeItemsAsync(cancellationToken);
					if (cancellationToken.IsCancellationRequested)
						return;
					items = computed;
					// A plain repaint isn't enough - reserving/un-reserving vertical space requires
					// the element generators to run again, so this needs a full line regeneration.
					textView.Redraw();
				}
				catch (OperationCanceledException) { }
				catch (Exception ex) { LoggingService.Warn("CodeLens computation failed for '" + fileName + "'. " + ex.Message); }
			}));
		}

		async Task<IReadOnlyList<CodeLensItem>> ComputeItemsAsync(CancellationToken cancellationToken)
		{
			var text = document.Text;
			var id = new DocumentId(fileName);
			await languageService.UpsertDocumentAsync(id, text, cancellationToken);
			var outline = await languageService.GetDocumentOutlineAsync(id, cancellationToken);

			var declarations = new List<DocumentOutlineNode>();
			foreach (var type in outline) {
				declarations.Add(type);
				declarations.AddRange(type.Children);
			}

			var results = new List<CodeLensItem>();
			foreach (var declaration in declarations) {
				cancellationToken.ThrowIfCancellationRequested();
				int offset;
				DocumentLine line;
				try {
					line = document.GetLineByNumber(declaration.Span.Start.Line);
					offset = line.Offset;
				} catch (ArgumentOutOfRangeException) {
					continue;
				}

				var references = await languageService.FindReferencesAsync(id, offset, cancellationToken);
				int referenceCount = references?.References.Count ?? 0;

				int? implementationCount = null;
				if (IsOverridableDeclaration(declaration.Kind)) {
					var derived = await languageService.GetDerivedSymbolsAsync(id, offset, cancellationToken);
					if (derived != null)
						implementationCount = derived.Nodes.Count;
				}

				results.Add(new CodeLensItem(offset, referenceCount, implementationCount));
			}
			return results.OrderBy(i => i.Offset).ToList();
		}

		static bool IsOverridableDeclaration(string kind)
		{
			return kind is "Class" or "Interface" or "Struct" or "Method";
		}

		public override int GetFirstInterestedOffset(int startOffset)
		{
			foreach (var item in items) {
				if (item.Offset >= startOffset)
					return item.Offset;
			}
			return -1;
		}

		public override VisualLineElement ConstructElement(int offset)
		{
			var item = items.FirstOrDefault(i => i.Offset == offset);
			if (item == null)
				return null;
			return new InlineObjectElement(0, CreateElement(item));
		}

		UIElement CreateElement(CodeLensItem item)
		{
			// Half a normal line, not a full extra line plus a spacer - InlineObjectRun bottom-aligns
			// this element to the text baseline by default, so its bottom edge already sits right
			// where the real code line begins; keeping the label bottom-aligned within this shorter
			// box keeps it hugging that same edge (the declaration line below), instead of floating
			// up near the previous line the way a taller box (with an unused lower spacer) did.
			double height = textView.DefaultLineHeight / 2;

			var label = new TextBlock {
				Text = FormatLabel(item),
				FontSize = ((double)textView.GetValue(TextBlock.FontSizeProperty)) * 0.85,
				Foreground = Brushes.Gray,
				Background = Brushes.Transparent,
				Cursor = Cursors.Hand,
				VerticalAlignment = VerticalAlignment.Bottom,
				HorizontalAlignment = HorizontalAlignment.Left,
			};
			label.MouseLeftButtonDown += (sender, e) => {
				e.Handled = true;
				_ = ShowReferencesAsync(item);
			};

			var container = new Grid {
				// 0-width so this doesn't indent the real code text that follows on the same line -
				// see the class doc comment for why the overflowing label still paints regardless.
				Width = 0,
				Height = height,
				ClipToBounds = false,
				SnapsToDevicePixels = true,
			};
			container.Children.Add(label);
			return container;
		}

		static string FormatLabel(CodeLensItem item)
		{
			string label = item.ReferenceCount == 1 ? "1 reference" : item.ReferenceCount + " references";
			if (item.ImplementationCount is int count)
				label += count == 1 ? " | 1 implementation" : " | " + count + " implementations";
			return label;
		}

		async Task ShowReferencesAsync(CodeLensItem item)
		{
			try {
				var id = new DocumentId(fileName);
				await languageService.UpsertDocumentAsync(id, document.Text, CancellationToken.None);
				var result = await languageService.FindReferencesAsync(id, item.Offset, CancellationToken.None);
				if (result == null)
					return;

				var matches = result.References.Where(t => t.Span != null).Select(ToSearchResultMatch).Where(m => m != null).ToArray();
				string title = StringParser.Parse("${res:SharpDevelop.Refactoring.FindReferences}") + " '" + result.Subject + "'";
				SearchResultsPad.Instance.ShowSearchResults(title, matches);
				SearchResultsPad.Instance.BringToFront();
			} catch (Exception ex) {
				LoggingService.Warn("CodeLens: find references failed. " + ex.Message);
			}
		}

		SearchResultMatch ToSearchResultMatch(NavigationTarget target)
		{
			var span = target.Span.Value;
			string text;
			try {
				text = string.Equals(target.FileName, fileName, StringComparison.OrdinalIgnoreCase) ? document.Text : File.ReadAllText(target.FileName);
			} catch (IOException) {
				return null;
			} catch (UnauthorizedAccessException) {
				return null;
			}
			int startOffset = GetOffset(text, span.Start.Line, span.Start.Column);
			int endOffset = GetOffset(text, span.End.Line, span.End.Column);
			return new SearchResultMatch(
				FileName.Create(target.FileName),
				new TextLocation(span.Start.Line, span.Start.Column),
				new TextLocation(span.End.Line, span.End.Column),
				startOffset, Math.Max(0, endOffset - startOffset),
				displayText: null, defaultTextColor: null);
		}

		static int GetOffset(string text, int requestedLine, int requestedColumn)
		{
			int line = 1;
			int offset = 0;
			while (offset < text.Length && line < requestedLine) {
				if (text[offset++] == '\n')
					line++;
			}
			return Math.Min(text.Length, offset + Math.Max(0, requestedColumn - 1));
		}

		public void Dispose()
		{
			document.Changed -= DocumentChanged;
			textView.ElementGenerators.Remove(this);
			refreshCancellation.Cancel();
			refreshCancellation.Dispose();
		}

		sealed class CodeLensItem
		{
			public CodeLensItem(int offset, int referenceCount, int? implementationCount)
			{
				Offset = offset;
				ReferenceCount = referenceCount;
				ImplementationCount = implementationCount;
			}

			public int Offset { get; }
			public int ReferenceCount { get; }
			public int? ImplementationCount { get; }
		}
	}
}
