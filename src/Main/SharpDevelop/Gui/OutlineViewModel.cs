using System;
using System.Composition;
using System.Windows.Controls;

using ICSharpCode.Core;
using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Modern (doc/technotes/ilspy.md "Docking and layout replacement" item 4, 2026-08-03)
/// replacement for the legacy AddInTree-registered <see cref="OutlinePad"/>: shows a single
/// child control determined by whichever document currently has focus, same behavior as before,
/// just as a <see cref="ToolPaneModel"/> instead of an <see cref="AbstractPadContent"/>.
/// </summary>
[Export(typeof(OutlineViewModel))]
[Export("ToolPane", typeof(ToolPaneModel))]
[Shared]
internal sealed class OutlineViewModel : ToolPaneModel, IOutlinePadHost, IDisposable
{
    readonly ContentPresenter contentControl = new ContentPresenter();
    bool subscribed;

    public OutlineViewModel()
    {
        Title = "Outline";
        ContentId = "Outline";
        IsVisible = false; // Matches the legacy Pad's `defaultPosition = "Left, Hidden"`.
        IsCloseable = true;
        PreferredDockSide = ICSharpCode.ILSpy.ViewModels.PreferredDockSide.Left;
        LegacyPadClass = typeof(OutlinePad).FullName;
        Content = contentControl;
		SD.Services.AddService(typeof(IOutlinePadHost), this);
    }

	public object HostedContent
	{
		get
		{
			EnsureSubscribed();
			// Active-content notifications can be coalesced while one designer is closed and
			// another is opened in the same dispatcher turn. A host query is also a synchronization
			// point: reconcile against the current workbench view instead of returning stale pad
			// ownership indefinitely.
			WorkbenchActiveContentChanged(null, EventArgs.Empty);
			return contentControl.Content;
		}
	}

    /// <summary>
    /// Subscribes to <c>SD.Workbench.ActiveViewContentChanged</c> on first real use rather than in
    /// the constructor. This model is constructed eagerly, while MEF composes every
    /// <c>[Export("ToolPane", typeof(ToolPaneModel))]</c> part (<see cref="DockWorkspace.ToolPanes"/>'s
    /// getter, reached from <c>AvalonDockLayout.BindSources()</c>) - which happens before
    /// <c>SD.Workbench</c> is registered, so touching it in the constructor throws. Same
    /// early-startup hazard, and the same "defer until the service exists" shape, as
    /// <c>CodeCoverageService</c>'s <c>TryHookViewOpened</c> (see doc/technotes/ilspy.md).
    /// </summary>
    internal void EnsureSubscribed()
    {
        if (subscribed || SD.Services.GetService(typeof(IWorkbench)) == null)
            return;
        subscribed = true;
        SD.Workbench.ActiveViewContentChanged += WorkbenchActiveContentChanged;
		SD.Workbench.ActiveContentChanged += WorkbenchActiveContentChanged;
        WorkbenchActiveContentChanged(null, null);
    }

    public override void Show()
    {
        EnsureSubscribed();
        base.Show();
    }

    void WorkbenchActiveContentChanged(object sender, EventArgs e)
    {
        // AvalonDock is the live authority. During rapid close/open/switch sequences the
        // workbench's cached ActiveViewContent can remain on the previous document even though
        // the dock already displays the new designer (the same condition handled by
        // od.active-view). Reading the layout first keeps the pad attached to what is visible.
        var view = (SD.Workbench as WpfWorkbench)?.WorkbenchLayout?.ActiveContent as IViewContent
            ?? SD.Workbench.ActiveViewContent;
        var host = view?.GetService(typeof(IOutlineContentHost)) as IOutlineContentHost;
        contentControl.Content = host != null
            ? host.OutlineContent
            : StringParser.Parse("${res:MainWindow.Windows.OutlinePad.NoContentAvailable}");
    }

    public void Dispose()
    {
        if (subscribed)
        {
            SD.Workbench.ActiveViewContentChanged -= WorkbenchActiveContentChanged;
			SD.Workbench.ActiveContentChanged -= WorkbenchActiveContentChanged;
		}
    }
}
