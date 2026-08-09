using System;
using System.Diagnostics;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;

namespace FSharpBinding
{
	/// <summary>
	/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Legacy Pad migration",
	/// 2026-08-09) - the real implementation is now <see cref="FSharpInteractiveViewModel"/>.
	/// Constructed once with a plain <c>new</c> and cached in a static field (the AddIn's
	/// assembly is never scanned by <c>OpenDevelopMefHost</c>), then registered with the real
	/// docking host via <c>IPaneModelHost.Add</c>. Must stay a real, constructible
	/// <see cref="AbstractPadContent"/> for the same
	/// <c>PadDescriptor.BringPadToFront()</c>/<c>CreatePad()</c> reason as every other shim in
	/// this migration - and because <c>SentToFSharpInteractive</c> still reaches the process
	/// through <c>SD.Workbench.GetPad(typeof(FSharpInteractive)).PadContent as FSharpInteractive</c>.
	/// </summary>
	public class FSharpInteractive : AbstractPadContent
	{
		static FSharpInteractiveViewModel viewModel;

		public FSharpInteractive()
		{
			if (viewModel == null) {
				viewModel = new FSharpInteractiveViewModel();
				(SD.Services.GetService(typeof(IPaneModelHost)) as IPaneModelHost)?.Add(viewModel);
			}
		}

		public override object Control => viewModel?.Content;

		internal Process fsiProcess => viewModel?.fsiProcess;

		internal bool foundCompiler => viewModel?.foundCompiler ?? false;
	}

	public class SentToFSharpInteractive : AbstractMenuCommand
	{
		public override void Run()
		{
			PadDescriptor pad = SD.Workbench.GetPad(typeof(FSharpInteractive));
			pad.BringPadToFront();
			FSharpInteractive fsharpInteractive = (FSharpInteractive)pad.PadContent;
			if (fsharpInteractive.foundCompiler) {
				ITextEditor textEditor = SD.GetActiveViewContentService<ITextEditor>();
				if (textEditor != null) {
					if (textEditor.SelectionLength > 0) {
						fsharpInteractive.fsiProcess.StandardInput.WriteLine(textEditor.SelectedText);
					} else {
						var line = textEditor.Document.GetLineByNumber(textEditor.Caret.Line);
						fsharpInteractive.fsiProcess.StandardInput.WriteLine(textEditor.Document.GetText(line));
					}
					fsharpInteractive.fsiProcess.StandardInput.WriteLine(";;");
				}
			}
		}
	}
}
