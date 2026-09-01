using System.Composition;
using System.Windows;
using System.Windows.Controls;

using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Editor.Bookmarks;
using ICSharpCode.ILSpy.ViewModels;

namespace ICSharpCode.SharpDevelop.Gui;

/// <summary>
/// Modern (doc/technotes/ilspy.md "Legacy Pad migration", 2026-08-04) replacement for the legacy
/// AddInTree-registered <see cref="BookmarkPad"/> (AddInTree pad id "Bookmarks"): shows the
/// bookmark list, same behavior as before, just as a <see cref="ToolPaneModel"/>. See
/// <see cref="BookmarkPadViewModelBase"/> for the shared behavior with Debugger.AddIn's
/// <c>BreakPointsPadViewModel</c>, and <see cref="Editor.Bookmarks.BookmarkPad"/>'s doc comment for
/// why that shared base moved to the Base project instead of here.
/// </summary>
[Export(typeof(BookmarkPadViewModel))]
[Export("ToolPane", typeof(ToolPaneModel))]
[Shared]
internal sealed class BookmarkPadViewModel : BookmarkPadViewModelBase
{
    public BookmarkPadViewModel()
    {
        Title = "Bookmarks";
        ContentId = "BookmarkPad";
        IsVisible = false; // Matches the legacy Pad's `defaultPosition = "Bottom, Hidden"`.
        IsCloseable = true;
        PreferredDockSide = ICSharpCode.ILSpy.ViewModels.PreferredDockSide.Bottom;
        LegacyPadClass = typeof(BookmarkPad).FullName;
    }

    protected override void CreateToolBarContent()
    {
        ToolBar toolbar = ToolBarService.CreateToolBar((UIElement)control, this, "/SharpDevelop/Pads/BookmarkPad/Toolbar");
        control.Children.Add(toolbar);
    }

    protected override bool ShowBookmarkInThisPad(SDBookmark bookmark) => bookmark.ShowInPad(this);
}
