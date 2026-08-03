using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Docking and layout
/// replacement" item 4, 2026-08-03) - the real implementation is now
/// <see cref="ErrorListViewModel"/>, a MEF-exported <c>ToolPaneModel</c>. Keeps the static
/// <c>ShowAfterBuild</c> surface some external callers used directly (it never depended on the pad
/// instance, just forwarded to <see cref="BuildOptions"/>).
/// </summary>
internal sealed class ErrorListPad : AbstractPadContent
{
    readonly ErrorListViewModel viewModel;

    public static bool ShowAfterBuild {
        get => Project.BuildOptions.ShowErrorListAfterBuild;
        set => Project.BuildOptions.ShowErrorListAfterBuild = value;
    }

    public ErrorListPad()
    {
        viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<ErrorListViewModel>();
        viewModel.EnsureSubscribed();
    }

    public override object Control => viewModel.Content;
}
