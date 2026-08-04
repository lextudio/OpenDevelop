using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Docking and layout
/// replacement" item 4, 2026-08-04) - the real implementation is now
/// <see cref="CompilerMessageViewViewModel"/>, a MEF-exported <c>ToolPaneModel</c>. Needed for real
/// (not just as a routing marker like <see cref="ToolsPad"/>'s namesake): <c>PadDescriptor.
/// BringPadToFront()</c>/<c>CreatePad()</c> unconditionally construct the AddInTree-registered
/// class via reflection regardless of whether a MEF <c>ToolPaneModel</c> already exists for it (the
/// "already a MEF tool pane" skip only applies to <c>AvalonDockLayout</c>'s own startup loop), so
/// this class must stay a real, constructible <see cref="AbstractPadContent"/> - same as
/// <see cref="ErrorListPad"/>/<see cref="ToolsPad"/>.
/// </summary>
internal sealed class CompilerMessageView : AbstractPadContent
{
    readonly CompilerMessageViewViewModel viewModel;

    public CompilerMessageView()
    {
        viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<CompilerMessageViewViewModel>();
    }

    public override object Control => viewModel.Content;
}
