// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Parser;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// Drives AvalonEdit's FoldingManager. <see cref="UpdateFoldings"/> (ParseInformation.GetFoldings,
	/// walking IUnresolvedFile.TopLevelTypeDefinitions) is effectively dead for every language routed
	/// through <c>LanguageServiceParserAdapter</c> (the actual SD.ParserService since the
	/// ILanguageService migration - see doc/technotes/language-services.md): its
	/// CreateUnresolvedFile always returns an EmptyUnresolvedFile with zero TopLevelTypeDefinitions,
	/// so GetFoldings always returns nothing, regardless of what the real Roslyn/LSP backend knows.
	/// <see cref="UpdateFoldingsFromOutlineAsync"/> is the real (async) source now, mirroring
	/// QuickClassBrowser.FetchClassesAsync's use of the same ILanguageService.GetDocumentOutlineAsync
	/// contract (kept, not replaced: it's a legitimate source for any IParser that still populates
	/// IUnresolvedFile directly, e.g. the ILSpy decompiler view's synthetic one).
	/// </summary>
	[TextEditorService]
	public class ParserFoldingStrategy : IDisposable
	{
		readonly FoldingManager foldingManager;

		TextArea textArea;

		public FoldingManager FoldingManager {
			get { return foldingManager; }
		}

		public ParserFoldingStrategy(TextArea textArea)
		{
			this.textArea = textArea;
			foldingManager = FoldingManager.Install(textArea);
		}

		public void Dispose()
		{
			if (textArea != null) {
				FoldingManager.Uninstall(foldingManager);
				textArea = null;
			}
		}

		public void UpdateFoldings(ParseInformation parseInfo)
		{
			if (!textArea.Document.Version.Equals(parseInfo.ParsedVersion)) {
				SD.Log.Debug("Folding update ignored; parse information is outdated version");
				return;
			}
			SD.Log.Debug("Update Foldings");
			int firstErrorOffset = -1;
			IEnumerable<NewFolding> newFoldings = parseInfo.GetFoldings(textArea.Document, out firstErrorOffset);
			newFoldings = newFoldings.OrderBy(f => f.StartOffset);
			foldingManager.UpdateFoldings(newFoldings, firstErrorOffset);
		}

		/// <summary>
		/// Fetches the document outline (types + members, each with a full-declaration
		/// <c>ExtentSpan</c> - see <see cref="DocumentOutlineNode"/>) from the file's
		/// <see cref="ILanguageService"/> and drives the FoldingManager from it. Async because the
		/// outline round-trip is Roslyn-in-process or an LSP request - blocking the UI thread on it
		/// deadlocks whenever the language service's own continuations need the dispatcher (the same
		/// trap QuickClassBrowser.FetchClassesAsync documents for the identical call).
		/// </summary>
		public async Task UpdateFoldingsFromOutlineAsync(FileName fileName)
		{
			if (fileName == null || textArea == null)
				return;
			var document = textArea.Document;
			try {
				var registry = SD.GetService<LanguageServiceRegistry>();
				if (registry == null || !registry.TryGetService(fileName, out var service))
					return;
				var documentId = new DocumentId(fileName);
				var outline = await service.GetDocumentOutlineAsync(documentId, CancellationToken.None).ConfigureAwait(false);

				await SD.MainThread.InvokeAsync(() => {
					// The editor may have navigated to a different file (or closed) while the
					// outline request was in flight - same staleness check UpdateFoldings makes
					// via ParsedVersion, just against document identity instead.
					if (textArea == null || textArea.Document != document)
						return;
					// document is the UI-thread-owned AvalonEdit TextDocument (GetLineByNumber etc.
					// throw off-thread), so the offset conversion has to happen in here too, not
					// before this InvokeAsync - the outline itself was fetched off-thread above.
					var newFoldings = new List<NewFolding>();
					CollectFoldings(outline, document, newFoldings);
					newFoldings = newFoldings.OrderBy(f => f.StartOffset).ToList();
					foldingManager.UpdateFoldings(newFoldings, -1);
				});
			} catch (Exception ex) {
				LoggingService.Warn("Folding update from document outline failed for '" + fileName + "': " + ex.Message);
			}
		}

		static void CollectFoldings(IEnumerable<DocumentOutlineNode> nodes, TextDocument document, List<NewFolding> result)
		{
			foreach (var node in nodes) {
				if (node.ExtentSpan.Start.Line < node.ExtentSpan.End.Line) {
					int startOffset = GetOffset(document, node.ExtentSpan.Start);
					int endOffset = GetOffset(document, node.ExtentSpan.End);
					if (endOffset > startOffset)
						result.Add(new NewFolding(startOffset, endOffset));
				}
				CollectFoldings(node.Children, document, result);
			}
		}

		static int GetOffset(TextDocument document, TextPosition position)
		{
			if (position.Line < 1)
				return 0;
			if (position.Line > document.LineCount)
				return document.TextLength;
			var line = document.GetLineByNumber(position.Line);
			return Math.Min(line.Offset + position.Column - 1, document.TextLength);
		}
	}
}
