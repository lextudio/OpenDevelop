using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Docking and layout
/// replacement" item 4, 2026-08-03) - the real implementation is now
/// <see cref="ToolsPadViewModel"/>, a MEF-exported <c>ToolPaneModel</c>.
/// </summary>
internal sealed class ToolsPad : AbstractPadContent
{
    readonly ToolsPadViewModel viewModel;

    public ToolsPad()
    {
        viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<ToolsPadViewModel>();
        viewModel.EnsureSubscribed();
    }

    public override object Control => viewModel.Content;
}
