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
			// Deliberately does NOT bring the Outline pad to front. Opening a .xaml file is not a
			// request to rearrange the user's pad layout, and doing it on every open stole focus
			// from whatever pad they had chosen to keep there. The outline still populates; it is
			// simply shown when the user opens the pad themselves (View > Outline, or the
			// designer's own Outline command).
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
			SD.MainThread.InvokeIfRequired(() => outline.SetRoots(nodes.Select(n => n.ToElementNode())));
		}

		void OnSelectionCommitted(object sender, EventArgs e)
		{
			if (!(outline.SelectedNode is { } node))
				return;
			// Jump to the first source node with the same symbol name (the row's Id; names are
			// unique per namescope in practice; the LSP tree's own ordering is authoritative).
			var match = Walk(lastNodes).FirstOrDefault(n => n.Name == node.Id);
			if (match != null)
				editor.JumpTo(match.Span.Start.Line, match.Span.Start.Column);
		}

		static IEnumerable<DocumentOutlineNode> Walk(IReadOnlyList<DocumentOutlineNode> nodes)
		{
			foreach (var node in nodes)
			{
				yield return node;
				foreach (var descendant in Walk(node.Children))
					yield return descendant;
			}
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
		/// model, the way the designers fill it: an element symbol's name is its XAML type, with
		/// its x:Name appended in brackets ("Button [okButton]"), so it is split back into
		/// Type="Button" and Name="okButton" - the row then reads "okButton" or "[Button]" with the
		/// Button glyph, exactly like the designer's outline. Id keeps the symbol name, which is what
		/// selection jumps by.</summary>
		public static DesignerElementNode ToElementNode(this DocumentOutlineNode node)
		{
			string name = node.Name, type = node.Kind;
			if (node.Kind == "Object") {
				type = node.Name;
				name = null;
				var bracket = node.Name.IndexOf(" [", StringComparison.Ordinal);
				if (bracket > 0 && node.Name.EndsWith("]", StringComparison.Ordinal)) {
					type = node.Name.Substring(0, bracket);
					name = node.Name.Substring(bracket + 2, node.Name.Length - bracket - 3);
				}
			}
			return new DesignerElementNode {
				Id = node.Name,
				Name = name,
				Type = type,
				IsDesignable = true,
				Children = node.Children.Select(ToElementNode).ToList()
			};
		}
	}
}
