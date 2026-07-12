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
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace ICSharpCode.XamlBinding
{
	/// <summary>
	/// Populates the Outline pad for a .xaml text editor view from the XAML language server's
	/// textDocument/documentSymbol response (via <see cref="XamlOutlineLspProvider"/>), instead of
	/// any local XAML parsing.
	/// </summary>
	public sealed class XamlOutlineContentHost : IOutlineContentHost, IDisposable
	{
		readonly ITextEditor editor;
		readonly TreeView treeView = new TreeView();
		CancellationTokenSource refreshCts;

		public XamlOutlineContentHost(ITextEditor editor)
		{
			this.editor = editor;

			var itemTemplate = new HierarchicalDataTemplate(typeof(DocumentOutlineNode)) {
				ItemsSource = new System.Windows.Data.Binding(nameof(DocumentOutlineNode.Children))
			};
			var text = new FrameworkElementFactory(typeof(TextBlock));
			text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DocumentOutlineNode.Name)));
			itemTemplate.VisualTree = text;
			treeView.ItemTemplate = itemTemplate;

			treeView.MouseDoubleClick += OnMouseDoubleClick;

			editor.Document.TextChanged += OnDocumentChanged;
			RefreshAsync();
		}

		public object OutlineContent {
			get { return treeView; }
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

			SD.MainThread.InvokeIfRequired(() => treeView.ItemsSource = nodes);
		}

		void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
		{
			if (!(treeView.SelectedItem is DocumentOutlineNode node))
				return;

			editor.JumpTo(node.Span.Start.Line, node.Span.Start.Column);
		}

		public void Dispose()
		{
			refreshCts?.Cancel();
			editor.Document.TextChanged -= OnDocumentChanged;
			treeView.MouseDoubleClick -= OnMouseDoubleClick;
		}
	}
}
