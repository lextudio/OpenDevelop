using System.Windows.Controls;

using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Docking and layout
/// replacement" item 4, 2026-08-03) - the real implementation is now
/// <see cref="TaskListViewModel"/>, a MEF-exported <c>ToolPaneModel</c>. Also keeps the static
/// <c>Instance</c>/<c>SelectedScopeIndex</c>/<c>DisplayedTokens</c>/<c>IsInitialized</c>/
/// <c>UpdateItems()</c> surface <c>TaskListPadCommands.cs</c>'s toolbar items already depend on
/// (they reference <c>TaskListPad.Instance</c> directly, not <c>this.Owner</c>, so forwarding
/// here means that file needs no changes at all).
/// </summary>
internal sealed class TaskListPad : AbstractPadContent
{
    readonly TaskListViewModel viewModel;

    public TaskListPad()
    {
        viewModel = OpenDevelopMefHost.ExportProvider.GetExportedValue<TaskListViewModel>();
        viewModel.EnsureSubscribed();
    }

    public override object Control => viewModel.Content;
}
