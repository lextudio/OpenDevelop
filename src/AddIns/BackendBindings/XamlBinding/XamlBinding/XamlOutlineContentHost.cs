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
using System.Linq;
using System.Threading;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.Widgets;

namespace ICSharpCode.XamlBinding
{
	/// <summary>
	/// Populates the Outline pad for a .xaml text editor view from the XAML language server's
	/// textDocument/documentSymbol response (via <see cref="XamlOutlineLspProvider"/>), projected
	/// onto the shared <see cref="DocumentOutlineControl"/> so the code-editor outline looks and
	/// behaves like the designers' Document Outline. Selecting a node jumps to its source span.
	/// </summary>
	public sealed class XamlOutlineContentHost : IOutlineContentHost, IDisposable
	{
		readonly ITextEditor editor;
		readonly DocumentOutlineControl outline = new DocumentOutlineControl();
		CancellationTokenSource refreshCts;
		IReadOnlyList<DocumentOutlineNode> lastNodes = Array.Empty<DocumentOutlineNode>();

		public XamlOutlineContentHost(ITextEditor editor)
		{
			this.editor = editor;

			outline.SelectionCommitted += OnSelectionCommitted;

			editor.Document.TextChanged += OnDocumentChanged;
			RefreshAsync();
			ActivateOutlinePad();
		}

		public object OutlineContent {
			get { return outline; }
		}

		/// <summary>
		/// Shows the Document Outline pad once, mirroring the WinForms designer's behavior
		/// (FormsDesignerViewContent.ActivateOutlinePadOnce): a .xaml code editor is expected to
		/// show its element tree without the user opening the pad manually.
		/// </summary>
		void ActivateOutlinePad()
		{
			try {
				SD.Workbench.GetPad("ICSharpCode.SharpDevelop.Gui.OutlinePad")?.BringPadToFront();
			} catch (Exception ex) {
				LoggingService.Debug("XamlOutlineContentHost: could not activate the Outline pad: " + ex.Message);
			}
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
				await System.Threading.Tasks.Task.Delay(500, cts.Token);
			} catch (OperationCanceledException) {
				return;
			}
			if (cts.IsCancellationRequested)
				return;

			IReadOnlyList<DocumentOutlineNode> nodes;
			try {
				nodes = await XamlOutlineLspProvider.GetOutlineAsync(editor, cts.Token);
			} catch (Exception ex) {
				LoggingService.Warn("XamlOutlineContentHost: failed to fetch outline. " + ex.Message);
				return;
			}

			if (cts.IsCancellationRequested)
				return;

			lastNodes = nodes;
			SD.MainThread.InvokeIfRequired(() => outline.SetRoot(nodes.FirstOrDefault()?.ToElementNode()));
		}

		void OnSelectionCommitted(object sender, EventArgs e)
		{
			if (!(outline.SelectedNode is { } node))
				return;
			// Jump to the first source node with the same name (names are unique per
			// namescope in practice; the LSP tree's own ordering is authoritative).
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

	internal static class XamlOutlineNodeExtensions
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
