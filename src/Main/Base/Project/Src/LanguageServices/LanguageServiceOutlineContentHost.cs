// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Generic Document Outline pad bridge for any plain text file backed by a registered
// ILanguageService (via LanguageServiceRegistry), not just XAML. Based directly on
// XamlBinding's XamlOutlineContentHost/XamlOutlineLspProvider, which turned out to already be
// fully backend-agnostic internally (nothing XAML-specific in the implementation - only the
// name and where it lived) - promoted here so TypeScript/CSS (and any future LSP-registered
// language) get the same Outline pad support without duplicating that logic per addin.
// XamlBinding keeps its own copy for now rather than being risked by a refactor to share this
// one; a future cleanup could point it here too.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Widgets;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
	/// <summary>
	/// Populates the Outline pad for a text editor view from its registered
	/// <see cref="ILanguageService"/> (<see cref="ILanguageService.GetDocumentOutlineAsync"/>,
	/// LSP's <c>textDocument/documentSymbol</c> under the hood for LSP-backed languages),
	/// projected onto the shared <see cref="DocumentOutlineControl"/> so the code-editor outline
	/// looks and behaves like the designers' Document Outline. Selecting a node jumps to its
	/// source span.
	/// </summary>
	public sealed class LanguageServiceOutlineContentHost : IOutlineContentHost, IDisposable
	{
		readonly ITextEditor editor;
		readonly DocumentOutlineControl outline = new DocumentOutlineControl();
		CancellationTokenSource refreshCts;
		IReadOnlyList<DocumentOutlineNode> lastNodes = Array.Empty<DocumentOutlineNode>();

		public LanguageServiceOutlineContentHost(ITextEditor editor)
		{
			this.editor = editor;

			outline.SelectionCommitted += OnSelectionCommitted;

			editor.Document.TextChanged += OnDocumentChanged;
			RefreshAsync();
		}

		public object OutlineContent {
			get { return outline; }
		}

		void OnDocumentChanged(object sender, EventArgs e)
		{
			RefreshAsync();
		}

		async void RefreshAsync()
		{
			refreshCts?.Cancel();
			var cts = new CancellationTokenSource();
			refreshCts = cts;

			// Debounce: avoid re-querying the language server on every keystroke.
			try {
				await Task.Delay(500, cts.Token);
			} catch (OperationCanceledException) {
				return;
			}
			if (cts.IsCancellationRequested)
				return;

			IReadOnlyList<DocumentOutlineNode> nodes;
			try {
				nodes = await GetOutlineAsync(cts.Token);
			} catch (Exception ex) {
				LoggingService.Warn("LanguageServiceOutlineContentHost: failed to fetch outline. " + ex.Message);
				return;
			}

			if (cts.IsCancellationRequested)
				return;

			lastNodes = nodes;
			// Unlike XAML (always exactly one root element), a plain source file's outline is a
			// FLAT LIST of top-level symbols (multiple top-level functions/classes/selectors,
			// etc.) - DocumentOutlineControl only accepts a single root, so wrap them all under
			// one synthetic, unnamed root rather than dropping every symbol but the first (the
			// bug an earlier version of this had, found live: only the first top-level TS
			// function ever showed up in the pad). An unnamed root (Id/Name both empty/null) is
			// an already-established convention in this codebase for "not a real selectable
			// node" (see WpfSurfaceDesignerControl's own root-id handling).
			var root = new DesignerElementNode {
				Id = "",
				Name = null,
				Type = System.IO.Path.GetFileName(editor.FileName) ?? "",
				IsDesignable = true,
				Children = nodes.Select(n => n.ToElementNode()).ToList()
			};
			SD.MainThread.InvokeIfRequired(() => outline.SetRoot(nodes.Count > 0 ? root : null));
		}

		async Task<IReadOnlyList<DocumentOutlineNode>> GetOutlineAsync(CancellationToken cancellationToken)
		{
			if (editor.FileName == null)
				return Array.Empty<DocumentOutlineNode>();

			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(editor.FileName, out var languageService))
				return Array.Empty<DocumentOutlineNode>();

			var documentId = new DocumentId(editor.FileName);
			await languageService.UpsertDocumentAsync(documentId, editor.Document.Text, cancellationToken).ConfigureAwait(false);
			return await languageService.GetDocumentOutlineAsync(documentId, cancellationToken).ConfigureAwait(false);
		}

		void OnSelectionCommitted(object sender, EventArgs e)
		{
			if (!(outline.SelectedNode is { } node))
				return;
			// Jump to the first source node with the same name (names are unique per
			// namescope in practice; the language service's own ordering is authoritative).
			var match = lastNodes.FirstOrDefault(n => n.Name == node.Name);
			if (match != null)
				editor.JumpTo(match.Span.Start.Line, match.Span.Start.Column);
		}

		public void Dispose()
		{
			refreshCts?.Cancel();
			editor.Document.TextChanged -= OnDocumentChanged;
			outline.SelectionCommitted -= OnSelectionCommitted;
		}
	}

	static class LanguageServiceOutlineNodeExtensions
	{
		/// <summary>Projects a language-service outline node onto the shared Document Outline
		/// model (name + kind as the gray type hint).</summary>
		public static DesignerElementNode ToElementNode(this DocumentOutlineNode node)
		{
			return new DesignerElementNode {
				Id = node.Name,
				Name = node.Name,
				Type = node.Kind,
				IsDesignable = true,
				Children = node.Children.Select(ToElementNode).ToList()
			};
		}
	}
}
