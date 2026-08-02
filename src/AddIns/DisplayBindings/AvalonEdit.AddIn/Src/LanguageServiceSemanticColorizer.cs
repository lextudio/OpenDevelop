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

		static Brush GetBrush(string type) => type switch
		{
			"ReferenceTypes" => Brushes.Teal,
			"ValueTypes" => Brushes.DarkCyan,
			"MethodCall" => Brushes.DarkViolet,
			"FieldAccess" => Brushes.SaddleBrown,
			"xamlDelimiter" or "xamlAttributeQuotes" => Brushes.DimGray,
			"xamlName" or "xamlMarkupExtensionClass" => Brushes.Teal,
			"xamlAttribute" or "xamlMarkupExtensionParameterName" => Brushes.SaddleBrown,
			"xamlAttributeValue" or "xamlMarkupExtensionParameterValue" or "xamlText" => Brushes.Brown,
			"xamlNamespacePrefix" => Brushes.DarkCyan,
			"xamlKeyword" => Brushes.Blue,
			"xamlComment" => Brushes.Green,
			"namespace" or "module" => Brushes.DarkCyan,
			"class" or "struct" or "interface" or "enum" or "type" => Brushes.Teal,
			"typeParameter" => Brushes.DarkSlateGray,
			"function" or "method" or "macro" => Brushes.DarkViolet,
			"property" or "field" or "event" => Brushes.SaddleBrown,
			"parameter" => Brushes.DarkGoldenrod,
			"variable" => Brushes.DarkBlue,
			"keyword" or "modifier" => Brushes.Blue,
			"string" => Brushes.Brown,
			"number" => Brushes.DarkGreen,
			"comment" => Brushes.Green,
			"operator" => Brushes.DarkSlateBlue,
			_ => null
		};

		public void Dispose()
		{
			document.Changed -= DocumentChanged;
			refreshCancellation.Cancel();
			refreshCancellation.Dispose();
		}

		readonly record struct ColoredToken(int Offset, int Length, Brush Brush);
	}
}
