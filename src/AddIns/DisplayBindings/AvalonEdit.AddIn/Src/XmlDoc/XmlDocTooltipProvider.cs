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

// Backend-neutral tooltip provider. Roslyn and LSP both supply QuickInfo through ILanguageService.

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

using ICSharpCode.AvalonEdit.AddIn.Options;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace ICSharpCode.AvalonEdit.AddIn.XmlDoc
{
	public class XmlDocTooltipProvider : ITextAreaToolTipProvider
	{
		public void HandleToolTipRequest(ToolTipRequestEventArgs e)
		{
			if (!e.InDocument)
				return;
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(e.Editor.FileName, out var service))
				return;
			try {
				var documentId = new ICSharpCode.SharpDevelop.LanguageServices.DocumentId(e.Editor.FileName);
				// Never block the UI thread on the async LSP round-trip: its continuations resume
				// on the captured DispatcherSynchronizationContext, so a synchronous
				// .GetAwaiter().GetResult() here deadlocks the whole app the moment the server
				// has to do real work (same trap as LanguageServiceParserAdapter's
				// UpsertLanguageServiceDocument). Run the chain on a thread-pool thread instead,
				// and only wait a bounded time so a slow server degrades to "no tooltip" instead
				// of freezing the hover.
				var quickInfo = FetchQuickInfoAsync(
					service, documentId, e.Editor.Document.Text, e.Editor.Document.GetOffset(e.LogicalPosition))
					.WaitForResult(TimeSpan.FromMilliseconds(300));
				if (quickInfo == null)
					return;
				var builder = new DocumentationUIBuilder();
				builder.AddCodeBlock(quickInfo.Text, keepLargeMargin: true);
				e.SetToolTip(new FlowDocumentTooltip(builder.CreateFlowDocument()));
			} catch (Exception ex) {
				LoggingService.Warn("Quick info failed for '" + e.Editor.FileName + "'. " + ex.Message);
			}
		}

		static async Task<QuickInfo> FetchQuickInfoAsync(
			ICSharpCode.SharpDevelop.LanguageServices.ILanguageService service,
			ICSharpCode.SharpDevelop.LanguageServices.DocumentId documentId,
			string text,
			int offset)
		{
			// ConfigureAwait(false): keep every continuation off the UI thread so the bounded
			// wait in HandleToolTipRequest can never deadlock against the dispatcher.
			await service.UpsertDocumentAsync(documentId, text, CancellationToken.None).ConfigureAwait(false);
			return await service.GetQuickInfoAsync(documentId, offset, CancellationToken.None).ConfigureAwait(false);
		}

		sealed class FlowDocumentTooltip : Popup, ITooltip
		{
			FlowDocumentScrollViewer viewer;

			public FlowDocumentTooltip(FlowDocument document)
			{
				TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
				viewer = new FlowDocumentScrollViewer();
				viewer.Document = document;
				Border border = new Border {
					Background = SystemColors.InfoBrush,
					BorderBrush = SystemColors.InfoTextBrush,
					BorderThickness = new Thickness(1),
					MaxHeight = 400,
					Child = viewer
				};
				this.Child = border;
				viewer.Foreground = SystemColors.InfoTextBrush;
				document.FontSize = CodeEditorOptions.Instance.FontSize;
			}

			public bool CloseWhenMouseMovesAway {
				get { return !this.IsKeyboardFocusWithin; }
			}

			protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
			{
				base.OnLostKeyboardFocus(e);
				this.IsOpen = false;
			}

			protected override void OnMouseLeave(MouseEventArgs e)
			{
				base.OnMouseLeave(e);
				// When the mouse is over the popup, it is possible for SharpDevelop to be minimized,
				// or moved into the background, and yet the popup stays open.
				// We don't have a good method here to check whether the mouse moved back into the text area
				// or somewhere else, so we'll just close the popup.
				if (CloseWhenMouseMovesAway)
					this.IsOpen = false;
			}
		}
	}

	static class TaskExtensions
	{
		/// <summary>
		/// Waits up to <paramref name="timeout"/> for the task; returns default(T) on timeout
		/// instead of throwing. Safe on the UI thread only if the task was started with
		/// <c>ConfigureAwait(false)</c> (so no continuation needs the dispatcher).
		/// </summary>
		public static T WaitForResult<T>(this Task<T> task, TimeSpan timeout)
		{
			if (task.Wait(timeout))
				return task.Result;
			LoggingService.Warn("Language service quick info timed out after " + timeout + "; tooltip suppressed.");
			return default;
		}
	}
}
