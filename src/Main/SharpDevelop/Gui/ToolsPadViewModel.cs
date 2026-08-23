using System;
using System.Composition;
using System.Windows.Controls;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Modern (doc/technotes/ilspy.md "Docking and layout replacement" item 4, 2026-08-03)
/// replacement for the legacy AddInTree-registered <see cref="ToolsPad"/> (AddInTree pad id
/// "SideBar", historically the FormsDesigner toolbox host): shows whatever
/// <see cref="IToolsHost.ToolsContent"/> the active view content exposes, same behavior as before,
/// just as a <see cref="ToolPaneModel"/>.
/// </summary>
[Export(typeof(ToolsPadViewModel))]
[Export("ToolPane", typeof(ToolPaneModel))]
[Shared]
internal sealed class ToolsPadViewModel : ToolPaneModel, IToolsPadHost
{
    readonly ContentPresenter contentControl = new ContentPresenter();
    bool subscribed;

    public ToolsPadViewModel()
    {
        Title = "Tools";
        ContentId = "ToolsPad";
        IsVisible = true; // Matches the legacy Pad's `defaultPosition = "Left"`.
        IsCloseable = true;
        PreferredDockSide = ICSharpCode.SharpDevelop.ViewModels.PreferredDockSide.Left;
        LegacyPadClass = typeof(ToolsPad).FullName;
        Content = contentControl;
        SD.Services.AddService(typeof(IToolsPadHost), this);
        // The workbench service is installed before its layout composes ToolPane exports. Calling
        // this here covers the normal, already-visible startup path where Show() is never invoked;
        // the guarded calls below remain for unusual early composition and legacy activation.
        EnsureSubscribed();
    }

    public object HostedContent {
        get {
            EnsureSubscribed();
            return contentControl.Content;
        }
    }

    /// <summary>
    /// Subscribes to <c>SD.Workbench.ActiveViewContentChanged</c> on first real use rather than in
    /// the constructor - same early-startup hazard already found and fixed for the other migrated
    /// pads (<see cref="ErrorListViewModel"/> et al). Deferred to every externally-reachable entry
    /// point, not only <see cref="Show"/>, since this pad defaults visible - nothing calls Show()
    /// on an already-visible MEF-composed pane.
    /// </summary>
    internal void EnsureSubscribed()
    {
        if (subscribed || SD.Services.GetService(typeof(IWorkbench)) == null)
            return;
        subscribed = true;

        SD.Workbench.ActiveViewContentChanged += WorkbenchActiveContentChanged;
        WorkbenchActiveContentChanged(null, null);
    }

    public override void Show()
    {
        EnsureSubscribed();
        base.Show();
    }

    void WorkbenchActiveContentChanged(object sender, EventArgs e)
    {
        IToolsHost th = SD.GetActiveViewContentService<IToolsHost>();
        contentControl.Content = th != null && th.ToolsContent != null
            ? th.ToolsContent
            : StringParser.Parse("${res:SharpDevelop.SideBar.NoToolsAvailableForCurrentDocument}");
    }
}
