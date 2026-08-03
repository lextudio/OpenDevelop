using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Docking and layout
/// replacement" item 4, 2026-08-03) - the real implementation is now <see cref="OutlineViewModel"/>,
/// a MEF-exported <c>ToolPaneModel</c> that <c>AvalonDockLayout.ShowPad</c> routes to directly via
/// <c>ToolPaneModel.LegacyPadClass</c> before this class would ever actually be constructed (the
/// same "IsMefToolPane" bridge <c>ProjectBrowserPad</c>/<c>ProjectBrowserViewModel</c> already use).
/// Kept only so the AddInTree <c>&lt;Pad class="ICSharpCode.SharpDevelop.Gui.OutlinePad"&gt;</c>
/// entry (title/icon/category/default-position metadata) still resolves to a constructible type,
/// and so any caller reaching <c>PadDescriptor.PadContent</c> directly (bypassing the
/// AvalonDockLayout bridge - e.g. a diagnostic action) still gets the real content.
/// </summary>
internal sealed class OutlinePad : AbstractPadContent
{
    readonly OutlineViewModel viewModel;

    public OutlinePad()
    {
        viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<OutlineViewModel>();
        // The MEF route subscribes lazily from OutlineViewModel.Show(); this legacy route never
        // calls it (AvalonDockLayout drives AvalonPadContent, not the model), so without this the
        // shim would hand AvalonDock an empty ContentPresenter - the pad would dock but render
        // nothing. Constructing this shim is itself the "pad is being materialized" signal, which
        // is exactly when the original OutlinePad subscribed, so this restores that timing.
        viewModel.EnsureSubscribed();
    }

    public override object Control => viewModel.Content;

    public override void Dispose()
    {
        viewModel.Dispose();
    }
}
