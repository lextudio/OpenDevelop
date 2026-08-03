// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using ICSharpCode.AvalonEdit.AddIn;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.Core;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpyX.TreeView;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.ILSpyAddIn
{
	/// <summary>
	/// Hosts the combined decompilation of an arbitrary multi-node Assemblies-tree selection - the
	/// native-document counterpart of real ILSpy's own bespoke-pane "select several nodes, see them
	/// all decompiled together" behavior (<see cref="TextView.DecompilerTextView.DecompileAsync"/>'s
	/// per-node loop, mirrored by <see cref="ILSpyDecompilerService.DecompileNodes"/>).
	///
	/// Unlike <see cref="DecompiledViewContent"/>, there is no stable per-selection identity to
	/// reuse by - an arbitrary combination of nodes has no natural URI - so this is always a single
	/// instance, reused and overwritten on every multi-selection (see
	/// <see cref="IlSpyWorkspaceHost"/>), exactly how the retired-for-this-case bespoke pane behaved:
	/// one shared "current decompile" surface, content replaced each time, not one tab per
	/// selection.
	/// </summary>
	sealed class DecompiledSelectionViewContent : AbstractViewContentWithoutFile, IDisposable
	{
		readonly CodeEditor codeEditor = new CodeEditor();
		CancellationTokenSource cancellation = new CancellationTokenSource();
		IReadOnlyList<DecompiledReferenceSpan> references = Array.Empty<DecompiledReferenceSpan>();

		public DecompiledSelectionViewContent()
		{
			this.Services = codeEditor.GetRequiredService<IServiceContainer>();
			codeEditor.PrimaryTextEditor.TextArea.LeftMargins.RemoveAll(m => m is ChangeMarkerMargin);
			// See DecompiledViewContent's identical hookup for why this is a dedicated handler
			// rather than relying on CodeEditorView's own Ctrl+Click "Go To Definition" - that one
			// resolves through the Roslyn-backed LanguageServiceRegistry, which has no entry for
			// virtual ilspy:// content.
			codeEditor.PrimaryTextEditor.PreviewMouseDown += OnPreviewMouseDown;

			this.TitleName = "[Selection]";
			this.codeEditor.FileName = FileName.Create("ilspy://selection.cs");
			this.codeEditor.ActiveTextEditor.IsReadOnly = true;
			this.codeEditor.ActiveTextEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
		}

		public override FileName PrimaryFileName => codeEditor.FileName;
		public override object Control => codeEditor;
		public override bool IsReadOnly => true;

		/// <summary>
		/// The document's current text, for callers (od.ilspy.status) that need to read it
		/// deterministically rather than through <c>SD.Workbench.ActiveViewContent</c> - see
		/// <see cref="IlSpyWorkspaceHost.DecompiledSelectionView"/>'s doc comment for why.
		/// </summary>
		public string CurrentText => codeEditor.Document.Text;

		public override void Load()
		{
			// nothing to do - RefreshAsync is what populates content.
		}

		public override void Save()
		{
			// Read-only, ephemeral combined view - nothing to persist (matches DecompiledViewContent).
		}

		public override void Dispose()
		{
			cancellation.Cancel();
			codeEditor.PrimaryTextEditor.PreviewMouseDown -= OnPreviewMouseDown;
			codeEditor.Dispose();
			base.Dispose();
		}

		/// <summary>
		/// Re-decompiles this reused document's content for a fresh multi-node selection,
		/// cancelling whatever decompile was still in flight for the previous one - mirrors
		/// DecompilerTextView.DecompileAsync's own "starting a new one cancels the old one"
		/// contract for the same reason (rapid tree-selection changes must not queue up stale work).
		/// </summary>
		public async Task RefreshAsync(SharpTreeNode[] nodes, Language language, DecompilationOptions options)
		{
			cancellation.Cancel();
			var token = (cancellation = new CancellationTokenSource()).Token;
			try {
				// Only the decompile itself runs on the background thread. `await` without
				// ConfigureAwait(false) resumes on the original SynchronizationContext (the WPF
				// Dispatcher) - required, since AvalonEdit's Document.Text is not thread-safe and
				// setting it directly from inside Task.Run's own thread (an earlier version of this
				// method did exactly that) throws a cross-thread-access exception with nothing to
				// observe it, silently leaving the document empty. Mirrors
				// DecompiledViewContent.InitializeView's identical await-then-set-Text shape.
				var result = await Task.Run(() => ILSpyDecompilerService.DecompileNodes(nodes, language, options), token);
				if (token.IsCancellationRequested)
					return;
				references = result.References;
				codeEditor.Document.Text = result.Output;
				codeEditor.Document.UndoStack.ClearAll();
			} catch (OperationCanceledException) {
				// a newer RefreshAsync superseded this one - not a failure.
			} catch (Exception ex) {
				if (token.IsCancellationRequested)
					return;
				SD.AnalyticsMonitor.TrackException(ex);
				var writer = new StringWriter();
				writer.WriteLine("Exception while decompiling the selected nodes.");
				writer.WriteLine();
				writer.WriteLine(ex.ToString());
				references = Array.Empty<DecompiledReferenceSpan>();
				codeEditor.Document.Text = writer.ToString();
				codeEditor.Document.UndoStack.ClearAll();
			}
		}

		#region Reference hyperlink navigation - see DecompiledViewContent's identical region
		void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
		{
			if (e.ChangedButton != MouseButton.Left || Keyboard.Modifiers != ModifierKeys.Control)
				return;
			var editor = codeEditor.ActiveTextEditor;
			var position = editor.GetPositionFromPoint(e.GetPosition(editor));
			if (position == null)
				return;
			int offset = editor.Document.GetOffset(position.Value.Location);
			if (TryNavigateAtOffset(offset))
				e.Handled = true;
		}

		internal bool TryNavigateAtOffset(int offset)
		{
			var span = references.FirstOrDefault(r => offset >= r.Offset && offset < r.Offset + r.Length);
			if (span == null)
				return false;
			// Unlike DecompiledViewContent, there is no single "this document's assembly" to
			// resolve the reference against - a multi-selection can span several. The reference's
			// own module (captured alongside its span) supplies it instead.
			if (span.AssemblyFile == null)
				return false;
			NavigateToDecompiledEntityService.NavigateTo(span.AssemblyFile, span.TopLevelTypeReflectionName, span.MemberKey);
			return true;
		}
		#endregion
	}
}
