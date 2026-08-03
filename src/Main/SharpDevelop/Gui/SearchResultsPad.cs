using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Editor.Search;

/// <summary>
/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Docking and layout
/// replacement" item 4, 2026-08-03) - the real implementation is now
/// <see cref="SearchResultsPadViewModel"/>, a MEF-exported <c>ToolPaneModel</c>. Kept in this
/// original namespace (not <c>ICSharpCode.SharpDevelop.Gui</c>, unlike this migration's other
/// shims) so the existing AddInTree <c>&lt;Pad class="ICSharpCode.SharpDevelop.Editor.Search.
/// SearchResultsPad"&gt;</c> entry needs no change. Kept only so that entry still resolves to a
/// constructible type, and so any caller reaching <c>PadDescriptor.PadContent</c> directly still
/// gets real content. External callers that used to reach the static
/// <c>SearchResultsPad.Instance</c> singleton now go through <see cref="SearchResultsHost.Current"/>
/// instead - this class doesn't expose <c>Instance</c> anymore.
/// </summary>
internal sealed class SearchResultsPad : AbstractPadContent
{
    readonly SearchResultsPadViewModel viewModel;

    public SearchResultsPad()
    {
        viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<SearchResultsPadViewModel>();
    }

    public override object Control => viewModel.Content;
}
