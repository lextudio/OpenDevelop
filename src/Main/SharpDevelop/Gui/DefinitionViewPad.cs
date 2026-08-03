using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Docking and layout
/// replacement" item 4, 2026-08-03) - the real implementation is now
/// <see cref="DefinitionViewViewModel"/>, a MEF-exported <c>ToolPaneModel</c> that
/// <c>AvalonDockLayout.ShowPad</c> routes to directly via <c>ToolPaneModel.LegacyPadClass</c>
/// before this class would ever actually be constructed. Kept only so the AddInTree
/// <c>&lt;Pad class="ICSharpCode.SharpDevelop.Gui.DefinitionViewPad"&gt;</c> entry still resolves
/// to a constructible type, and so any caller reaching <c>PadDescriptor.PadContent</c> directly
/// still gets real content.
/// </summary>
internal sealed class DefinitionViewPad : AbstractPadContent
{
    readonly DefinitionViewViewModel viewModel;

    public DefinitionViewPad()
    {
        viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<DefinitionViewViewModel>();
        viewModel.EnsureSubscribed();
    }

    public override object Control => viewModel.Content;

    public override void Dispose()
    {
        viewModel.Dispose();
    }
}
