using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Docking and layout
/// replacement" item 4, 2026-08-03) - the real implementation is now
/// <see cref="PropertyPadViewModel"/>, a MEF-exported <c>ToolPaneModel</c>. Kept only so the
/// AddInTree <c>&lt;Pad class="ICSharpCode.SharpDevelop.Gui.PropertyPad"&gt;</c> entry still
/// resolves to a constructible type, and so any caller reaching <c>PadDescriptor.PadContent</c>
/// directly still gets real content. External callers that used to reach the static
/// <c>PropertyPad.Grid</c>/<c>ActiveContainer</c> members now go through
/// <see cref="IPropertyPadHost"/> (Base project) instead - this class doesn't expose them anymore.
/// </summary>
internal sealed class PropertyPad : AbstractPadContent
{
    readonly PropertyPadViewModel viewModel;

    public PropertyPad()
    {
        viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<PropertyPadViewModel>();
        viewModel.EnsureSubscribed();
    }

    public override object Control => viewModel.Content;

    public override void Dispose()
    {
        viewModel.Dispose();
    }
}
