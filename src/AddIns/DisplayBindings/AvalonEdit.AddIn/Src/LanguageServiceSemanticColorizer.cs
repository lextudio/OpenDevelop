using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Workbench;
using SemanticLanguageService = ICSharpCode.SharpDevelop.LanguageServices.ILanguageService;

namespace ICSharpCode.AvalonEdit.AddIn
{
	// Backend-agnostic semantic overlay. Roslyn and LSP both enter through ILanguageService and
	// return the same SemanticToken DTO; only token vocabulary-to-theme mapping belongs here.
	sealed class LanguageServiceSemanticColorizer : DocumentColorizingTransformer, IDisposable
	{
		readonly TextDocument document;
		readonly TextView textView;
		readonly string fileName;
		readonly SemanticLanguageService languageService;
		CancellationTokenSource refreshCancellation = new();
		IReadOnlyList<ColoredToken> tokens = Array.Empty<ColoredToken>();

		LanguageServiceSemanticColorizer(TextDocument document, TextView textView, string fileName, SemanticLanguageService languageService)
		{
			this.document = document;
			this.textView = textView;
			this.fileName = fileName;
			this.languageService = languageService;
			document.Changed += DocumentChanged;
			ScheduleRefresh();
		}

		public static LanguageServiceSemanticColorizer Create(TextDocument document, TextView textView, string fileName)
		{
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(fileName, out var languageService))
				return null;
			return new LanguageServiceSemanticColorizer(document, textView, fileName, languageService);
		}

		void DocumentChanged(object sender, DocumentChangeEventArgs e) => ScheduleRefresh();

		void ScheduleRefresh()
		{
			refreshCancellation.Cancel();
			refreshCancellation.Dispose();
			refreshCancellation = new CancellationTokenSource();
			var cancellationToken = refreshCancellation.Token;
			_ = textView.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => {
				try {
					await Task.Delay(150, cancellationToken);
					var text = document.Text;
					var documentId = new ICSharpCode.SharpDevelop.LanguageServices.DocumentId(fileName);
					await languageService.UpsertDocumentAsync(documentId, text, cancellationToken);
					var semanticTokens = await languageService.GetSemanticTokensAsync(documentId, cancellationToken);
					if (cancellationToken.IsCancellationRequested)
						return;
					tokens = semanticTokens.Select(token => ConvertToken(token)).Where(token => token.Length > 0).ToArray();
					textView.Redraw();
				}
				catch (OperationCanceledException) { }
				catch (Exception ex) { LoggingService.Warn("Semantic highlighting failed for '" + fileName + "'. " + ex.Message); }
			}));
		}

		ColoredToken ConvertToken(SemanticToken token)
		{
			try {
				var start = document.GetOffset(token.Span.Start.Line, token.Span.Start.Column);
				var end = document.GetOffset(token.Span.End.Line, token.Span.End.Column);
				return new ColoredToken(start, Math.Max(0, end - start), GetBrush(token.Type));
			}
			catch (ArgumentOutOfRangeException) {
				return default;
			}
		}

		protected override void ColorizeLine(DocumentLine line)
		{
			var lineEnd = line.Offset + line.Length;
			foreach (var token in tokens) {
				var start = Math.Max(line.Offset, token.Offset);
				var end = Math.Min(lineEnd, token.Offset + token.Length);
				if (start < end && token.Brush != null)
					ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(token.Brush));
			}
		}

		// The semantic token palette is hardcoded per theme: the original table used WPF named
		// light-theme brushes (Teal/DarkViolet/Blue...) which stay readable in Light but look
		// wrong on the dark workbench - e.g. method names in DarkViolet or XAML namespaces in
		// Blue. The dark table mirrors VS Code's Dark+ palette (type cyan #4EC9B0, method yellow
		// #DCDCAA, attribute light-blue #9CDCFE, string amber #CE9178, keyword medium-blue
		// #569CD6, comment green #6A9955). CodeEditor rebuilds the pipeline on IdeThemeService.
		// ThemeChanged, so switching themes re-resolves these brushes.
		static readonly Dictionary<string, Brush> LightBrushes = new(StringComparer.Ordinal) {
			["ReferenceTypes"] = Brushes.Teal,
			["ValueTypes"] = Brushes.DarkCyan,
			["MethodCall"] = Brushes.DarkViolet,
			["FieldAccess"] = Brushes.SaddleBrown,
			["xamlDelimiter"] = Brushes.DimGray,
			["xamlAttributeQuotes"] = Brushes.DimGray,
			["xamlName"] = Brushes.Teal,
			["xamlMarkupExtensionClass"] = Brushes.Teal,
			["xamlAttribute"] = Brushes.SaddleBrown,
			["xamlMarkupExtensionParameterName"] = Brushes.SaddleBrown,
			["xamlAttributeValue"] = Brushes.Brown,
			["xamlMarkupExtensionParameterValue"] = Brushes.Brown,
			["xamlText"] = Brushes.Brown,
			["xamlNamespacePrefix"] = Brushes.DarkCyan,
			["xamlKeyword"] = Brushes.Blue,
			["xamlComment"] = Brushes.Green,
			["namespace"] = Brushes.DarkCyan,
			["module"] = Brushes.DarkCyan,
			["class"] = Brushes.Teal,
			["struct"] = Brushes.Teal,
			["interface"] = Brushes.Teal,
			["enum"] = Brushes.Teal,
			["type"] = Brushes.Teal,
			["typeParameter"] = Brushes.DarkSlateGray,
			["function"] = Brushes.DarkViolet,
			["method"] = Brushes.DarkViolet,
			["macro"] = Brushes.DarkViolet,
			["property"] = Brushes.SaddleBrown,
			["field"] = Brushes.SaddleBrown,
			["event"] = Brushes.SaddleBrown,
			["parameter"] = Brushes.DarkGoldenrod,
			["variable"] = Brushes.DarkBlue,
			["keyword"] = Brushes.Blue,
			["modifier"] = Brushes.Blue,
			["string"] = Brushes.Brown,
			["number"] = Brushes.DarkGreen,
			["comment"] = Brushes.Green,
			["operator"] = Brushes.DarkSlateBlue
		};

		static readonly Dictionary<string, Brush> DarkBrushes = new(StringComparer.Ordinal) {
			["ReferenceTypes"] = CreateBrush(0x4E, 0xC9, 0xB0),
			["ValueTypes"] = CreateBrush(0x4E, 0xC9, 0xB0),
			["MethodCall"] = CreateBrush(0xDC, 0xDC, 0xAA),
			["FieldAccess"] = CreateBrush(0xDC, 0xDC, 0xAA),
			["xamlDelimiter"] = CreateBrush(0x80, 0x80, 0x80),
			["xamlAttributeQuotes"] = CreateBrush(0x80, 0x80, 0x80),
			["xamlName"] = CreateBrush(0x4E, 0xC9, 0xB0),
			["xamlMarkupExtensionClass"] = CreateBrush(0x4E, 0xC9, 0xB0),
			["xamlAttribute"] = CreateBrush(0x9C, 0xDC, 0xFE),
			["xamlMarkupExtensionParameterName"] = CreateBrush(0x9C, 0xDC, 0xFE),
			["xamlAttributeValue"] = CreateBrush(0xCE, 0x91, 0x78),
			["xamlMarkupExtensionParameterValue"] = CreateBrush(0xCE, 0x91, 0x78),
			["xamlText"] = CreateBrush(0xCE, 0x91, 0x78),
			["xamlNamespacePrefix"] = CreateBrush(0x9C, 0xDC, 0xFE),
			["xamlKeyword"] = CreateBrush(0x56, 0x9C, 0xD6),
			["xamlComment"] = CreateBrush(0x6A, 0x99, 0x55),
			["namespace"] = CreateBrush(0x4E, 0xC9, 0xB0),
			["module"] = CreateBrush(0x4E, 0xC9, 0xB0),
			["class"] = CreateBrush(0x4E, 0xC9, 0xB0),
			["struct"] = CreateBrush(0x4E, 0xC9, 0xB0),
			["interface"] = CreateBrush(0x4E, 0xC9, 0xB0),
			["enum"] = CreateBrush(0x4E, 0xC9, 0xB0),
			["type"] = CreateBrush(0x4E, 0xC9, 0xB0),
			["typeParameter"] = CreateBrush(0x9C, 0xDC, 0xFE),
			["function"] = CreateBrush(0xDC, 0xDC, 0xAA),
			["method"] = CreateBrush(0xDC, 0xDC, 0xAA),
			["macro"] = CreateBrush(0xDC, 0xDC, 0xAA),
			["property"] = CreateBrush(0xDC, 0xDC, 0xAA),
			["field"] = CreateBrush(0xDC, 0xDC, 0xAA),
			["event"] = CreateBrush(0xDC, 0xDC, 0xAA),
			["parameter"] = CreateBrush(0x9C, 0xDC, 0xFE),
			["variable"] = CreateBrush(0x9C, 0xDC, 0xFE),
			["keyword"] = CreateBrush(0x56, 0x9C, 0xD6),
			["modifier"] = CreateBrush(0x56, 0x9C, 0xD6),
			["string"] = CreateBrush(0xCE, 0x91, 0x78),
			["number"] = CreateBrush(0xB5, 0xCE, 0xA8),
			["comment"] = CreateBrush(0x6A, 0x99, 0x55),
			["operator"] = CreateBrush(0xD4, 0xD4, 0xD4)
		};

		static Brush CreateBrush(byte r, byte g, byte b)
		{
			var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
			brush.Freeze();
			return brush;
		}

		static Brush GetBrush(string type)
		{
			var table = IdeThemeService.CurrentTheme == IdeThemeService.Dark ? DarkBrushes : LightBrushes;
			return table.TryGetValue(type, out var brush) ? brush : null;
		}

		public void Dispose()
		{
			document.Changed -= DocumentChanged;
			refreshCancellation.Cancel();
			refreshCancellation.Dispose();
		}

		readonly record struct ColoredToken(int Offset, int Length, Brush Brush);
	}
}
