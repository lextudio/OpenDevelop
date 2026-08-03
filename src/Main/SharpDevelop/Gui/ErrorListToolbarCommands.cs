using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Toolbar items for the Error List pad. Resolve <see cref="ErrorListViewModel"/> directly via MEF
/// (doc/technotes/ilspy.md "Docking and layout replacement" item 4, 2026-08-03) rather than through
/// <c>ErrorListPad.Instance</c> (a legacy singleton set only when that shim class actually gets
/// constructed, which no longer happens on the common MEF-first path) - same pattern already used
/// for <c>TaskListPadCommands.cs</c>.
/// </summary>
public class ShowErrorsToggleButton : AbstractCheckableMenuCommand
{
    public override bool IsChecked {
        get => OpenDevelopMefHost.ExportProvider.GetExportedValue<ErrorListViewModel>().ShowErrors;
        set => OpenDevelopMefHost.ExportProvider.GetExportedValue<ErrorListViewModel>().ShowErrors = value;
    }
}

public class ShowWarningsToggleButton : AbstractCheckableMenuCommand
{
    public override bool IsChecked {
        get => OpenDevelopMefHost.ExportProvider.GetExportedValue<ErrorListViewModel>().ShowWarnings;
        set => OpenDevelopMefHost.ExportProvider.GetExportedValue<ErrorListViewModel>().ShowWarnings = value;
    }
}

public class ShowMessagesToggleButton : AbstractCheckableMenuCommand
{
    public override bool IsChecked {
        get => OpenDevelopMefHost.ExportProvider.GetExportedValue<ErrorListViewModel>().ShowMessages;
        set => OpenDevelopMefHost.ExportProvider.GetExportedValue<ErrorListViewModel>().ShowMessages = value;
    }
}
