using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Editor.Bookmarks;

/// <summary>
/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Legacy Pad migration",
/// 2026-08-04) - the real implementation is now <see cref="BookmarkPadViewModel"/>, a MEF-exported
/// <c>ToolPaneModel</c>.
/// </summary>
/// <remarks>
/// Lives in the App project (same reasoning as <c>CompilerMessageView</c>'s shim: needs
/// <c>OpenDevelopMefHost.ExportProvider</c>, internal to this assembly) but keeps its original
/// namespace (<c>ICSharpCode.SharpDevelop.Editor.Bookmarks</c>, not <c>Gui</c>) since
/// <c>PadDescriptor.Class</c> ("ICSharpCode.SharpDevelop.Editor.Bookmarks.BookmarkPad") and this
/// class's own <c>LegacyPadClass</c> value must keep resolving to the same fully-qualified name
/// regardless of which project/folder the file physically lives in. Must stay a real,
/// constructible <see cref="AbstractPadContent"/> - not a bare marker - for the same
/// <c>PadDescriptor.BringPadToFront()</c>/<c>CreatePad()</c> reason as <c>CompilerMessageView</c>'s
/// shim.
/// </remarks>
internal sealed class BookmarkPad : AbstractPadContent
{
    readonly BookmarkPadViewModel viewModel;

    public BookmarkPad()
    {
        viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<BookmarkPadViewModel>();
    }

    public override object Control => viewModel.Content;
}
